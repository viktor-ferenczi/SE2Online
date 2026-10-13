using System;
using System.Globalization;
using System.Linq;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Net;

public static class ConnectString
{
	public static string Build(ulong hostSteamId, int virtualPort)
	{
		string text = "-y51online:join:" + hostSteamId.ToString(CultureInfo.InvariantCulture);
		if (virtualPort != 7742)
		{
			text = text + " -y51online:vport:" + virtualPort.ToString(CultureInfo.InvariantCulture);
		}
		return text;
	}

	/// <summary>"Join Game" on a friend playing on a dedicated server.</summary>
	public static string BuildAddress(string address) => "-y51online:join-address:" + address;

	public static bool TryParseAddress(string? text, out string address)
	{
		const string prefix = "-y51online:join-address:";
		address = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim('"', '\'')).FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Substring(prefix.Length) ?? "";
		return address.Length > 0;
	}

	public static bool TryParse(string? text, out ulong hostSteamId, out int? virtualPort)
	{
		hostSteamId = 0uL;
		virtualPort = null;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string text2 = "-y51online:";
		string[] array = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string text3 = array[i].Trim().Trim(new char[2] { '"', '\'' });
			if (!text3.StartsWith(text2, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string text4 = text3.Substring(text2.Length);
			int result;
			if (text4.StartsWith("join:", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					hostSteamId = MpOptions.ParseSteamId(text4.Substring(5));
				}
				catch (FormatException)
				{
				}
			}
			else if (text4.StartsWith("vport:", StringComparison.OrdinalIgnoreCase) && int.TryParse(text4.Substring(6), NumberStyles.None, CultureInfo.InvariantCulture, out result) && result <= 65535)
			{
				virtualPort = result;
			}
		}
		return hostSteamId != 0;
	}
}
