using System;
using System.Threading;
using Keen.Game2.Client.WorldObjects.Tools;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

internal static class BuildGuards
{
	public static long TopSpawnsSkipped;

	public static long RenderDestroysDropped;

	internal static IMpLog? Log;

	private static int _topLogged;

	private static int _renderLogged;

	public static long PaintToolFallbacks;

	private static int _paintLogged;

	public static bool SpawnTopPrefix()
	{
		Interlocked.Increment(ref TopSpawnsSkipped);
		if (Interlocked.Increment(ref _topLogged) <= 3)
		{
			Log?.Info("client: a mechanical block (rotor/hinge/piston) wanted to spawn its own top on this PC; skipped (the host's top arrives with the world)");
		}
		return false;
	}

	public static Exception? RenderDestroyFinalizer(Exception? __exception)
	{
		if (!(__exception is NullReferenceException))
		{
			return __exception;
		}
		Interlocked.Increment(ref RenderDestroysDropped);
		if (Interlocked.Increment(ref _renderLogged) <= 3)
		{
			Log?.Warn("render: a destroy for an entity the render scene does not have was skipped (it would have crashed the render thread; 0.8.2 guard)");
		}
		return null;
	}

	public static long PaintColorFallbacks;

	/// <summary>
	/// Palette[PaletteIndex] with the index out of range (a joiner can see a paint tool before its owner's palette
	/// arrives, or with an empty one) threw from every paint-tool render job and crashed the game.
	/// </summary>
	public static bool PaintColorPrefix(PaintToolPerPlayerData __instance, ref Keen.VRage.Library.Mathematics.ColorHSV __result)
	{
		var palette = __instance.Palette;
		int index = __instance.PaletteIndex;
		if (palette != null && (uint)index < (uint)palette.Count) return true;
		__result = palette is { Count: > 0 } ? palette[0] : default;
		if (Interlocked.Increment(ref PaintColorFallbacks) == 1)
			Log?.Warn($"client: a paint tool's palette has no colour {index} ({palette?.Count ?? 0} colours); using {(palette is { Count: > 0 } ? "its first" : "a default")} colour instead of crashing");
		return false;
	}

	public static bool PaintToolAddedPrefix(PaintToolControllableComponent __instance)
	{
		try
		{
			if (!(__instance._perPlayerData is InProcessPerPlayerDataClientSessionComponent inProcessPerPlayerDataClientSessionComponent))
			{
				return true;
			}
			IdentityId? identityId = __instance._owner?.Owner;
			PaintToolPerPlayerData paintToolPerPlayerData = null;
			PaintToolPerPlayerData paintToolPerPlayerData2 = null;
			foreach (InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData datum in inProcessPerPlayerDataClientSessionComponent._data.Data)
			{
				if (datum.DataByType.TryGetValue(typeof(PaintToolPerPlayerData), out var value) && value is PaintToolPerPlayerData paintToolPerPlayerData3)
				{
					if (paintToolPerPlayerData2 == null)
					{
						paintToolPerPlayerData2 = paintToolPerPlayerData3;
					}
					if (identityId.HasValue && (long)datum.IdentityId == (long)identityId.Value)
					{
						paintToolPerPlayerData = paintToolPerPlayerData3;
					}
				}
			}
			if (paintToolPerPlayerData != null)
			{
				return true;
			}
			if (paintToolPerPlayerData2 == null)
			{
				paintToolPerPlayerData2 = new PaintToolPerPlayerData();
				paintToolPerPlayerData2.Palette.Add(default);
			}
			__instance.PaintData = paintToolPerPlayerData2;
			paintToolPerPlayerData2.PropertyChanged += __instance.PaintDataChanged;
			paintToolPerPlayerData2.Palette.CollectionChanged += __instance.PaintCollectionChanged;
			Interlocked.Increment(ref PaintToolFallbacks);
			if (Interlocked.Increment(ref _paintLogged) <= 3)
			{
				Log?.Info($"client: another player's paint tool (owner IdentityId {(identityId.HasValue ? ((long)identityId.Value) : 0)}) uses this game's palette for its preview; its owner's paint data is not on this PC");
			}
			return false;
		}
		catch (Exception ex)
		{
			Log?.WarnOnce("client-paint-guard", $"client: paint tool guard failed ({ex.GetType().Name}: {ex.Message}); the game's own code runs");
			return true;
		}
	}

	public static string Describe()
	{
		return $"0.8.2 build guards: tops not spawned here {TopSpawnsSkipped}, render destroys of missing entities skipped {RenderDestroysDropped}, others' paint tools on this palette {PaintToolFallbacks}";
	}
}
