using System;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class StoryFinish
{
	public const byte Ok = 0;

	public const byte Failed = 1;

	public const byte Skipped = 2;

	public uint StoryId;

	public byte Status;

	public string Text = "";

	public WireValue Result = WireValue.NullValue;

	public byte[] Encode()
	{
		WireWriter wireWriter = new WireWriter().U8(42).U32(StoryId).U8(Status)
			.Str(Text);
		WireValue.Write(wireWriter, Result);
		return wireWriter.ToArray();
	}

	public static StoryFinish Decode(ReadOnlySpan<byte> d)
	{
		WireReader r = new WireReader(d.Slice(1));
		return new StoryFinish
		{
			StoryId = r.U32(),
			Status = r.U8(),
			Text = r.Str(),
			Result = WireValue.Read(ref r)
		};
	}
}
