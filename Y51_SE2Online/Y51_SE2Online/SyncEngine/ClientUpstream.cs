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
		int count = CommandBuffers.Count(cb);
		for (int i = 0; i < count; i++)
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
		_dropped[key] = _dropped.TryGetValue(key, out var count) ? count + 1 : 1;
	}

	private bool Walk(ReplicationCommandBuffer cb, byte[] bytes, Func<Entity?, uint?> controlled, ObjectSpace space, Action<Delegate>? onStory)
	{
		int at = 0;
		while (at + 8 <= bytes.Length)
		{
			int stub = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
			int p = at + 8;
			CommandLayout layout = CommandLayouts.ByStub(stub);
			if (layout == null)
			{
				return false;
			}
			CommandsSeen++;
			int size;
			switch (layout.Kind)
			{
			case CommandKind.PostData:
			{
				if (p + 16 > bytes.Length)
				{
					return false;
				}
				int entityId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 4));
				int localTypeId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8));
				int dataLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 12));
				if (dataLength < 0 || p + 16 + dataLength > bytes.Length)
				{
					return false;
				}
				size = 16 + dataLength;
				uint? owner = controlled(entityId >= 0 ? cb.ReadObject(entityId) as Entity : null);
				if (!owner.HasValue)
				{
					NotOurs++;
					if (!_notOursNames.TryGetValue(localTypeId, out string? typeName))
					{
						try
						{
							typeName = EngineCodec.DescribeType(localTypeId).Name;
						}
						catch (Exception)
						{
							typeName = $"type {localTypeId}";
						}
						_notOursNames[localTypeId] = typeName;
					}
					Drop(typeName, "data on an entity this player does not control");
					break;
				}
				ushort typeKey;
				try
				{
					typeKey = _types.GetOrAdd(localTypeId, EngineCodec.DescribeType).Key;
				}
				catch (Exception)
				{
					NotForwardable++;
					break;
				}
				(uint, ushort) key = (owner.Value, typeKey);
				byte[] data = bytes.AsSpan(p + 16, dataLength).ToArray();
				if (!_current.TryGetValue(key, out byte[]? previous) || !previous.AsSpan().SequenceEqual(data))
				{
					_changed.Add(key);
				}
				_current[key] = data;
				_removed.Remove(key);
				_removedChanged.Remove(key);
				DataKept++;
				break;
			}
			case CommandKind.RemoveData:
			{
				size = layout.PayloadBytes;
				if (size < 12 || p + size > bytes.Length)
				{
					return false;
				}
				int entityId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 4));
				int localTypeId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8));
				uint? owner = (_removable ?? controlled)(entityId >= 0 ? cb.ReadObject(entityId) as Entity : null);
				if (!owner.HasValue)
				{
					NotOurs++;
					break;
				}
				ushort typeKey;
				try
				{
					typeKey = _types.GetOrAdd(localTypeId, EngineCodec.DescribeType).Key;
				}
				catch (Exception)
				{
					NotForwardable++;
					break;
				}
				(uint, ushort) key = (owner.Value, typeKey);
				_current.Remove(key);
				_changed.Remove(key);
				_removed[key] = 10;
				_removedChanged.Add(key);
				DataRemoved++;
				break;
			}
			case CommandKind.Signal:
			{
				if (layout.Name.StartsWith("Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+", StringComparison.Ordinal))
				{
					size = layout.PayloadBytes;
					if (size < 0 || p + size > bytes.Length)
					{
						return false;
					}
					if (layout.Name == "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReplicateMemberChangedSignal" && OnObservableEdit != null && size >= 16)
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
				if (!layout.Forwardable || layout.DownstreamOnly)
				{
					if (layout.PayloadBytes < 0 && !layout.Dynamic)
					{
						return false;
					}
					size = layout.PayloadBytes >= 0 ? layout.PayloadBytes : layout.Frame(bytes.AsSpan(p), new List<(int, Segment)>());
					if (size < 0)
					{
						return false;
					}
					NotForwardable++;
					Drop(layout.Name, layout.Why ?? "host to joiner only");
					break;
				}
				SignalKeyDef signal = _signals.GetOrAdd(layout.Name, layout.Def(0).PayloadBytes, layout.Signature);
				string? fail = null;
				space.ClearLocalIds();
				object? first = null;
				bool firstRead = false;
				WireCommand? command = SignalCodec.Encode(layout, signal.Key, bytes.AsSpan(p), i =>
				{
					object resolved = cb.ReadObject(i);
					if (!firstRead)
					{
						firstRead = true;
						first = resolved;
					}
					return resolved;
				}, space, _scratch, out size, ref fail);
				if (size < 0)
				{
					return false;
				}
				if (command != null && layout.Segments.Length != 0 && layout.Segments[0].Kind == SegmentKind.Ref && ControlGuard.DropUpstream(layout, first))
				{
					NotOurs++;
					break;
				}
				if (command != null && ControlGuard.IsHeldByForeignCharacter(first as Entity))
				{
					NotOurs++;
					Drop(layout.Name, "another player's tool");
					break;
				}
				if (command != null && HostOwnsBookkeeping(layout.Name))
				{
					NotForwardable++;
					Drop(layout.Name, "fracture bookkeeping (the host's own game sends it)");
					break;
				}
				if (command != null && (layout.Name == "AnimationEventSignal" || layout.Name.EndsWith(".AnimationEventSignal", StringComparison.Ordinal)))
				{
					NotForwardable++;
					Drop(layout.Name, "client-only animation event");
					break;
				}
				if (command != null && layout.StringIdOffsets.Length != 0)
				{
					byte[]? mapped = StringIdTable.Client?.Map(command.Payload, layout.StringIdOffsets, toLocal: false);
					if (mapped == null)
					{
						BadRefs++;
						Drop(layout.Name, "StringId the host does not know");
						break;
					}
					command.Payload = mapped;
				}
				if (command == null)
				{
					BadRefs++;
					Drop(layout.Name, fail ?? "?");
				}
				else
				{
					_commands.Add(command);
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
				if (cb.ReadObject(id) is Delegate story && story.Method.Name.Contains("MoveNextRemotely", StringComparison.Ordinal) && onStory != null)
				{
					Stories++;
					onStory(story);
				}
				else
				{
					Closures++;
				}
				break;
			}
			default:
				size = layout.PayloadBytes;
				if (size < 0 || p + size > bytes.Length)
				{
					return false;
				}
				NotForwardable++;
				break;
			}
			at = p + size;

			object? Slot(int off)
			{
				int id = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + off));
				if (id < 0)
				{
					return null;
				}
				return cb.ReadObject(id);
			}
		}
		return at == bytes.Length;
	}

	public static int CollectStoryMoves(ReplicationCommandBuffer cb, List<Delegate> into, int max)
	{
		int found = 0;
		int count = CommandBuffers.Count(cb);
		for (int i = 0; i < count; i++)
		{
			byte[] bytes = CommandBuffers.Bytes(cb, i);
			int at = 0;
			while (at + 8 <= bytes.Length)
			{
				CommandLayout layout = CommandLayouts.ByStub(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4)));
				int p = at + 8;
				if (layout == null)
				{
					break;
				}
				int size;
				if (layout.Kind == CommandKind.PostData)
				{
					if (p + 16 > bytes.Length)
					{
						break;
					}
					size = 16 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 12));
				}
				else if (layout.Kind == CommandKind.PostInvoke)
				{
					size = 8;
					if (p + 8 <= bytes.Length && cb.ReadObject(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 4))) is Delegate story && story.Method.Name.Contains("MoveNextRemotely", StringComparison.Ordinal) && into.Count < max)
					{
						into.Add(story);
						found++;
					}
				}
				else if (layout.PayloadBytes >= 0)
				{
					size = layout.PayloadBytes;
				}
				else
				{
					size = layout.Dynamic ? layout.Frame(bytes.AsSpan(p), new List<(int, Segment)>()) : -1;
				}
				if (size < 0 || p + size > bytes.Length)
				{
					break;
				}
				at = p + size;
			}
		}
		return found;
	}

	public void Flush(ClientSession session, long nowMs)
	{
		List<TypeKeyDef> newTypes = _types.TakeUnannounced();
		if (newTypes.Count > 0)
		{
			session.AnnounceInputTypes(newTypes);
			_log.Info("client: upstream input types: " + string.Join(", ", newTypes.ConvertAll(d => $"{d.Key}={d.ShortName}")));
		}
		List<SignalKeyDef> newSignals = _signals.TakeUnannounced();
		if (newSignals.Count > 0)
		{
			session.AnnounceSignals(newSignals);
			foreach (SignalKeyDef signal in newSignals)
			{
				_log.Info($"client: upstream signal key {signal.Key} = {Short(signal.Name)} ({(signal.PayloadBytes == ushort.MaxValue ? "dynamic" : signal.PayloadBytes + " B")}, [{signal.RefOffsets}])");
			}
		}
		var entries = new List<InputEntry>();
		bool full = nowMs - _lastFullMs >= 100;
		if (full)
		{
			_lastFullMs = nowMs;
		}
		foreach (var (key, bytes) in _current)
		{
			if (full || _changed.Contains(key))
			{
				entries.Add(new InputEntry(key.Item1, key.Item2, bytes));
			}
		}
		_changed.Clear();
		if (_removed.Count > 0)
		{
			foreach (var key in new List<(uint, ushort)>(_removed.Keys))
			{
				bool justRemoved = _removedChanged.Contains(key);
				if (justRemoved || full)
				{
					entries.Add(new InputEntry(key.Item1, key.Item2, Array.Empty<byte>()));
					if (!justRemoved && --_removed[key] <= 0)
					{
						_removed.Remove(key);
					}
				}
			}
			_removedChanged.Clear();
		}
		if (entries.Count > 0 || _commands.Count > 0)
		{
			session.SendUpstream(entries, _commands);
		}
		_commands.Clear();
	}

	public string Describe()
	{
		string dropped = _dropped.Count == 0 ? "" : "; dropped: " + string.Join(", ",
			from kv in _dropped.OrderByDescending(kv => kv.Value).Take(6)
			select $"{kv.Key} x{kv.Value}");
		return $"upstream: {CommandsSeen} commands seen in {Buffers} buffers, kept {DataKept} data ({DataRemoved} removals) / {SignalsKept} signals / {Stories} story moves / {ObservableEdits} model edits ({ObservableSwallowed} observable confirmations kept here), not ours {NotOurs}, not forwardable {NotForwardable}, unresolvable {BadRefs}, closures {Closures}, unparsed buffers {Unparsed}{dropped}";
	}

	private static bool HostOwnsBookkeeping(string name)
	{
		return name.EndsWith("BreakableComponent+BeforeFracturesChangedSignal", StringComparison.Ordinal)
			|| name.EndsWith("BreakableComponent+AfterFracturesChangedSignal", StringComparison.Ordinal)
			|| name.EndsWith("BreakableComponent+FracturesChangedSignal", StringComparison.Ordinal);
	}

	internal static string Short(string full)
	{
		int dot = full.LastIndexOf('.');
		if (dot < 0)
		{
			return full;
		}
		return full[(dot + 1)..];
	}
}
