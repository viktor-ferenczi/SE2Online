using System.Collections.Generic;

namespace Y51_SE2Online.Net;

public sealed class AckTracker
{
	private readonly struct Entry(ulong token, long sentMs)
	{
		public readonly ulong Token = token;

		public readonly long SentMs = sentMs;
	}

	private readonly Dictionary<uint, Entry> _inFlight = new();

	private readonly List<uint> _scratch = new();

	public int InFlight => _inFlight.Count;

	public long Acked { get; private set; }

	public long Failed { get; private set; }

	public void Register(uint seq, ulong token, long nowMs) => _inFlight[seq] = new Entry(token, nowMs);

	public void ProcessAck(uint ackSeq, uint ackBits, List<ulong> acked)
	{
		if (_inFlight.Count == 0)
		{
			return;
		}
		TryAck(ackSeq, acked);
		for (int i = 0; i < 32; i++)
		{
			if ((ackBits & (uint)(1 << i)) != 0)
			{
				TryAck(ackSeq - 1 - (uint)i, acked);
			}
		}
	}

	public void Expire(long nowMs, int timeoutMs, uint? newestAck, List<ulong> failed)
	{
		if (_inFlight.Count == 0)
		{
			return;
		}
		_scratch.Clear();
		foreach (var pair in _inFlight)
		{
			if ((newestAck.HasValue && Seq.Diff(newestAck.Value, pair.Key) > 32) || nowMs - pair.Value.SentMs > timeoutMs)
			{
				_scratch.Add(pair.Key);
			}
		}
		foreach (uint seq in _scratch)
		{
			failed.Add(_inFlight[seq].Token);
			_inFlight.Remove(seq);
			Failed++;
		}
	}

	private void TryAck(uint seq, List<ulong> acked)
	{
		if (_inFlight.Remove(seq, out var entry))
		{
			acked.Add(entry.Token);
			Acked++;
		}
	}
}
