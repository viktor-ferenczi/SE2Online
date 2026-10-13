namespace Y51_SE2Online.Net;

public readonly struct LinkEvent(LinkEventKind kind, uint conn, ulong remoteId, int code = 0, string reason = "", bool byPeer = false)
{
	public readonly LinkEventKind Kind = kind;

	public readonly uint Conn = conn;

	public readonly ulong RemoteId = remoteId;

	public readonly int Code = code;

	public readonly string Reason = reason;

	public readonly bool ByPeer = byPeer;
}
