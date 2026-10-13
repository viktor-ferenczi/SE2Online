using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class StringTableMessage
{
	public static byte[] Encode(uint lifeSeq, IReadOnlyList<string> names, int start, int count)
	{
		WireWriter wireWriter = new WireWriter(64 + count * 24).U8(56).U32(lifeSeq).U32((uint)count);
		for (int i = start; i < start + count; i++)
		{
			wireWriter.Str(names[i]);
		}
		return wireWriter.ToArray();
	}

	public static (uint LifeSeq, List<string> Names) Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		uint item = wireReader.U32();
		int num = (int)wireReader.U32();
		if (num < 0 || num > d.Length)
		{
			throw new FormatException($"string table count {num} is impossible for {d.Length} bytes");
		}
		List<string> list = new List<string>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(wireReader.Str());
		}
		return (LifeSeq: item, Names: list);
	}
}
