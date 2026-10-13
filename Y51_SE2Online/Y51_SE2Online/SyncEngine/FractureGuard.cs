using System;
using System.Buffers.Binary;
using System.Threading;
using Keen.VRage.Core.Physics;
using Keen.VRage.DCS.Components;
using Keen.VRage.Game.Components.Models;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics.Colliders.BreakableCollider;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class FractureGuard
{
	public static long Checked;

	public static long Dropped;

	private const string Signal = "BreakableComponent+OnFractureIntegrityChangedSignal";

	public static string? Check(CommandLayout l, WireCommand c, Entity target)
	{
		if (!l.Name.EndsWith("BreakableComponent+OnFractureIntegrityChangedSignal", StringComparison.Ordinal) || l.StringIdOffsets.Length == 0)
		{
			return null;
		}
		try
		{
			Interlocked.Increment(ref Checked);
			int num = l.StringIdOffsets[0];
			if (num + 4 > c.Payload.Length)
			{
				return null;
			}
			int id = BinaryPrimitives.ReadInt32LittleEndian(c.Payload.AsSpan(num));
			IRootBreakableCollider rootBreakableCollider = target.TryGet<BreakableModelComponent>()?.BreakableCollider;
			if (rootBreakableCollider == null)
			{
				return null;
			}
			FractureId id2 = new FractureId(StringId.FromId(id));
			if (rootBreakableCollider.TryGetFracture(id2, out var _))
			{
				return null;
			}
			Interlocked.Increment(ref Dropped);
			return "fracture not on this block's collider here";
		}
		catch (Exception)
		{
			return null;
		}
	}

	public static string Describe()
	{
		return $"fracture changes checked {Checked}, dropped (not on this PC's collider) {Dropped}";
	}
}
