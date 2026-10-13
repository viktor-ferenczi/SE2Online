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
			int num = 0;
			for (int i = 0; i < 32; i++)
			{
				uint a = _latest - 1 - (uint)i;
				if (_any && (_bits & (uint)(1 << i)) == 0 && Seq.Diff(a, _first) >= 0)
				{
					num++;
				}
			}
			return num;
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
		int num = Seq.Diff(seq, _latest);
		if (num > 0)
		{
			Missing += HolesShiftedOut(num);
			if (num > 33)
			{
				Missing += num - 33;
			}
			if (num < 32)
			{
				_bits = (_bits << num) | (uint)(1 << num - 1);
			}
			else if (num == 32)
			{
				_bits = 2147483648u;
			}
			else
			{
				_bits = 0u;
			}
			_latest = seq;
			Received++;
			return ReceiveResult.New;
		}
		if (num == 0)
		{
			Duplicates++;
			return ReceiveResult.Duplicate;
		}
		int num2 = -num;
		if (num2 > 32 || Seq.Diff(seq, _first) < 0)
		{
			TooOld++;
			return ReceiveResult.TooOld;
		}
		uint num3 = (uint)(1 << num2 - 1);
		if ((_bits & num3) != 0)
		{
			Duplicates++;
			return ReceiveResult.Duplicate;
		}
		_bits |= num3;
		Late++;
		Received++;
		return ReceiveResult.Late;
	}

	private long HolesShiftedOut(int shift)
	{
		int num = ((shift > 32) ? 32 : shift);
		long num2 = 0L;
		for (int i = 0; i < num; i++)
		{
			int num3 = 31 - i;
			uint a = _latest - 1 - (uint)num3;
			if ((_bits & (uint)(1 << num3)) == 0 && Seq.Diff(a, _first) >= 0)
			{
				num2++;
			}
		}
		return num2;
	}
}
