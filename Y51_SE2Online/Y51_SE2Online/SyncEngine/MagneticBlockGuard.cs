using System;
using System.Threading;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Components;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Joining client only. A landing gear's saved "ready to lock" target can name an entity that is built in a later world
/// part, so it is not in a scene yet when the gear's bundle enters the scene; the game's WeakEntityReference then throws,
/// the bundle fails to build, and destroying that half-initialised gear later crashes the game. The host owns lock
/// state and re-sends it, so on the client an out-of-scene target is treated as "nothing to lock to".
/// </summary>
internal static class MagneticBlockGuard
{
	public static long TargetsDropped;

	public static long DestroyFaults;

	public static void ReadyToLockPrefix(ref Entity? newValue)
	{
		if (newValue == null)
		{
			return;
		}
		try
		{
			_ = newValue.GetSession();
		}
		catch (NullReferenceException)
		{
			newValue = null;
			Interlocked.Increment(ref TargetsDropped);
		}
	}

	public static Exception? DestroyFinalizer(Exception? __exception)
	{
		if (__exception is NullReferenceException)
		{
			if (Interlocked.Increment(ref DestroyFaults) == 1)
			{
				GameLog.Instance.Warn("client: a landing gear failed to clean up its lock target reference while being removed; ignored");
			}
			return null;
		}
		return __exception;
	}

	public static string Describe() => $"landing gear lock targets not in scene {TargetsDropped}, cleanup faults {DestroyFaults}";
}
