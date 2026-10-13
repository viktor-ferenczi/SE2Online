using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class BlobDataMessage
{
	public static byte[] Encode(IReadOnlyList<BlobPiece> pieces)
	{
		int num = 5;
		foreach (BlobPiece piece in pieces)
		{
			num += 28 + piece.Bytes.Length;
		}
		WireWriter wireWriter = new WireWriter(num).U8(52).I32(pieces.Count);
		Span<byte> span = stackalloc byte[16];
		foreach (BlobPiece piece2 in pieces)
		{
			piece2.Id.TryWriteBytes(span);
			wireWriter.Bytes(span).I32(piece2.Total).I32(piece2.Offset)
				.I32(piece2.Bytes.Length)
				.Bytes(piece2.Bytes);
		}
		return wireWriter.ToArray();
	}

	public static List<BlobPiece> Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		int num = wireReader.I32();
		if (num < 0 || num > wireReader.Remaining / 28)
		{
			throw new ArgumentOutOfRangeException("n", $"{num} blob pieces in {wireReader.Remaining} B");
		}
		List<BlobPiece> list = new List<BlobPiece>(num);
		for (int i = 0; i < num; i++)
		{
			Guid guid = new Guid(wireReader.Bytes(16));
			int num2 = wireReader.I32();
			int num3 = wireReader.I32();
			int num4 = wireReader.I32();
			if (num4 < 0 || (num2 >= 0 && (num3 < 0 || (long)num3 + (long)num4 > num2)))
			{
				throw new ArgumentOutOfRangeException("len", $"blob {guid}: piece {num3}+{num4} of {num2}");
			}
			list.Add(new BlobPiece(guid, num2, num3, wireReader.Bytes(num4).ToArray()));
		}
		return list;
	}

	public static void Pack(IEnumerable<KeyValuePair<Guid, byte[]?>> blobs, int maxBytes, List<byte[]> output)
	{
		maxBytes = Math.Max(1024, maxBytes);
		List<BlobPiece> cur = new List<BlobPiece>();
		int curBytes = 0;
		foreach (var (id, array2) in blobs)
		{
			if (array2 == null)
			{
				if (curBytes + 28 > maxBytes)
				{
					Flush();
				}
				cur.Add(new BlobPiece(id, -1, 0, Array.Empty<byte>()));
				curBytes += 28;
				continue;
			}
			int num = 0;
			do
			{
				int num2 = maxBytes - curBytes - 28;
				if (num2 < Math.Min(4096, array2.Length - num))
				{
					Flush();
					num2 = maxBytes - 28;
				}
				int num3 = Math.Min(num2, array2.Length - num);
				cur.Add(new BlobPiece(id, array2.Length, num, array2.AsSpan(num, num3).ToArray()));
				curBytes += 28 + num3;
				num += num3;
			}
			while (num < array2.Length);
		}
		Flush();
		void Flush()
		{
			if (cur.Count != 0)
			{
				output.Add(Encode(cur));
				cur.Clear();
				curBytes = 0;
			}
		}
	}
}
