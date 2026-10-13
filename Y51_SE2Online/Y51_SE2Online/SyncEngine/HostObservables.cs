using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.UI;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Y51_SE2Online.Core;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class HostObservables
{
	private sealed class Entry
	{
		public uint Oid;

		public object Replica;

		public bool IsList;

		public AccessibleTypeInfo? Info;

		public bool Subscribed;

		public PropertyChangedEventHandler? OnProperty;

		public NotifyCollectionChangedEventHandler? OnList;
	}

	private readonly IMpLog _log;

	private readonly object _sync = new object();

	private readonly ConcurrentDictionary<object, Entry> _byReplica = new ConcurrentDictionary<object, Entry>(ReferenceEqualityComparer.Instance);

	private readonly ConcurrentDictionary<uint, Entry> _byOid = new ConcurrentDictionary<uint, Entry>();

	private readonly ObservableRelevance<PeerId> _relevance;

	private readonly ConcurrentQueue<(PeerId Peer, uint Oid)> _roots = new ConcurrentQueue<(PeerId, uint)>();

	private readonly ConcurrentQueue<(PeerId Peer, ushort ClientId, IReadOnlyList<MemberEdit> Edits)> _edits = new ConcurrentQueue<(PeerId, ushort, IReadOnlyList<MemberEdit>)>();

	private readonly ConcurrentQueue<PeerId> _forget = new ConcurrentQueue<PeerId>();

	private readonly ConcurrentDictionary<string, long> _counts = new ConcurrentDictionary<string, long>();

	private readonly ObjectSpace _encode;

	private PeerId? _origin;

	private int _nextOid;

	public long Created;

	public long Released;

	public long ChangesSent;

	public long EncodeFailures;

	public long EditsApplied;

	public long EditsRefused;

	public long Batches;

	public long BatchOps;

	public long BatchBytes;

	public int Count => _byOid.Count;

	public HostObservables(IMpLog log)
	{
		_log = log;
		_relevance = new ObservableRelevance<PeerId>(Snapshot);
		_encode = new ObjectSpace
		{
			Name = "host-observables",
			IdOf = (Entity _) => (uint?)null,
			Resolve = (uint _, bool _) => (Entity?)null,
			ObservableId = (object o) => GetOrAdd(o, isList: false).Oid,
			ListId = (IList l) => GetOrAdd(l, isList: true).Oid,
			OpaqueHandles = true
		};
	}

	private void Note(string what)
	{
		_counts.AddOrUpdate(what, 1L, (string _, long c) => c + 1);
	}

	private Entry GetOrAdd(object replica, bool isList)
	{
		if (_byReplica.TryGetValue(replica, out Entry value))
		{
			return value;
		}
		Entry entry = new Entry
		{
			Oid = (uint)Interlocked.Increment(ref _nextOid),
			Replica = replica,
			IsList = isList,
			Info = (isList ? null : (replica as IAccessible)?.GetTypeInfo())
		};
        if (HeadlessReplication.Active && replica is ObservableObject model)
        {
            var type = ObservableObjectReplicationServerSessionComponent.ObservedHierarchy.TryGetObservableObjectReplicatedType(model);
            if (type == null || !MetadataIndexerBase<AccessibleTypeIndexer>.Instance.TryGetAccessibleTypeInfo(type, out var info))
                throw new InvalidOperationException("No native replicated model schema for " + replica.GetType().FullName);
            entry.Info = info;
        }
		value = _byReplica.GetOrAdd(replica, entry);
		if (value == entry)
		{
			_byOid[value.Oid] = value;
			Interlocked.Increment(ref Created);
		}
		return value;
	}

	public uint? OidOnServer(object model, ReplayContext server)
	{
		if (HeadlessReplication.Active && model is ObservableObject) return GetOrAdd(model, isList: false).Oid;
		if (_byReplica.TryGetValue(model, out Entry value))
		{
			return value.Oid;
		}
		object obj = null;
		if (server.HasPairingRemote(model))
		{
			obj = model;
		}
		else if (server.HasPairingLocal(model))
		{
			obj = server.TranslateLocalToRemote(model);
		}
		if (!(obj is ObservableObject))
		{
			return null;
		}
		return GetOrAdd(obj, isList: false).Oid;
	}

	public object? ResolveOnServer(uint oid, bool inCollection, ReplayContext server)
	{
		if (!_byOid.TryGetValue(oid, out Entry value))
		{
			return null;
		}
		if (value.IsList)
		{
			if (!inCollection)
			{
				return null;
			}
			return value.Replica;
		}
		if (inCollection)
		{
			return value.Replica;
		}
		if (HeadlessReplication.Active) return value.Replica;
		bool skip = false;
		return server.TranslateObject(value.Replica, ReplicationFailureStrategy.NullOnAnyFail, ref skip);
	}

	public void AddRoots(PeerId peer, IReadOnlyList<uint> oids)
	{
		foreach (uint oid in oids)
		{
			_roots.Enqueue((peer, oid));
		}
	}

	public void QueueEdits(PeerId peer, ushort clientId, IReadOnlyList<MemberEdit> edits)
	{
		_edits.Enqueue((peer, clientId, edits));
	}

	public void Forget(PeerId peer)
	{
		_forget.Enqueue(peer);
	}

	public void OnRelease(object? serverKey, bool list, ReplayContext? hostClient)
	{
		if (serverKey == null || hostClient == null || _byReplica.IsEmpty)
		{
			return;
		}
		try
		{
			bool skip = false;
			object obj = HeadlessReplication.Active ? (list ? serverKey.GetType().GetProperty("Source")?.GetValue(serverKey) : serverKey) : hostClient.TranslateObject(serverKey, ReplicationFailureStrategy.NullOnAnyFail, ref skip);
			if (list && obj is ObservableObjectReplicationSessionComponent.ClientObservedCollection clientObservedCollection)
			{
				obj = clientObservedCollection.Source;
			}
			if (obj == null || !_byReplica.TryRemove(obj, out Entry value))
			{
				return;
			}
			lock (_sync)
			{
				Unsubscribe(value);
				_byOid.TryRemove(value.Oid, out Entry _);
				_relevance.OnRelease(value.Oid);
				Released++;
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("obs-release", $"host observables: handling a release failed ({ex.GetType().Name}: {ex.Message}); the model stays known");
		}
	}

	private void Subscribe(Entry e)
	{
		if (e.Subscribed)
		{
			return;
		}
		e.Subscribed = true;
		if (e.IsList)
		{
			if (e.Replica is INotifyCollectionChanged notifyCollectionChanged)
			{
				e.OnList = (object? _, NotifyCollectionChangedEventArgs a) =>
				{
					OnListChanged(e, a);
				};
				notifyCollectionChanged.CollectionChanged += e.OnList;
			}
		}
		else if (e.Replica is INotifyPropertyChanged notifyPropertyChanged)
		{
			e.OnProperty = (object? _, PropertyChangedEventArgs a) =>
			{
				OnPropertyChanged(e, a);
			};
			notifyPropertyChanged.PropertyChanged += e.OnProperty;
		}
	}

	private static void Unsubscribe(Entry e)
	{
		if (e.OnList != null && e.Replica is INotifyCollectionChanged notifyCollectionChanged)
		{
			notifyCollectionChanged.CollectionChanged -= e.OnList;
		}
		if (e.OnProperty != null && e.Replica is INotifyPropertyChanged notifyPropertyChanged)
		{
			notifyPropertyChanged.PropertyChanged -= e.OnProperty;
		}
		e.OnList = null;
		e.OnProperty = null;
	}

	private void OnPropertyChanged(Entry e, PropertyChangedEventArgs a)
	{
		string propertyName = a.PropertyName;
		if (string.IsNullOrEmpty(propertyName) || e.Info == null)
		{
			return;
		}
		try
		{
			lock (_sync)
			{
				if (_relevance.AnyHas(e.Oid) && ObservableRules.TryReplicating(e.Info, propertyName, out var member))
				{
					WireValue value = EncodeValue(member.Get(e.Replica), member.Type, e.Info.Type.Name + "." + propertyName);
					_relevance.OnChange(ObservableOp.Member(e.Oid, propertyName, value), _origin.GetValueOrDefault(), _origin.HasValue);
					ChangesSent++;
				}
			}
		}
		catch (Exception ex)
		{
			Note("member change failed: " + ex.GetType().Name);
			_log.WarnOnce("obs-prop-" + e.Info.Type.Name, $"host observables: forwarding {e.Info.Type.Name}.{propertyName} failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private void OnListChanged(Entry e, NotifyCollectionChangedEventArgs a)
	{
		IList list;
		Type et;
		try
		{
			lock (_sync)
			{
				if (!_relevance.AnyHas(e.Oid))
				{
					return;
				}
				list = (IList)e.Replica;
				et = ObservableRules.ElementType(list.GetType());
				List<ObservableOp> list2 = new List<ObservableOp>(2);
				switch (a.Action)
				{
				case NotifyCollectionChangedAction.Add:
					list2.Add((a.NewStartingIndex < 0) ? Reset() : ObservableOp.ListChange(e.Oid, 0, a.NewStartingIndex, 0, Items(a.NewItems)));
					break;
				case NotifyCollectionChangedAction.Remove:
					list2.Add((a.OldStartingIndex < 0) ? Reset() : ObservableOp.ListChange(e.Oid, 1, a.OldStartingIndex, a.OldItems?.Count ?? 1, null));
					break;
				case NotifyCollectionChangedAction.Replace:
					list2.Add((a.NewStartingIndex < 0) ? Reset() : ObservableOp.ListChange(e.Oid, 2, a.NewStartingIndex, 0, Items(a.NewItems)));
					break;
				case NotifyCollectionChangedAction.Move:
					if (a.OldStartingIndex < 0 || a.NewStartingIndex < 0)
					{
						list2.Add(Reset());
						break;
					}
					list2.Add(ObservableOp.ListChange(e.Oid, 1, a.OldStartingIndex, a.OldItems?.Count ?? 1, null));
					list2.Add(ObservableOp.ListChange(e.Oid, 0, a.NewStartingIndex, 0, Items(a.NewItems)));
					break;
				default:
					list2.Add(Reset());
					break;
				}
				foreach (ObservableOp item in list2)
				{
					_relevance.OnChange(item);
				}
				ChangesSent += list2.Count;
			}
		}
		catch (Exception ex)
		{
			Note("list change failed: " + ex.GetType().Name);
			_log.WarnOnce("obs-list-" + e.Replica.GetType().Name, $"host observables: forwarding a change of a {e.Replica.GetType().Name} failed ({ex.GetType().Name}: {ex.Message})");
		}
		List<WireValue> Items(IList? items)
		{
			if (items != null)
			{
				return (from object i in items
					select EncodeValue(i, et, "list item")).ToList();
			}
			return new List<WireValue>();
		}
		ObservableOp Reset()
		{
			return ObservableOp.ListChange(e.Oid, 3, 0, 0, Items(list));
		}
	}

	private WireValue EncodeValue(object? value, Type declared, string what)
	{
		if (value is IList list && !(list is INotifyCollectionChanged) && ObservableRules.ElementType(value.GetType()).Name == "AttachObservablePropertyBindingOp")
		{
			return WireValue.NullValue;
		}
		if (value is Delegate || typeof(Delegate).IsAssignableFrom(declared))
		{
			return WireValue.NullValue;
		}
		string fail = null;
		_encode.ClearLocalIds();
		_encode.Observed.Clear();
		WireValue result = ObjectCodec.Encode(value, declared, _encode, ref fail);
		if (fail == null && _encode.LocalIds.Count > 0)
		{
			fail = "refers to an entity (Keen never replicates those)";
		}
		if (fail == null)
		{
			return result;
		}
		EncodeFailures++;
		Note(what + ": " + fail);
		_log.WarnOnce("obs-encode-" + what, $"host observables: {what} cannot travel ({fail}); joiners get null there");
		return WireValue.NullValue;
	}

	private (ObservableOp Op, IReadOnlyList<uint> Children)? Snapshot(uint oid)
	{
		if (!_byOid.TryGetValue(oid, out Entry value))
		{
			return null;
		}
		try
		{
			Subscribe(value);
			ObservableOp observableOp;
			if (value.IsList)
			{
				IList list = (IList)value.Replica;
				Type declared = ObservableRules.ElementType(list.GetType());
				List<WireValue> list2 = new List<WireValue>(list.Count);
				foreach (object item in list)
				{
					list2.Add(EncodeValue(item, declared, "list item"));
				}
				observableOp = ObservableOp.NewList(oid, ObjectCodec.TypeName(list.GetType()), list2);
			}
			else
			{
				AccessibleTypeInfo accessibleTypeInfo = value.Info ?? throw new InvalidOperationException(value.Replica.GetType().Name + " is not IAccessible");
				List<(string, WireValue)> list3 = new List<(string, WireValue)>();
				foreach (AccessibleTypeInfo.MemberInfo orderedMember in accessibleTypeInfo.OrderedMembers)
				{
					if (!ObservableRules.IsNoReplicate(orderedMember))
					{
						object obj;
						try
						{
							obj = orderedMember.Get(value.Replica);
						}
						catch (Exception) when (!orderedMember.CanWrite)
						{
							continue;
						}
						if (ObservableRules.IsObservedList(obj))
						{
							list3.Add((orderedMember.Accessor.Name, WireValue.List(GetOrAdd(obj, isList: true).Oid)));
						}
						else if (orderedMember.CanWrite)
						{
							list3.Add((orderedMember.Accessor.Name, EncodeValue(obj, orderedMember.Type, accessibleTypeInfo.Type.Name + "." + orderedMember.Accessor.Name)));
						}
					}
				}
				observableOp = ObservableOp.NewObject(oid, ObjectCodec.TypeName(accessibleTypeInfo.Type), list3);
			}
			return (observableOp, observableOp.Refs());
		}
		catch (Exception ex2)
		{
			Note("snapshot failed: " + ex2.GetType().Name);
			_log.WarnOnce("obs-snap-" + value.Replica.GetType().Name, $"host observables: cannot snapshot a {value.Replica.GetType().Name} ({ex2.GetType().Name}: {ex2.Message}); joiners do not get it");
			return null;
		}
	}

	public void Flush(Func<PeerId, byte[], bool> publish, object? exchangeLock)
	{
		if (_roots.IsEmpty && _edits.IsEmpty && _forget.IsEmpty && _relevance.PeerCount == 0)
		{
			return;
		}
		List<(PeerId, List<ObservableOp>)> list;
		lock (_sync)
		{
			PeerId result;
			while (_forget.TryDequeue(out result))
			{
				_relevance.Forget(result);
			}
			long nowMs = MpClock.NowMs();
			(PeerId, uint) result2;
			while (_roots.TryDequeue(out result2))
			{
				_relevance.AddRoot(result2.Item1, result2.Item2, nowMs);
			}
			_relevance.ProcessPendingRoots(nowMs);
			if (!_edits.IsEmpty)
			{
				if (exchangeLock != null)
				{
					lock (exchangeLock)
					{
						ApplyEdits();
					}
				}
				else
				{
					ApplyEdits();
				}
			}
			list = _relevance.TakeOutput();
		}
		foreach (var item3 in list)
		{
			PeerId item = item3.Item1;
			List<ObservableOp> item2 = item3.Item2;
			byte[] array = ObservableBatchMessage.Encode(0u, item2);
			if (!publish(item, array))
			{
				_forget.Enqueue(item);
				continue;
			}
			Batches++;
			BatchOps += item2.Count;
			BatchBytes += array.Length;
			if (Batches <= 3 || item2.Count >= 200)
			{
				_log.Info($"host observables: batch for {item}: {item2.Count} step(s), {MpLogExtensions.Bytes(array.Length)} ({Summary(item2)})");
			}
		}
	}

	private static string Summary(List<ObservableOp> ops)
	{
		string text = string.Join(", ", from o in ops
			group o by o.Kind into g
			select $"{g.Count()} {g.Key}");
		ObservableOp observableOp = ops.FirstOrDefault((ObservableOp o) => o.Kind == ObservableOpKind.NewObject);
		return text + ((observableOp != null) ? ("; first new: " + ObservableOp.ShortType(observableOp.Text)) : "");
	}

	private void ApplyEdits()
	{
		ObjectSpace sp = new ObjectSpace
		{
			Name = "host-edit",
			IdOf = (Entity _) => (uint?)null,
			Resolve = (uint _, bool _) => (Entity?)null
		};
		(PeerId, ushort, IReadOnlyList<MemberEdit>) result;
		while (_edits.TryDequeue(out result))
		{
			foreach (MemberEdit item in result.Item3)
			{
				string text = null;
				AccessibleTypeInfo.MemberInfo member;
				if (!_byOid.TryGetValue(item.Oid, out Entry value) || value.IsList || value.Info == null)
				{
					text = "unknown model";
				}
				else if (!_relevance.Has(result.Item1, item.Oid))
				{
					text = "not one of this player's models";
				}
				else if (HostWorld.Current?.IdentityOf(result.Item2) is long actor && actor != 0 && SocialGuard.CheckObject(value.Replica, actor) is string denied)
				{
					text = denied;
				}
				else if (!ObservableRules.TryReplicating(value.Info, item.Name, out member) || !ObservableRules.IsSyncFromClients(member))
				{
					text = value.Info.Type.Name + "." + item.Name + " is not a [SyncFromClients] member";
				}
				else
				{
					WireKind kind = item.Value.Kind;
					if ((kind - 2 <= WireKind.Bytes || kind - 14 <= WireKind.Bytes) ? true : false)
					{
						text = "clients only send constants (Keen's ClientObservedObject.EnsureMemberValueCanReplicate)";
					}
					else
					{
						string fail = null;
						object obj = ObjectCodec.Decode(item.Value, member.Type, sp, translated: false, ref fail);
						if (fail != null)
						{
							text = fail;
						}
						else
						{
							_origin = result.Item1;
							try
							{
								member.Set(value.Replica, obj);
								EditsApplied++;
								if (EditsApplied <= 20)
								{
									_log.Info($"host observables: ClientId {result.Item2} set {value.Info.Type.Name}.{item.Name} = {obj ?? "null"} (observable #{item.Oid}); the server applies it like the host's own edit");
								}
							}
							catch (Exception ex)
							{
								text = ex.GetType().Name + ": " + ex.Message;
							}
							finally
							{
								_origin = null;
							}
						}
					}
				}
				if (text != null)
				{
					EditsRefused++;
					Note("edit refused: " + text);
					_log.WarnOnce("obs-edit-" + text, $"host observables: ClientId {result.Item2}: edit of {item.Name} on observable #{item.Oid} refused ({text})");
				}
			}
		}
	}

	public string Describe()
	{
		string value;
		lock (_sync)
		{
			value = _relevance.Describe();
		}
		return $"observables: {_byOid.Count} known (created {Created}, released {Released}); {value}; changes {ChangesSent}, batches {Batches} ({BatchOps} steps, {MpLogExtensions.Bytes(BatchBytes)}), edits applied {EditsApplied} refused {EditsRefused}, encode failures {EncodeFailures}" + (_counts.IsEmpty ? "" : ("; " + string.Join(", ", from kv in _counts.OrderByDescending((KeyValuePair<string, long> kv) => kv.Value).Take(6)
			select $"{kv.Key} x{kv.Value}")));
	}
}
