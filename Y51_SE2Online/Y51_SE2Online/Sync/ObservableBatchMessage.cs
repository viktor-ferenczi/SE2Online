using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class ObservableBatchMessage
{
	public static byte[] Encode(uint lifeSeq, IReadOnlyList<ObservableOp> ops)
	{
		WireWriter wireWriter = new WireWriter(64 + ops.Count * 24).U8(57).U32(lifeSeq).I32(ops.Count);
		foreach (ObservableOp op in ops)
		{
			op.Write(wireWriter);
		}
		return wireWriter.ToArray();
	}

	public static (uint LifeSeq, List<ObservableOp> Ops) Decode(ReadOnlySpan<byte> d)
	{
		WireReader r = new WireReader(d.Slice(1));
		uint item = r.U32();
		int num = r.I32();
		if (num < 0 || num > d.Length)
		{
			throw new FormatException($"observable batch count {num} is impossible for {d.Length} bytes");
		}
		List<ObservableOp> list = new List<ObservableOp>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(ObservableOp.Read(ref r));
		}
		return (LifeSeq: item, Ops: list);
	}
}
