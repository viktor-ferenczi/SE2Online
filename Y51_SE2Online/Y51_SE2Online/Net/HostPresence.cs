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
		string text = $"{"y51online1"};{SyncVersion.ToString(CultureInfo.InvariantCulture)};{VirtualPort.ToString(CultureInfo.InvariantCulture)};{Clean(PluginVersion, 32).Replace(';', ',')};";
		string text2 = Clean(WorldName, 200);
		while (text2.Length > 0 && Encoding.UTF8.GetByteCount(text + text2) > 255)
		{
			text2 = text2.Substring(0, text2.Length - 1);
		}
		return text + text2;
	}

	public static bool TryDecode(string? value, out HostPresence presence)
	{
		presence = null;
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}
		string[] array = value.Split(';', 5);
		if (array.Length < 5 || array[0] != "y51online1")
		{
			return false;
		}
		if (!ushort.TryParse(array[1], NumberStyles.None, CultureInfo.InvariantCulture, out var result) || !int.TryParse(array[2], NumberStyles.None, CultureInfo.InvariantCulture, out var result2) || result2 < 0 || result2 > 65535)
		{
			return false;
		}
		presence = new HostPresence(result, array[3], result2, array[4]);
		return true;
	}

	public static string Clean(string? s, int max)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder(Math.Min(s.Length, max));
		foreach (char c in s)
		{
			if (stringBuilder.Length >= max)
			{
				break;
			}
			stringBuilder.Append(char.IsControl(c) ? ' ' : c);
		}
		return stringBuilder.ToString().Trim();
	}
}
