using System.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Net;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Steamworks;

namespace Y51_SE2Online.Steam;

internal sealed class SteamLink : IPacketLink, IDisposable
{
	private sealed class Conn
	{
		public HSteamNetConnection Handle;

		public ulong Remote;

		public bool Incoming;

		public bool Connected;

		public bool LanesOk;
	}

	private readonly IMpLog _log;

	private readonly int _vport;

	private readonly Func<ulong, bool> _accept;

	private readonly int _sendRateMin;

	private readonly int _sendRateMax;

	private readonly SteamNetworkingConfigValue_t[] _options;

	private readonly ConnectionTable _table = new ConnectionTable();

	private readonly ConcurrentQueue<SteamNetConnectionStatusChangedCallback_t> _status = new ConcurrentQueue<SteamNetConnectionStatusChangedCallback_t>();

	private readonly Dictionary<uint, Conn> _conns = new Dictionary<uint, Conn>();

	private readonly nint[] _recv = new nint[64];

	private readonly nint[] _sendPtr = new nint[1];

	private readonly long[] _sendResult = new long[1];

	private Callback<SteamNetConnectionStatusChangedCallback_t>? _statusCallback;

	private HSteamListenSocket _listen = HSteamListenSocket.Invalid;
	private HSteamListenSocket _ipListen = HSteamListenSocket.Invalid;
	private readonly SteamSocketApi _api;
	private readonly bool _gameServer;
	private readonly IPEndPoint _listenAddress;

	private HSteamNetPollGroup _pollGroup = HSteamNetPollGroup.Invalid;

	private bool _disposed;

	public long CallbacksSeen;

	private readonly List<LinkEvent> _pendingEvents = new List<LinkEvent>();

	public string Name => _gameServer ? "steam-dedicated" : "steam-p2p-ip";

	public int MaxMessageSize => 524288;

	public bool AuthenticatesPeers => true;

