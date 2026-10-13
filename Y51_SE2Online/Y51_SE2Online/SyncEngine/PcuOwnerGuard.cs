using System;
using System.Threading;
using Keen.Game2.Client.GameSystems.PCU;
using Keen.Game2.Simulation.GameSystems.Ownership;

namespace Y51_SE2Online.SyncEngine;

internal static class PcuOwnerGuard
{
	public static long Redirected;

	public static void Prefix(PCUClientSessionComponent __instance, ref IdentityId owner)
	{
		try
		{
			IdentityId? identityId = __instance._players?.LocalPlayerIdentity;
			if (identityId.HasValue)
			{
				IdentityId valueOrDefault = identityId.GetValueOrDefault();
				if ((long)owner != (long)valueOrDefault)
				{
					owner = valueOrDefault;
					Interlocked.Increment(ref Redirected);
				}
			}
		}
		catch (Exception)
		{
		}
	}
}
