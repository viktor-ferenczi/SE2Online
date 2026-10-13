using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class DespawnMessage
{
	public const byte ModeRemove = 0;

	public const byte ModeForget = 1;

	public static byte[] Encode(uint lifeSeq, byte mode, IReadOnlyList<uint> netIds)
	{
		WireWriter wireWriter = new WireWriter(13 + netIds.Count * 4).U8(50).U32(lifeSeq).U8(mode)
			.I32(netIds.Count);
		foreach (uint netId in netIds)
		{
			wireWriter.U32(netId);
		}
		return wireWriter.ToArray();
	}

	public static (uint LifeSeq, byte Mode, List<uint> NetIds) Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		uint item = wireReader.U32();
		byte item2 = wireReader.U8();
		int num = wireReader.I32();
		List<uint> list = new List<uint>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(wireReader.U32());
		}
		return (LifeSeq: item, Mode: item2, NetIds: list);
	}
}
