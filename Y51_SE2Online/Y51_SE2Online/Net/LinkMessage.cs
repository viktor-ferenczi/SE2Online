namespace Y51_SE2Online.Net;

public readonly struct LinkMessage(uint conn, byte[] data)
{
	public readonly uint Conn = conn;

	public readonly byte[] Data = data;
}
