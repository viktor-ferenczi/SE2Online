namespace Y51_SE2Online.Net;

public sealed class DisconnectInfo
{
	public int Code { get; }

	public string Reason { get; }

	public bool ByPeer { get; }

	public string Source { get; }

	public DisconnectInfo(int code, string reason, bool byPeer, string source)
	{
		Code = code;
		Reason = reason;
		ByPeer = byPeer;
		Source = source;
	}

	public override string ToString()
	{
		return $"code={Code} source={Source}{(ByPeer ? " by-peer" : "")} reason='{Reason}'";
	}
}
