using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class WireGuids
{
	public static void Write(WireWriter w, IReadOnlyList<Guid>? ids)
	{
		w.I32(ids?.Count ?? 0);
		if (ids == null)
		{
			return;
		}
		Span<byte> span = stackalloc byte[16];
		foreach (Guid id in ids)
		{
			id.TryWriteBytes(span);
			w.Bytes(span);
		}
	}

	public static List<Guid> Read(ref WireReader r)
	{
		if (r.Remaining < 4)
		{
			return new List<Guid>();
		}
		int num = r.I32();
		if (num < 0 || num > r.Remaining / 16)
		{
			throw new ArgumentOutOfRangeException("n", $"guid list of {num} entries in {r.Remaining} B");
		}
		List<Guid> list = new List<Guid>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(new Guid(r.Bytes(16)));
		}
		return list;
	}
}
