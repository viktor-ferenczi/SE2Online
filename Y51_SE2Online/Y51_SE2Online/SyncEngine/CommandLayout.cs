using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class CommandLayout
{
	public int Stub;

	public string Name;

	public CommandKind Kind;

	public int PayloadBytes = -1;

	public int[] RefOffsets = Array.Empty<int>();

	public Segment[] Segments = Array.Empty<Segment>();

	public bool Forwardable;

	public string? Why;

	public Type? SignalType;

	public Dictionary<int, int> Translated = new Dictionary<int, int>();

	public int[] StringIdOffsets = Array.Empty<int>();

	public bool Dynamic => Segments.Any((Segment s) => s.Kind == SegmentKind.Span);

	public string Signature
	{
		get
		{
			if (Segments.Length != 0)
			{
				return string.Join(",", Segments.Select((Segment s) => s.Kind switch
				{
					SegmentKind.Ref => "R", 
					SegmentKind.Span => "S", 
					_ => "B" + s.Size, 
				}));
			}
			return string.Join(",", RefOffsets);
		}
	}

	public SignalKeyDef Def(ushort key)
	{
		return new SignalKeyDef(key, Name, Dynamic ? ushort.MaxValue : ((ushort)Math.Max(0, PayloadBytes)), Signature);
	}

	public int Frame(ReadOnlySpan<byte> payload, List<(int Offset, Segment Seg)> refs)
	{
		refs.Clear();
		int num = 0;
		Segment[] segments = Segments;
		for (int i = 0; i < segments.Length; i++)
		{
			Segment item = segments[i];
			switch (item.Kind)
			{
			case SegmentKind.Bytes:
				num += item.Size;
				break;
			case SegmentKind.Ref:
				refs.Add((num, item));
				num += 4;
				break;
			case SegmentKind.Span:
			{
				if (num + 4 > payload.Length)
				{
					return -1;
				}
				int num2 = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(num));
				if (num2 < 0 || num2 > 16777216)
				{
					return -1;
				}
				num += 4 + num2;
				break;
			}
			}
			if (num > payload.Length)
			{
				return -1;
			}
		}
		return num;
	}
}
