using System;
using System.Globalization;
using System.Text;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class PlayerIdentities
{
	public const long HotfixIdentity = -1559595548L;

	public const long HostPlayer = 1559595546L;

	public const long NpcIdentity = 1L;

	public const long HostileNpcIdentity = 2L;

	public const long MinDerived = 1048576L;

	public static bool IsNpc(long id) => id == NpcIdentity || id == HostileNpcIdentity;

	public static bool IsHuman(long id) => id != 0L && !IsNpc(id);

	public static bool SameTeam(long a, long b) => IsHuman(a) && IsHuman(b);

	public static string KeyFor(PeerId peer, string recordKey)
	{
		if (!TcpLink.IsSyntheticId(peer.Value) && peer.Value != 0L)
		{
			return "steam:" + peer.Value.ToString(CultureInfo.InvariantCulture);
		}
		return "name:" + recordKey;
	}

	public static long Derive(string key, Func<long, bool>? takenByOther = null)
	{
		int attempt = 0;
		long id;
		while (true)
		{
			// 64-bit FNV-1a, folded to a positive id.
			ulong hash = 14695981039346656037uL;
			byte[] bytes = Encoding.UTF8.GetBytes(attempt == 0 ? key : $"{key}\u0001{attempt}");
			foreach (byte b in bytes)
			{
				hash ^= b;
				hash *= 1099511628211L;
			}
			id = (long)(hash & 0x7FFFFFFFFFFFFFFFL);
			if (id >= MinDerived && id != HotfixIdentity && !IsNpc(id))
			{
				if (takenByOther == null || !takenByOther(id))
				{
					break;
				}
				if (attempt > 64)
				{
					throw new InvalidOperationException($"no free identity for '{key}' after {attempt} attempts");
				}
			}
			attempt++;
		}
		return id;
	}
}
