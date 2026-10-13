using System;
using System.Linq;
using System.Threading;
using Keen.Game2.Simulation.GameSystems.Physicss;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Host-side gameplay fix that LOOSENS a server check. The game's InteractedEntityProviderServerComponent.Validate only
/// tests line of sight from the tool origin to the hit point. For a joiner the server's copy of the character lags its
/// client, so that ray often grazes the deck the joiner stands on and the weld/grind target is refused. This postfix turns
/// such a refusal into an accept only when all of these hold: the actor is a joiner's character (never the host's own),
/// the target is a block, the hit point is within <see cref="Reach"/> of the tool origin, and the joiner is standing on
/// the same grid. Nothing else about the interaction changes; in particular the game applies no extra distance limit here,
/// so <see cref="Reach"/> is the whole bound.
/// </summary>
internal static class StandingToolReach
{
	public const double Reach = 4.0;

	public static long Accepted;

	public static void ValidatePostfix(InteractedEntityProviderServerComponent __instance, Entity entity, Vector3D intersection, ref bool __result)
	{
		if (__result)
		{
			return;
		}
		try
		{
			Entity? grid = entity.TryGet<CubeBlockComponent>()?.Grid?.Entity;
			IToolOriginProvider? origin = __instance._origin;
			if (grid == null || origin == null || (intersection - origin.WorldTransform.Position).LengthSquared() > Reach * Reach)
			{
				return;
			}
			Entity actor = __instance.Entity.GetTopLevelParent();
			if (HostWorld.Current?.IsJoinerCharacter(actor) != true)
			{
				return;
			}
			Entity gridRoot = grid.GetTopLevelParent();
			CharacterSupportTrackerComponent? support = actor.TryGet<CharacterSupportTrackerComponent>();
			if (support != null && support.SupportingEntities.Any(s => s.GetTopLevelParent() == gridRoot))
			{
				__result = true;
				Interlocked.Increment(ref Accepted);
			}
		}
		catch (Exception)
		{
		}
	}
}
