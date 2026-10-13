using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Keen.Game2.Client.WorldObjects;
using Keen.Game2.Client.WorldObjects.Weather;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Joining client only. A weather event's render component looks up its planet when it enters the scene. On a joining
/// client the planet can arrive in a later world part, and the lookup's exception then fails the whole world part the
/// event is in. When the planet is not usable yet the event's fog and cloud overlay is held back (the event itself still
/// replicates) and built once a planet's environment renderer enters the scene.
/// </summary>
internal static class WeatherRenderGuard
{
	public static long Skipped;

	public static long Recovered;

	private static readonly List<WeatherModifierRenderComponent> Pending = new();

	private static readonly System.Reflection.FieldInfo ModifierField = HarmonyLib.AccessTools.Field(typeof(WeatherModifierRenderComponent), "_weatherModifierEntity");

	public static bool AddedPrefix(WeatherModifierRenderComponent __instance)
	{
		if (PlanetReady(__instance))
		{
			return true;
		}
		if (Interlocked.Increment(ref Skipped) == 1)
		{
			GameLog.Instance.Info("client: a weather event's planet is not here yet; its fog and cloud overlay waits for it");
		}
		lock (Pending)
		{
			if (!Pending.Contains(__instance)) Pending.Add(__instance);
		}
		return false;
	}

	public static void PlanetAddedPostfix()
	{
		WeatherModifierRenderComponent[] ready;
		lock (Pending)
		{
			ready = Pending.Where(PlanetReady).ToArray();
			Pending.RemoveAll(ready.Contains);
		}
		foreach (var weather in ready)
		{
			try
			{
				weather.OnAddedToScene();
				Interlocked.Increment(ref Recovered);
			}
			catch (Exception ex)
			{
				GameLog.Instance.Warn("client: building a held-back weather overlay failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}
	}

	public static bool RemovedPrefix(WeatherModifierRenderComponent __instance)
	{
		lock (Pending) Pending.Remove(__instance);
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
