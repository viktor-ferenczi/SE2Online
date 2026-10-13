namespace Y51_SE2Online.Net;

public enum FrameKind : byte
{
	Hello = 1,
	Ping = 2,
	Pong = 3,
	Bye = 4,
	Ack = 5,
	Data = 16,
	Fragment = 17
}
