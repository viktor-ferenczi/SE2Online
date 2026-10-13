using System;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class TextMessage
{
	public static byte[] Encode(SyncMsg type, byte code, string text)
	{
		return new WireWriter(16 + text.Length).U8((byte)type).U8(code).Str(text)
			.ToArray();
	}

	public static (byte Code, string Text) Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		return (Code: wireReader.U8(), Text: wireReader.Str());
	}
}
