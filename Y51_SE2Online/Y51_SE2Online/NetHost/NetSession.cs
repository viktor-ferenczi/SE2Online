using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Spike;
using Y51_SE2Online.Steam;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;
using Steamworks;

namespace Y51_SE2Online.NetHost;

internal sealed class NetSession
{
	private static NetSession? _current;

	private readonly MpOptions _o;

	private readonly IMpLog _log;

	private readonly string _pluginVersion;

	private readonly string _gameVersion;

	private readonly ManualResetEventSlim _steamReady = new ManualResetEventSlim(initialState: false);

	private readonly Thread _thread;

	private volatile bool _stop;

	private string _stopReason = "stopped";

	private long _lastDetailedMs;

	private HostAdvertiser? _advertiser;
	private DirectoryPublisher _directory;
	private ulong _localIdentity;
	private long _nextDirectoryMs;

	private FriendFinder? _friends;

	private ulong _joinTarget;

	private long _nextFriendScanMs;

	private long _nextPresenceMs;

	private string _lastFriendsLine = "";

	private bool _joinTargetLocked;

	private PlayerNamesPublisher? _names;

	private ClientSession? _client;

	private volatile MpTransport? _transport;

	private volatile HostSession? _host;

	private volatile string? _rejoinWhy;

	private bool _rejoinClosing;

	private long _rejoinSinceMs;

	private string _lastLoss = "connection closed";

	private const int RejoinCloseTimeoutMs = 5000;

	private const int RejoinFirstAttemptMs = 2000;

	private string _playerName = "";

	private Action<string> _rejoinHandler;

	private const int FriendScanMs = 10000;

	private const int PresenceUpdateMs = 2000;

	private long _lastNoHostLogMs;

	private string _targetSource = "";

	private bool _waitLogged;

	public static NetSession? Current => _current;

	public bool HasStopped => !_thread.IsAlive;

	public static bool ClearStopped()
	{
		if (_current != null && !_current.HasStopped) return false;
		_current = null;
		return true;
	}

	public IReadOnlyList<HostSession.PlayerRecord> Players => _host?.Players() ?? Array.Empty<HostSession.PlayerRecord>();
	public bool IsHost => _o.Host;
	public string Failure { get; private set; }
	public string DirectoryError => _directory?.Error;
	public ulong LocalIdentity => _localIdentity;
	public bool TransportReady => _transport != null && !HasStopped;
	public HostSession? Host => _host;
	public ClientSession? Client => _client;
	public bool IsDedicated => _o.Dedicated;
	public string SessionName => _o.SessionName;

	/// <summary>Kick/ban/admin action by the local host or dedicated console; joiner admins send AdminRequest instead.</summary>
	public string LocalAdmin(AdminRequest request)
	{
		HostSession host = _host;
		if (!_o.Host || host == null) return "Not hosting.";
		return host.LocalAdmin(request);
	}

	public void Kick(ulong id, string reason = "") => LocalAdmin(new AdminRequest(AdminAction.Kick, id, reason));

	internal static string AccessPath(bool dedicated) => dedicated && Y51_SE2Online.Dedicated.DedicatedRuntime.Config != null
		? System.IO.Path.Combine(Y51_SE2Online.Dedicated.DedicatedRuntime.Config.Directory, "access.json")
		: PluginPaths.Data("Y51_SE2Online.access.json");


