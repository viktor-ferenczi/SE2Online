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

	private readonly Dictionary<uint, Pending> _pending = new();

	private readonly HashSet<uint> _recentlyCompleted = new();

	private readonly Queue<uint> _recentOrder = new();

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
		if (_pending.TryGetValue(transferId, out var pending))
		{
			received = pending.Received;
			count = pending.Have.Length;
			startedMs = pending.StartedMs;
			return true;
		}
		received = count = 0;
		startedMs = 0;
		return false;
	}

	public ReassemblyResult Add(in FragmentHeader h, ReadOnlySpan<byte> payload, long nowMs, out byte[]? complete)
	{
		complete = null;
		string error = h.Validate(payload.Length);
		if (error is null && h.TotalLength > MaxMessageBytes)
		{
			error = $"message of {h.TotalLength} B exceeds limit {MaxMessageBytes}";
		}
		if (error is not null)
		{
			return Reject(error);
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
		if (!_pending.TryGetValue(h.TransferId, out var pending))
		{
			if (_pending.Count >= MaxConcurrent)
			{
				return Reject($"too many concurrent transfers ({_pending.Count})");
			}
			if (BufferedBytes + h.TotalLength > MaxBufferedBytes)
			{
				return Reject($"buffer limit: {BufferedBytes} + {h.TotalLength} > {MaxBufferedBytes}");
			}
			pending = new Pending
			{
				First = h,
				Buffer = new byte[h.TotalLength],
				Have = new bool[h.Count],
				StartedMs = nowMs
			};
			_pending.Add(h.TransferId, pending);
			BufferedBytes += h.TotalLength;
		}
		else if (pending.First.Count != h.Count || pending.First.TotalLength != h.TotalLength || pending.First.FragmentSize != h.FragmentSize)
		{
			Drop(h.TransferId, pending);
			return Reject($"transfer {h.TransferId}: geometry changed mid-transfer");
		}
		pending.LastActivityMs = nowMs;
		if (pending.Have[h.Index])
		{
			Duplicates++;
			return ReassemblyResult.Duplicate;
		}
		payload.CopyTo(pending.Buffer.AsSpan((int)((long)h.Index * h.FragmentSize)));
		pending.Have[h.Index] = true;
		pending.Received++;
		if (pending.Received < pending.Have.Length)
		{
			return ReassemblyResult.Partial;
		}
		Drop(h.TransferId, pending);
		Remember(h.TransferId);
		Completed++;
		complete = pending.Buffer;
		return ReassemblyResult.Complete;
	}

	public int Expire(long nowMs)
	{
		List<uint> expired = null;
		foreach (var pair in _pending)
		{
			if (nowMs - pair.Value.LastActivityMs > TimeoutMs)
			{
				(expired ??= new List<uint>()).Add(pair.Key);
			}
		}
		if (expired is null)
		{
			return 0;
		}
		foreach (uint id in expired)
		{
			Drop(id, _pending[id]);
			TimedOut++;
			LastError = $"transfer {id} timed out";
		}
		return expired.Count;
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
