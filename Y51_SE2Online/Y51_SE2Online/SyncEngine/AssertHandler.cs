using Keen.VRage.Core.Diagnostics;
using Keen.VRage.Library.Diagnostics;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The Windows platform layer installs a handler that logs a failed game assertion and carries on; other platforms
/// (SE2 running natively on Linux through Pulsar) keep the default that throws, so every assertion aborted whatever was
/// running, e.g. building a replicated entity. This is the Windows handler without its dialog.
/// </summary>
internal sealed class AssertHandler : DiagnosticHandlerBase
{
	internal static void Install()
	{
		if (Assert.Handler is not FatalDiagnosticHandler) return;
		Assert.Handler = new AssertHandler();
		GameLog.Instance.Info("game assertions now log and continue (the platform left them fatal)");
	}

	protected override void ShowDialog(AssertionRecord record, string traceString, bool sourceFrame, ref bool abort) { }
}
