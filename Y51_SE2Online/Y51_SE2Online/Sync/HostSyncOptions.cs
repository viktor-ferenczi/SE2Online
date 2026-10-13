using System;

namespace Y51_SE2Online.Sync;

public sealed class HostSyncOptions
{
	public int MaxPlayers = 8;
	public bool HostCountsAsPlayer = true;
	public string PluginVersion = "";

	public string GameVersion = "";

	public int StateBudgetBytesPerSec = 1048576;

	public RateCapPolicy Caps = new RateCapPolicy();

	public int StatusIntervalMs = 10000;

	public int StateEmitIntervalMs = 8;

	public Func<string, (int TypeId, int Size)?>? ResolveType;

	public Func<SignalKeyDef, string?>? CheckSignal;

	public HostSettings Settings = new HostSettings(Identities: false, GridPermissionMode.Team, ResearchMode.Shared);

	public string HostName = "Host";

	public bool LookTotals = true;

	public AccessList Access = new AccessList();

	public string Password = "";

	public string SessionName = "";

	public bool Dedicated;

	public ulong HostSteamId;

	public long HostIdentity;

	public Func<long, string?>? FactionTagOf;

	public Func<SafeZoneInfo[]>? SafeZones;

	public bool Pvp = true;

	public bool OfflineRaidProtection = true;

	/// <summary>Per-player PCU limit: null keeps the world's value, 0 is unlimited.</summary>
	public int? PcuLimit;

	public int PcuInForce;

	/// <summary>Sun settings from the host or server config: null keeps the world's own.</summary>
	public bool? SunRotation;

	public double? SunPeriodMinutes;

	public (bool Rotation, double PeriodMinutes) SunInForce;

	public Action<bool>? OfflineRaidChanged;

	public Action<bool>? PvpChanged;

}
