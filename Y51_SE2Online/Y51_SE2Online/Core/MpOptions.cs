using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.Core;

public sealed class MpOptions
{
    public int MaxPlayers { get; internal set; } = 8;

    public bool FriendsOnly { get; internal set; } = true;

    public bool UseSteamIp { get; private set; }
    public IPEndPoint SteamListenIp { get; private set; }
    public bool Dedicated { get; internal set; }
    public bool Advertise { get; internal set; } = true;
    public string SessionName { get; internal set; } = "My SE2 session";
    public string Description { get; internal set; } = "";
    public string PublicAddress { get; internal set; } = "";
    public string Password { get; internal set; } = "";
    public bool Pvp { get; internal set; } = true;
    public bool OfflineRaidProtection { get; internal set; } = true;
    public int? PcuLimit { get; internal set; }
    public bool? SunRotation { get; internal set; }
    public double? SunPeriodMinutes { get; internal set; }
    public string JoinPassword { get; internal set; } = "";
    public string ServersDirectory { get; internal set; } = "https://se2-online-servers.yellow51.dev";
    public string SessionsDirectory { get; internal set; } = "https://se2-online-sessions.yellow51.dev";
	public const int DefaultVirtualPort = 7742;

	public const int DefaultIpPort = 7742;

	public const string Prefix = "-y51online:";

	public const int DefaultInterpMs = 60;

	public const double DefaultNametagRange = 200.0;

	internal static bool TestAllowNonLoopbackTcp;

	public bool Probe { get; private set; }

	public bool ProbeBundleSize { get; private set; } = true;

	public bool HostSteam { get; private set; }

	public IPEndPoint? HostIp { get; private set; }

	public ulong JoinSteamId { get; private set; }

	public IPEndPoint? JoinIp { get; private set; }

	public int VirtualPort { get; private set; } = 7742;

	public bool Spike { get; private set; }

	public int BlobBytes { get; private set; } = 5242880;

	public int SendRateMaxBytes { get; private set; } = 8388608;

	public int SendRateMinBytes { get; private set; } = 1048576;

	public bool Sync { get; private set; } = true;

	public bool ClientGame { get; private set; } = true;

	public bool Spectator { get; private set; } = true;

	public bool? ClientMissions { get; private set; }

	public int StateBudgetBytesPerSec { get; private set; } = 1048576;

	public string? PlayerName { get; private set; }

	public int InterpMs { get; private set; } = 60;

	public bool Character { get; private set; } = true;

	public bool Predict { get; private set; } = true;

	public bool SpawnNearHost { get; private set; }

	public bool Identities { get; private set; } = true;

	public GridPermissionMode Grids { get; private set; }

	public ResearchMode Research { get; private set; }

	public bool Rejoin { get; private set; } = true;

	public bool ExitFix { get; private set; } = true;

	public bool AudioFix { get; private set; } = true;

	public bool OnePilot { get; private set; } = true;

	public bool BuildFix { get; private set; } = true;

	public bool ControlFix { get; private set; } = true;

	public bool FastSmooth { get; private set; }

	public bool BodyFilter { get; private set; } = true;

	public bool DeckFrame { get; private set; } = true;

	public bool AdaptiveInterp { get; private set; } = true;

