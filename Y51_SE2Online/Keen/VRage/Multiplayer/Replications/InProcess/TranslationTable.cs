using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;

namespace Keen.VRage.Multiplayer.Replications.InProcess;

internal static class TranslationTable
{
	public static void Translate(ReplayContext __context__, ref Entity value, ReplicationFailureStrategy failureStrategy, ref bool skip)
	{
		value = (Entity)__context__.TranslateObject(value, failureStrategy, ref skip);
	}
}
