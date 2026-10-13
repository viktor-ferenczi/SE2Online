using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class PlayerTableMessage
{
	public static byte[] Encode(IReadOnlyList<PlayerInfo> players)
	{
		WireWriter wireWriter = new WireWriter(8 + players.Count * 40).U8(43);
		Write(wireWriter, players);
		return wireWriter.ToArray();
	}

	public static List<PlayerInfo> Decode(ReadOnlySpan<byte> d)
	{
		WireReader r = new WireReader(d.Slice(1));
		return Read(ref r);
	}

	internal static void Write(WireWriter w, IReadOnlyList<PlayerInfo>? players)
	{
		w.U16((ushort)(players?.Count ?? 0));
		if (players == null)
		{
			return;
		}
		foreach (PlayerInfo player in players)
		{
			w.U16(player.ClientId).I64(player.Player).I64(player.Identity)
				.Str(player.Name)
				.U8(player.Connected ? ((byte)1) : ((byte)0));
		}
	}

	internal static List<PlayerInfo> Read(ref WireReader r)
	{
		List<PlayerInfo> list = new List<PlayerInfo>();
		if (r.Remaining < 2)
		{
			return list;
		}
		int num = r.U16();
		for (int i = 0; i < num; i++)
		{
			list.Add(new PlayerInfo(r.U16(), r.I64(), r.I64(), r.Str(), r.U8() != 0));
		}
		return list;
	}
}
