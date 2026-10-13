using Y51_SE2Online.Core;

namespace Y51_SE2Online.Net;

public static class JoinTargetRule
{
	public static ulong SteamConnectId(ulong runtimeTarget, MpOptions o)
	{
		if (runtimeTarget == 0L)
		{
			return o.JoinSteamId;
		}
		return runtimeTarget;
	}
}
