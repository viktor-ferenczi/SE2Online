using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class ClientInputMessage
{
	public static byte[] Encode(uint seq, IReadOnlyList<InputEntry> entries)
	{
		int num = 7;
		foreach (InputEntry entry in entries)
		{
			num += 8 + entry.Bytes.Length;
		}
		WireWriter wireWriter = new WireWriter(num).U8(65).U32(seq).U16((ushort)entries.Count);
		foreach (InputEntry entry2 in entries)
		{
			wireWriter.U32(entry2.NetId).U16(entry2.Key).U16((ushort)entry2.Bytes.Length)
				.Bytes(entry2.Bytes);
		}
		return wireWriter.ToArray();
	}

	public static (uint Seq, List<InputEntry> Entries) Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		uint item = wireReader.U32();
		int num = wireReader.U16();
		List<InputEntry> list = new List<InputEntry>(num);
		for (int i = 0; i < num; i++)
		{
			uint netId = wireReader.U32();
			ushort key = wireReader.U16();
			int n = wireReader.U16();
			list.Add(new InputEntry(netId, key, wireReader.Bytes(n).ToArray()));
		}
		return (Seq: item, Entries: list);
	}
}