	public Dictionary<string, double> RateCaps { get; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

	public HashSet<ulong> Allow { get; } = new HashSet<ulong>();

	public bool JoinFriends { get; private set; }

	public bool JoinWait { get; private set; }

	public string? JoinFriendFilter { get; private set; }

	public bool Nametags { get; private set; } = true;

	public double NametagRange { get; private set; } = 200.0;

	public bool JoinSteamDynamic
	{
		get
		{
			if (JoinSteamId == 0L && JoinIp == null)
			{
				if (!JoinFriends)
				{
					return JoinWait;
				}
				return true;
			}
			return false;
		}
	}

	public List<string> Recognised { get; } = new List<string>();

	public List<string> Errors { get; } = new List<string>();

	public bool Host
	{
		get
		{
			if (!HostSteam)
			{
				return HostIp != null;
			}
			return true;
		}
	}

	public bool Join
	{
		get
		{
			if (JoinSteamId == 0L && JoinIp == null && !JoinFriends)
			{
				return JoinWait;
			}
			return true;
		}
	}

	public bool Networking
	{
		get
		{
			if (Host || Join)
			{
				return Errors.Count == 0;
			}
			return false;
		}
	}

	public bool Any
	{
		get
		{
			if (!Probe && !Host)
			{
				return Join;
			}
			return true;
		}
	}

	public bool NeedsSteam
	{
		get
		{
			if (UseSteamIp || Dedicated) return true;
			if (!HostSteam && JoinSteamId == 0L)
			{
				if (JoinIp == null)
				{
					if (!JoinFriends)
					{
						return JoinWait;
					}
					return true;
				}
				return false;
			}
			return true;
		}
	}

	public static MpOptions Parse(IEnumerable<string>? args)
	{
		MpOptions mpOptions = new MpOptions();
		if (args == null)
		{
			return mpOptions;
		}
		foreach (string arg in args)
		{
			if (arg == null)
			{
				continue;
			}
			string text = arg.Trim().Trim('"');
			if (!text.StartsWith("-y51online:", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string text2 = text.Substring("-y51online:".Length);
			int num = text2.IndexOf(':');
			string key = ((num < 0) ? text2 : text2.Substring(0, num)).ToLowerInvariant();
			string value = ((num < 0) ? null : text2.Substring(num + 1));
			try
			{
				if (!mpOptions.Apply(key, value))
				{
					mpOptions.Errors.Add("unknown argument '" + text + "'");
				}
				else
				{
					mpOptions.Recognised.Add(text);
				}
			}
			catch (FormatException ex)
			{
				mpOptions.Errors.Add("bad argument '" + text + "': " + ex.Message);
			}
		}
		if (mpOptions.Host && mpOptions.Join)
		{
			mpOptions.Errors.Add("host and join arguments are mutually exclusive");
		}
		if (mpOptions.JoinSteamId != 0L && mpOptions.JoinIp != null)
		{
			mpOptions.Errors.Add("-y51online:join and -y51online:join-ip are mutually exclusive");
		}
		if (mpOptions.JoinIp != null && (mpOptions.JoinFriends || mpOptions.JoinWait))
		{
			mpOptions.Errors.Add("-y51online:join:friends / join:wait and -y51online:join-ip are mutually exclusive");
		}
		if (mpOptions.HostIp != null && !TestAllowNonLoopbackTcp && !IsLoopback(mpOptions.HostIp.Address))
		{
			mpOptions.Errors.Add($"-y51online:host-ip is for same-PC tests and only listens on 127.0.0.1 / ::1 (got {mpOptions.HostIp.Address}); to play over the internet or a LAN, host with -y51online:host (Steam)");
		}
		if (mpOptions.JoinIp != null && !mpOptions.UseSteamIp && !TestAllowNonLoopbackTcp && !IsLoopback(mpOptions.JoinIp.Address))
		{
			mpOptions.Errors.Add($"-y51online:join-ip is for same-PC tests and only connects to 127.0.0.1 / ::1 (got {mpOptions.JoinIp.Address}); join a friend over Steam with -y51online:join");
		}
		if (mpOptions.HostSteam && mpOptions.HostIp != null)
		{
			mpOptions.Errors.Add("-y51online:host and -y51online:host-ip are mutually exclusive (one link per session in this build)");
		}
		return mpOptions;
	}

	private static bool IsLoopback(IPAddress address)
	{
		return IPAddress.IsLoopback(address) || (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()));
	}

	private bool Apply(string key, string? value)
	{
		switch (key)
		{
		case "listen-address":
			SteamListenIp = ServerEndpoint.Parse(value, listen: true);
			return true;
		case "join-address":
			JoinIp = ServerEndpoint.Parse(value);
			UseSteamIp = true;
			return true;
		case "probe":
			Probe = true;
			if (value != null)
			{
				if (!value.Equals("nosize", StringComparison.OrdinalIgnoreCase))
				{
					throw new FormatException("expected -y51online:probe or -y51online:probe:nosize");
				}
				ProbeBundleSize = false;
			}
			return true;
		case "host":
			if (value != null)
			{
				throw new FormatException("-y51online:host takes no value (use -y51online:host-ip:<port> for TCP)");
			}
			HostSteam = true;
			return true;
		case "host-ip":
			HostIp = ((value == null) ? new IPEndPoint(IPAddress.Loopback, 7742) : TcpLink.ParseEndpoint(value, IPAddress.Loopback));
			return true;
		case "join":
			if (value != null && value.Equals("friends", StringComparison.OrdinalIgnoreCase))
			{
				JoinFriends = true;
				return true;
			}
			if (value != null && value.Equals("wait", StringComparison.OrdinalIgnoreCase))
			{
				JoinWait = true;
				return true;
			}
			if (value != null && value.StartsWith("friend:", StringComparison.OrdinalIgnoreCase))
			{
				string text = value.Substring("friend:".Length).Trim().Trim('"');
				if (text.Length == 0 || text.Length > 64)
				{
					throw new FormatException("expected -y51online:join:friend:<part of the friend's Steam name, or its SteamID64>");
				}
				JoinFriends = true;
				JoinFriendFilter = text;
				return true;
			}
			JoinSteamId = ParseSteamId(value);
			return true;
		case "join-ip":
			JoinIp = ((value == null) ? new IPEndPoint(IPAddress.Loopback, 7742) : TcpLink.ParseEndpoint(value, IPAddress.Loopback));
			if (JoinIp.Address.Equals(IPAddress.Any))
			{
				throw new FormatException("join address 0.0.0.0 is not connectable; use 127.0.0.1 or the host's LAN address");
			}
			return true;
		case "vport":
			VirtualPort = ParseInt(value, 0, 65535);
			return true;
		case "spike":
			if (value != null)
			{
				throw new FormatException("-y51online:spike takes no value");
			}
			Spike = true;
			return true;
		case "blob":
			BlobBytes = checked(ParseInt(value, 0, 256) * 1024 * 1024);
			Spike = true;
			return true;
		case "sendrate":
			SendRateMaxBytes = checked(ParseInt(value, 64, 1048576) * 1024);
			return true;
		case "sendratemin":
			SendRateMinBytes = checked(ParseInt(value, 16, 1048576) * 1024);
			return true;
		case "nosync":
			Sync = false;
			return true;
		case "noclientgame":
			ClientGame = false;
			return true;
		case "nospectator":
			Spectator = false;
			return true;
		case "missions":
			ClientMissions = value?.ToLowerInvariant() switch
			{
				"auto" => (bool?)null, 
				"on" => true, 
				"off" => false, 
				_ => throw new FormatException("expected -y51online:missions:auto, :on or :off"), 
			};
			return true;
		case "statebudget":
			StateBudgetBytesPerSec = checked(ParseInt(value, 8, 1048576) * 1024);
			return true;
		case "cap":
		{
			if (string.IsNullOrEmpty(value) || value.IndexOf('=') <= 0)
			{
				throw new FormatException("expected -y51online:cap:<DataTypeShortName>=<Hz>");
			}
			string key2 = value.Substring(0, value.IndexOf('='));
			string text6 = value.Substring(value.IndexOf('=') + 1);
			if (!double.TryParse(text6, NumberStyles.Float, CultureInfo.InvariantCulture, out var result3) || result3 < 0.0 || result3 > 1000.0)
			{
				throw new FormatException("rate '" + text6 + "' must be 0..1000 Hz (0 = never send)");
			}
			RateCaps[key2] = result3;
			return true;
		}
		case "name":
		{
			string text5 = (value ?? "").Trim();
			if (text5.Length == 0 || text5.Length > 32)
			{
				throw new FormatException("expected a player name of 1-32 characters");
			}
			string text2 = text5;
			for (int i = 0; i < text2.Length; i++)
			{
				if (char.IsControl(text2[i]))
				{
					throw new FormatException("player name contains a control character");
				}
			}
			PlayerName = text5;
			return true;
		}
		case "interp":
			InterpMs = ParseInt(value, 0, 500);
			return true;
		case "nocharacter":
			Character = false;
			return true;
		case "predict":
		{
			string text2 = value?.ToLowerInvariant();
			bool predict;
			if (!(text2 == "on"))
			{
				if (!(text2 == "off"))
				{
					throw new FormatException("expected -y51online:predict:on or :off");
				}
				predict = false;
			}
			else
			{
				predict = true;
			}
			Predict = predict;
			return true;
		}
		case "spawn":
		{
			string text2 = value?.ToLowerInvariant();
			bool predict;
			if (!(text2 == "respawn"))
			{
				if (!(text2 == "host"))
				{
					throw new FormatException("expected -y51online:spawn:respawn or :host");
				}
				predict = true;
			}
			else
			{
				predict = false;
			}
			SpawnNearHost = predict;
			return true;
		}
		case "rejoin":
			Rejoin = OnOff(value, "rejoin");
			return true;
		case "exitfix":
			ExitFix = OnOff(value, "exitfix");
			return true;
		case "audiofix":
			AudioFix = OnOff(value, "audiofix");
			return true;
		case "onepilot":
			OnePilot = OnOff(value, "onepilot");
			return true;
		case "buildfix":
			BuildFix = OnOff(value, "buildfix");
			return true;
		case "controlfix":
			ControlFix = OnOff(value, "controlfix");
			return true;
		case "fastsmooth":
			FastSmooth = OnOff(value, "fastsmooth");
			return true;
		case "bodyfilter":
			BodyFilter = OnOff(value, "bodyfilter");
			return true;
		case "deckframe":
			DeckFrame = OnOff(value, "deckframe");
			return true;
		case "adaptive":
			AdaptiveInterp = OnOff(value, "adaptive");
			return true;
		case "identities":
		{
			string text2 = value?.ToLowerInvariant();
			bool predict;
			if (!(text2 == "on"))
			{
				if (!(text2 == "off"))
				{
					throw new FormatException("expected -y51online:identities:on or :off");
				}
				predict = false;
			}
			else
			{
				predict = true;
			}
			Identities = predict;
			return true;
		}
		case "grids":
		{
			string text2 = value?.ToLowerInvariant();
			GridPermissionMode grids;
			if (!(text2 == "team"))
			{
				if (!(text2 == "strict"))
				{
					throw new FormatException("expected -y51online:grids:team or :strict");
				}
				grids = GridPermissionMode.Strict;
			}
			else
			{
				grids = GridPermissionMode.Team;
			}
			Grids = grids;
			return true;
		}
		case "research":
		{
			string text2 = value?.ToLowerInvariant();
			ResearchMode research;
			if (!(text2 == "shared"))
			{
				if (!(text2 == "own"))
				{
					throw new FormatException("expected -y51online:research:shared or :own");
				}
				research = ResearchMode.Own;
			}
			else
			{
				research = ResearchMode.Shared;
			}
			Research = research;
			return true;
		}
		case "nametags":
		{
			string text4 = (value ?? "on").Trim().ToLowerInvariant();
			if (text4 == "on")
			{
				Nametags = true;
			}
			else if (text4 == "off")
			{
				Nametags = false;
			}
			else
			{
				if (!double.TryParse(text4, NumberStyles.Float, CultureInfo.InvariantCulture, out var result2) || !(result2 >= 1.0) || !(result2 <= 100000.0))
				{
					throw new FormatException("expected -y51online:nametags:on, :off or a range in metres (1-100000)");
				}
				Nametags = true;
				NametagRange = result2;
			}
			return true;
		}
		case "allow":
		{
			if (string.IsNullOrEmpty(value))
			{
				throw new FormatException("expected a comma-separated SteamID64 list");
			}
			string[] array = value.Split(',');
			foreach (string s in array)
			{
				Allow.Add(ParseSteamId(s));
			}
			return true;
		}
		default:
			return false;
		}
	}

	public static ulong ParseSteamId(string? s)
	{
		if (string.IsNullOrWhiteSpace(s) || !ulong.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var result))
		{
			throw new FormatException("'" + s + "' is not a SteamID64 for a player or game server");
		}
		ulong accountType = (result >> 52) & 15;
		if ((result >> 56) != 1 || (uint)result == 0 || accountType is not (1 or 3 or 4)
			|| (accountType == 1 && ((result >> 32) & 0xFFFFF) != 1))
		{
			throw new FormatException("'" + s + "' is not a public player or game-server SteamID64");
		}
		return result;
	}

	private static bool OnOff(string? value, string name)
	{
		string text = value?.ToLowerInvariant();
		if (!(text == "on"))
		{
			if (text == "off")
			{
				return false;
			}
			throw new FormatException("expected -y51online:" + name + ":on or :off");
		}
		return true;
	}

	private static int ParseInt(string? s, int min, int max)
	{
		if (s == null || !int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < min || result > max)
		{
			throw new FormatException($"expected an integer in [{min}, {max}]");
		}
		return result;
	}

	public string Describe()
	{
		List<string> list = new List<string>();
		if (Probe)
		{
			list.Add(ProbeBundleSize ? "probe" : "probe(nosize)");
		}
		if (HostSteam)
		{
			list.Add("host(steam)");
		}
		if (HostIp != null)
		{
			list.Add($"host(tcp {HostIp})");
		}
		if (JoinSteamId != 0L)
		{
			list.Add($"join(steam {JoinSteamId})");
		}
		else if (JoinFriends && JoinIp == null)
		{
			list.Add((JoinFriendFilter != null) ? ("join(steam friends matching '" + JoinFriendFilter + "')") : "join(steam friends)");
		}
		else if (JoinWait && JoinIp == null)
		{
			list.Add("join(steam: wait for a Join Game click)");
		}
		if (JoinIp != null)
		{
			list.Add($"join(tcp {JoinIp})");
		}
		if (Host || Join)
		{
			if (NeedsSteam)
			{
				list.Add($"vport={VirtualPort}");
				list.Add($"sendrate={SendRateMinBytes / 1024}..{SendRateMaxBytes / 1024}KiB/s");
			}
			list.Add(Sync ? "sync=on" : "sync=off");
			if (Spike)
			{
				list.Add($"spike blob={BlobBytes / 1048576}MiB");
			}
			if (Join && !ClientGame)
			{
				list.Add("noclientgame");
			}
			if (Join && !Spectator)
			{
				list.Add("nospectator");
			}
			if (Join && ClientMissions.HasValue)
			{
				list.Add("missions=" + (ClientMissions.Value ? "on" : "off"));
			}
			if (Join && PlayerName != null)
			{
				list.Add("name='" + PlayerName + "'");
			}
			if (Join && InterpMs != 60)
			{
				list.Add($"interp={InterpMs}ms");
			}
			if (Join && !Character)
			{
				list.Add("nocharacter");
			}
			if (!Predict)
			{
				list.Add("predict=off");
			}
			if (Join && !Rejoin)
			{
				list.Add("rejoin=off");
			}
			if (!ExitFix)
			{
				list.Add("exitfix=off");
			}
			if (!AudioFix)
			{
				list.Add("audiofix=off");
			}
			if (Host && !OnePilot)
			{
				list.Add("onepilot=off");
			}
			if (!BuildFix)
			{
				list.Add("buildfix=off");
			}
			if (!ControlFix)
			{
				list.Add("controlfix=off");
			}
			if (Join && FastSmooth)
			{
				list.Add("fastsmooth=on");
			}
			if (Join && !BodyFilter)
			{
				list.Add("bodyfilter=off");
			}
			if (Join && !DeckFrame)
			{
				list.Add("deckframe=off");
			}
			if (Join && !AdaptiveInterp)
			{
				list.Add("adaptive=off");
			}
			if (Host && SpawnNearHost)
			{
				list.Add("spawn=host");
			}
			list.Add(Nametags ? ("nametags<=" + NametagRange.ToString(CultureInfo.InvariantCulture) + "m") : "nametags=off");
			if (Host)
			{
				list.Add(Identities ? ("identities=on grids=" + ((Grids == GridPermissionMode.Team) ? "team" : "strict") + " research=" + ((Research == ResearchMode.Shared) ? "shared" : "own")) : "identities=off");
			}
			if (Host)
			{
				list.Add($"statebudget={StateBudgetBytesPerSec / 1024}KiB/s");
			}
			foreach (KeyValuePair<string, double> rateCap in RateCaps)
			{
				list.Add($"cap {rateCap.Key}={rateCap.Value.ToString(CultureInfo.InvariantCulture)}Hz");
			}
			if (Allow.Count > 0)
			{
				list.Add("allow=" + string.Join(",", Allow));
			}

		}
		if (list.Count != 0)
		{
			return string.Join(" ", list);
		}
		return "none";
	}
}
