using System;
using System.Linq;
using System.Threading;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Physicss;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Logs why the server refused a joiner's tool target. Read-only: it never changes the outcome.</summary>
internal static class ToolTargetDiagnostics
{
	public static long Refused;

	public static long RefusedStandingOnTarget;

	private static int _logged;

	private static int _failed;

	public static void Postfix(InteractedEntityProviderServerComponent __instance, Entity? interactedEntity)
	{
		try
		{
			if (interactedEntity == null || __instance.InteractedEntity != null)
			{
				return;
			}
			Interlocked.Increment(ref Refused);
			Entity actor = __instance.Entity.GetTopLevelParent();
			CubeBlockComponent block = interactedEntity.TryGet<CubeBlockComponent>();
			Entity grid = block?.Grid?.Entity ?? interactedEntity;
			Entity gridRoot = grid.GetTopLevelParent();
			bool standingOnIt = false;
			CharacterSupportTrackerComponent support = actor.TryGet<CharacterSupportTrackerComponent>();
			if (support != null)
			{
				standingOnIt = support.SupportingEntities.Any(s => s == grid || s.GetTopLevelParent() == gridRoot);
			}
			if (standingOnIt)
			{
				Interlocked.Increment(ref RefusedStandingOnTarget);
			}
			if (Interlocked.Increment(ref _logged) > 12)
			{
				return;
			}
			HostWorld host = HostWorld.Current;
			string actorName = host?.DescribeActor(actor) ?? actor.ToString() ?? "?";
			long owner = (long?)grid.TryGet<OwnershipComponent>()?.Owner ?? 0;
			string lineOfSight = "nothing hit";
			Vector3D? origin = __instance._origin?.WorldTransform.Position;
			if (__instance._invalidIntersection is { } hit)
			{
				lineOfSight = origin is { } from
					? $"its ray stopped {Math.Sqrt((hit - from).LengthSquared()):0.00} m from the tool origin"
					: "its ray stopped early";
			}
			GameLog.Instance.Info($"host world sync: the server refused a tool target: {actorName} aimed at {(block != null ? "a block" : "an entity")} of grid {host?.DescribeNetId(gridRoot) ?? gridRoot.ToString()} (owner IdentityId {owner}); standing on that grid: {(standingOnIt ? "YES" : "no")}; line-of-sight check: {lineOfSight} (0.7.6 diagnostics; {Refused} refused so far, {RefusedStandingOnTarget} while standing on the target)");
		}
		catch (Exception ex)
		{
			if (Interlocked.Exchange(ref _failed, 1) == 0)
			{
				GameLog.Instance.Warn($"host world sync: tool target diagnostics failed ({ex.GetType().Name}: {ex.Message}); switched off");
			}
		}
	}

	public static string Describe() => $"tool targets refused by the server {Refused} ({RefusedStandingOnTarget} while standing on the target grid), accepted while standing on it {StandingToolReach.Accepted}";
}