	private NetSession(MpOptions o, IMpLog log, string pluginVersion, string gameVersion)
	{
		_o = o;
		_log = log;
		_pluginVersion = pluginVersion;
		_gameVersion = gameVersion;
		_thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "Y51_SE2Online-Net"
		};
	}

	public static void Start(MpOptions o, IMpLog log, string pluginVersion, string gameVersion)
	{
		if (_current == null)
		{
			_current = new NetSession(o, log, pluginVersion, gameVersion);
			_current._thread.Start();
			log.Info("net: thread started (" + o.Describe() + ")" + (o.NeedsSteam ? ", waiting for Steam" : ""));
			if (o.Join && o.Sync)
			{
				ClientWorld.Progress.Set(o.NeedsSteam ? JoinStage.WaitingForSteam : JoinStage.Connecting, MpClock.NowMs());
			}
		}
	}

	/// <summary>Connected links with a display name: joiners when hosting, the host when joined.</summary>
	public List<(string Name, PeerStats Stats)> LinkStats()
	{
		var list = new List<(string, PeerStats)>();
		MpTransport t = _transport;
		if (t == null || _stop) return list;
		HostSession host = _host;
		foreach (PeerId peer in t.Peers)
		{
			if (!t.TryGetStats(peer, out PeerStats stats)) continue;
			string name = host?.Players().FirstOrDefault(p => p.Peer == peer)?.Name ?? stats.RemoteName;
			list.Add((name, stats));
		}
		return list;
	}

	public string DescribeStatus()
	{
		StringBuilder sb = new StringBuilder();
		sb.Append(_o.Host ? "Hosting" : (_o.Join ? "Joining" : "Transport only"));
		sb.Append(_o.Dedicated ? " (Steam dedicated server)" : (_o.UseSteamIp ? $" (Steam IP {_o.JoinIp})" : (_o.HostIp != null ? $" (local sandbox {_o.HostIp})" : (_o.JoinIp != null ? $" (local sandbox {_o.JoinIp})" : " (Steam)"))));
		if (_stop)
		{
			sb.Append(" - stopped: ").Append(_stopReason);
			return sb.ToString();
		}
		MpTransport? t = _transport;
		if (t == null)
		{
			sb.Append(_steamReady.IsSet ? " - starting" : " - waiting for Steam");
			return sb.ToString();
		}
		HostSession? host = _host;
		if (host != null)
		{
			IReadOnlyList<HostSession.PlayerRecord> players = host.Players();
			sb.Append($"\nPlayers: {players.Count}");
			foreach (HostSession.PlayerRecord r in players)
			{
				sb.Append('\n').Append(r.Name).Append(": ");
				if (!r.Peer.HasValue)
				{
					sb.Append("offline");
					continue;
				}
				sb.Append(host.StatusOf(r.Peer.Value)?.ToString() ?? "gone");
				if (t.TryGetStats(r.Peer.Value, out PeerStats st) && st.LastRttMs >= 0.0)
				{
					sb.Append($" {st.LastRttMs:0} ms");
				}
			}
			return sb.ToString();
		}
		ClientSession? client = _client;
		if (client != null)
		{
			sb.Append("\nStage: ").Append(ClientWorld.Progress.Stage).Append(" / ").Append(client.CurrentPhase);
			sb.Append($"\nPlayers: {ClientSession.DescribePlayers(client.Players)}");
			if (client.Status.Version != 0)
			{
				sb.Append($"\nPCU {client.Status.Pcu}/{client.Status.MaxPcu}");
			}
		}
		foreach (PeerId peer in t.Peers)
		{
			if (t.TryGetStats(peer, out PeerStats st))
			{
				sb.Append($"\nLink {st.RemoteName}: {(st.Ready ? "ready" : "handshake")}");
				if (st.LastRttMs >= 0.0)
				{
					sb.Append($", {st.LastRttMs:0} ms, lost {st.StateLost}");
				}
			}
		}
		return sb.ToString();
	}

	public void SignalSteamReady(string source)
	{
		if (!_steamReady.IsSet)
		{
			_log.Info("net: Steam initialised (" + source + ")");
			_steamReady.Set();
		}
	}

	public void Stop(string reason, int timeoutMs = 3000)
	{
		if (!_stop)
		{
			_stopReason = reason;
			_stop = true;
			_steamReady.Set();
			if (Thread.CurrentThread != _thread && _thread.IsAlive && !_thread.Join(timeoutMs) && timeoutMs > 0)
			{
				_log.Warn($"net: thread did not stop within {timeoutMs} ms");
			}
		}
	}

	private void Run()
	{
		bool flag = false;
		try
		{
			if (OperatingSystem.IsWindows())
			{
				flag = TimeBeginPeriod(1u) == 0;
				_log.Info("net: timer resolution 1 ms " + (flag ? "requested" : "refused") + " for the net loop");
			}
		}
		catch (Exception ex)
		{
			_log.Warn("net: timeBeginPeriod failed (" + ex.GetType().Name + "); the net loop polls at the system timer rate");
		}
		try
		{
			RunCore();
		}
		catch (Exception value)
		{
			Failure = value.Message;
			_log.Error($"net: thread crashed, networking disabled (game unaffected): {value}");
		}
		finally
		{
			if (_rejoinHandler != null) ClientWorld.RejoinRequested -= _rejoinHandler;
			_client?.Detach();
			try
			{
				if (flag)
				{
					TimeEndPeriod(1u);
				}
			}
			catch (Exception)
			{
			}
		}
	}

	[DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
	private static extern uint TimeBeginPeriod(uint ms);

	[DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
	private static extern uint TimeEndPeriod(uint ms);

	private bool WaitForSteam()
	{
		long num = MpClock.NowMs();
		bool flag = false;
		while (!_steamReady.Wait(1000))
		{
			if (!flag && MpClock.NowMs() - num > 120000)
			{
				flag = true;
				_log.Warn("net: still waiting for Steam after 2 min (is Steam running and the game launched through Steam?)");
			}
		}
		return !_stop;
	}

	private (ulong Id, string Name) Identity()
	{
		try
		{
			if (_steamReady.IsSet)
			{
				return (Id: SteamUser.GetSteamID().m_SteamID, Name: SteamFriends.GetPersonaName());
			}
		}
		catch (Exception)
		{
		}
		return (Id: 0uL, Name: Environment.UserName);
	}

	private void RunCore()
	{
		SteamLink steamLink = null;
		using var dedicatedSteam = _o.Dedicated ? new SteamDedicatedSession() : null;
		TcpLink tcpLink = null;
		ulong num;
		string text;
		IPacketLink packetLink;
		if (_o.NeedsSteam)
		{
			if (!_o.Dedicated && !WaitForSteam())
			{
				_log.Info("net: stopped before Steam was ready (" + _stopReason + ")");
				return;
			}
			if (_o.Dedicated)
			{
				dedicatedSteam.Initialize(_o, _gameVersion, () => _stop);
				num = dedicatedSteam.SteamId;
				text = _o.SessionName;
			}
			else try
			{
				num = SteamUser.GetSteamID().m_SteamID;
				text = SteamFriends.GetPersonaName();
			}
			catch (InvalidOperationException ex)
			{
				_log.Error("net: Steam API not available (" + ex.Message + "); networking disabled");
				return;
			}
			_log.Info($"net: Steam user '{text}' SteamID64={num}");
			if (_o.HostSteam && !_o.Dedicated)
			{
				_log.Info($"HOST READY: join through Multiplayer using Steam identity {num} or the advertised listing.");
			}
			if (!_o.Dedicated) SteamNetworkingUtils.InitRelayNetworkAccess();
			_log.Info("net: relay network init requested; " + SteamLink.RelayStatus(_o.Dedicated));
			steamLink = new SteamLink(_log, _o.VirtualPort, _o.SendRateMinBytes, _o.SendRateMaxBytes, (ulong id) => (_o.Allow.Count == 0 || _o.Allow.Contains(id)) && (!_o.FriendsOnly || SteamFriends.GetFriendRelationship(new CSteamID(id)) == EFriendRelationship.k_EFriendRelationshipFriend), _o.SteamListenIp, _o.Dedicated);
			steamLink.Start(_o.Host);
			packetLink = steamLink;
			SteamJoinStart(num, text);
			if (_o.Host && _o.Advertise) _directory = new DirectoryPublisher(new DirectoryClient(_o.Dedicated ? _o.ServersDirectory : _o.SessionsDirectory), message => _log.WarnOnce("directory-publish", message));
		}
		else
		{
			(num, text) = Identity();
			if (_o.Join)
			{
				text += " (tcp)";
			}
			tcpLink = new TcpLink("tcp", _log);
			if (_o.HostIp != null)
			{
				tcpLink.Listen(_o.HostIp);
				_log.Info("HOST READY (local sandbox): join through Multiplayer using Join local test.");
			}
			packetLink = tcpLink;
		}
		if (_o.PlayerName != null)
		{
			text = _o.PlayerName;
			_log.Info("net: player name '" + text + "'");
		}
		_localIdentity = num;
		MpTransport mpTransport = null;
		try
		{
			mpTransport = new MpTransport(packetLink, new TransportOptions
			{
				LocalId = num,
				Role = ((!_o.Host) ? ((byte)1) : ((byte)0)),
				LocalName = text,
				PluginVersion = _pluginVersion,
				GameVersion = _gameVersion
			}, _log);
			SpikeDriver spike = (_o.Spike ? new SpikeDriver(mpTransport, _log, MpClock.NowMs, _o.BlobBytes, num) : null);
			HostSession host = null;
			ClientSession clientSession = null;
			if (_o.Sync)
			{
				if (_o.Host && HostWorld.Current != null)
				{
					host = new HostSession(mpTransport, _log, HostWorld.Current, HostWorld.Current.Keys, new HostSyncOptions
					{
						PluginVersion = _pluginVersion,
						GameVersion = _gameVersion,
						StateBudgetBytesPerSec = _o.StateBudgetBytesPerSec,
						Caps = new RateCapPolicy(_o.RateCaps),
						ResolveType = EngineCodec.ResolveType,
						CheckSignal = CommandLayouts.Check,
						Settings = new HostSettings(_o.Identities, _o.Grids, _o.Research),
						HostName = text,
						LookTotals = _o.Predict,
						MaxPlayers = _o.MaxPlayers,
						HostCountsAsPlayer = !_o.Dedicated,
						Access = AccessList.Load(AccessPath(_o.Dedicated)),
						Password = _o.Password ?? "",
						SessionName = _o.SessionName,
						Dedicated = _o.Dedicated,
						HostSteamId = _o.Dedicated ? 0 : num,
						HostIdentity = PlayerIdentities.HotfixIdentity,
						FactionTagOf = HostWorld.Current.FactionTagOf,
						SafeZones = () => HostWorld.Current?.SafeZones,
						Pvp = _o.Pvp,
						PvpChanged = TurretRelations.SetPvp,
						OfflineRaidProtection = _o.OfflineRaidProtection,
						PcuLimit = _o.PcuLimit,
						SunRotation = _o.SunRotation,
						SunPeriodMinutes = _o.SunPeriodMinutes,
						OfflineRaidChanged = on => PvpRules.OfflineRaid = on
					});
					TurretRelations.SetPvp(host.Pvp);
					PvpRules.OfflineRaid = host.OfflineRaidProtection;
					_log.Info("net: joiners' turning " + (_o.Predict ? "applied from their running look totals (exact, matches their look prediction)" : "applied from the newest ControlData per tick (-y51online:predict:off)"));
					HostWorld.Current.Bind(host);
				}
				else if (_o.Join && ClientWorld.Current != null)
				{
					_playerName = text;
					clientSession = NewClientSession(mpTransport);
					_log.Info("net: remote motion smoothing " + ((_o.InterpMs > 0) ? $"on, {_o.InterpMs} ms playout delay" : "off (-y51online:interp:0)"));
					_log.Info("net: look prediction " + (_o.Predict ? "on: the own character turns locally the same frame, the host's orientation only corrects it" : "off (-y51online:predict:off): the own character turns when the host's state comes back"));
					_log.Info(_o.Rejoin ? "net: rejoin without restarting: on (lost connection: back to the main menu and join again by itself; after leaving: F3 at the main menu or 'Join Game' in Steam)" : "net: rejoin without restarting: off (-y51online:rejoin:off): restart the game to join again");
					ClientWorld.Current.Bind(clientSession);
					_rejoinHandler = why => _rejoinWhy = why;
					ClientWorld.RejoinRequested += _rejoinHandler;
				}
			}
			if (!_o.Sync)
			{
				_log.Info("net: -y51online:nosync: transport only, no world sync");
			}
			if (host != null && _o.Nametags)
			{
				_names = new PlayerNamesPublisher(mpTransport, _log, HostNametags.Roster, (PeerId p) => host.StatusOf(p) == HostSession.ClientStatus.Synced);
			}
			if (_o.Host)
			{
				HostNametags.HostName = text;
			}
			_client = clientSession;
			_host = host;
			_transport = mpTransport;
			Loop(steamLink, tcpLink, mpTransport, spike, host);
		}
		finally
		{
			try
			{
				_directory?.StopAsync().GetAwaiter().GetResult();
				_advertiser?.Clear(_stopReason);
				_friends?.Dispose();
			}
			catch (Exception)
			{
			}
			try
			{
				bool num2 = mpTransport != null && mpTransport.Peers.Count > 0;
				mpTransport?.DisconnectAll(1004, _stopReason);
				mpTransport?.Poll();
				if (num2)
				{
					Thread.Sleep(500);
				}
			}
			catch (Exception ex4)
			{
				_log.Warn("net: error while disconnecting: " + ex4.Message);
			}
			packetLink.Dispose();
			_log.Info("net: stopped (" + _stopReason + ")");
		}
	}

	private void UpdateDirectory(long now)
	{
		if (_directory == null || now < _nextDirectoryMs) return;
		_nextDirectoryMs = now + 1000;
		var world = HostWorld.Current;
		DirectoryListing listing = null;
		if (world?.WorldLoaded == true)
		{
			listing = new DirectoryListing
			{
				Kind = _o.Dedicated ? "dedicated" : "session", Name = _o.SessionName, WorldName = (world.WorldName ?? "World").Length > 100 ? world.WorldName.Substring(0, 100) : world.WorldName ?? "World",
				Description = _o.Description, SteamId = _localIdentity.ToString(System.Globalization.CultureInfo.InvariantCulture), Address = _o.PublicAddress,
				VirtualPort = _o.VirtualPort, Players = (_host?.Players().Count(p => p.Peer.HasValue) ?? 0) + (_o.Dedicated ? 0 : 1), MaxPlayers = _o.MaxPlayers,
				PluginVersion = _pluginVersion, GameVersion = _gameVersion, SyncVersion = SyncProtocol.Version, FriendsOnly = _o.FriendsOnly,
				Tags = new[] { string.IsNullOrEmpty(_o.Password) ? null : "Password", _host?.Access.WhitelistOnly == true ? "Whitelist" : null }.Where(t => t != null).ToArray()
			};
		}
		_directory.Tick(listing, now);
	}

	private ClientSession NewClientSession(MpTransport transport)
	{
		ClientSession clientSession = new ClientSession(transport, _log, new ClientSyncOptions
		{
			PluginVersion = _pluginVersion,
			GameVersion = _gameVersion,
			PlayerName = _playerName,
			Password = _o.JoinPassword ?? "",
			CheckSignal = CommandLayouts.Check,
			Progress = ClientWorld.Progress
		}, EngineCodec.ResolveType);
		clientSession.Smoother.DelayMs = _o.InterpMs;
		clientSession.Smoother.Adaptive = _o.AdaptiveInterp;
		return clientSession;
	}

	private void Loop(SteamLink? steam, TcpLink? tcp, MpTransport transport, SpikeDriver? spike, HostSession? host)
	{
		Dictionary<PeerId, long> connectedAt = new Dictionary<PeerId, long>();
		HashSet<PeerId> detailedDone = new HashSet<PeerId>();
		transport.PeerConnected += (PeerId p) =>
		{
			connectedAt[p] = MpClock.NowMs();
		};
		transport.PeerDisconnected += (PeerId p, DisconnectInfo info) =>
		{
			connectedAt.Remove(p);
			detailedDone.Remove(p);
			_lastLoss = DescribeLoss(info);
		};
		long num = MpClock.NowMs();
		long num2 = 0L;
		string text = "";
		uint num3 = 0u;
		if (steam == null)
		{
			_joinTarget = 0uL;
		}
		long num4 = MpClock.NowMs() + ((steam != null) ? 2000 : 500);
		int num5 = 0;
		int num6 = ((steam != null) ? 20 : 400);
		int num7 = ((steam != null) ? 15000 : 3000);
		long num8 = MpClock.NowMs();
		Stopwatch stopwatch = new Stopwatch();
		long val = 0L;
		bool flag = false;
		bool flag2 = false;
		Stopwatch iteration = new Stopwatch();
		while (!_stop)
		{
			iteration.Restart();
			long num9 = MpClock.NowMs();
			if (steam != null && num9 - num2 >= 2000)
			{
				num2 = num9;
				string text2 = SteamLink.RelayStatus(_o.Dedicated);
				if (text2 != text)
				{
					_log.Info("net: " + text2);
					text = text2;
				}
			}
			ClientSession clientSession = _client;
			bool flag3 = transport.Peers.Count > 0;
			bool flag4 = num3 != 0 && (steam?.HasConnection(num3) ?? tcp.HasConnection(num3));
			string rejoinWhy = _rejoinWhy;
			if (rejoinWhy != null && clientSession != null)
			{
				if ((flag3 | flag4) && num9 - _rejoinSinceMs < 5000)
				{
					if (!_rejoinClosing)
					{
						_rejoinClosing = true;
						_rejoinSinceMs = num9;
						foreach (PeerId peer in transport.Peers)
						{
							transport.Disconnect(peer, 1000, "joining again");
						}
						if (!flag3 & flag4)
						{
							if (steam != null)
							{
								steam.Close(num3, 1000, "joining again", linger: false);
							}
							else
							{
								tcp.Close(num3, 1000, "joining again", linger: false);
							}
						}
						_log.Info("net: fresh join requested (" + rejoinWhy + "); closing the previous connection first");
					}
				}
				else
				{
					clientSession.Detach();
					clientSession = (_client = NewClientSession(transport));
					ClientWorld.Current?.Bind(clientSession);
					flag = (flag2 = false);
					num3 = 0u;
					num5 = 0;
					num8 = num9;
					num4 = num9 + 2000;
					if (steam != null && !_joinTargetLocked)
					{
						SetJoinTarget(0uL, "", locked: false);
						_nextFriendScanMs = 0L;
					}
					_rejoinWhy = null;
					_rejoinClosing = false;
					IMpLog log = _log;
					string text3 = $"net: FRESH JOIN #{ClientWorld.Progress.JoinNumber} ({rejoinWhy}): new world sync session; ";
					string? text4;
					if (steam == null)
					{
						text4 = $"connecting to TCP host {_o.JoinIp} in {2} s";
					}
					else
					{
						text4 = ((_joinTarget != 0L) ? $"connecting to host {_joinTarget} in {2} s" : "looking for a hosting friend");
					}
					log.Info(text3 + text4);
				}
				flag3 = transport.Peers.Count > 0;
				flag4 = num3 != 0 && (steam?.HasConnection(num3) ?? tcp.HasConnection(num3));
			}
			if (flag3)
			{
				flag = true;
			}
			if ((_o.Join & flag) && !flag3 && !flag4 && clientSession != null && !flag2 && _rejoinWhy == null)
			{
				flag2 = true;
				num4 = long.MaxValue;
				if (clientSession.LeftOnPurpose)
				{
					_log.Info("net: left the host (quit to the main menu); " + (_o.Rejoin ? "F3 at the main menu (or 'Join Game' in Steam) joins again" : "restart the game to join again"));
				}
				else if (clientSession.CurrentPhase == ClientSession.Phase.Rejected)
				{
					_log.Info("net: the host refused this join and closed the connection; " + (_o.Rejoin ? "F3 at the main menu tries again" : "restart the game to try again"));
				}
				else if (_o.Rejoin)
				{
					_log.Warn("net: lost the host after joining (" + _lastLoss + "); the game joins again with a fresh world sync (back to the main menu first if a world is loaded)");
				}
				else
				{
					_log.Warn("net: lost the host after joining (" + _lastLoss + "); not reconnecting automatically (-y51online:rejoin:off). Quit to the main menu and restart the game to rejoin.");
				}
				if (!clientSession.LeftOnPurpose)
				{
					ClientWorld.ConnectionLost(clientSession, _lastLoss);
				}
			}
			if (steam != null && !_o.UseSteamIp)
			{
				ulong joinTarget = _joinTarget;
				SteamJoinTick(num9, flag3 | flag4 | flag, host);
				if (_joinTarget != joinTarget && !flag3 && !flag4)
				{
					num5 = 0;
					num4 = num9;
				}
			}
			_names?.Tick();
			if (_o.Join && !flag3 && !flag4 && num9 >= num4 && (clientSession == null || clientSession.CurrentPhase != ClientSession.Phase.Rejected) && (steam == null || _joinTarget != 0L || _o.UseSteamIp))
			{
				bool flag5 = steam == null || (_o.UseSteamIp ? SteamLink.NetworkConfigReady() : SteamLink.RelayReady());
				if (flag5 || num9 - num8 > 20000)
				{
					if (num5 >= num6)
					{
						if (num5 == num6)
						{
							_log.Error($"net: giving up after {num6} connection attempts; " + (_o.Rejoin ? "F3 at the main menu tries again" : "restart the game to retry"));
							ClientWorld.Progress.SetAttempt(num6, num6);
							ClientWorld.Progress.Set(JoinStage.GaveUp, num9);
						}
						num5++;
						num4 = long.MaxValue;
					}
					else
					{
						num5++;
						ClientWorld.Progress.SetAttempt(num5, num6);
						JoinStage stage = ClientWorld.Progress.Stage;
						if ((stage <= JoinStage.LookingForHost || stage == JoinStage.GaveUp) ? true : false)
						{
							ClientWorld.Progress.Set(JoinStage.Connecting, num9, flag5 ? null : (_o.UseSteamIp ? "Steam authentication configuration loading" : "relay starting"));
						}
						if (steam != null)
						{
							ulong num10 = JoinTargetRule.SteamConnectId(_joinTarget, _o);
							_log.Info($"net: connect attempt {num5} to host {num10}{(flag5 ? "" : " (relay network not ready yet)")}");
							num3 = _o.UseSteamIp ? steam.Connect(_o.JoinIp) : steam.Connect(num10);
						}
						else
						{
							if (num5 == 1 || num5 % 10 == 0)
							{
								_log.Info($"net: connect attempt {num5} to TCP host {_o.JoinIp}");
							}
							num3 = tcp.Connect(_o.JoinIp);
						}
						num4 = num9 + num7;
					}
				}
			}
			long beforePoll = iteration.ElapsedMilliseconds;
			stopwatch.Restart();
			transport.Poll();
			if (_o.Dedicated) GameServer.RunCallbacks();
			UpdateDirectory(num9);
			long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
			host?.Tick();
			_client?.Tick();
			ClientWorld.Current?.NetTick(num9);
			spike?.Tick();
			long elapsedMilliseconds2 = stopwatch.ElapsedMilliseconds;
			if (elapsedMilliseconds2 > 250)
			{
				val = Math.Max(val, elapsedMilliseconds2);
				_log.Warn($"net: loop iteration took {elapsedMilliseconds2} ms (transport.Poll {elapsedMilliseconds} ms incl. the link's Pump); timestamps taken before it would be stale");
			}
			foreach (KeyValuePair<PeerId, long> item in connectedAt)
			{
				if (steam != null && !detailedDone.Contains(item.Key) && num9 - item.Value >= 3000)
				{
					detailedDone.Add(item.Key);
					LogDetailed(steam, item.Key);
				}
			}
			if (num9 - num >= 10000)
			{
				num = num9;
				LogStatus(steam, tcp, transport, num9);
				if (HostWorld.Current != null && _o.Host)
				{
					_log.Info($"net status: host world sync {HostWorld.Current.Stats()}; world ready={HostWorld.Current.WorldReady}");
					if (HostWorld.Current.SignalsForwarded + HostWorld.Current.SignalsDropped + HostWorld.Current.InputCommandsApplied + HostWorld.Current.InputRejected > 0)
					{
						_log.Info("net status: " + HostWorld.Current.SignalStats());
					}
				}
			}
			if (iteration.ElapsedMilliseconds > 150)
			{
				_log.Warn($"net: whole loop iteration took {iteration.ElapsedMilliseconds} ms (before the poll {beforePoll} ms, poll and ticks {elapsedMilliseconds2} ms, status logging and the rest {iteration.ElapsedMilliseconds - beforePoll - elapsedMilliseconds2} ms)");
			}
			Thread.Sleep(1);
		}
	}

	internal static string DescribeLoss(DisconnectInfo info)
	{
		string text = (string.IsNullOrWhiteSpace(info.Reason) ? $"code {info.Code}" : info.Reason.Trim());
		switch (info.Code)
		{
		case 1003:
			return "no answer from the host for 30 s";
		case 1004:
			if (info.ByPeer)
			{
				return "the host closed the game (" + text + ")";
			}
			break;
		case 1000:
			if (info.ByPeer)
			{
				return "the host ended the session: " + text;
			}
			break;
		}
		return info.ByPeer ? ("closed by the host: " + text) : text;
	}

	private void LogStatus(SteamLink? steam, TcpLink? tcp, MpTransport transport, long now)
	{
		bool flag = false;
		foreach (var item8 in (steam != null) ? Steam(steam) : Tcp(tcp))
		{
			uint item = item8.Item1;
			ulong item2 = item8.Item2;
			bool item3 = item8.Item3;
			string item4 = item8.Item4;
			flag = true;
			string value = "";
			if (transport.TryGetStats(new PeerId(item2), out PeerStats stats))
			{
				ChannelStats[] channels = stats.Channels;
				value = $" | app rtt={stats.LastRttMs:0} ms (avg {stats.AvgRttMs:0.0}, max {stats.MaxRttMs:0}, pongs {stats.PongsReceived}/{stats.PingsSent}) ctl {channels[0].FramesSent}/{channels[0].FramesReceived} fr, bulk {channels[1].BytesSent / 1024}/{channels[1].BytesReceived / 1024} KiB tx/rx, state {channels[2].FramesSent}/{channels[2].FramesReceived} fr {channels[2].BytesSent / 1024}/{channels[2].BytesReceived / 1024} KiB, queued {MpLogExtensions.Bytes(stats.BulkQueuedBytes)}, reasm {stats.ReassemblyInProgress}, seqGaps {stats.SeqGaps} protoErr {stats.ProtocolErrors} busy {stats.SendBusy}";
			}
			_log.Info($"net status {item2} conn {item} {(item3 ? "connected" : "pending")}: {item4}{value}");
		}
		if (((steam != null) & flag) && now - _lastDetailedMs >= 60000)
		{
			_lastDetailedMs = now;
			foreach (var item9 in steam.Connections())
			{
				ulong item5 = item9.Remote;
				if (item9.Connected)
				{
					LogDetailed(steam, new PeerId(item5));
				}
			}
		}
		if (!flag)
		{
			_log.Info(_o.Host ? "net status: no clients connected (waiting)" : "net status: not connected");
		}
		static IEnumerable<(uint, ulong, bool, string)> Steam(SteamLink l)
		{
			foreach (var (num, item6, item7) in l.Connections())
			{
				yield return (num, item6, item7, l.QuickStatus(num));
			}
		}
		static IEnumerable<(uint, ulong, bool, string)> Tcp(TcpLink l)
		{
			foreach (var (num, _, item6) in l.Connections())
			{
				yield return (num, TcpLink.SyntheticId(num), item6, l.QuickStatus(num));
			}
		}
	}

	private void LogDetailed(SteamLink link, PeerId peer)
	{
		foreach (var (conn, num, flag) in link.Connections())
		{
			if ((num == peer.Value) & flag)
			{
				_log.Info($"net detailed status {num}:\n{link.DetailedStatus(conn).TrimEnd()}");
			}
		}
	}

	private void SteamJoinStart(ulong me, string name)
	{
		try
		{
			if (_o.HostSteam && !_o.Dedicated)
			{
				_advertiser = new HostAdvertiser(_log, me, _o.VirtualPort, SyncProtocol.Version, _pluginVersion);
			}
			if (!_o.Join)
			{
				return;
			}
			if (_o.UseSteamIp) { ClientWorld.Progress.SetHost(_o.JoinIp.ToString()); return; }
			_friends = new FriendFinder(_log);
			_friends.Start();
			if (_o.JoinSteamId != 0L)
			{
				SetJoinTarget(_o.JoinSteamId, "command line (-y51online:join:<id>, also what Steam's 'Join Game' passes)", locked: true);
				return;
			}
			string text = _friends.LaunchCommandLine();
			if (text.Length > 0)
			{
				_log.Info("steam friends: Steam launch command line: '" + text + "'");
			}
			if (ConnectString.TryParse(text, out var hostSteamId, out var _))
			{
				SetJoinTarget(hostSteamId, "Steam launch command line", locked: true);
			}
			else if (_o.JoinFriends)
			{
				_log.Info("steam friends: looking for a Steam friend who hosts SE2 multiplayer" + ((_o.JoinFriendFilter != null) ? (" (name contains '" + _o.JoinFriendFilter + "')") : "") + "; a 'Join Game' click in Steam works too");
				ClientWorld.Progress.Set(JoinStage.LookingForHost, MpClock.NowMs(), "Looking for a friend who is hosting...");
			}
			else if (_o.JoinWait)
			{
				ClientWorld.Progress.Set(JoinStage.LookingForHost, MpClock.NowMs(), "Click 'Join Game' on a friend in Steam (Shift+Tab).");
			}
		}
		catch (Exception ex)
		{
			_log.Error($"steam friends: setting up rich presence / friend join failed ({ex.GetType().Name}: {ex.Message}); " + ((_o.JoinSteamId != 0L) ? "joining the given SteamID64 still works" : "use -y51online:join:<host SteamID64> instead"));
			_friends = null;
			if (_o.JoinSteamId != 0L)
			{
				SetJoinTarget(_o.JoinSteamId, "command line", locked: true);
			}
		}
	}

	private void SetJoinTarget(ulong id, string source, bool locked, string? label = null)
	{
		_joinTarget = id;
		_joinTargetLocked = locked;
		_targetSource = source;
		if (id == 0L)
		{
			return;
		}
		_log.Info($"net: join target = host {id} (from {source})");
		if (_o.Join)
		{
			ClientWorld.Progress.SetHost(label ?? FriendName(id) ?? $"host {id}");
			JoinStage stage = ClientWorld.Progress.Stage;
			if (stage <= JoinStage.LookingForHost)
			{
				ClientWorld.Progress.Set(JoinStage.Connecting, MpClock.NowMs());
			}
		}
	}

	private string? FriendName(ulong id)
	{
		try
		{
			if (!_steamReady.IsSet)
			{
				return null;
			}
			string friendPersonaName = SteamFriends.GetFriendPersonaName(new CSteamID(id));
			return (string.IsNullOrWhiteSpace(friendPersonaName) || friendPersonaName == "[unknown]") ? null : friendPersonaName;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void SteamJoinTick(long now, bool joined, HostSession? host)
	{
		try
		{
			if (_advertiser != null && now >= _nextPresenceMs)
			{
				_nextPresenceMs = now + 2000;
				HostWorld current = HostWorld.Current;
				bool worldLoaded = current?.WorldLoaded ?? true;
				_advertiser.Update(worldLoaded, current?.WorldName ?? "?");
			}
			FriendFinder friends = _friends;
			if (friends == null)
			{
				return;
			}
			ulong from;
			string connect;
			while (friends.TryTakeJoinRequest(out from, out connect))
			{
				if (!ConnectString.TryParse(connect, out var hostSteamId, out var virtualPort))
				{
					_log.Warn($"steam friends: 'Join Game' from {from} carries connect string '{connect}', which has no -y51online:join:<SteamID64>; ignored " + "(the friend does not host with this plugin)");
					continue;
				}
				if (virtualPort.HasValue && virtualPort.Value != _o.VirtualPort)
				{
					_log.Warn($"steam friends: host {hostSteamId} listens on virtual port {virtualPort.Value}, this game uses {_o.VirtualPort}; restart with -y51online:vport:{virtualPort.Value} if the connection fails");
				}
				if (joined)
				{
					ClientWorld current2 = ClientWorld.Current;
					if (current2 != null && current2.IdleAfterJoin)
					{
						SetJoinTarget(hostSteamId, "Steam 'Join Game' / invite after the last join ended (connect '" + connect + "')", locked: true);
						current2.AskRejoin($"Steam 'Join Game' for host {hostSteamId}");
						continue;
					}
				}
				if (joined)
				{
					_log.Warn($"steam friends: 'Join Game' for host {hostSteamId} ignored: this game is already connected (or connecting) to host {_joinTarget} " + (_o.Rejoin ? "(leave to the main menu first, then click 'Join Game' again or press F3)" : "(quit the game and click 'Join Game' again to switch)"));
				}
				else
				{
					SetJoinTarget(hostSteamId, "Steam 'Join Game' / invite (connect '" + connect + "')", locked: true);
				}
			}
			if (joined || _joinTargetLocked)
			{
				return;
			}
			if (!_o.JoinFriends)
			{
				if (_o.JoinWait && !_waitLogged)
				{
					_waitLogged = true;
					_log.Info("steam friends: -y51online:join:wait: waiting for you to click 'Join Game' on a hosting friend in Steam (friends list or overlay, Shift+Tab)");
				}
				return;
			}
			bool flag = friends.TakePresenceChanged();
			if (now < _nextFriendScanMs && (!flag || now < _nextFriendScanMs - 10000 + 1000))
			{
				return;
			}
			_nextFriendScanMs = now + 10000;
			List<FriendHost> friends2 = friends.Scan(now, out var friendsTotal, out var inGame);
			FriendPicker.Choice choice = FriendPicker.Pick(friends2, _o.JoinFriendFilter, SyncProtocol.Version);
			string text = ((choice.Hosting.Count == 0) ? $"no friend is hosting SE2 multiplayer right now ({friendsTotal} friends, {inGame} in Space Engineers 2{InGameNames(friends2)}); retrying every {10} s" : $"found {choice.Hosting.Count} friend(s) hosting: {string.Join("; ", choice.Hosting.Select((FriendHost h) => h.Describe()))}");
			if (text != _lastFriendsLine || (choice.Friend == null && now - _lastNoHostLogMs >= 60000))
			{
				_lastFriendsLine = text;
				_lastNoHostLogMs = now;
				_log.Info("steam friends: " + text);
			}
			bool flag2 = choice.Friend == null;
			if (flag2)
			{
				JoinStage stage = ClientWorld.Progress.Stage;
				bool flag3 = stage - 1 <= JoinStage.LookingForHost;
				flag2 = flag3;
			}
			if (flag2)
			{
				string detail = ScreenTexts.NoFriendHosting(_o.JoinFriendFilter, 10, (choice.Hosting.Count == 0) ? null : choice.Reason);
				ClientWorld.Progress.Set(JoinStage.LookingForHost, now, detail);
			}
			if (_joinTarget != 0L)
			{
				if (choice.Hosting.Any((FriendHost h) => h.SteamId == _joinTarget))
				{
					return;
				}
				_log.Warn($"steam friends: host {_joinTarget} no longer advertises SE2 multiplayer; looking again");
				SetJoinTarget(0uL, "", locked: false);
				ClientWorld.Progress.Set(JoinStage.LookingForHost, now, "That host stopped; looking for another...");
			}
			if (choice.Friend != null)
			{
				_log.Info("steam friends: joining " + choice.Friend.Describe() + ": " + choice.Reason);
				if (choice.Friend.Presence.VirtualPort != _o.VirtualPort)
				{
					_log.Warn($"steam friends: that host uses virtual port {choice.Friend.Presence.VirtualPort}, this game {_o.VirtualPort}; restart with -y51online:vport:{choice.Friend.Presence.VirtualPort}");
				}
				SetJoinTarget(choice.Friend.SteamId, "friends list (" + choice.Reason + ")", locked: false, choice.Friend.Name);
			}
			else if (choice.Hosting.Count > 0)
			{
				_log.Info($"steam friends: {choice.Reason}; retrying every {10} s");
			}
		}
		catch (Exception ex)
		{
			_log.Error($"steam friends: friend join failed and is switched off ({ex.GetType().Name}: {ex.Message}); use -y51online:join:<host SteamID64>");
			_friends = null;
		}
		static string InGameNames(List<FriendHost> list)
		{
			if (list.Count != 0)
			{
				return ": " + string.Join(", ", from x in list.Take(8)
					select (x.RawPresence != null) ? (x.Name + " [unreadable presence '" + x.RawPresence + "']") : x.Name);
			}
			return "";
		}
	}
}
