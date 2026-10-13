using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class TypeKeysMessage
{
	public static byte[] Encode(IReadOnlyList<TypeKeyDef> defs)
	{
		WireWriter wireWriter = new WireWriter(16 + defs.Count * 64).U8(36).U16((ushort)defs.Count);
		foreach (TypeKeyDef def in defs)
		{
			wireWriter.U16(def.Key).U16(def.Size).Str(def.Name);
		}
		return wireWriter.ToArray();
	}

	public static List<TypeKeyDef> Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		int num = wireReader.U16();
		List<TypeKeyDef> list = new List<TypeKeyDef>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(new TypeKeyDef(wireReader.U16(), wireReader.U16(), wireReader.Str()));
		}
		return list;
	}
}
