using System;
using System.Threading;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class TeamPermissions
{
	public static volatile bool Enabled;

	public static long Overrides;

	private static int _logged;

	public static IMpLog? Log;

	internal static Func<Entity?, long>? OwnerOverride;

	internal static long OwnerOf(Entity? e)
	{
		if (OwnerOverride != null)
		{
			return OwnerOverride(e);
		}
		// The nearest ownership up the hierarchy (a block's grid, a subgrid's parent grid).
		for (int depth = 0; e != null && depth < 64; depth++)
		{
			OwnershipComponent ownership = e.TryGet<OwnershipComponent>();
			if (ownership != null)
			{
				return ownership.Owner;
			}
			e = e.TryGet<HierarchyComponent>()?.Parent;
		}
		return 0L;
	}

	private static void Grant(ref bool result, string what, long owner, long asker)
	{
		result = true;
		Interlocked.Increment(ref Overrides);
		if (Interlocked.Increment(ref _logged) <= 5)
		{
			Log?.Info($"team grids: {what} allowed for identity {asker} on a grid of identity {owner} (game rule: owner only)");
		}
	}

	public static void IsOwnerPostfix(Entity entity, IdentityId owner, ref bool __result)
	{
		if (ApplyAccess(entity, owner, ref __result)) return;
		if (!__result && Enabled)
		{
			long gridOwner = OwnerOf(entity);
			if (PlayerIdentities.SameTeam(gridOwner, owner))
			{
				Grant(ref __result, "ownership check", gridOwner, owner);
			}
		}
	}

	public static void CanPlaceBlockPostfix(Entity target, IdentityId identity, ref bool __result)
	{
		if (ApplyAccess(target, identity, ref __result)) return;
		if (!__result && Enabled)
		{
			long owner = OwnerOf(target);
			if (PlayerIdentities.SameTeam(owner, identity))
			{
				Grant(ref __result, "building", owner, identity);
			}
		}
	}

	public static void CanUseBlockActionPostfix(Entity grid, IdentityId identity, ref bool __result)
	{
		if (ApplyAccess(grid, identity, ref __result)) return;
		if (!__result && Enabled)
		{
			long owner = OwnerOf(grid);
			if (PlayerIdentities.SameTeam(owner, identity))
			{
				Grant(ref __result, "block use", owner, identity);
			}
		}
	}

	public static void CanLinkGridsPostfix(Entity firstGrid, Entity secondGrid, ref bool __result)
	{
		if (ApplyLink(firstGrid, secondGrid, false, ref __result)) return;
		if (!__result && Enabled)
		{
			long firstOwner = OwnerOf(firstGrid);
			long secondOwner = OwnerOf(secondGrid);
			if (PlayerIdentities.SameTeam(firstOwner, secondOwner))
			{
				Grant(ref __result, "connecting grids", secondOwner, firstOwner);
			}
		}
	}

	public static void CanMergeGridsPostfix(Entity firstGrid, Entity secondGrid, ref bool __result)
	{
		if (ApplyLink(firstGrid, secondGrid, true, ref __result)) return;
		if (!__result && Enabled && firstGrid != secondGrid)
		{
			long firstOwner = OwnerOf(firstGrid);
			long secondOwner = OwnerOf(secondGrid);
			if (PlayerIdentities.SameTeam(firstOwner, secondOwner))
			{
				Grant(ref __result, "merging grids", secondOwner, firstOwner);
			}
		}
	}

	private static bool ApplyAccess(Entity target, long actor, ref bool result)
	{
		bool? access = WorldSocial.Access(target, actor);
		if (!access.HasValue) return false;
		if (access.Value && !result) Interlocked.Increment(ref Overrides);
		result = access.Value;
		return true;
	}

	private static bool ApplyLink(Entity first, Entity second, bool merge, ref bool result)
	{
		bool? firstAccess = WorldSocial.Access(first, OwnerOf(second));
		bool? secondAccess = WorldSocial.Access(second, OwnerOf(first));
		if (!firstAccess.HasValue || !secondAccess.HasValue) return false;
		result = first == second ? !merge : firstAccess.Value && secondAccess.Value;
		return true;
	}

	public static void IsFriendlyPostfix(IdentityId source, Entity target, ref bool __result)
	{
		long owner = OwnerOf(target);
		if (!PlayerIdentities.IsHuman(owner) || !PlayerIdentities.IsHuman(source)) return;
		__result = owner == (long)source;
		if (ClientWorld.Current?.BoundSession is { } client)
			__result |= client.Social.Factions.Any(f => f.Members.Contains(owner) && f.Members.Contains((long)source));
		else if (HostWorld.Current?.Social is { } social)
			lock (social.Sync) __result |= social.State.Factions.Any(f => f.Members.Contains(owner) && f.Members.Contains((long)source));
	}
}
