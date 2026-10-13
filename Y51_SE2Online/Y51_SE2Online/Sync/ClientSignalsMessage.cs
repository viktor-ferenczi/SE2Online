using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class ClientSignalsMessage
{
	public static byte[] Encode(uint seq, IReadOnlyList<WireCommand> cmds)
	{
		WireWriter wireWriter = new WireWriter().U8(54).U32(seq).U16((ushort)cmds.Count);
		foreach (WireCommand cmd in cmds)
		{
			WireCommand.Write(wireWriter, cmd);
		}
		return wireWriter.ToArray();
	}

	public static (uint Seq, List<WireCommand> Commands) Decode(ReadOnlySpan<byte> d)
	{
		WireReader r = new WireReader(d.Slice(1));
		uint item = r.U32();
		int num = r.U16();
		List<WireCommand> list = new List<WireCommand>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(WireCommand.Read(ref r));
		}
		return (Seq: item, Commands: list);
	}
}
