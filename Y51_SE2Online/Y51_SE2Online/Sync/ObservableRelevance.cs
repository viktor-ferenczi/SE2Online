using System;
using System.Collections.Generic;
using System.Linq;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

public sealed class ObservableRelevance<TPeer> where TPeer : notnull
{
	private sealed class PeerState
	{
		public readonly HashSet<uint> Has = new HashSet<uint>();

		public readonly HashSet<uint> Roots = new HashSet<uint>();

		public readonly Dictionary<uint, long> PendingRoots = new Dictionary<uint, long>();

		public List<ObservableOp> Out = new List<ObservableOp>();

		public long Snapshots;

		public long Ops;

		public long Releases;

		public long Deferred;

		public readonly Dictionary<(uint Oid, string Member), long> LastSent = new Dictionary<(uint, string), long>();

		public readonly Dictionary<(uint Oid, string Member), ObservableOp> Held = new Dictionary<(uint, string), ObservableOp>();
	}

	public delegate(ObservableOp Op, IReadOnlyList<uint> Children)? SnapshotFn(uint oid);

	private readonly SnapshotFn _snapshot;

	private readonly Func<long> _clock;

	private readonly Dictionary<TPeer, PeerState> _peers = new Dictionary<TPeer, PeerState>();

	private readonly HashSet<uint> _inProgress = new HashSet<uint>();

	private readonly Dictionary<uint, int> _holders = new Dictionary<uint, int>();

	public long RootsAdded;

	public long RootsExpired;

	public long SnapshotsMissing;

	public long Coalesced;

	public const int RootWaitMs = 10000;

	public const int MemberIntervalMs = 50;

	public int PeerCount => _peers.Count;

	public ObservableRelevance(SnapshotFn snapshot, Func<long>? clock = null)
	{
		_snapshot = snapshot;
		_clock = clock ?? new Func<long>(MpClock.NowMs);
	}

	public bool AnyHas(uint oid)
	{
		return _holders.ContainsKey(oid);
	}

	public bool Has(TPeer peer, uint oid)
	{
		if (_peers.TryGetValue(peer, out PeerState value))
		{
			return value.Has.Contains(oid);
		}
		return false;
	}

	public int CountFor(TPeer peer)
	{
		if (!_peers.TryGetValue(peer, out PeerState value))
		{
			return 0;
		}
		return value.Has.Count;
	}

	private PeerState State(TPeer peer)
	{
		if (!_peers.TryGetValue(peer, out PeerState value))
		{
			value = (_peers[peer] = new PeerState());
		}
		return value;
	}

	public void AddRoot(TPeer peer, uint oid, long nowMs)
	{
		PeerState peerState = State(peer);
		if (!peerState.Roots.Contains(oid) || !peerState.Has.Contains(oid))
		{
			peerState.PendingRoots.TryAdd(oid, nowMs);
		}
	}

	public int ProcessPendingRoots(long nowMs)
	{
		int num = 0;
		foreach (var (peer, peerState2) in _peers)
		{
			if (peerState2.PendingRoots.Count == 0)
			{
				continue;
			}
			foreach (var (num4, num5) in peerState2.PendingRoots.ToList())
			{
				if (EnsureSent(peer, peerState2, num4))
				{
					peerState2.PendingRoots.Remove(num4);
					peerState2.Roots.Add(num4);
					RootsAdded++;
					num++;
				}
				else if (nowMs - num5 > 10000)
				{
					peerState2.PendingRoots.Remove(num4);
					RootsExpired++;
				}
			}
		}
		return num;
	}

	private bool EnsureSent(TPeer peer, PeerState p, uint oid)
	{
		if (p.Has.Contains(oid))
		{
			return true;
		}
		if (!_inProgress.Add(oid))
		{
			return false;
		}
		try
		{
			(ObservableOp, IReadOnlyList<uint>)? tuple = _snapshot(oid);
			if (!tuple.HasValue)
			{
				SnapshotsMissing++;
				return false;
			}
			foreach (uint item in tuple.Value.Item2)
			{
				if (item != oid)
				{
					EnsureSent(peer, p, item);
				}
			}
			p.Out.Add(tuple.Value.Item1);
			p.Has.Add(oid);
			_holders[oid] = ((!_holders.TryGetValue(oid, out var value)) ? 1 : (value + 1));
			p.Snapshots++;
			return true;
		}
		finally
		{
			_inProgress.Remove(oid);
		}
	}

