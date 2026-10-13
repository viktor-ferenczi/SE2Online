using System;
using System.Threading;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online;

internal static class CharacterRemovalGuard
{
	public static long Skipped;

	private static int _logged;

	public static bool OnEntityRemovedPrefix(Component __instance)
	{
		try
		{
			Entity entity = __instance.Entity;
			if (entity != null && !EngineCodec.IsDead(entity))
			{
				return true;
			}
		}
		catch (Exception)
		{
		}
		Interlocked.Increment(ref Skipped);
		if (Interlocked.Exchange(ref _logged, 1) == 0)
		{
			GameLog.Instance.Warn("world sync: a destroyed character was still subscribed to entity removals (leaked by a dampener request that failed); its handler is skipped instead of throwing (0.7.4 host exit crash, CharacterRemovalGuard)");
		}
		return false;
	}
}
