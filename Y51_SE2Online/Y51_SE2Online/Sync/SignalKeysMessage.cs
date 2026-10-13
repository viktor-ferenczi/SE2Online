using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class SignalKeysMessage
{
	public static byte[] Encode(IReadOnlyList<SignalKeyDef> defs)
	{
		WireWriter wireWriter = new WireWriter(16 + defs.Count * 96).U8(39).U16((ushort)defs.Count);
		foreach (SignalKeyDef def in defs)
		{
			wireWriter.U16(def.Key).Str(def.Name).U16(def.PayloadBytes)
				.Str(def.RefOffsets);
		}
		return wireWriter.ToArray();
	}

	public static List<SignalKeyDef> Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		int num = wireReader.U16();
		List<SignalKeyDef> list = new List<SignalKeyDef>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(new SignalKeyDef(wireReader.U16(), wireReader.Str(), wireReader.U16(), wireReader.Str()));
		}
		return list;
	}
}
