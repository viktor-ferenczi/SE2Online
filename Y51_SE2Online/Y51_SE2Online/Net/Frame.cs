using System;
using System.Buffers.Binary;

namespace Y51_SE2Online.Net;

public static class Frame
{
	public const byte Magic = 94;

	public const byte WireVersion = 1;

	public const ushort ProtocolVersion = 2;

	public const int HeaderSize = 9;

	public static void Write(Span<byte> dst, in FrameHeader h)
	{
		if (dst.Length < 9)
		{
			throw new ArgumentException("frame buffer too small", "dst");
		}
		dst[0] = 94;
		dst[1] = 1;
		dst[2] = (byte)h.Channel;
		dst[3] = (byte)h.Kind;
		BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4), h.Seq);
		dst[8] = h.Flags;
	}

	public static byte[] Build(in FrameHeader h, ReadOnlySpan<byte> payload)
	{
		byte[] array = new byte[9 + payload.Length];
		Write(array, in h);
		payload.CopyTo(array.AsSpan(9));
		return array;
	}

	public static bool TryRead(ReadOnlySpan<byte> src, out FrameHeader header, out ReadOnlySpan<byte> payload, out string? error)
	{
		header = default;
		payload = default;
		if (src.Length < 9)
		{
			error = $"short frame ({src.Length} B)";
			return false;
		}
		if (src[0] != 94)
		{
			error = $"bad magic 0x{src[0]:x2}";
			return false;
		}
		if (src[1] != 1)
		{
			error = $"frame version {src[1]} (ours {1})";
			return false;
		}
		byte b = src[2];
		if (b > 2)
		{
			error = $"unknown channel {b}";
			return false;
		}
		byte b2 = src[3];
		if (!Enum.IsDefined(typeof(FrameKind), b2))
		{
			error = $"unknown frame kind {b2}";
			return false;
		}
		header = new FrameHeader((Channel)b, (FrameKind)b2, BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4)), src[8]);
		payload = src.Slice(9);
		error = null;
		return true;
	}
}
