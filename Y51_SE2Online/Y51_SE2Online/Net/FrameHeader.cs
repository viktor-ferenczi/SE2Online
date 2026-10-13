namespace Y51_SE2Online.Net;

public readonly struct FrameHeader(Channel channel, FrameKind kind, uint seq, byte flags = 0)
{
	public readonly Channel Channel = channel;

	public readonly FrameKind Kind = kind;

	public readonly byte Flags = flags;

	public readonly uint Seq = seq;

	public override string ToString()
	{
		return $"{Channel}/{Kind}#{Seq}";
	}
}
