using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.OreDetection;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Collections;
using Keen.VRage.Voxels.Components;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2 keeps one ore marker list for the whole session and fills it once any character is antenna-connected to the
/// detector, so in multiplayer every player saw every detector's ores. Each joiner gets its own list instead, and the
/// session list becomes the local player's; a marker shows only within its detector's range.
/// </summary>
internal sealed partial class HostWorld
{
	// ponytail: one list per peer that ever joined this session, emptied (not dropped) when they leave.
	private static readonly ConcurrentDictionary<PeerId, OreDetectionHUDSessionComponent.OreDetectorMarkers> OrePerPeer = new();

	public static void InstallOreMarkers(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(OreDetectionHUDSessionComponent), nameof(OreDetectionHUDSessionComponent.UpdateMarkers)), new HarmonyMethod(typeof(HostWorld), nameof(OreUpdatePrefix)));
		harmony.Patch(AccessTools.Method(typeof(OreDetectionHUDSessionComponent), nameof(OreDetectionHUDSessionComponent.RemoveOreDetection)), postfix: new HarmonyMethod(typeof(HostWorld), nameof(OreRemovePostfix)));
	}

	internal static bool IsOwnOreList(object model) => model is OreDetectionHUDSessionComponent.OreDetectorMarkers list && OrePerPeer.Values.Contains(list);

	/// <summary>The session's ore list asked for by a joiner becomes that joiner's own list.</summary>
	internal static object? OreListFor(PeerId peer, object? result) =>
		result is OreDetectionHUDSessionComponent.OreDetectorMarkers ? OrePerPeer.GetOrAdd(peer, _ => new OreDetectionHUDSessionComponent.OreDetectorMarkers()) : result;

	private static bool OreUpdatePrefix(OreDetectionHUDSessionComponent __instance)
	{
		var world = Current;
		if (world == null) return true;
		var all = __instance._markers.DetectorToMarker;
		ShowInRange(__instance._markers.ActiveMarkers, all, world._hostCharacterServer);
		foreach (var (peer, own) in OrePerPeer)
		{
			ShowInRange(own.ActiveMarkers, all, world._joiners.Values.FirstOrDefault(j => j.Peer.Equals(peer))?.Character);
		}
		return false;
	}

	private static void OreRemovePostfix(OreDetectionHUDSessionComponent __instance)
	{
		var all = __instance._markers.DetectorToMarker;
		foreach (var own in OrePerPeer.Values)
		{
			for (int i = own.ActiveMarkers.Count - 1; i >= 0; i--)
			{
				if (!all.ContainsValue(own.ActiveMarkers[i])) own.ActiveMarkers.RemoveAt(i);
			}
		}
	}

	private static void ShowInRange(ObservableList<GPSMarker> shown, Dictionary<OreDetectionHUDSessionComponent.OreDetectionOrigin, GPSMarker> all, Entity? body)
	{
		foreach (var (origin, marker) in all)
		{
			if (InOreRange(origin.Owner.Entity, body))
			{
				if (!shown.Contains(marker)) shown.Add(marker);
			}
			else
			{
				shown.Remove(marker);
			}
		}
	}

	private static bool InOreRange(Entity detector, Entity? body)
	{
		if (body == null || EngineCodec.IsDead(body) || EngineCodec.IsDead(detector)) return false;
		float range = detector.TryGet<OreDetectorComponent>()?.Range ?? 0f;
		return (detector.Data.GetWorldTransform().Position - body.Data.GetWorldTransform().Position).LengthSquared() <= (double)range * range;
	}
}
