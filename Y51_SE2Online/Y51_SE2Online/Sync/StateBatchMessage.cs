using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class StateBatchMessage
{
	public const int HeaderBytes = 3;

	public static byte[] Encode(IReadOnlyList<StateEntry> entries, int start, int count)
	{
		int num = 3;
		for (int i = start; i < start + count; i++)
		{
			num += entries[i].WireSize;
		}
		WireWriter wireWriter = new WireWriter(num).U8(64).U16((ushort)count);
		for (int j = start; j < start + count; j++)
		{
			StateEntry stateEntry = entries[j];
			wireWriter.U32(stateEntry.NetId).U16(stateEntry.Key).U32(stateEntry.Version)
				.U32(stateEntry.HostMs)
				.U16((ushort)stateEntry.Bytes.Length)
				.Bytes(stateEntry.Bytes);
		}
		return wireWriter.ToArray();
	}

	public static List<StateEntry> Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		int num = wireReader.U16();
		List<StateEntry> list = new List<StateEntry>(num);
		for (int i = 0; i < num; i++)
		{
			uint netId = wireReader.U32();
			ushort key = wireReader.U16();
			uint version = wireReader.U32();
			uint hostMs = wireReader.U32();
			int n = wireReader.U16();
			list.Add(new StateEntry(netId, key, version, wireReader.Bytes(n).ToArray(), hostMs));
		}
		return list;
	}

	public static void Pack(IReadOnlyList<StateEntry> entries, int maxBytes, List<byte[]> output)
	{
		int j;
		for (int i = 0; i < entries.Count; i += j)
		{
			int num = 3;
			for (j = 0; i + j < entries.Count && (j == 0 || num + entries[i + j].WireSize <= maxBytes) && j < 65535; j++)
			{
				num += entries[i + j].WireSize;
			}
			output.Add(Encode(entries, i, j));
		}
	}
}
