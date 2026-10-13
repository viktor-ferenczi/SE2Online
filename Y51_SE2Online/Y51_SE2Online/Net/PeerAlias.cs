namespace Y51_SE2Online.Net;

/// <summary>
/// A second simultaneous connection from the same Steam account (two game instances on one PC) gets an alias
/// peer id so it is its own player. Aliases keep the account id in the low 56 bits; top byte 0x20 | slot.
/// </summary>
public static class PeerAlias
{
	private const ulong Low = 0x00FF_FFFF_FFFF_FFFFUL;

	public const int MaxSlots = 31;

	public static ulong Make(ulong steamId, int slot) => (steamId & Low) | ((ulong)(0x20 | slot) << 56);

	public static bool IsAlias(ulong id) => (id >> 56 & 0xE0) == 0x20 && (id >> 56 & 0x1F) != 0;

	/// <summary>The real SteamID64 behind a peer id (individual accounts are universe 1).</summary>
	public static ulong SteamOf(ulong id) => IsAlias(id) ? (id & Low) | 0x0100_0000_0000_0000UL : id;

	public static int SlotOf(ulong id) => IsAlias(id) ? (int)(id >> 56 & 0x1F) : 0;
}
