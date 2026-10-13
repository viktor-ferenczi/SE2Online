using System;
using System.Buffers.Binary;

namespace Y51_SE2Online.Net;

public static class StatePrefix
{
	public const int Size = 8;

	public static void Write(Span<byte> dst, uint ackSeq, uint ackBits)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(dst, ackSeq);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4), ackBits);
	}

	public static bool TryRead(ReadOnlySpan<byte> src, out uint ackSeq, out uint ackBits, out ReadOnlySpan<byte> rest)
	{
		if (src.Length < 8)
		{
			ackSeq = (ackBits = 0u);
			rest = default;
			return false;
		}
		ackSeq = BinaryPrimitives.ReadUInt32LittleEndian(src);
		ackBits = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4));
		rest = src.Slice(8);
		return true;
	}
}
