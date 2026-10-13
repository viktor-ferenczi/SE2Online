using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class DataRemoveMessage
{
	public static byte[] Encode(uint lifeSeq, IReadOnlyList<(uint NetId, ushort Key)> items)
	{
		WireWriter wireWriter = new WireWriter(12 + items.Count * 6).U8(51).U32(lifeSeq).I32(items.Count);
		foreach (var (v, v2) in items)
		{
			wireWriter.U32(v).U16(v2);
		}
		return wireWriter.ToArray();
	}

	public static (uint LifeSeq, List<(uint NetId, ushort Key)> Items) Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		uint item = wireReader.U32();
		int num = wireReader.I32();
		List<(uint, ushort)> list = new List<(uint, ushort)>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add((wireReader.U32(), wireReader.U16()));
		}
		return (LifeSeq: item, Items: list);
	}
}
