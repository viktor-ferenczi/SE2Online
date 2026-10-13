using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class StateCoalescer
{
	private sealed class Entry
	{
		public uint NetId;

		public ushort Key;

		public byte[] Bytes = Array.Empty<byte>();

		public uint Version;

		public uint HostMs;

		public bool Dirty;

		public long DirtySinceMs;

		public long LastSentMs = -4611686018427387904L;

		public int IntervalMs;
	}

	private readonly Dictionary<(uint, ushort), Entry> _entries = new();

	public readonly Dictionary<ushort, long> SentByKey = new();

	private readonly List<Entry> _dirty = new();

	private readonly Func<ushort, int> _intervalForKey;

	private readonly int _budgetBytesPerSec;

	private double _tokens;

	private long _lastEmitMs = -1L;

	private readonly List<StateEntry> _scratch = new();

	public long Puts;

	public long Coalesced;

	public long Sent;

	public long SentBytes;

	public long DroppedByCap;

	public long DeferredByCap;

	public long DeferredByBudget;

	public long Messages;

	// Test diagnostics: put/send times of one NetId's state.
	public static volatile uint WatchNetId;

	public static readonly System.Collections.Concurrent.ConcurrentQueue<string> WatchLog = new();

	public int DirtyCount => _dirty.Count;

	public int EntryCount => _entries.Count;

	public StateCoalescer(Func<ushort, int> intervalForKey, int budgetBytesPerSec)
	{
		_intervalForKey = intervalForKey;
		_budgetBytesPerSec = budgetBytesPerSec;
	}

	public void Put(in StateEntry e, long nowMs)
	{
		Puts++;
		if (e.NetId == WatchNetId && WatchLog.Count < 20000)
		{
			WatchLog.Enqueue($"put,{nowMs},{e.Key},{e.HostMs}");
		}
		if (!_entries.TryGetValue((e.NetId, e.Key), out Entry entry))
		{
			entry = new Entry
			{
				NetId = e.NetId,
				Key = e.Key,
				IntervalMs = _intervalForKey(e.Key)
			};
			_entries[(e.NetId, e.Key)] = entry;
		}
		if (entry.IntervalMs < 0)
		{
			DroppedByCap++;
			return;
		}
		if (entry.Dirty)
		{
			if (Seq.Newer(entry.Version, e.Version))
			{
				return;
			}
			Coalesced++;
		}
		else
		{
			entry.Dirty = true;
			entry.DirtySinceMs = nowMs;
			_dirty.Add(entry);
		}
		entry.Bytes = e.Bytes;
		entry.Version = e.Version;
		entry.HostMs = e.HostMs;
	}

	public void Remove(uint netId)
	{
		var doomed = new List<(uint, ushort)>();
		foreach (var key in _entries.Keys)
		{
			if (key.Item1 == netId)
			{
				doomed.Add(key);
			}
		}
		foreach (var key in doomed)
		{
			if (_entries.Remove(key, out Entry entry) && entry.Dirty)
			{
				entry.Dirty = false;
				_dirty.Remove(entry);
			}
		}
	}

	public void Emit(long nowMs, int maxMessageBytes, List<byte[]> output)
	{
		double elapsedSec = _lastEmitMs < 0 ? 0.05 : Math.Max(0.0, (nowMs - _lastEmitMs) / 1000.0);
		_lastEmitMs = nowMs;
		_tokens = Math.Min(_tokens + elapsedSec * _budgetBytesPerSec, _budgetBytesPerSec / 4.0 + maxMessageBytes);
		if (_dirty.Count == 0)
		{
			return;
		}
		_dirty.Sort((a, b) => a.DirtySinceMs.CompareTo(b.DirtySinceMs));
		_scratch.Clear();
		int kept = 0;
		// Once one entry is out of budget, hold every later one too so the oldest goes first next time.
		bool outOfBudget = false;
		for (int i = 0; i < _dirty.Count; i++)
		{
			Entry entry = _dirty[i];
			bool intervalElapsed = nowMs - entry.LastSentMs >= entry.IntervalMs;
			int cost = 16 + entry.Bytes.Length;
			if (intervalElapsed && !outOfBudget && _tokens >= cost)
			{
				_tokens -= cost;
				entry.Dirty = false;
				entry.LastSentMs = nowMs;
				_scratch.Add(new StateEntry(entry.NetId, entry.Key, entry.Version, entry.Bytes, entry.HostMs));
				Sent++;
				SentBytes += cost;
				SentByKey[entry.Key] = SentByKey.TryGetValue(entry.Key, out var sent) ? sent + 1 : 1;
			}
			else
			{
				if (!intervalElapsed)
				{
					DeferredByCap++;
				}
				else
				{
					DeferredByBudget++;
					outOfBudget = true;
				}
				_dirty[kept++] = entry;
			}
		}
		_dirty.RemoveRange(kept, _dirty.Count - kept);
		int messagesBefore = output.Count;
		StateBatchMessage.Pack(_scratch, maxMessageBytes, output);
		Messages += output.Count - messagesBefore;
	}
}
