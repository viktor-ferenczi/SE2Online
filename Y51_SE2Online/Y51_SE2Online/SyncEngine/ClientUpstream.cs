using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class ClientUpstream
{
	private readonly IMpLog _log;

	private readonly TypeKeyTable _types = new TypeKeyTable();

	private readonly SignalKeyTable _signals = new SignalKeyTable();

	private readonly Dictionary<(uint, ushort), byte[]> _current = new Dictionary<(uint, ushort), byte[]>();

	private readonly HashSet<(uint, ushort)> _changed = new HashSet<(uint, ushort)>();

	private readonly Dictionary<(uint, ushort), int> _removed = new Dictionary<(uint, ushort), int>();

	private readonly HashSet<(uint, ushort)> _removedChanged = new HashSet<(uint, ushort)>();

	public const int RemovalResends = 10;

	private readonly List<WireCommand> _commands = new List<WireCommand>();

	private readonly Dictionary<string, long> _dropped = new Dictionary<string, long>();

	private readonly Dictionary<int, string> _notOursNames = new Dictionary<int, string>();

	private readonly List<(int, Segment)> _scratch = new List<(int, Segment)>();

	private long _lastFullMs;

	public const int FullResendMs = 100;

	public long Buffers;

	public long CommandsSeen;

	public long DataKept;

	public long DataRemoved;

	public long SignalsKept;

	public long NotOurs;

	public long NotForwardable;

	public long BadRefs;

	public long Unparsed;

	public long Stories;

	public long Closures;

	public long ObservableEdits;

	public long ObservableSwallowed;

	public Action<object?, string?, object?>? OnObservableEdit;

	private Func<Entity?, uint?>? _removable;

	internal IReadOnlyDictionary<(uint, ushort), byte[]> CurrentInput => _current;

	internal IReadOnlyDictionary<(uint, ushort), int> PendingRemovals => _removed;

	internal IReadOnlyList<WireCommand> PendingCommands => _commands;

	internal TypeKeyTable InputTypes => _types;

	internal SignalKeyTable SignalKeys => _signals;

	public ClientUpstream(IMpLog log)
	{
		_log = log;
	}

	public void Reset()
	{
		_current.Clear();
		_changed.Clear();
		_removed.Clear();
		_removedChanged.Clear();
	}

	public void Capture(ReplicationCommandBuffer cb, Func<Entity?, uint?> controlled, ObjectSpace space, Action<Delegate>? onStory, Func<Entity?, uint?>? removable = null)
	{
		_removable = removable ?? controlled;
		int num = CommandBuffers.Count(cb);
		for (int i = 0; i < num; i++)
		{
			byte[] bytes = CommandBuffers.Bytes(cb, i);
			Buffers++;
			if (!Walk(cb, bytes, controlled, space, onStory))
			{
				Unparsed++;
			}
		}
	}

	private void Drop(string name, string why)
	{
		string key = Short(name) + ": " + why;
		_dropped[key] = (_dropped.TryGetValue(key, out var value) ? (value + 1) : 1);
	}

	private bool Walk(ReplicationCommandBuffer cb, byte[] bytes, Func<Entity?, uint?> controlled, ObjectSpace space, Action<Delegate>? onStory)
	{
		int num = 0;
		while (num + 8 <= bytes.Length)
		{
			int stub = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(num + 4));
			int p = num + 8;
			CommandLayout commandLayout = CommandLayouts.ByStub(stub);
			if (commandLayout == null)
			{
				return false;
			}
			CommandsSeen++;
			int size;
			switch (commandLayout.Kind)
			{
			case CommandKind.PostData:
			{
				if (p + 16 > bytes.Length)
				{
					return false;
				}
				int num2 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 4));
				int num3 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8));
				int num4 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 12));
				if (num4 < 0 || p + 16 + num4 > bytes.Length)
				{
					return false;
				}
				size = 16 + num4;
				uint? num5 = controlled((num2 >= 0) ? (cb.ReadObject(num2) as Entity) : null);
				if (!num5.HasValue)
				{
					NotOurs++;
					if (!_notOursNames.TryGetValue(num3, out string value))
					{
						try
						{
							value = EngineCodec.DescribeType(num3).Name;
						}
						catch (Exception)
						{
							value = $"type {num3}";
						}
						_notOursNames[num3] = value;
					}
					Drop(value, "data on an entity this player does not control");
					break;
				}
				ushort key;
				try
				{
					key = _types.GetOrAdd(num3, EngineCodec.DescribeType).Key;
				}
				catch (Exception)
				{
					NotForwardable++;
					break;
				}
				(uint, ushort) tuple = (num5.Value, key);
				byte[] array = bytes.AsSpan(p + 16, num4).ToArray();
				if (!_current.TryGetValue(tuple, out byte[] value2) || !value2.AsSpan().SequenceEqual(array))
				{
					_changed.Add(tuple);
				}
				_current[tuple] = array;
				_removed.Remove(tuple);
				_removedChanged.Remove(tuple);
				DataKept++;
				break;
			}
			case CommandKind.RemoveData:
			{
				size = commandLayout.PayloadBytes;
				if (size < 12 || p + size > bytes.Length)
				{
					return false;
				}
				int num6 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 4));
				int localTypeId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8));
				uint? num7 = (_removable ?? controlled)((num6 >= 0) ? (cb.ReadObject(num6) as Entity) : null);
				if (!num7.HasValue)
				{
					NotOurs++;
					break;
				}
				ushort key2;
				try
				{
					key2 = _types.GetOrAdd(localTypeId, EngineCodec.DescribeType).Key;
				}
				catch (Exception)
				{
					NotForwardable++;
					break;
				}
				(uint, ushort) tuple2 = (num7.Value, key2);
				_current.Remove(tuple2);
				_changed.Remove(tuple2);
				_removed[tuple2] = 10;
				_removedChanged.Add(tuple2);
				DataRemoved++;
				break;
			}
			case CommandKind.Signal:
			{
				if (commandLayout.Name.StartsWith("Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+", StringComparison.Ordinal))
				{
					size = commandLayout.PayloadBytes;
					if (size < 0 || p + size > bytes.Length)
					{
						return false;
					}
					if (commandLayout.Name == "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReplicateMemberChangedSignal" && OnObservableEdit != null && size >= 16)
					{
						ObservableEdits++;
						OnObservableEdit(Slot(4), Slot(8) as string, Slot(12));
					}
					else
					{
						ObservableSwallowed++;
					}
					break;
				}
				if (!commandLayout.Forwardable)
				{
					if (commandLayout.PayloadBytes < 0 && !commandLayout.Dynamic)
					{
						return false;
					}
					size = ((commandLayout.PayloadBytes >= 0) ? commandLayout.PayloadBytes : commandLayout.Frame(bytes.AsSpan(p), new List<(int, Segment)>()));
					if (size < 0)
					{
						return false;
					}
					NotForwardable++;
					Drop(commandLayout.Name, commandLayout.Why ?? "not forwardable");
					break;
				}
				SignalKeyDef orAdd = _signals.GetOrAdd(commandLayout.Name, commandLayout.Def(0).PayloadBytes, commandLayout.Signature);
				string fail = null;
				space.ClearLocalIds();
				object first = null;
				bool firstRead = false;
				WireCommand wireCommand = SignalCodec.Encode(commandLayout, orAdd.Key, bytes.AsSpan(p), (int i) =>
				{
					object obj2 = cb.ReadObject(i);
					if (!firstRead)
					{
						firstRead = true;
						first = obj2;
					}
					return obj2;
				}, space, _scratch, out size, ref fail);
				if (size < 0)
				{
					return false;
				}
				if (wireCommand != null && commandLayout.Segments.Length != 0 && commandLayout.Segments[0].Kind == SegmentKind.Ref && ControlGuard.DropUpstream(commandLayout, first))
				{
					NotOurs++;
					break;
				}
				if (wireCommand != null && ControlGuard.IsHeldByForeignCharacter(first as Entity))
				{
					NotOurs++;
					Drop(commandLayout.Name, "another player's tool");
					break;
				}
				if (wireCommand != null && HostOwnsBookkeeping(commandLayout.Name))
				{
					NotForwardable++;
					Drop(commandLayout.Name, "fracture bookkeeping (the host's own game sends it)");
					break;
				}
				if (wireCommand != null && (commandLayout.Name == "AnimationEventSignal" || commandLayout.Name.EndsWith(".AnimationEventSignal", StringComparison.Ordinal)))
				{
					NotForwardable++;
					Drop(commandLayout.Name, "client-only animation event");
					break;
				}
				if (wireCommand != null && commandLayout.StringIdOffsets.Length != 0)
				{
					byte[] array2 = StringIdTable.Client?.Map(wireCommand.Payload, commandLayout.StringIdOffsets, toLocal: false);
					if (array2 == null)
					{
						BadRefs++;
						Drop(commandLayout.Name, "StringId the host does not know");
						break;
					}
					wireCommand.Payload = array2;
				}
				if (wireCommand == null)
				{
					BadRefs++;
					Drop(commandLayout.Name, fail ?? "?");
				}
				else
				{
					_commands.Add(wireCommand);
					SignalsKept++;
				}
				break;
			}
			case CommandKind.PostInvoke:
			{
				size = 8;
				if (p + size > bytes.Length)
				{
					return false;
				}
				int id = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 4));
				if (cb.ReadObject(id) is Delegate obj && obj.Method.Name.Contains("MoveNextRemotely", StringComparison.Ordinal) && onStory != null)
				{
					Stories++;
					onStory(obj);
				}
				else
				{
					Closures++;
				}
				break;
			}
			default:
				size = commandLayout.PayloadBytes;
				if (size < 0 || p + size > bytes.Length)
				{
					return false;
				}
				NotForwardable++;
				break;
			}
			num = p + size;
			object? Slot(int off)
			{
				int num8 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + off));
				if (num8 < 0)
				{
					return null;
				}
				return cb.ReadObject(num8);
			}
		}
		return num == bytes.Length;
	}

	public static int CollectStoryMoves(ReplicationCommandBuffer cb, List<Delegate> into, int max)
	{
		int num = 0;
		int num2 = CommandBuffers.Count(cb);
		for (int i = 0; i < num2; i++)
		{
			byte[] array = CommandBuffers.Bytes(cb, i);
			int num3 = 0;
			while (num3 + 8 <= array.Length)
			{
				CommandLayout commandLayout = CommandLayouts.ByStub(BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(num3 + 4)));
				int num4 = num3 + 8;
				if (commandLayout == null)
				{
					break;
				}
				int num5;
				if (commandLayout.Kind == CommandKind.PostData)
				{
					if (num4 + 16 > array.Length)
					{
						break;
					}
					num5 = 16 + BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(num4 + 12));
				}
				else if (commandLayout.Kind == CommandKind.PostInvoke)
				{
					num5 = 8;
					if (num4 + 8 <= array.Length && cb.ReadObject(BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(num4 + 4))) is Delegate obj && obj.Method.Name.Contains("MoveNextRemotely", StringComparison.Ordinal) && into.Count < max)
					{
						into.Add(obj);
						num++;
					}
				}
				else if (commandLayout.PayloadBytes >= 0)
				{
					num5 = commandLayout.PayloadBytes;
				}
				else
				{
					num5 = (commandLayout.Dynamic ? commandLayout.Frame(array.AsSpan(num4), new List<(int, Segment)>()) : (-1));
				}
				if (num5 < 0 || num4 + num5 > array.Length)
				{
					break;
				}
				num3 = num4 + num5;
			}
		}
		return num;
	}

	public void Flush(ClientSession session, long nowMs)
	{
		List<TypeKeyDef> list = _types.TakeUnannounced();
		if (list.Count > 0)
		{
			session.AnnounceInputTypes(list);
			_log.Info("client: upstream input types: " + string.Join(", ", list.ConvertAll((TypeKeyDef d) => $"{d.Key}={d.ShortName}")));
		}
		List<SignalKeyDef> list2 = _signals.TakeUnannounced();
		if (list2.Count > 0)
		{
			session.AnnounceSignals(list2);
			foreach (SignalKeyDef item2 in list2)
			{
				_log.Info($"client: upstream signal key {item2.Key} = {Short(item2.Name)} ({((item2.PayloadBytes == ushort.MaxValue) ? "dynamic" : (item2.PayloadBytes + " B"))}, [{item2.RefOffsets}])");
			}
		}
		List<InputEntry> list3 = new List<InputEntry>();
		bool flag = nowMs - _lastFullMs >= 100;
		if (flag)
		{
			_lastFullMs = nowMs;
		}
		foreach (var (item, bytes) in _current)
		{
			if (flag || _changed.Contains(item))
			{
				list3.Add(new InputEntry(item.Item1, item.Item2, bytes));
			}
		}
		_changed.Clear();
		if (_removed.Count > 0)
		{
			foreach (var item3 in new List<(uint, ushort)>(_removed.Keys))
			{
				bool flag2 = _removedChanged.Contains(item3);
				if (flag2 || flag)
				{
					list3.Add(new InputEntry(item3.Item1, item3.Item2, Array.Empty<byte>()));
					if (!flag2 && --_removed[item3] <= 0)
					{
						_removed.Remove(item3);
					}
				}
			}
			_removedChanged.Clear();
		}
		if (list3.Count > 0 || _commands.Count > 0)
		{
			session.SendUpstream(list3, _commands);
		}
		_commands.Clear();
	}

	public string Describe()
	{
		string value = ((_dropped.Count == 0) ? "" : ("; dropped: " + string.Join(", ", from kv in _dropped.OrderByDescending((KeyValuePair<string, long> kv) => kv.Value).Take(6)
			select $"{kv.Key} x{kv.Value}")));
		return $"upstream: {CommandsSeen} commands seen in {Buffers} buffers, kept {DataKept} data ({DataRemoved} removals) / {SignalsKept} signals / {Stories} story moves / {ObservableEdits} model edits ({ObservableSwallowed} observable confirmations kept here), not ours {NotOurs}, not forwardable {NotForwardable}, unresolvable {BadRefs}, closures {Closures}, unparsed buffers {Unparsed}{value}";
	}

	private static bool HostOwnsBookkeeping(string name)
	{
		if (!name.EndsWith("BreakableComponent+BeforeFracturesChangedSignal", StringComparison.Ordinal) && !name.EndsWith("BreakableComponent+AfterFracturesChangedSignal", StringComparison.Ordinal))
		{
			return name.EndsWith("BreakableComponent+FracturesChangedSignal", StringComparison.Ordinal);
		}
		return true;
	}

	internal static string Short(string full)
	{
		int num = full.LastIndexOf('.');
		if (num < 0)
		{
			return full;
		}
		int num2 = num + 1;
		return full.Substring(num2, full.Length - num2);
	}
}
