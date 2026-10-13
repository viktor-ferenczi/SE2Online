using System;
using System.Globalization;
using System.Text;

namespace Y51_SE2Online.Net;

public sealed record HostPresence(ushort SyncVersion, string PluginVersion, int VirtualPort, string WorldName)
{
	public const string Key = "y51online_host";

	public const string Tag = "y51online1";

	public const int MaxValueBytes = 255;

	public string Encode()
	{
		string head = $"{Tag};{SyncVersion.ToString(CultureInfo.InvariantCulture)};{VirtualPort.ToString(CultureInfo.InvariantCulture)};{Clean(PluginVersion, 32).Replace(';', ',')};";
		string world = Clean(WorldName, 200);
		while (world.Length > 0 && Encoding.UTF8.GetByteCount(head + world) > MaxValueBytes)
		{
			world = world.Substring(0, world.Length - 1);
		}
		return head + world;
	}

	public static bool TryDecode(string? value, out HostPresence presence)
	{
		presence = null;
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}
		string[] fields = value.Split(';', 5);
		if (fields.Length < 5 || fields[0] != Tag)
		{
			return false;
		}
		if (!ushort.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort syncVersion)
			|| !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int virtualPort)
			|| virtualPort < 0 || virtualPort > 65535)
		{
			return false;
		}
		presence = new HostPresence(syncVersion, fields[3], virtualPort, fields[4]);
		return true;
	}

	public static string Clean(string? s, int max)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		var sb = new StringBuilder(Math.Min(s.Length, max));
		foreach (char c in s)
		{
			if (sb.Length >= max)
			{
				break;
			}
			sb.Append(char.IsControl(c) ? ' ' : c);
		}
		return sb.ToString().Trim();
	}
}
