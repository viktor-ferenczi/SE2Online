using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.VRage.DCS.Utils;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

internal static class CommandBuffers
{
	public delegate ref byte ReadBufferFn(CommandBufferBase cb, int index, out nint offset, out nint activeBytes);

	private static AccessTools.FieldRef<CommandBufferBase, List<byte[]>>? _committed;

	private static ReadBufferFn? _read;

	private static void Init()
	{
		if (_read == null)
		{
			_committed = AccessTools.FieldRefAccess<CommandBufferBase, List<byte[]>>(EngineMembers.Field(R("cb-buffers")));
			_read = (ReadBufferFn)Delegate.CreateDelegate(typeof(ReadBufferFn), EngineMembers.Method(R("cb-read")));
		}
		static EngineMember R(string id)
		{
			return HookTargets.Reflected.Single((EngineMember m) => m.Id == id);
		}
	}

	public static int Count(ReplicationCommandBuffer cb)
	{
		Init();
		return _committed(cb).Count;
	}

	public unsafe static byte[] Bytes(ReplicationCommandBuffer cb, int index)
	{
		Init();
		ref byte reference = ref _read(cb, index, out var offset, out var activeBytes);
		int num = (int)(activeBytes - offset);
		if (num <= 0)
		{
			return Array.Empty<byte>();
		}
		byte[] array = new byte[num];
		fixed (byte* ptr = &reference)
		{
			new ReadOnlySpan<byte>(ptr + offset, num).CopyTo(array);
		}
		return array;
	}

	public unsafe static void Append(ReplicationCommandBuffer cb, int stub, ReadOnlySpan<byte> payload, Func<int, int>? refSlot, int[] refOffsets, int marker = 0)
	{
		fixed (byte* ptr = &cb.AllocWriteBuffer(8 + payload.Length))
		{
			Unsafe.WriteUnaligned(ptr, marker);
			Unsafe.WriteUnaligned(ptr + 4, stub);
			payload.CopyTo(new Span<byte>(ptr + 8, payload.Length));
			if (refSlot != null)
			{
				foreach (int num in refOffsets)
				{
					Unsafe.WriteUnaligned(ptr + 8 + num, refSlot(num));
				}
			}
		}
	}
}
