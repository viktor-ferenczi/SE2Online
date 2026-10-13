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

	private readonly Dictionary<(uint, ushort), Entry> _entries = new Dictionary<(uint, ushort), Entry>();

	public readonly Dictionary<ushort, long> SentByKey = new Dictionary<ushort, long>();

	private readonly List<Entry> _dirty = new List<Entry>();

	private readonly Func<ushort, int> _intervalForKey;

	private readonly int _budgetBytesPerSec;

	private double _tokens;

	private long _lastEmitMs = -1L;

	private readonly List<StateEntry> _scratch = new List<StateEntry>();

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

	public static readonly System.Collections.Concurrent.ConcurrentQueue<string> WatchLog = new System.Collections.Concurrent.ConcurrentQueue<string>();

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
		if (!_entries.TryGetValue((e.NetId, e.Key), out Entry value))
		{
			value = new Entry
			{
				NetId = e.NetId,
				Key = e.Key,
				IntervalMs = _intervalForKey(e.Key)
			};
			_entries[(e.NetId, e.Key)] = value;
		}
		if (value.IntervalMs < 0)
		{
			DroppedByCap++;
			return;
		}
		if (value.Dirty)
		{
			if (Seq.Newer(value.Version, e.Version))
			{
				return;
			}
			Coalesced++;
		}
		else
		{
			value.Dirty = true;
			value.DirtySinceMs = nowMs;
			_dirty.Add(value);
		}
		value.Bytes = e.Bytes;
		value.Version = e.Version;
		value.HostMs = e.HostMs;
	}

	public void Remove(uint netId)
	{
		List<(uint, ushort)> list = new List<(uint, ushort)>();
		foreach (var key in _entries.Keys)
		{
			if (key.Item1 == netId)
			{
				list.Add(key);
			}
		}
		foreach (var item in list)
		{
			if (_entries.Remove(item, out Entry value) && value.Dirty)
			{
				value.Dirty = false;
				_dirty.Remove(value);
			}
		}
	}

	public void Emit(long nowMs, int maxMessageBytes, List<byte[]> output)
	{
		double num = ((_lastEmitMs < 0) ? 0.05 : Math.Max(0.0, (double)(nowMs - _lastEmitMs) / 1000.0));
		_lastEmitMs = nowMs;
		_tokens = Math.Min(_tokens + num * (double)_budgetBytesPerSec, (double)_budgetBytesPerSec / 4.0 + (double)maxMessageBytes);
		if (_dirty.Count == 0)
		{
			return;
		}
		_dirty.Sort((Entry a, Entry b) => a.DirtySinceMs.CompareTo(b.DirtySinceMs));
		_scratch.Clear();
		int num2 = 0;
		bool flag = false;
		for (int num3 = 0; num3 < _dirty.Count; num3++)
		{
			Entry entry = _dirty[num3];
			bool flag2 = nowMs - entry.LastSentMs >= entry.IntervalMs;
			int num4 = 16 + entry.Bytes.Length;
			if (flag2 && !flag && _tokens >= (double)num4)
			{
				_tokens -= num4;
				entry.Dirty = false;
				entry.LastSentMs = nowMs;
				_scratch.Add(new StateEntry(entry.NetId, entry.Key, entry.Version, entry.Bytes, entry.HostMs));
				Sent++;
				SentBytes += num4;
				SentByKey[entry.Key] = (SentByKey.TryGetValue(entry.Key, out var value) ? (value + 1) : 1);
			}
			else
			{
				if (!flag2)
				{
					DeferredByCap++;
				}
				else
				{
					DeferredByBudget++;
					flag = true;
				}
				_dirty[num2++] = entry;
			}
		}
		_dirty.RemoveRange(num2, _dirty.Count - num2);
		int count = output.Count;
		StateBatchMessage.Pack(_scratch, maxMessageBytes, output);
		Messages += output.Count - count;
	}
}
