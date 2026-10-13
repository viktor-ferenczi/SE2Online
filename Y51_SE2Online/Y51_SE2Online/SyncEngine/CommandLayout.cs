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

	public bool DownstreamOnly;

	public string? Why;

	public Type? SignalType;

	public Dictionary<int, int> Translated = new();

	public int[] StringIdOffsets = Array.Empty<int>();

	public bool Dynamic => Segments.Any(s => s.Kind == SegmentKind.Span);

	public string Signature
	{
		get
		{
			if (Segments.Length != 0)
			{
				return string.Join(",", Segments.Select(s => s.Kind switch
				{
					SegmentKind.Ref => "R", 
					SegmentKind.Span => "S", 
					_ => "B" + s.Size, 
				}));
			}
			return string.Join(",", RefOffsets);
		}
	}

	public SignalKeyDef Def(ushort key) =>
		new(key, Name, Dynamic ? ushort.MaxValue : (ushort)Math.Max(0, PayloadBytes), Signature);

	public int Frame(ReadOnlySpan<byte> payload, List<(int Offset, Segment Seg)> refs)
	{
		refs.Clear();
		int offset = 0;
		foreach (Segment seg in Segments)
		{
			switch (seg.Kind)
			{
			case SegmentKind.Bytes:
				offset += seg.Size;
				break;
			case SegmentKind.Ref:
				refs.Add((offset, seg));
				offset += 4;
				break;
			case SegmentKind.Span:
			{
				if (offset + 4 > payload.Length)
				{
					return -1;
				}
				int spanLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
				if (spanLength < 0 || spanLength > 16777216)
				{
					return -1;
				}
				offset += 4 + spanLength;
				break;
			}
			}
			if (offset > payload.Length)
			{
				return -1;
			}
		}
		return offset;
	}
}
