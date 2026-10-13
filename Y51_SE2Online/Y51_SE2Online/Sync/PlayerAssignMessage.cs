using System;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class PlayerAssignMessage
{
	public const byte Assigned = 0;

	public const byte Failed = 1;

	public const byte Lost = 2;

	public static byte[] Encode(ushort clientId, byte status, uint netId, string text)
	{
		return new WireWriter(16 + text.Length).U8(40).U16(clientId).U8(status)
			.U32(netId)
			.Str(text)
			.ToArray();
	}

	public static (ushort ClientId, byte Status, uint NetId, string Text) Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		return (ClientId: wireReader.U16(), Status: wireReader.U8(), NetId: wireReader.U32(), Text: wireReader.Str());
	}
}
