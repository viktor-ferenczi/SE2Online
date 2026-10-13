using System;
using System.Reflection;
using System.Threading;
using Y51_SE2Online.Core;

namespace Y51_SE2Online;

internal static class RenderExitGuard
{
	private static FieldInfo? _copyList;

	private static Func<bool>? _earlyExit;

	internal static Func<bool>? EarlyExitOverride;

	private static int _logged;

	public static long Abandoned;

	public static void Prepare()
	{
		_copyList = EngineMembers.Field(Pin("render-streaming-copylist"));
		MethodInfo method = EngineMembers.Property(Pin("render-early-exit")).GetGetMethod(nonPublic: true) ?? throw new MissingMemberException("CoreSystems.EarlyTaskExit has no getter");
		_earlyExit = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), method);
		static EngineMember Pin(string id)
		{
			return Array.Find(HookTargets.Reflected, (EngineMember m) => m.Id == id) ?? throw new MissingMemberException(id);
		}
	}

	public static void DestroyPrefix(object __instance)
	{
		try
		{
			Func<bool> func = EarlyExitOverride ?? _earlyExit;
			if (func != null && !(_copyList == null) && func() && _copyList.GetValue(__instance) != null)
			{
				_copyList.SetValue(__instance, null);
				Interlocked.Increment(ref Abandoned);
				if (Interlocked.Exchange(ref _logged, 1) == 0)
				{
					GameLog.Instance.Info("exit: render engine shutting down with the texture streamer's copy list still open; it is dropped instead of recording barriers on textures that were just released (Keen 2.4 exit crash 0xC0000005 in ResourceBarrier, RenderExitGuard)");
				}
			}
		}
		catch (Exception)
		{
		}
	}
}
