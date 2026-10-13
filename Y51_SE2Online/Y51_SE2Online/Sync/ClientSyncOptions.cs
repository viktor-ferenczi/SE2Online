using System;

namespace Y51_SE2Online.Sync;

public sealed class ClientSyncOptions
{
	public string PluginVersion = "";

	public string GameVersion = "";

	public string PlayerName = "";

	public string Password = "";

	public Func<SignalKeyDef, string?>? CheckSignal;

	public JoinProgress? Progress;
}
