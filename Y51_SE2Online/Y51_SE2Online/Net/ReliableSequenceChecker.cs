namespace Y51_SE2Online.Net;

public sealed class ReliableSequenceChecker
{
	private uint _last;

	public uint Last => _last;

	public long Gaps { get; private set; }

	public long Duplicates { get; private set; }

	public ReliableCheck Observe(uint seq)
	{
		if (seq == _last + 1)
		{
			_last = seq;
			return ReliableCheck.Ok;
		}
		if (Seq.Newer(seq, _last))
		{
			Gaps++;
			_last = seq;
			return ReliableCheck.Gap;
		}
		Duplicates++;
		return ReliableCheck.Duplicate;
	}
}
