using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class SignalBatchMessage
{
	public static byte[] Encode(uint lifeSeq, uint hostTick, IReadOnlyList<WireCommand> cmds)
	{
		WireWriter wireWriter = new WireWriter().U8(55).U32(lifeSeq).U32(hostTick)
			.U16((ushort)cmds.Count);
		foreach (WireCommand cmd in cmds)
		{
			WireCommand.Write(wireWriter, cmd);
		}
		return wireWriter.ToArray();
	}

	public static (uint LifeSeq, uint HostTick, List<WireCommand> Commands) Decode(ReadOnlySpan<byte> d)
	{
		WireReader r = new WireReader(d.Slice(1));
		uint item = r.U32();
		uint item2 = r.U32();
		int num = r.U16();
		List<WireCommand> list = new List<WireCommand>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(WireCommand.Read(ref r));
		}
		return (LifeSeq: item, HostTick: item2, Commands: list);
	}
}
