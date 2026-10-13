using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Net;

public sealed class Reassembler
{
	private sealed class Pending
	{
		public FragmentHeader First;

		public byte[] Buffer;

		public bool[] Have;

		public int Received;

		public long LastActivityMs;

		public long StartedMs;
	}

	private readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>();

	private readonly HashSet<uint> _recentlyCompleted = new HashSet<uint>();

	private readonly Queue<uint> _recentOrder = new Queue<uint>();

	public int MaxMessageBytes { get; }

	public int MaxConcurrent { get; }

	public long MaxBufferedBytes { get; }

	public int TimeoutMs { get; }

	public long BufferedBytes { get; private set; }

	public int InProgress => _pending.Count;

	public long Completed { get; private set; }

	public long Rejected { get; private set; }

	public long Duplicates { get; private set; }

	public long TimedOut { get; private set; }

	public string? LastError { get; private set; }

	public Reassembler(int maxMessageBytes = 268435456, int maxConcurrent = 8, long maxBufferedBytes = 536870912L, int timeoutMs = 120000)
	{
		MaxMessageBytes = maxMessageBytes;
		MaxConcurrent = maxConcurrent;
		MaxBufferedBytes = maxBufferedBytes;
		TimeoutMs = timeoutMs;
	}

	public bool TryGetProgress(uint transferId, out int received, out int count, out long startedMs)
	{
		if (_pending.TryGetValue(transferId, out Pending value))
		{
			received = value.Received;
			count = value.Have.Length;
			startedMs = value.StartedMs;
			return true;
		}
		received = (count = 0);
		startedMs = 0L;
		return false;
	}

	public ReassemblyResult Add(in FragmentHeader h, ReadOnlySpan<byte> payload, long nowMs, out byte[]? complete)
	{
		complete = null;
		string text = h.Validate(payload.Length);
		if (text == null && h.TotalLength > MaxMessageBytes)
		{
			text = $"message of {h.TotalLength} B exceeds limit {MaxMessageBytes}";
		}
		if (text != null)
		{
			return Reject(text);
		}
		if (_recentlyCompleted.Contains(h.TransferId))
		{
			Duplicates++;
			return ReassemblyResult.Duplicate;
		}
		if (h.Count == 1)
		{
			complete = payload.ToArray();
			Remember(h.TransferId);
			Completed++;
			return ReassemblyResult.Complete;
		}
		if (!_pending.TryGetValue(h.TransferId, out Pending value))
		{
			if (_pending.Count >= MaxConcurrent)
			{
				return Reject($"too many concurrent transfers ({_pending.Count})");
			}
			if (BufferedBytes + h.TotalLength > MaxBufferedBytes)
			{
				return Reject($"buffer limit: {BufferedBytes} + {h.TotalLength} > {MaxBufferedBytes}");
			}
			value = new Pending
			{
				First = h,
				Buffer = new byte[h.TotalLength],
				Have = new bool[h.Count],
				StartedMs = nowMs
			};
			_pending.Add(h.TransferId, value);
			BufferedBytes += h.TotalLength;
		}
		else if (value.First.Count != h.Count || value.First.TotalLength != h.TotalLength || value.First.FragmentSize != h.FragmentSize)
		{
			Drop(h.TransferId, value);
			return Reject($"transfer {h.TransferId}: geometry changed mid-transfer");
		}
		value.LastActivityMs = nowMs;
		if (value.Have[h.Index])
		{
			Duplicates++;
			return ReassemblyResult.Duplicate;
		}
		payload.CopyTo(value.Buffer.AsSpan((int)((long)h.Index * (long)h.FragmentSize)));
		value.Have[h.Index] = true;
		value.Received++;
		if (value.Received < value.Have.Length)
		{
			return ReassemblyResult.Partial;
		}
		Drop(h.TransferId, value);
		Remember(h.TransferId);
		Completed++;
		complete = value.Buffer;
		return ReassemblyResult.Complete;
	}

	public int Expire(long nowMs)
	{
		List<uint> list = null;
		foreach (KeyValuePair<uint, Pending> item in _pending)
		{
			if (nowMs - item.Value.LastActivityMs > TimeoutMs)
			{
				(list ?? (list = new List<uint>())).Add(item.Key);
			}
		}
		if (list == null)
		{
			return 0;
		}
		foreach (uint item2 in list)
		{
			Drop(item2, _pending[item2]);
			TimedOut++;
			LastError = $"transfer {item2} timed out";
		}
		return list.Count;
	}

	private void Drop(uint id, Pending p)
	{
		_pending.Remove(id);
		BufferedBytes -= p.Buffer.Length;
	}

	private void Remember(uint id)
	{
		if (_recentlyCompleted.Add(id))
		{
			_recentOrder.Enqueue(id);
			if (_recentOrder.Count > 1024)
			{
				_recentlyCompleted.Remove(_recentOrder.Dequeue());
			}
		}
	}

	private ReassemblyResult Reject(string error)
	{
		Rejected++;
		LastError = error;
		return ReassemblyResult.Rejected;
	}
}
