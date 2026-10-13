using System;
using System.Buffers.Binary;

namespace Y51_SE2Online.Sync;

/// <summary>Host to joiner: the newest input message applied to its character and the host time it was applied.</summary>
public static class InputAckMessage
{
	public static byte[] Encode(uint seq, uint appliedHostMs)
	{
		var data = new byte[9];
		data[0] = (byte)SyncMsg.InputAck;
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1), seq);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(5), appliedHostMs);
		return data;
	}

	public static (uint Seq, uint AppliedHostMs) Decode(ReadOnlySpan<byte> data)
	{
		if (data.Length < 9) throw new ArgumentException("Invalid input ack.");
		return (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1)), BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(5)));
	}
}
