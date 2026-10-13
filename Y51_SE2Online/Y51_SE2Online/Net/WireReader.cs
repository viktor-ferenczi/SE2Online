using System;
using System.Buffers.Binary;
using System.Text;

namespace Y51_SE2Online.Net;

public ref struct WireReader(ReadOnlySpan<byte> s)
{
	private ReadOnlySpan<byte> _s = s;

	public int Remaining => _s.Length;

	public byte U8()
	{
		byte result = _s[0];
		_s = _s.Slice(1);
		return result;
	}

	public ushort U16()
	{
		ushort result = BinaryPrimitives.ReadUInt16LittleEndian(_s);
		_s = _s.Slice(2);
		return result;
	}

	public uint U32()
	{
		uint result = BinaryPrimitives.ReadUInt32LittleEndian(_s);
		_s = _s.Slice(4);
		return result;
	}

	public int I32()
	{
		int result = BinaryPrimitives.ReadInt32LittleEndian(_s);
		_s = _s.Slice(4);
		return result;
	}

	public ulong U64()
	{
		ulong result = BinaryPrimitives.ReadUInt64LittleEndian(_s);
		_s = _s.Slice(8);
		return result;
	}

	public long I64()
	{
		long result = BinaryPrimitives.ReadInt64LittleEndian(_s);
		_s = _s.Slice(8);
		return result;
	}

	public ReadOnlySpan<byte> Bytes(int n)
	{
		ReadOnlySpan<byte> result = _s.Slice(0, n);
		_s = _s.Slice(n);
		return result;
	}

	public string Str()
	{
		int n = U16();
		return Encoding.UTF8.GetString(Bytes(n));
	}

	public ReadOnlySpan<byte> Rest()
	{
		ReadOnlySpan<byte> s = _s;
		_s = default;
		return s;
	}
}
