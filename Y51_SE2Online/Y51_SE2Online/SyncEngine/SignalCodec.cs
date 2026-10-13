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
		byte[] array = payload.Slice(0, size).ToArray();
		WireCommand wireCommand = new WireCommand
		{
			Key = key,
			Payload = array
		};
		foreach (var item4 in scratch)
		{
			int item = item4.Offset;
			Segment item2 = item4.Seg;
			int arg = BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(item));
			WireValue item3 = ObjectCodec.Encode(readObject(arg), item2.Type ?? typeof(object), sp, ref fail);
			if (fail != null)
			{
				fail = $"arg {item2.Param}: {fail}";
				return null;
			}
			BinaryPrimitives.WriteInt32LittleEndian(array.AsSpan(item), 0);
			wireCommand.Refs.Add(item3);
		}
		if (sp.LocalIds.Count > 0)
		{
			fail = "refers to an entity the other side does not have";
			return null;
		}
		return wireCommand;
	}

	public static bool Write(ReplicationCommandBuffer cb, CommandLayout l, WireCommand c, ObjectSpace sp, List<(int Offset, Segment Seg)> scratch, ref string? fail)
	{
		int num = l.Frame(c.Payload, scratch);
		if (num != c.Payload.Length)
		{
			fail = $"payload {c.Payload.Length} B does not frame ({num})";
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
			Segment item = scratch[i].Seg;
			bool flag = l.Translated.TryGetValue(item.Param, out var value);
			objs[i] = ObjectCodec.Decode(c.Refs[i], item.Type ?? typeof(object), sp, flag, ref fail);
			if (fail != null)
			{
				if (!flag || value != 3 || c.Refs[i].Kind != WireKind.Entity || item.Param == 0)
				{
					if (flag && value == 1)
					{
						fail += " (the game skips the command then: dropped)";
					}
					fail = $"arg {item.Param}: {fail}";
					return false;
				}
				fail = null;
				objs[i] = null;
			}
			else if (((objs[i] == null) & flag) && (value == 0 || value == 2) && c.Refs[i].Kind != WireKind.Null)
			{
				fail = $"arg {item.Param}: unresolved where the callback would throw";
				return false;
			}
		}
		int[] offsets = new int[scratch.Count];
		for (int j = 0; j < scratch.Count; j++)
		{
			offsets[j] = scratch[j].Offset;
		}
		CommandBuffers.Append(cb, l.Stub, c.Payload, (int off) => cb.StoreObject(objs[Array.IndexOf(offsets, off)]), offsets, 1580056798);
		return true;
	}
}
