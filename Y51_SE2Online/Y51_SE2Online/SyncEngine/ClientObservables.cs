using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.CompilerServices;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.UI;
using Keen.VRage.Multiplayer.Replications;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class ClientObservables
{
	private sealed class RemoteObservable : ObservableObject
	{
		public uint Oid;

		public override string ToString() => $"host observable #{Oid}";
	}

	private sealed class RemoteCollection : IObservedCollection, IObserved, IDisposable
	{
		public uint Oid;

		public void UpdateFromRemote(ReplicatedCollectionChange change)
		{
		}

		public void Dispose()
		{
		}

		public override string ToString() => $"host observed list #{Oid}";
	}

	private sealed class Entry
	{
		public uint Oid;

		public object Proxy;

		public ObservableObject? Replica;

		public AccessibleTypeInfo? Info;

		public ObservableObjectReplicationSessionComponent.ClientObservedCollection? Wrapper;

		public bool IsList => Wrapper != null;

		public object? Local => (object?)Wrapper?.Source ?? Replica;
	}

	private readonly IMpLog _log;

	private readonly Dictionary<uint, Entry> _byOid = new Dictionary<uint, Entry>();

	private readonly Dictionary<object, Entry> _byLocal = new Dictionary<object, Entry>(ReferenceEqualityComparer.Instance);

	private readonly Dictionary<string, long> _counts = new Dictionary<string, long>();

	private readonly List<MemberEdit> _pendingEdits = new List<MemberEdit>();

	private readonly HashSet<uint> _broken = new HashSet<uint>();

	private readonly ConditionalWeakTable<object, object> _released = new ConditionalWeakTable<object, object>();

	private ObjectSpace? _space;

	public long Applied;

	public long Failed;

	public long NewObjects;

	public long NewLists;

	public long Releases;

	public long Edits;

	public long EditsDropped;

	public Func<Type, ObservableObject?>? Adopt;

	public int Count => _byOid.Count;

	public ClientObservables(IMpLog log)
	{
		_log = log;
	}

	private void Note(string k)
	{
		_counts[k] = _counts.TryGetValue(k, out var count) ? count + 1 : 1;
	}

	public uint? IdOf(object local)
	{
		if (!_byLocal.TryGetValue(local, out Entry? entry))
		{
			return null;
		}
		return entry.Oid;
	}

	public bool IsReplica(object? local)
	{
		return local != null && _byLocal.ContainsKey(local);
	}

	public bool WasReleased(object local)
	{
		return !_byLocal.ContainsKey(local) && _released.TryGetValue(local, out _);
	}

	public object? Resolve(uint oid)
	{
		if (!_byOid.TryGetValue(oid, out Entry? entry))
		{
			return null;
		}
		return entry.Local;
	}

	public ObjectSpace Attach(ObjectSpace sp)
	{
		sp.ObservableId = IdOf;
		sp.ListId = l => IdOf(l);
		sp.ResolveObservable = (oid, _) => !_byOid.TryGetValue(oid, out Entry? entry) ? null : entry.Replica;
		sp.ResolveList = oid => !_byOid.TryGetValue(oid, out Entry? entry) ? null : entry.Wrapper?.Source;
		sp.WasReleased = WasReleased;
		return sp;
	}

	public void Apply(ObservableObjectReplicationClientSessionComponent? comp, List<ObservableOp> ops, ObjectSpace space)
	{
		if (comp == null)
		{
			Failed += ops.Count;
			_log.WarnOnce("client-obs-nocomp", "client: no ObservableObjectReplicationClientSessionComponent in this session; the host's models cannot be shown");
			return;
		}
		_space = Attach(space);
		foreach (ObservableOp op in ops)
		{
			if (_broken.Contains(op.Oid) && op.Kind != ObservableOpKind.Release)
			{
				Failed++;
				Note($"{op.Kind} for a model that failed to build");
				continue;
			}
			try
			{
				string? failure = op.Kind switch
				{
					ObservableOpKind.NewList => NewList(comp, op),
					ObservableOpKind.NewObject => NewObject(comp, op),
					ObservableOpKind.Member => Member(comp, op),
					ObservableOpKind.ListChange => ListChange(comp, op),
					ObservableOpKind.Release => Release(comp, op),
					_ => $"unknown step {op.Kind}",
				};
				if (failure == null)
				{
					Applied++;
				}
				else
				{
					Fail(op, failure);
				}
			}
			catch (Exception ex)
			{
				Fail(op, ex.GetType().Name + ": " + ex.Message);
			}
		}
	}

	private void Fail(ObservableOp op, string why)
	{
		Failed++;
		// NewList or NewObject (the byte subtraction wraps 0 to 255)
		if (op.Kind - 1 <= ObservableOpKind.NewList)
		{
			_broken.Add(op.Oid);
		}
		Note($"{op.Kind}: {why}");
		_log.WarnOnce($"client-obs-{op.Kind}-{why}", $"client: host observable step '{op}' could not be applied ({why})");
	}

	private object? Decode(WireValue v, Type declared, out string? fail)
	{
		fail = null;
		_space.LocalIds.Clear();
		_space.LocalStubs.Clear();
		return ObjectCodec.Decode(v, declared, _space, translated: false, ref fail);
	}

	private string? NewList(ObservableObjectReplicationClientSessionComponent comp, ObservableOp op)
	{
		if (_byOid.ContainsKey(op.Oid))
		{
			return null;
		}
		Type type = ObjectCodec.FindType(op.Text);
		if (type == null)
		{
			return "list type " + op.Text + " not found";
		}
		var list = (IList)Activator.CreateInstance(type, nonPublic: true);
		Type declared = ObservableRules.ElementType(type);
		foreach (WireValue item in op.Items)
		{
			object? value = Decode(item, declared, out string? fail);
			if (fail != null)
			{
				return "item: " + fail;
			}
			list.Add(value);
		}
		var wrapper = new ObservableObjectReplicationSessionComponent.ClientObservedCollection(list);
		var proxy = new RemoteCollection
		{
			Oid = op.Oid
		};
		comp.OnReplicateNewCollection(new InProcessPairingKey<IObservedCollection>(proxy), wrapper);
		var entry = new Entry
		{
			Oid = op.Oid,
			Proxy = proxy,
			Wrapper = wrapper
		};
		_byOid[op.Oid] = entry;
		_byLocal[list] = entry;
		NewLists++;
		return null;
	}

	private string? NewObject(ObservableObjectReplicationClientSessionComponent comp, ObservableOp op)
	{
		if (_byOid.ContainsKey(op.Oid))
		{
			return null;
		}
		Type type = ObjectCodec.FindType(op.Text);
		if (type == null)
		{
			return "type " + op.Text + " not found";
		}
		ObservableObject? adopted = Adopt?.Invoke(type);
		ObservableObject replica = adopted ?? (ObservableObject)Activator.CreateInstance(type, nonPublic: true);
		AccessibleTypeInfo typeInfo = ((IAccessible)replica).GetTypeInfo();
		foreach (var (name, wire) in op.Members)
		{
			if (!typeInfo.Members.TryGetValue(name, out var member))
			{
				Note(type.Name + "." + name + ": no such member here");
			}
			else if (wire.Kind == WireKind.ObservedList)
			{
				if (!_byOid.TryGetValue(wire.Id, out Entry? listEntry) || listEntry.Wrapper == null)
				{
					Note($"{type.Name}.{name}: observed list #{wire.Id} is not here");
				}
				else if (member.Get(replica) is IList { IsReadOnly: false } list && list is INotifyCollectionChanged && list != listEntry.Wrapper.Source)
				{
					if (adopted != null)
					{
						list.Clear();
					}
					_byLocal.Remove(listEntry.Wrapper.Source);
					listEntry.Wrapper.AdoptExistingCollection(list);
					_byLocal[listEntry.Wrapper.Source] = listEntry;
				}
				else if (member.CanWrite)
				{
					member.Set(replica, listEntry.Wrapper.Source);
				}
			}
			else if (member.CanWrite)
			{
				object? value = Decode(wire, member.Type, out string? fail);
				if (fail != null)
				{
					Note($"{type.Name}.{name}: {fail}");
					_log.WarnOnce("client-obs-member-" + type.Name + "." + name, $"client: host observable {type.Name}.{name} could not be read here ({fail}); left at its default");
				}
				else
				{
					member.Set(replica, value);
				}
			}
		}
		var proxy = new RemoteObservable
		{
			Oid = op.Oid
		};
		comp.OnReplicateNewObject(new InProcessPairingKey<ObservableObject>(proxy), type, replica);
		var entry = new Entry
		{
			Oid = op.Oid,
			Proxy = proxy,
			Replica = replica,
			Info = typeInfo
		};
		_byOid[op.Oid] = entry;
		_byLocal[replica] = entry;
		NewObjects++;
		if (adopted != null)
		{
			Note(type.Name + ": the host's live copy drives this player's own data");
		}
		if (NewObjects <= 10)
		{
			_log.Info($"client: host observable #{op.Oid} {type.Name} arrived ({op.Members.Count} members); Keen's client pairs it");
		}
		return null;
	}

	private string? Member(ObservableObjectReplicationClientSessionComponent comp, ObservableOp op)
	{
		if (!_byOid.TryGetValue(op.Oid, out Entry? entry) || entry.Info == null)
		{
			return "model not here";
		}
		if (!ObservableRules.TryReplicating(entry.Info, op.Text, out var member))
		{
			return entry.Info.Type.Name + "." + op.Text + " is not a replicating member here";
		}
		object? value = Decode(op.Value, member.Type, out string? fail);
		if (fail != null)
		{
			return $"{entry.Info.Type.Name}.{op.Text}: {fail}";
		}
		((ObservableObjectReplicationSessionComponent)comp).OnReplicateMemberChanged((ObservableObject)entry.Proxy, op.Text, value);
		return null;
	}

	private string? ListChange(ObservableObjectReplicationClientSessionComponent comp, ObservableOp op)
	{
		if (!_byOid.TryGetValue(op.Oid, out Entry? entry) || entry.Wrapper == null)
		{
			return "list not here";
		}
		IList? newItems = null;
		if (op.Items != null)
		{
			Type declared = ObservableRules.ElementType(entry.Wrapper.Source.GetType());
			var decoded = new List<object>(op.Items.Count);
			foreach (WireValue item in op.Items)
			{
				object? value = Decode(item, declared, out string? fail);
				if (fail != null)
				{
					return "item: " + fail;
				}
				decoded.Add(value);
			}
			newItems = decoded;
		}
		var change = new ReplicatedCollectionChange((CollectionChangeKind)op.Change, op.Start, newItems, op.Count);
		comp.OnReplicateCollectionChanged((IObservedCollection)entry.Proxy, change);
		return null;
	}

	private string? Release(ObservableObjectReplicationClientSessionComponent comp, ObservableOp op)
	{
		_broken.Remove(op.Oid);
		if (!_byOid.Remove(op.Oid, out Entry? entry))
		{
			return null;
		}
		if (entry.Local != null)
		{
			_byLocal.Remove(entry.Local);
			_released.AddOrUpdate(entry.Local, entry);
		}
		Releases++;
		if (entry.Proxy is RemoteCollection list)
		{
			comp.OnReleaseReplicatedCollection(new InProcessPairingKey<IObservedCollection>(list), list);
		}
		else
		{
			comp.OnReleaseReplicatedObject(new InProcessPairingKey<ObservableObject>((ObservableObject)entry.Proxy), (ObservableObject)entry.Proxy);
		}
		return null;
	}

	public void OnLocalEdit(object? replica, string? name, object? value, ObjectSpace space)
	{
		if (replica == null || name == null || !_byLocal.TryGetValue(replica, out Entry? entry) || entry.Info == null)
		{
			EditsDropped++;
			Note("edit of a model the host does not know");
			return;
		}
		string? fail = null;
		space.ClearLocalIds();
		WireValue wire = ObjectCodec.Encode(value, entry.Info.Members.TryGetValue(name, out var member) ? member.Type : typeof(object), space, ref fail);
		if (fail != null || space.LocalIds.Count > 0)
		{
			EditsDropped++;
			Note($"edit {entry.Info.Type.Name}.{name} cannot travel: {fail ?? "entity"}");
			return;
		}
		if (_pendingEdits.Count > 0 && _pendingEdits[^1].Oid == entry.Oid && _pendingEdits[^1].Name == name)
		{
			_pendingEdits[^1] = new MemberEdit(entry.Oid, name, wire);
		}
		else
		{
			_pendingEdits.Add(new MemberEdit(entry.Oid, name, wire));
		}
		Edits++;
		if (Edits <= 20)
		{
			_log.Info($"client: edit {entry.Info.Type.Name}.{name} = {value ?? "null"} (observable #{entry.Oid}) -> host");
		}
	}

	public List<MemberEdit>? TakeEdits()
	{
		if (_pendingEdits.Count == 0)
		{
			return null;
		}
		var result = new List<MemberEdit>(_pendingEdits);
		_pendingEdits.Clear();
		return result;
	}

	public string Describe()
	{
		return $"observables: {_byOid.Count} here ({NewObjects} models / {NewLists} lists built, {Releases} released), steps applied {Applied}, failed {Failed}, edits sent {Edits} dropped {EditsDropped}" + (_counts.Count == 0 ? "" : "; " + string.Join(", ",
			from kv in _counts.OrderByDescending(kv => kv.Value).Take(6)
			select $"{kv.Key} x{kv.Value}"));
	}
}
