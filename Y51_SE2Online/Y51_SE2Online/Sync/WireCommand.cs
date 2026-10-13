using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class WireCommand
{
	public ushort Key;

	public byte[] Payload;

	public List<WireValue> Refs = new List<WireValue>();

	public static void Write(WireWriter w, WireCommand c)
	{
		w.U16(c.Key).I32(c.Payload.Length).Bytes(c.Payload)
			.U8((byte)c.Refs.Count);
		foreach (WireValue @ref in c.Refs)
		{
			WireValue.Write(w, @ref);
		}
	}

	public static WireCommand Read(ref WireReader r)
	{
		ushort key = r.U16();
		int num = r.I32();
		if (num < 0 || num > r.Remaining)
		{
			throw new ArgumentOutOfRangeException("len", $"command payload {num} B");
		}
		WireCommand wireCommand = new WireCommand
		{
			Key = key,
			Payload = r.Bytes(num).ToArray()
		};
		int refCount = r.U8();
		for (int i = 0; i < refCount; i++)
		{
			wireCommand.Refs.Add(WireValue.Read(ref r));
		}
		return wireCommand;
	}
}
