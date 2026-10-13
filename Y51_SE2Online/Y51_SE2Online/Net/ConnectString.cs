using System;
using System.Globalization;
using System.Linq;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Net;

public static class ConnectString
{
	public static string Build(ulong hostSteamId, int virtualPort)
	{
		string args = "-y51online:join:" + hostSteamId.ToString(CultureInfo.InvariantCulture);
		if (virtualPort != 7742)
		{
			args += " -y51online:vport:" + virtualPort.ToString(CultureInfo.InvariantCulture);
		}
		return args;
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
		string prefix = "-y51online:";
		foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
		{
			string arg = token.Trim().Trim('"', '\'');
			if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string option = arg.Substring(prefix.Length);
			if (option.StartsWith("join:", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					hostSteamId = MpOptions.ParseSteamId(option.Substring(5));
				}
				catch (FormatException)
				{
				}
			}
			else if (option.StartsWith("vport:", StringComparison.OrdinalIgnoreCase) && int.TryParse(option.Substring(6), NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port <= 65535)
			{
				virtualPort = port;
			}
		}
		return hostSteamId != 0;
	}
}
