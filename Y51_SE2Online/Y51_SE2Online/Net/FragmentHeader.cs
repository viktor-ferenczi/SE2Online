using System;
using System.Buffers.Binary;

namespace Y51_SE2Online.Net;

public readonly struct FragmentHeader(uint transferId, uint index, uint count, uint totalLength, uint fragmentSize)
{
	public const int Size = 20;

	public readonly uint TransferId = transferId;

	public readonly uint Index = index;

	public readonly uint Count = count;

	public readonly uint TotalLength = totalLength;

	public readonly uint FragmentSize = fragmentSize;

	public void Write(Span<byte> dst)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(dst, TransferId);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4), Index);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(8), Count);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(12), TotalLength);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(16), FragmentSize);
	}

	public static bool TryRead(ReadOnlySpan<byte> src, out FragmentHeader h, out ReadOnlySpan<byte> payload)
	{
		if (src.Length < 20)
		{
			h = default;
			payload = default;
			return false;
		}
		h = new FragmentHeader(BinaryPrimitives.ReadUInt32LittleEndian(src), BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4)), BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8)), BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12)), BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(16)));
		payload = src.Slice(20);
		return true;
	}

	public string? Validate(int payloadLength)
	{
		if (FragmentSize == 0)
		{
			return "fragmentSize 0";
		}
		if (Count != Fragmenter.CountFor(TotalLength, FragmentSize))
		{
			return $"count {Count} != ceil({TotalLength}/{FragmentSize})";
		}
		if (Index >= Count)
		{
			return $"index {Index} >= count {Count}";
		}
		long num = ((Index == Count - 1) ? (TotalLength - (long)Index * (long)FragmentSize) : FragmentSize);
		if (payloadLength != num)
		{
			return $"fragment {Index} has {payloadLength} B, expected {num}";
		}
		return null;
	}

	public override string ToString()
	{
		return $"xfer {TransferId} frag {Index + 1}/{Count} ({TotalLength} B)";
	}
}
