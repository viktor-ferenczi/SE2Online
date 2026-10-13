using System;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class PlayerStatusMessage
{
	public static byte[] Encode(ushort clientId, int pcu, int maxPcu)
	{
		return new WireWriter(12).U8(44).U16(clientId).I32(pcu)
			.I32(maxPcu)
			.ToArray();
	}

	public static (ushort ClientId, int Pcu, int MaxPcu) Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		return (ClientId: wireReader.U16(), Pcu: wireReader.I32(), MaxPcu: wireReader.I32());
	}
}
