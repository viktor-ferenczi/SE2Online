using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Components;

namespace Y51_SE2Online.SyncEngine;

internal static class ToolShadowGuard
{
	private sealed class Owner
	{
		public WeakReference<Entity>? Tool;
	}

	private static readonly ConditionalWeakTable<ToolsRenderAdapterComponent, Owner> Owners = new ConditionalWeakTable<ToolsRenderAdapterComponent, Owner>();

	public static long KeptForNewerTool;

	public static long OwnerCleared;

	public static long LocalToolKept;

	private static int _loggedKept;

	private static int _loggedCleared;

	private static int _loggedLocal;

	private static int _failed;

	public static void SpawnPostfix(ToolsRenderAdapterComponent __instance, Entity actualTool)
	{
		try
		{
			if (__instance._firstPersonToolShadowCaster?.Entity != null)
			{
				Owners.GetOrCreateValue(__instance).Tool = new WeakReference<Entity>(actualTool);
			}
		}
		catch (Exception e)
		{
			Fail(e);
		}
	}

	public static bool DespawnPrefix(ToolsRenderAdapterComponent __instance, Entity? actualTool)
	{
		try
		{
			if (__instance._firstPersonToolShadowCaster?.Entity == null)
			{
				return true;
			}
			Entity entity = null;
			if (Owners.TryGetValue(__instance, out Owner value) && value.Tool != null && value.Tool.TryGetTarget(out Entity target) && !EngineCodec.IsDead(target))
			{
				entity = target;
			}
			if (entity == null || entity == actualTool)
			{
				return true;
			}
			if (actualTool != null && !IsClosing(entity))
			{
				ClearMimics(actualTool);
				Interlocked.Increment(ref KeptForNewerTool);
				if (Interlocked.Exchange(ref _loggedKept, 1) == 0)
				{
					GameLog.Instance.Info("world sync: an older hand tool was removed after the newer one was equipped; the newer tool keeps its first-person shadow (0.7.6 fix for the MimicShadowTransformsJob crash; further ones counted)");
				}
				return false;
			}
			ClearMimics(entity);
			Interlocked.Increment(ref OwnerCleared);
			if (Interlocked.Exchange(ref _loggedCleared, 1) == 0)
			{
				GameLog.Instance.Info("world sync: a first-person tool shadow was removed for another tool than the one it copies; its tool's links to it were cleared first (0.7.6 crash fix; further ones counted)");
			}
			return true;
		}
		catch (Exception e)
		{
			Fail(e);
			return true;
		}
	}

	public static void UnregisterPrefix(ToolsComponent __instance, out Entity? __state)
	{
		__state = null;
		try
		{
			__state = __instance.LocalTool;
		}
		catch (Exception)
		{
		}
	}

	public static void UnregisterPostfix(ToolsComponent __instance, Entity tool, Entity? __state)
	{
		try
		{
			if (__state != null && __state != tool && !EngineCodec.IsDead(__state) && __instance.LocalTool == null)
			{
				__instance.LocalTool = __state;
				Interlocked.Increment(ref LocalToolKept);
				if (Interlocked.Exchange(ref _loggedLocal, 1) == 0)
				{
					GameLog.Instance.Info("world sync: an older hand tool was unregistered after the newer one; the newer one stays the character's tool (Keen's UnregisterTool cleared it; 0.7.6, further ones counted)");
				}
			}
		}
		catch (Exception e)
		{
			Fail(e);
		}
	}

	private static bool IsClosing(Entity e)
	{
		try
		{
			return EngineCodec.IsDead(e) || EngineCodec.IsClosing(e.GetSession(), e);
		}
		catch (Exception)
		{
			return true;
		}
	}

	internal static int ClearMimics(Entity tool)
	{
		int num = 0;
		if (EngineCodec.IsDead(tool))
		{
			return 0;
		}
		foreach (Entity descendant in tool.GetDescendants(includeRoot: false))
		{
			if (descendant.Data.TryRemove<ToolsRenderAdapterComponent.TransformMimicTarget>())
			{
				num++;
			}
		}
		return num;
	}

	private static void Fail(Exception e)
	{
		if (Interlocked.Exchange(ref _failed, 1) == 0)
		{
			GameLog.Instance.Warn($"world sync: the tool-shadow guard failed once ({e.GetType().Name}: {e.Message}); the game's own code ran");
		}
	}

	public static string Describe()
	{
		return $"tool shadow guard: kept for the newer tool {KeptForNewerTool}, owner links cleared {OwnerCleared}, LocalTool kept {LocalToolKept}";
	}
}
