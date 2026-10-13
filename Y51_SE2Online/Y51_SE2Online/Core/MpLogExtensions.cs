using System;
using System.Collections.Concurrent;

namespace Y51_SE2Online.Core;

public static class MpLogExtensions
{
	private static readonly ConcurrentDictionary<string, byte> Once = new ConcurrentDictionary<string, byte>();

	public static void WarnOnce(this IMpLog log, string key, string message)
	{
		if (Once.TryAdd(key, 0))
		{
			log.Warn(message);
		}
	}

	public static string Bytes(long b)
	{
		if (b >= 10240)
		{
			if (b >= 10485760)
			{
				return $"{(double)b / 1048576.0:0.00} MiB";
			}
			return $"{(double)b / 1024.0:0.0} KiB";
		}
		return $"{b} B";
	}

	public static string Hex(ReadOnlySpan<byte> bytes, int maxBytes = 8)
	{
		return Convert.ToHexString(bytes[..Math.Min(bytes.Length, maxBytes)]).ToLowerInvariant();
	}
}
