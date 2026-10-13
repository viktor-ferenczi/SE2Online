using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class SignalCodec
{
	public const int Throw = 0;

	public const int Skip = 1;

	public const int NullStrategy = 2;

	public const int NullOnAnyFail = 3;

	public static WireCommand? Encode(CommandLayout l, ushort key, ReadOnlySpan<byte> payload, Func<int, object?> readObject, ObjectSpace sp, List<(int Offset, Segment Seg)> scratch, out int size, ref string? fail)
	{
		size = l.Frame(payload, scratch);
		if (size < 0)
		{
			fail = "payload does not frame";
			return null;
		}
		byte[] bytes = payload.Slice(0, size).ToArray();
		var command = new WireCommand
		{
			Key = key,
			Payload = bytes
		};
		foreach (var (offset, seg) in scratch)
		{
			int handle = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
			WireValue encoded = ObjectCodec.Encode(readObject(handle), seg.Type ?? typeof(object), sp, ref fail);
			if (fail != null)
			{
				fail = $"arg {seg.Param}: {fail}";
				return null;
			}
			// The handle only means something in this process; the receiver rebinds the slot from Refs.
			BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), 0);
			command.Refs.Add(encoded);
		}
		if (sp.LocalIds.Count > 0)
		{
			fail = "refers to an entity the other side does not have";
			return null;
		}
		return command;
	}

	public static bool Write(ReplicationCommandBuffer cb, CommandLayout l, WireCommand c, ObjectSpace sp, List<(int Offset, Segment Seg)> scratch, ref string? fail)
	{
		int framed = l.Frame(c.Payload, scratch);
		if (framed != c.Payload.Length)
		{
			fail = $"payload {c.Payload.Length} B does not frame ({framed})";
			return false;
		}
		if (scratch.Count != c.Refs.Count)
		{
			fail = $"{c.Refs.Count} references for {scratch.Count} slots";
			return false;
		}
		object?[] objs = new object[scratch.Count];
		for (int i = 0; i < scratch.Count; i++)
		{
			Segment seg = scratch[i].Seg;
			bool translated = l.Translated.TryGetValue(seg.Param, out int strategy);
			objs[i] = ObjectCodec.Decode(c.Refs[i], seg.Type ?? typeof(object), sp, translated, ref fail);
			if (fail != null)
			{
				if (!translated || strategy != NullOnAnyFail || c.Refs[i].Kind != WireKind.Entity || seg.Param == 0)
				{
					if (translated && strategy == Skip)
					{
						fail += " (the game skips the command then: dropped)";
					}
					fail = $"arg {seg.Param}: {fail}";
					return false;
				}
				fail = null;
				objs[i] = null;
			}
			else if (objs[i] == null && translated && (strategy == Throw || strategy == NullStrategy) && c.Refs[i].Kind != WireKind.Null)
			{
				fail = $"arg {seg.Param}: unresolved where the callback would throw";
				return false;
			}
		}
		int[] offsets = new int[scratch.Count];
		for (int j = 0; j < scratch.Count; j++)
		{
			offsets[j] = scratch[j].Offset;
		}
		CommandBuffers.Append(cb, l.Stub, c.Payload, off => cb.StoreObject(objs[Array.IndexOf(offsets, off)]), offsets, 1580056798);
		return true;
	}
}
