using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class BlobDataMessage
{
	public static byte[] Encode(IReadOnlyList<BlobPiece> pieces)
	{
		int size = 5;
		foreach (BlobPiece piece in pieces)
		{
			size += 28 + piece.Bytes.Length;
		}
		WireWriter w = new WireWriter(size).U8(52).I32(pieces.Count);
		Span<byte> idBytes = stackalloc byte[16];
		foreach (BlobPiece piece in pieces)
		{
			piece.Id.TryWriteBytes(idBytes);
			w.Bytes(idBytes).I32(piece.Total).I32(piece.Offset)
				.I32(piece.Bytes.Length)
				.Bytes(piece.Bytes);
		}
		return w.ToArray();
	}

	public static List<BlobPiece> Decode(ReadOnlySpan<byte> d)
	{
		var r = new WireReader(d.Slice(1));
		int count = r.I32();
		if (count < 0 || count > r.Remaining / 28)
		{
			throw new ArgumentOutOfRangeException("n", $"{count} blob pieces in {r.Remaining} B");
		}
		var pieces = new List<BlobPiece>(count);
		for (int i = 0; i < count; i++)
		{
			var id = new Guid(r.Bytes(16));
			int total = r.I32();
			int offset = r.I32();
			int length = r.I32();
			if (length < 0 || (total >= 0 && (offset < 0 || (long)offset + length > total)))
			{
				throw new ArgumentOutOfRangeException("len", $"blob {id}: piece {offset}+{length} of {total}");
			}
			pieces.Add(new BlobPiece(id, total, offset, r.Bytes(length).ToArray()));
		}
		return pieces;
	}

	public static void Pack(IEnumerable<KeyValuePair<Guid, byte[]?>> blobs, int maxBytes, List<byte[]> output)
	{
		maxBytes = Math.Max(1024, maxBytes);
		var cur = new List<BlobPiece>();
		int curBytes = 0;
		foreach (var (id, bytes) in blobs)
		{
			if (bytes is null)
			{
				if (curBytes + 28 > maxBytes)
				{
					Flush();
				}
				cur.Add(new BlobPiece(id, -1, 0, Array.Empty<byte>()));
				curBytes += 28;
				continue;
			}
			int offset = 0;
			do
			{
				int room = maxBytes - curBytes - 28;
				if (room < Math.Min(4096, bytes.Length - offset))
				{
					Flush();
					room = maxBytes - 28;
				}
				int take = Math.Min(room, bytes.Length - offset);
				cur.Add(new BlobPiece(id, bytes.Length, offset, bytes.AsSpan(offset, take).ToArray()));
				curBytes += 28 + take;
				offset += take;
			}
			while (offset < bytes.Length);
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
