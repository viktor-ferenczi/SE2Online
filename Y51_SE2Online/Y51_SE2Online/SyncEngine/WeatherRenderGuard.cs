using System;
using System.Threading;
using Keen.Game2.Client.WorldObjects;
using Keen.Game2.Client.WorldObjects.Weather;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Joining client only. A weather event's render component looks up its planet when it enters the scene. On a joining
/// client the planet can arrive in a later world part, and the lookup's exception then fails the whole world part the
/// event is in. When the planet is not usable yet the event's fog and cloud overlay is skipped instead (the event itself
/// still replicates); the next weather event renders normally.
/// </summary>
internal static class WeatherRenderGuard
{
	public static long Skipped;

	private static readonly System.Reflection.FieldInfo ModifierField = HarmonyLib.AccessTools.Field(typeof(WeatherModifierRenderComponent), "_weatherModifierEntity");

	public static bool AddedPrefix(WeatherModifierRenderComponent __instance)
	{
		if (PlanetReady(__instance))
		{
			return true;
		}
		if (Interlocked.Increment(ref Skipped) == 1)
		{
			GameLog.Instance.Info("client: a weather event's planet is not here yet; its fog and cloud overlay is skipped");
		}
		return false;
	}

	public static bool RemovedPrefix(WeatherModifierRenderComponent __instance)
	{
		if (PlanetReady(__instance))
		{
			return true;
		}
		// The overlay's type lives in a render assembly this plugin does not reference.
		(ModifierField.GetValue(__instance) as IDisposable)?.Dispose();
		ModifierField.SetValue(__instance, null);
		return false;
	}

	private static bool PlanetReady(WeatherModifierRenderComponent component)
	{
		try
		{
			return component._event?.Planet?.TryGet<PlanetEnvironmentRenderComponent>() != null;
		}
		catch (NullReferenceException)
		{
			return false;
		}
	}
}