	public SteamLink(IMpLog log, int virtualPort, int sendRateMin, int sendRateMax, Func<ulong, bool> accept, IPEndPoint listenAddress = null, bool gameServer = false)
	{
		_api = new SteamSocketApi(gameServer);
		_gameServer = gameServer;
		_listenAddress = listenAddress;
		_log = log;
		_vport = virtualPort;
		_accept = accept;
		_sendRateMin = Math.Min(sendRateMin, sendRateMax);
		_sendRateMax = sendRateMax;
		_options = new SteamNetworkingConfigValue_t[5]
		{
			Int32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, _sendRateMin),
			Int32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, sendRateMax),
			Int32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 4194304),
			Int32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferSize, 8388608),
			Int32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, 20000)
		};
	}

	private static SteamNetworkingConfigValue_t Int32(ESteamNetworkingConfigValue key, int value)
	{
		SteamNetworkingConfigValue_t result = new SteamNetworkingConfigValue_t
		{
			m_eValue = key,
			m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32
		};
		result.m_val.m_int32 = value;
		return result;
	}

	public void Start(bool listen)
	{
		try { StartCore(listen); }
		catch { Dispose(); throw; }
	}

	private void StartCore(bool listen)
	{
		if (!SteamMessageLayout.MatchesNative(out string detail))
		{
			throw new InvalidOperationException("SteamNetworkingMessage_t layout mismatch: " + detail);
		}
		_statusCallback = _gameServer ? Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnStatusChanged) : Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
		_api.InitAuthentication();
		_pollGroup = _api.CreatePollGroup();
		if (_pollGroup == HSteamNetPollGroup.Invalid)
		{
			throw new InvalidOperationException("CreatePollGroup failed");
		}
		if (listen)
		{
			if (_gameServer && _listenAddress == null) throw new InvalidOperationException("Dedicated Steam servers require an IP listen address.");
			if (!_gameServer)
			{
			_listen = _api.CreateListenSocketP2P(_vport, _options.Length, _options);
			if (_listen == HSteamListenSocket.Invalid)
			{
				throw new InvalidOperationException($"CreateListenSocketP2P({_vport}) failed");
			}
			_log.Info($"listening for P2P connections on virtual port {_vport}");
			}
			if (_listenAddress != null)
			{
				var address = NativeAddress(_listenAddress);
				_ipListen = _api.CreateListenSocketIP(ref address, _options.Length, _options);
				if (_ipListen == HSteamListenSocket.Invalid) throw new InvalidOperationException("Could not listen on " + _listenAddress + ". Check that its UDP port is available.");
				_log.Info("Steam Networking Sockets IP listener: " + _listenAddress);
			}
		}
	}

	private static SteamNetworkingIPAddr NativeAddress(IPEndPoint endpoint)
	{
		var result = new SteamNetworkingIPAddr { m_ipv6 = new byte[16] };
		if (!result.ParseString(endpoint.ToString())) throw new FormatException("Steam could not parse the IP endpoint.");
		return result;
	}

	public uint Connect(IPEndPoint endpoint)
	{
		var address = NativeAddress(endpoint);
		var connection = _api.ConnectByIPAddress(ref address, _options.Length, _options);
		if (connection == HSteamNetConnection.Invalid) return 0;
		_api.SetConnectionPollGroup(connection, _pollGroup);
		_conns[connection.m_HSteamNetConnection] = new Conn { Handle = connection };
		ApplyRates(connection);
		_log.Info("Connecting with Steam Networking Sockets to " + endpoint);
		return connection.m_HSteamNetConnection;
	}

	public uint Connect(ulong steamId)
	{
		if (_gameServer) throw new InvalidOperationException("Dedicated servers cannot initiate P2P connections.");
		SteamNetworkingIdentity identityRemote = default;
		identityRemote.SetSteamID64(steamId);
		HSteamNetConnection hSteamNetConnection = _api.ConnectP2P(ref identityRemote, _vport, _options.Length, _options);
		if (hSteamNetConnection == HSteamNetConnection.Invalid)
		{
			_log.Error($"ConnectP2P({steamId}, vport {_vport}) returned an invalid handle");
			return 0u;
		}
		_api.SetConnectionPollGroup(hSteamNetConnection, _pollGroup);
		Register(hSteamNetConnection, steamId, incoming: false);
		_log.Info($"connecting to {steamId} on virtual port {_vport} (conn {hSteamNetConnection.m_HSteamNetConnection})");
		return hSteamNetConnection.m_HSteamNetConnection;
	}

	private void Register(HSteamNetConnection h, ulong remote, bool incoming)
	{
		uint hSteamNetConnection = h.m_HSteamNetConnection;
		// Another game instance on the same Steam account is a separate player, not a reconnect;
		// the transport's Hello instance check still replaces a genuinely stale connection.
		if (incoming && _conns.Values.Any(c => c.Remote == remote && c.Connected))
		{
			int slot = 1;
			while (slot <= PeerAlias.MaxSlots && _conns.Values.Any(c => c.Remote == PeerAlias.Make(remote, slot) && c.Connected)) slot++;
			if (slot > PeerAlias.MaxSlots)
			{
				_api.CloseConnection(h, 1005, "too many game instances on this Steam account", false);
				return;
			}
			remote = PeerAlias.Make(remote, slot);
			_log.Info($"incoming conn {hSteamNetConnection}: this Steam account is already connected; the new game instance joins as peer {remote}");
		}
		foreach (ConnectionTable.Entry item in _table.Add(hSteamNetConnection, remote, incoming, MpClock.NowMs()))
		{
			if (_conns.Remove(item.Conn, out Conn value))
			{
				_log.Warn($"conn {item.Conn} from {remote} ({(value.Connected ? "connected" : "still connecting")}) superseded by newer conn {hSteamNetConnection}: closing the stale one");
				_api.CloseConnection(value.Handle, 1006, "superseded by a newer connection", bEnableLinger: false);
				if (value.Connected)
				{
					_pendingEvents.Add(new LinkEvent(LinkEventKind.Disconnected, item.Conn, remote, 1006, "superseded by a newer connection"));
				}
			}
		}
		_conns[hSteamNetConnection] = new Conn
		{
			Handle = h,
			Remote = remote,
			Incoming = incoming
		};
		ApplyRates(h);
	}

	private void ApplyRates(HSteamNetConnection h)
	{
		try
		{
			bool flag = SetConnInt(h, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, _sendRateMin);
			bool flag2 = SetConnInt(h, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, _sendRateMax);
			if (!flag || !flag2)
			{
				_log.WarnOnce("steam-rate-set", $"SetConfigValue(SendRateMin/Max) on a connection failed (min ok={flag}, max ok={flag2}); relying on socket options");
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("steam-rate-set-ex", "SetConfigValue(SendRateMin/Max) threw " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private unsafe bool SetConnInt(HSteamNetConnection h, ESteamNetworkingConfigValue key, int value)
	{
		int num = value;
		return _api.SetConfigValue(key, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, (nint)h.m_HSteamNetConnection, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, (nint)(&num));
	}

	public bool HasConnection(uint conn)
	{
		return _conns.ContainsKey(conn);
	}

	private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t s)
	{
		_status.Enqueue(s);
	}

	public unsafe void Pump(List<LinkEvent> events, List<LinkMessage> messages)
	{
		if (_disposed)
		{
			return;
		}
		if (_pendingEvents.Count > 0)
		{
			events.AddRange(_pendingEvents);
			_pendingEvents.Clear();
		}
		SteamNetConnectionStatusChangedCallback_t result;
		while (_status.TryDequeue(out result))
		{
			CallbacksSeen++;
			HandleStatus(in result, events);
		}
		for (int i = 0; i < 32; i++)
		{
			int num = _api.ReceiveMessagesOnPollGroup(_pollGroup, _recv, _recv.Length);
			if (num <= 0)
			{
				break;
			}
			for (int j = 0; j < num; j++)
			{
				nint num2 = _recv[j];
				try
				{
					nint pointer = Marshal.ReadIntPtr(num2, SteamMessageLayout.Data);
					int num3 = Marshal.ReadInt32(num2, SteamMessageLayout.Size);
					uint num4 = (uint)Marshal.ReadInt32(num2, SteamMessageLayout.Conn);
					byte[] array = new byte[num3];
					if (num3 > 0)
					{
						new ReadOnlySpan<byte>((void*)pointer, num3).CopyTo(array);
					}
					if (_conns.TryGetValue(num4, out Conn value) && !value.Connected)
					{
						TryPromote(value, events);
					}
					messages.Add(new LinkMessage(num4, array));
				}
				finally
				{
					SteamNetworkingMessage_t.Release(num2);
				}
			}
			if (num < _recv.Length)
			{
				break;
			}
		}
	}

	private void TryPromote(Conn c, List<LinkEvent> events)
	{
		if (_api.GetConnectionInfo(c.Handle, out var pInfo) && pInfo.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
		{
			MarkConnected(c, events);
		}
	}

	private void HandleStatus(in SteamNetConnectionStatusChangedCallback_t s, List<LinkEvent> events)
	{
		uint hSteamNetConnection = s.m_hConn.m_HSteamNetConnection;
		ESteamNetworkingConnectionState eState = s.m_info.m_eState;
		ulong steamID = s.m_info.m_identityRemote.GetSteamID64();
		_conns.TryGetValue(hSteamNetConnection, out Conn value);
		// Steam reports other local game instances' connections (same account) to this process too; only
		// connections arriving on our own listen sockets are ours to accept or close.
		if (value == null && (s.m_info.m_hListenSocket.m_HSteamListenSocket == 0 || (s.m_info.m_hListenSocket != _listen && s.m_info.m_hListenSocket != _ipListen)))
		{
			return;
		}
		switch (eState)
		{
		case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
			if (value == null)
			{
				AcceptIncoming(s.m_hConn, steamID);
				break;
			}
			_log.Info($"conn {hSteamNetConnection} to {steamID}: connecting");
			break;
		case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute:
			_log.Info($"conn {hSteamNetConnection} to {steamID}: finding route");
			break;
		case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
			if (value != null && !value.Connected)
			{
				MarkConnected(value, events);
			}
			break;
		case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
		case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
		{
			bool flag = eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer;
			int eEndReason = s.m_info.m_eEndReason;
			string szEndDebug = s.m_info.m_szEndDebug;
			_log.Info($"conn {hSteamNetConnection} to {steamID}: {(flag ? "closed by peer" : "problem detected locally")} (was {Short(s.m_eOldState)}), end reason {eEndReason} {EndReasonName(eEndReason)}: '{szEndDebug}'");
			if (value != null)
			{
				_conns.Remove(hSteamNetConnection);
				_table.Remove(hSteamNetConnection);
				if (value.Connected)
				{
					events.Add(new LinkEvent(LinkEventKind.Disconnected, hSteamNetConnection, value.Remote, eEndReason, EndReasonName(eEndReason) + ": " + szEndDebug, flag));
				}
				else
				{
					events.Add(new LinkEvent(LinkEventKind.Disconnected, hSteamNetConnection, value.Remote, eEndReason, "connect failed, " + EndReasonName(eEndReason) + ": " + szEndDebug, flag));
				}
			}
			_api.CloseConnection(s.m_hConn, 0, null, bEnableLinger: false);
			break;
		}
		}
	}

	private void AcceptIncoming(HSteamNetConnection h, ulong remote)
	{
		if (remote == 0) { _api.CloseConnection(h, 1005, "Steam identity required", false); return; }
		uint hSteamNetConnection = h.m_HSteamNetConnection;
		if (!_accept(remote))
		{
			_log.Warn($"rejecting incoming connection from {remote} (not on -y51online:allow list)");
			_api.CloseConnection(h, 1005, "not on allow list", bEnableLinger: false);
			return;
		}
		EResult eResult = _api.AcceptConnection(h);
		if (eResult != EResult.k_EResultOK)
		{
			_log.Warn($"AcceptConnection from {remote} failed: {eResult}");
			_api.CloseConnection(h, 0, null, bEnableLinger: false);
		}
		else
		{
			_api.SetConnectionPollGroup(h, _pollGroup);
			Register(h, remote, incoming: true);
			_log.Info($"accepted incoming connection from {remote} (conn {hSteamNetConnection})");
		}
	}

	private void MarkConnected(Conn c, List<LinkEvent> events)
	{
		if (!_api.GetConnectionInfo(c.Handle, out var info) || info.m_identityRemote.GetSteamID64() == 0)
		{
			_api.CloseConnection(c.Handle, 1005, "Authenticated Steam identity required", false);
			events.Add(new LinkEvent(LinkEventKind.Disconnected, c.Handle.m_HSteamNetConnection, c.Remote, 1005, "No authenticated Steam identity"));
			_conns.Remove(c.Handle.m_HSteamNetConnection);
			return;
		}
		ulong identity = info.m_identityRemote.GetSteamID64();
		if (c.Remote == 0) { c.Remote = identity; _table.Add(c.Handle.m_HSteamNetConnection, identity, c.Incoming, MpClock.NowMs()); }
		else if (PeerAlias.SteamOf(c.Remote) != identity) { _api.CloseConnection(c.Handle, 1005, "Steam identity mismatch", false); return; }
		c.Connected = true;
		_table.MarkConnected(c.Handle.m_HSteamNetConnection);
		EResult eResult = _api.ConfigureConnectionLanes(c.Handle, LanePlan.Priorities.Length, LanePlan.Priorities, LanePlan.Weights);
		c.LanesOk = eResult == EResult.k_EResultOK;
		if (!c.LanesOk)
		{
			_log.Warn($"ConfigureConnectionLanes failed ({eResult}); all channels share lane 0");
		}
		_log.Info($"conn {c.Handle.m_HSteamNetConnection} to {c.Remote}: CONNECTED ({(c.Incoming ? "incoming" : "outgoing")}), lanes {(c.LanesOk ? "ok" : "off")}; {QuickStatus(c.Handle.m_HSteamNetConnection)}");
		events.Add(new LinkEvent(LinkEventKind.Connected, c.Handle.m_HSteamNetConnection, c.Remote));
	}

	public unsafe LinkSendResult Send(uint conn, Channel channel, bool reliable, ReadOnlySpan<byte> frame)
	{
		if (!_conns.TryGetValue(conn, out Conn value) || !value.Connected)
		{
			return LinkSendResult.Failed;
		}
		if (frame.Length > MaxMessageSize)
		{
			return LinkSendResult.Failed;
		}
		nint num = _api.AllocateMessage(frame.Length);
		if (num == IntPtr.Zero)
		{
			return LinkSendResult.Busy;
		}
		nint pointer = Marshal.ReadIntPtr(num, SteamMessageLayout.Data);
		frame.CopyTo(new Span<byte>((void*)pointer, frame.Length));
		Marshal.WriteInt32(num, SteamMessageLayout.Conn, (int)conn);
		Marshal.WriteInt32(num, SteamMessageLayout.Flags, (!reliable) ? 1 : 9);
		Marshal.WriteInt16(num, SteamMessageLayout.Lane, (short)(value.LanesOk ? channel : Channel.Control));
		_sendPtr[0] = num;
		_api.SendMessages(1, _sendPtr, _sendResult);
		long num2 = _sendResult[0];
		if (num2 >= 0)
		{
			return LinkSendResult.Ok;
		}
		EResult eResult = (EResult)(-num2);
		if (eResult == EResult.k_EResultLimitExceeded)
		{
			return LinkSendResult.Busy;
		}
		_log.WarnOnce($"steam-send-{eResult}", $"SendMessages to {value.Remote} failed: {eResult}");
		return LinkSendResult.Failed;
	}

	public bool HasLanePriorities(uint conn)
	{
		if (_conns.TryGetValue(conn, out Conn value))
		{
			return value.LanesOk;
		}
		return false;
	}

	public int GetPendingReliableBytes(uint conn)
	{
		if (!_conns.TryGetValue(conn, out Conn value))
		{
			return int.MaxValue;
		}
		SteamNetConnectionRealTimeStatus_t pStatus = new SteamNetConnectionRealTimeStatus_t
		{
			reserved = new uint[16]
		};
		SteamNetConnectionRealTimeLaneStatus_t pLanes = new SteamNetConnectionRealTimeLaneStatus_t
		{
			reserved = new uint[10]
		};
		if (_api.GetConnectionRealTimeStatus(value.Handle, ref pStatus, 0, ref pLanes) != EResult.k_EResultOK)
		{
			return int.MaxValue;
		}
		return pStatus.m_cbPendingReliable;
	}

	public void Close(uint conn, int code, string reason, bool linger)
	{
		_table.Remove(conn);
		if (_conns.Remove(conn, out Conn value))
		{
			_api.CloseConnection(value.Handle, code, Truncate(reason, 120), linger);
		}
	}

	public int ConnectionCountFor(ulong remote)
	{
		return _table.CountFor(remote);
	}

	public IEnumerable<(uint Conn, ulong Remote, bool Connected)> Connections()
	{
		foreach (KeyValuePair<uint, Conn> conn in _conns)
		{
			yield return (Conn: conn.Key, Remote: conn.Value.Remote, Connected: conn.Value.Connected);
		}
	}

	public string QuickStatus(uint conn)
	{
		if (!_conns.TryGetValue(conn, out Conn value))
		{
			return "no such connection";
		}
		if (!_api.GetConnectionInfo(value.Handle, out var pInfo))
		{
			return "GetConnectionInfo failed";
		}
		SteamNetConnectionRealTimeStatus_t pStatus = new SteamNetConnectionRealTimeStatus_t
		{
			reserved = new uint[16]
		};
		SteamNetConnectionRealTimeLaneStatus_t pLanes = new SteamNetConnectionRealTimeLaneStatus_t
		{
			reserved = new uint[10]
		};
		EResult connectionRealTimeStatus = _api.GetConnectionRealTimeStatus(value.Handle, ref pStatus, 0, ref pLanes);
		string value2 = (((pInfo.m_nFlags & 0x10) != 0) ? $"RELAYED via SDR pop={PopId(pInfo.m_idPOPRelay.m_SteamNetworkingPOPID)} (remote pop {PopId(pInfo.m_idPOPRemote.m_SteamNetworkingPOPID)})" : ("DIRECT " + Addr(pInfo.m_addrRemote)));
		string value3 = Flags(pInfo.m_nFlags);
		if (connectionRealTimeStatus == EResult.k_EResultOK)
		{
			return $"route={value2} flags={value3} ping={pStatus.m_nPing} ms quality local={pStatus.m_flConnectionQualityLocal:0.00} remote={pStatus.m_flConnectionQualityRemote:0.00} out={pStatus.m_flOutBytesPerSec / 1024f:0.0} KiB/s in={pStatus.m_flInBytesPerSec / 1024f:0.0} KiB/s sendRate={pStatus.m_nSendRateBytesPerSecond / 1024} KiB/s pendingRel={pStatus.m_cbPendingReliable} unackedRel={pStatus.m_cbSentUnackedReliable} pendingUnrel={pStatus.m_cbPendingUnreliable} queue={(double)pStatus.m_usecQueueTime.m_SteamNetworkingMicroseconds / 1000.0:0.0} ms desc='{pInfo.m_szConnectionDescription}'";
		}
		return $"route={value2} flags={value3} (realtime status {connectionRealTimeStatus})";
	}

	public string DetailedStatus(uint conn)
	{
		if (!_conns.TryGetValue(conn, out Conn value))
		{
			return "no such connection";
		}
		int detailedConnectionStatus = _api.GetDetailedConnectionStatus(value.Handle, out var pszBuf, 8192);
		if (detailedConnectionStatus != 0)
		{
			return $"GetDetailedConnectionStatus returned {detailedConnectionStatus}";
		}
		return pszBuf;
	}

	public static string RelayStatus(bool gameServer = false)
	{
		SteamRelayNetworkStatus_t pDetails;
		ESteamNetworkingAvailability relayNetworkStatus = gameServer
			? SteamGameServerNetworkingUtils.GetRelayNetworkStatus(out pDetails)
			: SteamNetworkingUtils.GetRelayNetworkStatus(out pDetails);
		return $"relay={Short(relayNetworkStatus)} anyRelay={Short(pDetails.m_eAvailAnyRelay)} netConfig={Short(pDetails.m_eAvailNetworkConfig)} pingMeasuring={pDetails.m_bPingMeasurementInProgress != 0} '{pDetails.m_debugMsg}'";
	}

	public static bool RelayReady()
	{
		SteamRelayNetworkStatus_t pDetails;
		return SteamNetworkingUtils.GetRelayNetworkStatus(out pDetails) == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current;
	}

	public static bool NetworkConfigReady()
	{
		SteamNetworkingUtils.GetRelayNetworkStatus(out SteamRelayNetworkStatus_t details);
		return details.m_eAvailNetworkConfig == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current;
	}

	private static string Short<T>(T e) where T : Enum
	{
		string text = e.ToString();
		int num = text.LastIndexOf('_');
		if (num < 0)
		{
			return text;
		}
		return text.Substring(num + 1);
	}

	private static string Flags(int f)
	{
		List<string> list = new List<string>();
		if ((f & 1) != 0)
		{
			list.Add("unauthenticated");
		}
		if ((f & 2) != 0)
		{
			list.Add("unencrypted");
		}
		if ((f & 4) != 0)
		{
			list.Add("loopback");
		}
		if ((f & 8) != 0)
		{
			list.Add("fast");
		}
		if ((f & 0x10) != 0)
		{
			list.Add("relayed");
		}
		if ((f & 0x20) != 0)
		{
			list.Add("dualwifi");
		}
		if (list.Count != 0)
		{
			return string.Join("|", list);
		}
		return "none";
	}

	private static string Addr(SteamNetworkingIPAddr a)
	{
		try
		{
			if (a.IsIPv6AllZeros())
			{
				return "(no ip)";
			}
			a.ToString(out var buf, bWithPort: true);
			return buf;
		}
		catch (Exception)
		{
			return "(ip?)";
		}
	}

	public static string PopId(uint id)
	{
		if (id == 0)
		{
			return "-";
		}
		Span<char> span = stackalloc char[4];
		span[0] = (char)((id >> 16) & 0xFF);
		span[1] = (char)((id >> 8) & 0xFF);
		span[2] = (char)(id & 0xFF);
		span[3] = (char)((id >> 24) & 0xFF);
		return new string((span[3] == '\0') ? span.Slice(0, 3) : span);
	}

	public static string EndReasonName(int reason)
	{
		if (Enum.IsDefined(typeof(ESteamNetConnectionEnd), reason))
		{
			return Short((ESteamNetConnectionEnd)reason);
		}
		if (reason < 3000)
		{
			if (reason >= 1000)
			{
				if (reason < 2000)
				{
					return reason switch
					{
						1000 => "App_Bye", 
						1001 => "App_ProtocolError", 
						1002 => "App_HelloTimeout", 
						1003 => "App_PingTimeout", 
						1004 => "App_Shutdown", 
						1005 => "App_NotAllowed", 
						1006 => "App_Superseded", 
						1007 => "App_VersionMismatch", 
						_ => "App", 
					};
				}
				return "AppException";
			}
		}
		else
		{
			if (reason < 5000)
			{
				if (reason < 4000)
				{
					return "Local";
				}
				return "Remote";
			}
			if (reason < 6000)
			{
				return "Misc";
			}
		}
		return "?";
	}

	private static string Truncate(string s, int n)
	{
		if (s.Length > n)
		{
			return s.Substring(0, n);
		}
		return s;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		foreach (Conn value in _conns.Values)
		{
			_api.CloseConnection(value.Handle, 1004, "shutting down", bEnableLinger: true);
		}
		_conns.Clear();
		_table.Clear();
		if (_listen != HSteamListenSocket.Invalid)
		{
			_api.CloseListenSocket(_listen);
		}
		if (_ipListen != HSteamListenSocket.Invalid) _api.CloseListenSocket(_ipListen);
		if (_pollGroup != HSteamNetPollGroup.Invalid)
		{
			_api.DestroyPollGroup(_pollGroup);
		}
		_statusCallback?.Dispose();
	}
}

