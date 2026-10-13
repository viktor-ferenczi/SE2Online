using System;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class ClientLookMessage
{
	public const int Size = 35;

	public static byte[] Encode(uint seq, uint netId, ushort epoch, double x, double y, double z)
	{
		return new WireWriter(35).U8(66).U32(seq).U32(netId)
			.U16(epoch)
			.I64(BitConverter.DoubleToInt64Bits(x))
			.I64(BitConverter.DoubleToInt64Bits(y))
			.I64(BitConverter.DoubleToInt64Bits(z))
			.ToArray();
	}

	public static (uint Seq, uint NetId, ushort Epoch, double X, double Y, double Z) Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		return (Seq: wireReader.U32(), NetId: wireReader.U32(), Epoch: wireReader.U16(), X: BitConverter.Int64BitsToDouble(wireReader.I64()), Y: BitConverter.Int64BitsToDouble(wireReader.I64()), Z: BitConverter.Int64BitsToDouble(wireReader.I64()));
	}
}
