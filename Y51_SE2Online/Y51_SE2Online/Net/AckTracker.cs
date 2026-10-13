using System.Collections.Generic;

namespace Y51_SE2Online.Net;

public sealed class AckTracker
{
	private readonly struct Entry(ulong token, long sentMs)
	{
		public readonly ulong Token = token;

		public readonly long SentMs = sentMs;
	}

	private readonly Dictionary<uint, Entry> _inFlight = new Dictionary<uint, Entry>();

	private readonly List<uint> _scratch = new List<uint>();

	public int InFlight => _inFlight.Count;

	public long Acked { get; private set; }

	public long Failed { get; private set; }

	public void Register(uint seq, ulong token, long nowMs)
	{
		_inFlight[seq] = new Entry(token, nowMs);
	}

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
		foreach (KeyValuePair<uint, Entry> item in _inFlight)
		{
			if ((newestAck.HasValue && Seq.Diff(newestAck.Value, item.Key) > 32) || nowMs - item.Value.SentMs > timeoutMs)
			{
				_scratch.Add(item.Key);
			}
		}
		foreach (uint item2 in _scratch)
		{
			failed.Add(_inFlight[item2].Token);
			_inFlight.Remove(item2);
			Failed++;
		}
	}

	private void TryAck(uint seq, List<ulong> acked)
	{
		if (_inFlight.Remove(seq, out var value))
		{
			acked.Add(value.Token);
			Acked++;
		}
	}
}
