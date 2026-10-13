namespace Y51_SE2Online.Net;

public sealed class ReceiveWindow
{
	private bool _any;

	private uint _first;

	private uint _latest;

	private uint _bits;

	public uint Latest => _latest;

	public uint Bits => _bits;

	public bool HasAny => _any;

	public long Received { get; private set; }

	public long Late { get; private set; }

	public long Duplicates { get; private set; }

	public long TooOld { get; private set; }

	public long Missing { get; private set; }

	public int PendingHoles
	{
		get
		{
			int holes = 0;
			for (int i = 0; i < 32; i++)
			{
				uint seq = _latest - 1 - (uint)i;
				if (_any && (_bits & (uint)(1 << i)) == 0 && Seq.Diff(seq, _first) >= 0)
				{
					holes++;
				}
			}
			return holes;
		}
	}

	public ReceiveResult Observe(uint seq)
	{
		if (!_any)
		{
			_any = true;
			_first = seq;
			_latest = seq;
			_bits = 0u;
			Received++;
			return ReceiveResult.New;
		}
		int ahead = Seq.Diff(seq, _latest);
		if (ahead > 0)
		{
			Missing += HolesShiftedOut(ahead);
			// Of the ahead - 1 skipped seqs, the newest 32 stay in the window as holes; the rest are lost outright.
			if (ahead > 33)
			{
				Missing += ahead - 33;
			}
			if (ahead < 32)
			{
				_bits = (_bits << ahead) | (uint)(1 << ahead - 1);
			}
			else if (ahead == 32)
			{
				_bits = 0x80000000u;
			}
			else
			{
				_bits = 0u;
			}
			_latest = seq;
			Received++;
			return ReceiveResult.New;
		}
		if (ahead == 0)
		{
			Duplicates++;
			return ReceiveResult.Duplicate;
		}
		int behind = -ahead;
		if (behind > 32 || Seq.Diff(seq, _first) < 0)
		{
			TooOld++;
			return ReceiveResult.TooOld;
		}
		uint mask = (uint)(1 << behind - 1);
		if ((_bits & mask) != 0)
		{
			Duplicates++;
			return ReceiveResult.Duplicate;
		}
		_bits |= mask;
		Late++;
		Received++;
		return ReceiveResult.Late;
	}

	private long HolesShiftedOut(int shift)
	{
		int count = shift > 32 ? 32 : shift;
		long holes = 0;
		for (int i = 0; i < count; i++)
		{
			int bit = 31 - i;
			uint seq = _latest - 1 - (uint)bit;
			if ((_bits & (uint)(1 << bit)) == 0 && Seq.Diff(seq, _first) >= 0)
			{
				holes++;
			}
		}
		return holes;
	}
}
