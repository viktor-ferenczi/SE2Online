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

	public static bool IsNpc(long id)
	{
		if (id != 1)
		{
			return id == 2;
		}
		return true;
	}

	public static bool IsHuman(long id)
	{
		if (id != 0L)
		{
			return !IsNpc(id);
		}
		return false;
	}

	public static bool SameTeam(long a, long b)
	{
		if (IsHuman(a))
		{
			return IsHuman(b);
		}
		return false;
	}

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
		int num = 0;
		long num3;
		while (true)
		{
			ulong num2 = 14695981039346656037uL;
			byte[] bytes = Encoding.UTF8.GetBytes((num == 0) ? key : $"{key}\u0001{num}");
			foreach (byte b in bytes)
			{
				num2 ^= b;
				num2 *= 1099511628211L;
			}
			num3 = (long)(num2 & 0x7FFFFFFFFFFFFFFFL);
			if (num3 >= 1048576 && num3 != -1559595548 && !IsNpc(num3))
			{
				if (takenByOther == null || !takenByOther(num3))
				{
					break;
				}
				if (num > 64)
				{
					throw new InvalidOperationException($"no free identity for '{key}' after {num} attempts");
				}
			}
			num++;
		}
		return num3;
	}
}
