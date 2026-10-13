namespace Y51_SE2Online.Net;

public readonly record struct PeerId(ulong Value)
{
	public override string ToString()
	{
		if (!TcpLink.IsSyntheticId(Value))
		{
			return Value.ToString();
		}
		return $"tcp#{Value & 0xFFFFFFFFu}";
	}
}
