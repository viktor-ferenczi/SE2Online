using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Net;

public static class Fragmenter
{
	public static uint CountFor(uint totalLength, uint fragmentSize)
	{
		if (totalLength == 0)
		{
			return 1u;
		}
		return (uint)((ulong)((long)totalLength + (long)fragmentSize - 1) / (ulong)fragmentSize);
	}

	public static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> message, uint index, uint fragmentSize)
	{
		long num = (long)index * (long)fragmentSize;
		int val = (int)Math.Min(fragmentSize, message.Length - num);
		return message.Slice((int)num, Math.Max(0, val));
	}

	public static IEnumerable<(FragmentHeader Header, ReadOnlyMemory<byte> Payload)> Split(uint transferId, ReadOnlyMemory<byte> message, int fragmentSize)
	{
		if (fragmentSize <= 0)
		{
			throw new ArgumentOutOfRangeException("fragmentSize");
		}
		uint total = (uint)message.Length;
		uint count = CountFor(total, (uint)fragmentSize);
		for (uint i = 0u; i < count; i++)
		{
			yield return (Header: new FragmentHeader(transferId, i, count, total, (uint)fragmentSize), Payload: Slice(message, i, (uint)fragmentSize));
		}
	}
}
