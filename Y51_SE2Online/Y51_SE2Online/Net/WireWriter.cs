using System;
using System.Buffers.Binary;
using System.Text;

namespace Y51_SE2Online.Net;

public sealed class WireWriter
{
	private byte[] _buf;

	private int _len;

	public int Length => _len;

	public WireWriter(int capacity = 64)
	{
		_buf = new byte[capacity];
	}

	private Span<byte> Take(int n)
	{
		if (_len + n > _buf.Length)
		{
			Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
		}
		Span<byte> result = _buf.AsSpan(_len, n);
		_len += n;
		return result;
	}

	public WireWriter U8(byte v)
	{
		Take(1)[0] = v;
		return this;
	}

	public WireWriter U16(ushort v)
	{
		BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
		return this;
	}

	public WireWriter U32(uint v)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
		return this;
	}

	public WireWriter I32(int v)
	{
		BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
		return this;
	}

	public WireWriter U64(ulong v)
	{
		BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v);
		return this;
	}

	public WireWriter I64(long v)
	{
		BinaryPrimitives.WriteInt64LittleEndian(Take(8), v);
		return this;
	}

	public WireWriter Bytes(ReadOnlySpan<byte> v)
	{
		v.CopyTo(Take(v.Length));
		return this;
	}

	public WireWriter Str(string s)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(s);
		if (bytes.Length > 65535)
		{
			throw new ArgumentException("string too long");
		}
		U16((ushort)bytes.Length);
		return Bytes(bytes);
	}

	public byte[] ToArray()
	{
		return _buf.AsSpan(0, _len).ToArray();
	}
}