	public void OnChange(ObservableOp op, TPeer? origin = default(TPeer?), bool hasOrigin = false)
	{
		if (!_holders.ContainsKey(op.Oid))
		{
			return;
		}
		List<uint> list = null;
		long num = 0L;
		foreach (var (val2, peerState2) in _peers)
		{
			if (!peerState2.Has.Contains(op.Oid) || (hasOrigin && EqualityComparer<TPeer>.Default.Equals(val2, origin)))
			{
				continue;
			}
			if (list == null)
			{
				list = op.Refs();
			}
			if (op.Kind == ObservableOpKind.Member && list.Count == 0)
			{
				(uint, string) key = (op.Oid, op.Text);
				if (num == 0L)
				{
					num = _clock();
				}
				if (peerState2.LastSent.TryGetValue(key, out var value) && num - value < 50)
				{
					peerState2.Held[key] = op;
					peerState2.Deferred++;
					continue;
				}
				peerState2.LastSent[key] = num;
				peerState2.Held.Remove(key);
			}
			foreach (uint item in list)
			{
				EnsureSent(val2, peerState2, item);
			}
			Append(peerState2, op);
		}
	}

	private void Append(PeerState p, ObservableOp op)
	{
		if (op.Kind == ObservableOpKind.Member && p.Out.Count > 0)
		{
			List<ObservableOp> list = p.Out;
			ObservableOp observableOp = list[list.Count - 1];
			if (observableOp != null && observableOp.Kind == ObservableOpKind.Member && observableOp.Oid == op.Oid && observableOp.Text == op.Text)
			{
				List<ObservableOp> list2 = p.Out;
				list2[list2.Count - 1] = op;
				Coalesced++;
				return;
			}
		}
		p.Out.Add(op);
		p.Ops++;
	}

	public void OnRelease(uint oid)
	{
		foreach (var (_, peerState2) in _peers)
		{
			peerState2.PendingRoots.Remove(oid);
			peerState2.Roots.Remove(oid);
			foreach (var item in peerState2.Held.Keys.Where(((uint Oid, string Member) k) => k.Oid == oid).ToList())
			{
				peerState2.Held.Remove(item);
			}
			if (peerState2.Has.Remove(oid))
			{
				peerState2.Out.Add(ObservableOp.Release(oid));
				peerState2.Releases++;
			}
		}
		_holders.Remove(oid);
	}

	public void Forget(TPeer peer)
	{
		if (!_peers.Remove(peer, out PeerState value))
		{
			return;
		}
		foreach (uint ha in value.Has)
		{
			if (_holders.TryGetValue(ha, out var value2) && value2 > 1)
			{
				_holders[ha] = value2 - 1;
			}
			else
			{
				_holders.Remove(ha);
			}
		}
	}

	public List<(TPeer Peer, List<ObservableOp> Ops)> TakeOutput()
	{
		List<(TPeer, List<ObservableOp>)> list = new List<(TPeer, List<ObservableOp>)>();
		long num = 0L;
		foreach (var (item, peerState2) in _peers)
		{
			if (peerState2.Held.Count > 0)
			{
				if (num == 0L)
				{
					num = _clock();
				}
				foreach (var (key, op) in peerState2.Held.ToList())
				{
					if (!peerState2.LastSent.TryGetValue(key, out var value) || num - value >= 50)
					{
						peerState2.Held.Remove(key);
						peerState2.LastSent[key] = num;
						Append(peerState2, op);
					}
				}
			}
			if (peerState2.Out.Count != 0)
			{
				list.Add((item, peerState2.Out));
				peerState2.Out = new List<ObservableOp>();
			}
		}
		return list;
	}

	public string Describe()
	{
		return $"{_holders.Count} observables held by {_peers.Count} joiner(s) (" + string.Join(", ", _peers.Select((KeyValuePair<TPeer, PeerState> kv) => $"{kv.Key}: {kv.Value.Has.Count} held, {kv.Value.Roots.Count} roots, {kv.Value.PendingRoots.Count} pending, {kv.Value.Snapshots} snapshots / {kv.Value.Ops} changes / {kv.Value.Releases} releases sent, {kv.Value.Deferred} changes rate-capped")) + $"), roots added {RootsAdded}, expired {RootsExpired}, snapshots unavailable {SnapshotsMissing}, coalesced {Coalesced}";
	}
}
