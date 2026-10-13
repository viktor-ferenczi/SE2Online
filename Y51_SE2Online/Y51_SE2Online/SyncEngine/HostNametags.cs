using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class HostNametags
{
	public static readonly PlayerRoster Roster = new PlayerRoster();

	public static volatile string HostName = "Host";
}
