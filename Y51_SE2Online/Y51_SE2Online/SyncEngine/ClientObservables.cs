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

		public override string ToString()
		{
			return $"host observable #{Oid}";
		}
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

		public override string ToString()
		{
			return $"host observed list #{Oid}";
		}
	}

	private sealed class Entry
	{
		public uint Oid;

		public object Proxy;

		public ObservableObject? Replica;

		public AccessibleTypeInfo? Info;

		public ObservableObjectReplicationSessionComponent.ClientObservedCollection? Wrapper;

		public bool IsList => Wrapper != null;

		public object? Local => ((object)Wrapper?.Source) ?? ((object)Replica);
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
		_counts[k] = (_counts.TryGetValue(k, out var value) ? (value + 1) : 1);
	}

	public uint? IdOf(object local)
	{
		if (!_byLocal.TryGetValue(local, out Entry value))
		{
			return null;
		}
		return value.Oid;
	}

	public bool IsReplica(object? local)
	{
		if (local != null)
		{
			return _byLocal.ContainsKey(local);
		}
		return false;
	}

	public bool WasReleased(object local)
	{
		object value;
		if (!_byLocal.ContainsKey(local))
		{
			return _released.TryGetValue(local, out value);
		}
		return false;
	}

	public object? Resolve(uint oid)
	{
		if (!_byOid.TryGetValue(oid, out Entry value))
		{
			return null;
		}
		return value.Local;
	}

	public ObjectSpace Attach(ObjectSpace sp)
	{
		sp.ObservableId = IdOf;
		sp.ListId = (IList l) => IdOf(l);
		sp.ResolveObservable = (uint oid, bool _) => (!_byOid.TryGetValue(oid, out Entry value)) ? null : value.Replica;
		sp.ResolveList = (uint oid) => (!_byOid.TryGetValue(oid, out Entry value)) ? null : value.Wrapper?.Source;
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
				string text = op.Kind switch
				{
					ObservableOpKind.NewList => NewList(comp, op), 
					ObservableOpKind.NewObject => NewObject(comp, op), 
					ObservableOpKind.Member => Member(comp, op), 
					ObservableOpKind.ListChange => ListChange(comp, op), 
					ObservableOpKind.Release => Release(comp, op), 
					_ => $"unknown step {op.Kind}", 
				};
				if (text == null)
				{
					Applied++;
				}
				else
				{
					Fail(op, text);
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
		ObservableOpKind kind = op.Kind;
		if (kind - 1 <= ObservableOpKind.NewList)
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
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		if (_byOid.ContainsKey(op.Oid))
		{
			return null;
		}
		Type type = ObjectCodec.FindType(op.Text);
		if (type == null)
		{
			return "list type " + op.Text + " not found";
		}
		IList list = (IList)Activator.CreateInstance(type, nonPublic: true);
		Type declared = ObservableRules.ElementType(type);
		foreach (WireValue item in op.Items)
		{
			object value = Decode(item, declared, out string fail);
			if (fail != null)
			{
				return "item: " + fail;
			}
			list.Add(value);
		}
		ObservableObjectReplicationSessionComponent.ClientObservedCollection clientObservedCollection = new ObservableObjectReplicationSessionComponent.ClientObservedCollection(list);
		RemoteCollection remoteCollection = new RemoteCollection
		{
			Oid = op.Oid
		};
		comp.OnReplicateNewCollection(new InProcessPairingKey<IObservedCollection>((IObservedCollection)remoteCollection), (object)clientObservedCollection);
		Entry value2 = new Entry
		{
			Oid = op.Oid,
			Proxy = remoteCollection,
			Wrapper = clientObservedCollection
		};
		_byOid[op.Oid] = value2;
		_byLocal[list] = value2;
		NewLists++;
		return null;
	}

	private string? NewObject(ObservableObjectReplicationClientSessionComponent comp, ObservableOp op)
	{
		//IL_0314: Unknown result type (might be due to invalid IL or missing references)
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
		ObservableObject observableObject = adopted ?? (ObservableObject)Activator.CreateInstance(type, nonPublic: true);
		AccessibleTypeInfo typeInfo = ((IAccessible)observableObject).GetTypeInfo();
		foreach (var (text, wireValue) in op.Members)
		{
			if (!typeInfo.Members.TryGetValue(text, out var value))
			{
				Note(type.Name + "." + text + ": no such member here");
			}
			else if (wireValue.Kind == WireKind.ObservedList)
			{
				if (!_byOid.TryGetValue(wireValue.Id, out Entry value2) || value2.Wrapper == null)
				{
					Note($"{type.Name}.{text}: observed list #{wireValue.Id} is not here");
				}
				else if (value.Get(observableObject) is IList { IsReadOnly: false } list && list is INotifyCollectionChanged && list != value2.Wrapper.Source)
				{
					if (adopted != null)
					{
						list.Clear();
					}
					_byLocal.Remove(value2.Wrapper.Source);
					value2.Wrapper.AdoptExistingCollection(list);
					_byLocal[value2.Wrapper.Source] = value2;
				}
				else if (value.CanWrite)
				{
					value.Set(observableObject, value2.Wrapper.Source);
				}
			}
			else if (value.CanWrite)
			{
				object value3 = Decode(wireValue, value.Type, out string fail);
				if (fail != null)
				{
					Note($"{type.Name}.{text}: {fail}");
					_log.WarnOnce("client-obs-member-" + type.Name + "." + text, $"client: host observable {type.Name}.{text} could not be read here ({fail}); left at its default");
				}
				else
				{
					value.Set(observableObject, value3);
				}
			}
		}
		RemoteObservable remoteObservable = new RemoteObservable
		{
			Oid = op.Oid
		};
		comp.OnReplicateNewObject(new InProcessPairingKey<ObservableObject>((ObservableObject)remoteObservable), type, (object)observableObject);
		Entry value4 = new Entry
		{
			Oid = op.Oid,
			Proxy = remoteObservable,
			Replica = observableObject,
			Info = typeInfo
		};
		_byOid[op.Oid] = value4;
		_byLocal[observableObject] = value4;
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
		if (!_byOid.TryGetValue(op.Oid, out Entry value) || value.Info == null)
		{
			return "model not here";
		}
		if (!ObservableRules.TryReplicating(value.Info, op.Text, out var member))
		{
			return value.Info.Type.Name + "." + op.Text + " is not a replicating member here";
		}
		object obj = Decode(op.Value, member.Type, out string fail);
		if (fail != null)
		{
			return $"{value.Info.Type.Name}.{op.Text}: {fail}";
		}
		((ObservableObjectReplicationSessionComponent)comp).OnReplicateMemberChanged((ObservableObject)value.Proxy, op.Text, obj);
		return null;
	}

	private string? ListChange(ObservableObjectReplicationClientSessionComponent comp, ObservableOp op)
	{
		if (!_byOid.TryGetValue(op.Oid, out Entry value) || value.Wrapper == null)
		{
			return "list not here";
		}
		IList newItems = null;
		if (op.Items != null)
		{
			Type declared = ObservableRules.ElementType(value.Wrapper.Source.GetType());
			List<object> list = new List<object>(op.Items.Count);
			foreach (WireValue item2 in op.Items)
			{
				object item = Decode(item2, declared, out string fail);
				if (fail != null)
				{
					return "item: " + fail;
				}
				list.Add(item);
			}
			newItems = list;
		}
		ReplicatedCollectionChange replicatedCollectionChange = new ReplicatedCollectionChange((CollectionChangeKind)op.Change, op.Start, newItems, op.Count);
		comp.OnReplicateCollectionChanged((IObservedCollection)value.Proxy, replicatedCollectionChange);
		return null;
	}

	private string? Release(ObservableObjectReplicationClientSessionComponent comp, ObservableOp op)
	{
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		_broken.Remove(op.Oid);
		if (!_byOid.Remove(op.Oid, out Entry value))
		{
			return null;
		}
		if (value.Local != null)
		{
			_byLocal.Remove(value.Local);
			_released.AddOrUpdate(value.Local, value);
		}
		Releases++;
		if (value.Proxy is RemoteCollection remoteCollection)
		{
			comp.OnReleaseReplicatedCollection(new InProcessPairingKey<IObservedCollection>((IObservedCollection)remoteCollection), (IObservedCollection)remoteCollection);
		}
		else
		{
			comp.OnReleaseReplicatedObject(new InProcessPairingKey<ObservableObject>((ObservableObject)value.Proxy), (ObservableObject)value.Proxy);
		}
		return null;
	}

	public void OnLocalEdit(object? replica, string? name, object? value, ObjectSpace space)
	{
		if (replica == null || name == null || !_byLocal.TryGetValue(replica, out Entry value2) || value2.Info == null)
		{
			EditsDropped++;
			Note("edit of a model the host does not know");
			return;
		}
		string fail = null;
		space.ClearLocalIds();
		WireValue value3 = ObjectCodec.Encode(value, value2.Info.Members.TryGetValue(name, out var value4) ? value4.Type : typeof(object), space, ref fail);
		if (fail != null || space.LocalIds.Count > 0)
		{
			EditsDropped++;
			Note($"edit {value2.Info.Type.Name}.{name} cannot travel: {fail ?? "entity"}");
			return;
		}
		if (_pendingEdits.Count > 0)
		{
			List<MemberEdit> pendingEdits = _pendingEdits;
			if (pendingEdits[pendingEdits.Count - 1].Oid == value2.Oid)
			{
				List<MemberEdit> pendingEdits2 = _pendingEdits;
				if (pendingEdits2[pendingEdits2.Count - 1].Name == name)
				{
					List<MemberEdit> pendingEdits3 = _pendingEdits;
					pendingEdits3[pendingEdits3.Count - 1] = new MemberEdit(value2.Oid, name, value3);
					goto IL_0194;
				}
			}
		}
		_pendingEdits.Add(new MemberEdit(value2.Oid, name, value3));
		goto IL_0194;
		IL_0194:
		Edits++;
		if (Edits <= 20)
		{
			_log.Info($"client: edit {value2.Info.Type.Name}.{name} = {value ?? "null"} (observable #{value2.Oid}) -> host");
		}
	}

	public List<MemberEdit>? TakeEdits()
	{
		if (_pendingEdits.Count == 0)
		{
			return null;
		}
		List<MemberEdit> result = new List<MemberEdit>(_pendingEdits);
		_pendingEdits.Clear();
		return result;
	}

	public string Describe()
	{
		return $"observables: {_byOid.Count} here ({NewObjects} models / {NewLists} lists built, {Releases} released), steps applied {Applied}, failed {Failed}, edits sent {Edits} dropped {EditsDropped}" + ((_counts.Count == 0) ? "" : ("; " + string.Join(", ", from kv in _counts.OrderByDescending((KeyValuePair<string, long> kv) => kv.Value).Take(6)
			select $"{kv.Key} x{kv.Value}")));
	}
}
