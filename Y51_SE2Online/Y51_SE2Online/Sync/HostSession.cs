using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession : IStorySink
{
	public enum ClientStatus
	{
		Connected,
		WaitingForWorld,
		CaptureRequested,
		Synced
	}

	private sealed class RemoteClient
	{
		public PeerId Peer;

		public string Name = "?";

		public ClientStatus Status;

		public bool AdminStateSent;

		public bool Removed;

		public uint NextLifeSeq = 1u;

		public StateCoalescer State;

		public long JoinedMs;

		public long SyncedMs;

		public long LifecycleMessages;

		public long LifecycleBytes;

		public long StateBytes;

		public long BlobsSent;

		public long BlobBytes;

		public readonly HashSet<Guid> SentBlobs = new HashSet<Guid>();

		public PlayerRecord? Player;

		public (int Pcu, int Max)? LastStatus;

		public readonly Dictionary<ushort, long> SentByKeyAtLastStatus = new Dictionary<ushort, long>();

		public readonly object Input = new object();

		public readonly RemoteTypeTable InputTypes = new RemoteTypeTable();

		public readonly Dictionary<ushort, (SignalKeyDef Def, string? Problem)> Signals = new Dictionary<ushort, (SignalKeyDef, string)>();

		public readonly Dictionary<(uint, ushort), (uint Seq, byte[] Bytes, bool Dirty)> InputData = new Dictionary<(uint, ushort), (uint, byte[], bool)>();

		public readonly List<(string Signal, WireCommand Command)> InputCommands = new List<(string, WireCommand)>();

		public LookAccumulator? Look;

		public long LookIgnored;

		public long LookControlWrites;

		public readonly Phase2Stats P2 = new Phase2Stats();

		public uint LastCommandSeq;

		public long InputMessages;

		public long InputEntries;

		public long InputRejected;

		public long InputRemoved;

		public long CommandsIn;

		public long CommandsRejected;

		public long InputApplied;

		public long CommandsApplied;

		public long InputGaps;

		public long InputGapMaxMs;

		public long InputLastArrivalMs = -1L;

		public long TicksWithoutInput;

		public long TicksWithInput;

		public long InputMessagesAtTake;

		public uint AckSeq;

		public uint AckHostMs;

		public uint AckSentSeq;

		public long AckSentMs;

		public long InputGapLogMs = -4611686018427387904L;

		public long LastStatusMs;

		public long StateBytesAtLastStatus;

		public long SentAtLastStatus;

		public string LastPhase = "";
	}

	public sealed class PlayerRecord
	{
		public string Key;

		public string Name;

		public ushort ClientId;

		public long IdentityId;

		public long PlayerId;

		public PeerId? Peer;

		public uint CharacterNetId;

		public bool CharacterRequested;

		public int Joins;
	}

	private readonly ITransport _t;

	private readonly IMpLog _log;

	private readonly IHostWorld _world;

	private readonly HostSyncOptions _o;

	private readonly Func<long> _clock;

	private readonly object _sync = new object();

	private readonly Dictionary<PeerId, RemoteClient> _clients = new Dictionary<PeerId, RemoteClient>();

	private readonly ConcurrentQueue<StateEntry> _stateIn = new ConcurrentQueue<StateEntry>();

	private readonly ConcurrentQueue<uint> _despawnedIn = new ConcurrentQueue<uint>();

	private readonly Dictionary<string, PlayerRecord> _players = new Dictionary<string, PlayerRecord>();

	private ushort _nextClientId = 2;

	private readonly List<byte[]> _out = new List<byte[]>();

	private readonly Dictionary<ushort, int> _intervalByKey = new Dictionary<ushort, int>();

	private volatile int _syncedCount;

	private long _lastWaitCheckMs;

	private long _lastEmitMs = -4611686018427387904L;

	public long StateIn;

	public long StateDroppedNoClient;

	public long PlayerTablesSent;

	private readonly List<string> _hostStrings = new List<string>();

	public TypeKeyTable Keys { get; }

	public bool AnySynced => _syncedCount > 0;

	public SignalKeyTable SignalKeys { get; } = new SignalKeyTable();

	public int HostStringCount
	{
		get
		{
			lock (_sync)
			{
				return _hostStrings.Count;
			}
		}
	}

	public HostSession(ITransport transport, IMpLog log, IHostWorld world, TypeKeyTable keys, HostSyncOptions options, Func<long>? clock = null)
	{
		_t = transport;
		_log = log;
		_world = world;
		Keys = keys;
		_o = options;
		_clock = clock ?? new Func<long>(MpClock.NowMs);
		_t.PeerConnected += OnConnected;
		_t.PeerDisconnected += OnDisconnected;
		_t.MessageReceived += OnMessage;
		_log.Info($"sync(host): ready; state budget {_o.StateBudgetBytesPerSec / 1024} KiB/s per client, rate caps {_o.Caps.Describe()}");
	}

	private int IntervalFor(ushort key)
	{
		lock (_intervalByKey)
		{
			if (_intervalByKey.TryGetValue(key, out var value))
			{
				return value;
			}
			string shortName = $"key#{key}";
			foreach (TypeKeyDef item in Keys.All())
			{
				if (item.Key == key)
				{
					shortName = item.ShortName;
					break;
				}
			}
			value = _o.Caps.IntervalMsFor(shortName);
			_intervalByKey[key] = value;
			return value;
		}
	}

	private void OnConnected(PeerId peer)
	{
		lock (_sync)
		{
			_clients[peer] = new RemoteClient
			{
				Peer = peer,
				Status = ClientStatus.Connected,
				State = new StateCoalescer(IntervalFor, _o.StateBudgetBytesPerSec),
				JoinedMs = _clock()
			};
		}
	}

	private void OnDisconnected(PeerId peer, DisconnectInfo info)
	{
		RemoteClient value;
		lock (_sync)
		{
			if (_clients.Remove(peer, out value) && value.Status == ClientStatus.Synced)
			{
				_syncedCount--;
			}
		}
		if (value == null)
		{
			return;
		}
		if (value.Status == ClientStatus.Synced && !value.Removed)
		{
			Announce(info.ByPeer ? $"{value.Name} left the server." : $"{value.Name} lost connection.");
		}
		if (value.Status == ClientStatus.CaptureRequested)
		{
			_world.CancelCapture(peer);
		}
		PlayerRecord player = value.Player;
		_world.ForgetStories(peer);
		if (player != null)
		{
			lock (_sync)
			{
				player.Peer = null;
			}
			if (player.IdentityId != 0L)
			{
				PushPlayerTable();
			}
			if (player.CharacterNetId != 0 || player.CharacterRequested)
			{
				_log.Info($"sync(host): ClientId {player.ClientId} '{player.Name}' left; releasing its character NetworkId {player.CharacterNetId}");
				player.CharacterNetId = 0u;
				player.CharacterRequested = false;
				_world.ReleaseCharacter(peer, player.ClientId);
			}
		}
		_log.Info($"sync(host): client {peer} '{value.Name}' left ({info}); was {value.Status}, {value.LifecycleMessages} lifecycle msgs / {MpLogExtensions.Bytes(value.LifecycleBytes)}, state {MpLogExtensions.Bytes(value.StateBytes)}, {value.BlobsSent} blobs / {MpLogExtensions.Bytes(value.BlobBytes)}");
	}

	private void OnMessage(PeerId peer, Channel ch, byte[] data)
	{
		if (!SyncProtocol.IsSyncMessage(data))
		{
			return;
		}
		try
		{
			switch ((SyncMsg)data[0])
			{
			case SyncMsg.JoinRequest:
				HandleJoin(peer, JoinRequest.Decode(data));
				break;
			case SyncMsg.TypeKeys:
			case SyncMsg.CharacterRequest:
			case SyncMsg.SignalKeys:
			case SyncMsg.StoryMove:
			case SyncMsg.ClientCommands:
			case SyncMsg.ObservableEdits:
			case SyncMsg.ClientInput:
			case SyncMsg.ClientLook:
			case SyncMsg.SocialRequest:
			case SyncMsg.ChatSend:
			case SyncMsg.AdminRequest:
				HandleUpstream(peer, ch, data);
				break;
			case SyncMsg.ClientStatus:
			{
				var (b, value) = TextMessage.Decode(data);
				string value2;
				lock (_sync)
				{
					value2 = (_clients.TryGetValue(peer, out RemoteClient value3) ? value3.Name : "?");
					if (value3 != null)
					{
						RemoteClient remoteClient = value3;
						ClientPhase clientPhase = (ClientPhase)b;
						remoteClient.LastPhase = clientPhase.ToString();
					}
				}
				string message = $"client {peer} '{value2}': {(ClientPhase)b}: {value}";
				if (b >= 10)
				{
					_log.Error(message);
				}
				else if (b >= 9)
				{
					_log.Warn(message);
				}
				else
				{
					_log.Info(message);
				}
				break;
			}
			default:
				_log.WarnOnce($"host-unexpected-{data[0]}", $"sync(host): unexpected {(SyncMsg)data[0]} from {peer}");
				break;
			}
		}
		catch (Exception ex) when ((ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException) ? true : false)
		{
			_log.Warn($"sync(host): malformed {(SyncMsg)data[0]} from {peer} ({data.Length} B)");
		}
	}

	private void HandleJoin(PeerId peer, JoinRequest req)
	{
		RemoteClient value;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out value);
		}
		if (value == null)
		{
			return;
		}
		value.Name = req.PlayerName;
		_log.Info($"sync(host): join request from {peer} '{req.PlayerName}' (plugin {req.PluginVersion}, game {req.GameVersion}, sync v{req.SyncVersion})");
		if (req.SyncVersion != SyncProtocol.Version)
		{
			Reject(peer, VersionMismatch.Refusal(_o.PluginVersion, req.PluginVersion), JoinRejectCode.Version);
			return;
		}
		if (_o.PluginVersion.Length > 0 && req.PluginVersion != _o.PluginVersion)
		{
			Reject(peer, VersionMismatch.Refusal(_o.PluginVersion, req.PluginVersion), JoinRejectCode.Version);
			return;
		}
		if (req.GameVersion != _o.GameVersion)
		{
			_log.Warn($"sync(host): {peer} runs game {req.GameVersion}, host {_o.GameVersion}; continuing, but entity data may not match");
		}
		if (value.Status == ClientStatus.Connected && value.Player == null && !CheckAccess(peer, req))
		{
			return;
		}
		if (value.Status != ClientStatus.Connected)
		{
			_log.Warn($"sync(host): duplicate join request from {peer} ignored (status {value.Status})");
			return;
		}
		lock (_sync)
		{
			if (value.Player == null && _clients.Values.Count(c => c.Player != null && c.Peer != peer) >= _o.MaxPlayers - (_o.HostCountsAsPlayer ? 1 : 0))
			{
				Reject(peer, "The session is full.");
				return;
			}
		}
		if (req.SyncVersion == SyncProtocol.Version && value.Player == null)
		{
			value.Player = Register(peer, req.PlayerName);
			_log.Info($"sync(host): {peer} '{req.PlayerName}' is ClientId {value.Player.ClientId}" + ((value.Player.Joins > 1) ? $" again (join #{value.Player.Joins})" : " (new player)") + ", identity key " + value.Player.Key);
		}
		ContinueJoin(value);
	}

	private void ContinueJoin(RemoteClient c)
	{
		PeerId peer = c.Peer;
		if (!_world.WorldReady)
		{
			c.Status = ClientStatus.WaitingForWorld;
			_t.Send(peer, Channel.Control, TextMessage.Encode(SyncMsg.JoinWait, 0, "the host has no world loaded yet; it will be sent as soon as the host loads one"));
			_log.Info($"sync(host): {peer} waits until a world is loaded on this PC");
		}
		else
		{
			RequestCapture(c);
		}
	}

	private PlayerRecord Register(PeerId peer, string name)
	{
		lock (_sync)
		{
			string text = PlayerIdentities.KeyFor(peer, string.IsNullOrWhiteSpace(name) ? "?" : name.Trim());
			int num = 1;
			string key;
			PlayerRecord value;
			while (true)
			{
				key = ((num == 1) ? text : $"{text}#{num}");
				if (!_players.TryGetValue(key, out value))
				{
					break;
				}
				if (!value.Peer.HasValue || !(value.Peer != peer))
				{
					value.Name = name;
					value.Peer = peer;
					value.Joins++;
					value.CharacterNetId = 0u;
					value.CharacterRequested = false;
					return value;
				}
				num++;
			}
			if (_nextClientId == ushort.MaxValue)
			{
				throw new InvalidOperationException("ClientId space exhausted");
			}
			value = new PlayerRecord
			{
				Key = key,
				Name = name,
				ClientId = _nextClientId++,
				Peer = peer,
				Joins = 1
			};
			_players[key] = value;
			return value;
		}
	}

	public IReadOnlyList<PlayerRecord> Players()
	{
		lock (_sync)
		{
			return new List<PlayerRecord>(_players.Values);
		}
	}

	public void PlayerIdentified(ushort clientId, long identity, long player)
	{
		lock (_sync)
		{
			PlayerRecord playerRecord = null;
			foreach (PlayerRecord value in _players.Values)
			{
				if (value.ClientId == clientId)
				{
					playerRecord = value;
				}
			}
			if (playerRecord == null)
			{
				return;
			}
			playerRecord.IdentityId = identity;
			playerRecord.PlayerId = player;
		}
		if (identity != 0L)
		{
			PushPlayerTable();
		}
	}

	public List<PlayerInfo> PlayerTable()
	{
        List<PlayerInfo> list = new List<PlayerInfo>();
        if (_o.HostCountsAsPlayer) list.Add(new PlayerInfo(1, 1559595546L, -1559595548L, _o.HostName, Connected: true));
		lock (_sync)
		{
			foreach (PlayerRecord value in _players.Values)
			{
				if (value.IdentityId != 0L)
				{
					list.Add(new PlayerInfo(value.ClientId, value.PlayerId, value.IdentityId, value.Name, value.Peer.HasValue));
				}
			}
		}
		list.Sort((PlayerInfo a, PlayerInfo b) => a.ClientId.CompareTo(b.ClientId));
		return list;
	}

	public void SendSocial(PeerId peer, SocialSnapshot snapshot)
	{
		lock (_sync)
			if (!_clients.TryGetValue(peer, out var client) || client.Status != ClientStatus.Synced) return;
		byte[] data = SocialMessage.Encode(SyncMsg.SocialSnapshot, snapshot);
		_t.Send(peer, data.Length > 8192 ? Channel.Bulk : Channel.Control, data);
	}

	private void PushPlayerTable()
	{
		byte[] array = PlayerTableMessage.Encode(PlayerTable());
		lock (_sync)
		{
			foreach (RemoteClient value in _clients.Values)
			{
				if (value.Status == ClientStatus.Synced)
				{
					_t.Send(value.Peer, Channel.Control, array);
					PlayerTablesSent++;
				}
			}
		}
	}

	public bool PublishPlayerStatus(ushort clientId, int pcu, int maxPcu)
	{
		bool flag;
		lock (_sync)
		{
			foreach (RemoteClient value in _clients.Values)
			{
				if (value.Status != ClientStatus.Synced || value.Player?.ClientId != clientId)
				{
					continue;
				}
				(int, int)? lastStatus = value.LastStatus;
				int num = pcu;
				flag = lastStatus.HasValue;
				if (!flag)
				{
					goto IL_00e9;
				}
				if (flag)
				{
					(int, int) valueOrDefault = lastStatus.GetValueOrDefault();
					if (valueOrDefault.Item1 != num || valueOrDefault.Item2 != maxPcu)
					{
						goto IL_00e9;
					}
				}
				flag = false;
				goto IL_0148;
				IL_00e9:
				value.LastStatus = (pcu, maxPcu);
				_t.Send(value.Peer, Channel.Control, PlayerStatusMessage.Encode(clientId, pcu, maxPcu));
				flag = true;
				goto IL_0148;
			}
		}
		return false;
		IL_0148:
		return flag;
	}

	private void HandleUpstream(PeerId peer, Channel ch, byte[] data)
	{
		try
		{
			HandleUpstreamCore(peer, data);
		}
		catch (Exception ex) when ((!(ex is ArgumentOutOfRangeException) && !(ex is IndexOutOfRangeException)) || 1 == 0)
		{
			_log.WarnOnce($"host-upstream-{data[0]}", $"sync(host): handling {(SyncMsg)data[0]} from {peer} failed: {ex.GetType().Name}: {ex.Message}");
		}
	}

	private void HandleUpstreamCore(PeerId peer, byte[] data)
	{
		RemoteClient value;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out value);
		}
		if (value == null || value.Player == null)
		{
			return;
		}
		switch ((SyncMsg)data[0])
		{
		case SyncMsg.ChatSend:
			HandleChat(value, data);
			break;
		case SyncMsg.AdminRequest:
			HandleAdmin(value, data);
			break;
		case SyncMsg.SocialRequest:
			if (value.Status == ClientStatus.Synced && value.Player.IdentityId != 0)
				_world.OnSocialRequest(peer, value.Player.ClientId, SocialMessage.Decode<SocialRequest>(data));
			break;
		case SyncMsg.TypeKeys:
		{
			List<TypeKeyDef> list = TypeKeysMessage.Decode(data);
			if (_o.ResolveType == null)
			{
				break;
			}
			List<string> list2;
			lock (value.Input)
			{
				list2 = value.InputTypes.Add(list, _o.ResolveType);
			}
			_log.Info($"sync(host): ClientId {value.Player.ClientId}: {list.Count} input type key(s) ({string.Join(", ", list.Select((TypeKeyDef d) => d.ShortName))}), {list2.Count} unusable");
			{
				foreach (string item2 in list2)
				{
					_log.Warn($"sync(host): ClientId {value.Player.ClientId}: {item2}");
				}
				break;
			}
		}
		case SyncMsg.SignalKeys:
		{
			List<SignalKeyDef> list5 = SignalKeysMessage.Decode(data);
			int num6 = 0;
			lock (value.Input)
			{
				foreach (SignalKeyDef item3 in list5)
				{
					string text = ((_o.CheckSignal == null) ? "client signals disabled" : _o.CheckSignal(item3));
					value.Signals[item3.Key] = (item3, text);
					if (text != null)
					{
						num6++;
						_log.Warn($"sync(host): ClientId {value.Player.ClientId}: signal {item3.Name}: {text}; ignored");
					}
				}
			}
			_log.Info($"sync(host): ClientId {value.Player.ClientId}: {list5.Count} signal key(s), {num6} unusable");
			break;
		}
		case SyncMsg.ClientInput:
			var (num3, list3) = ClientInputMessage.Decode(data);
			lock (value.Input)
			{
				long num4 = _clock();
				if (value.InputLastArrivalMs >= 0)
				{
					long num5 = num4 - value.InputLastArrivalMs;
					if (num5 > 50)
					{
						value.InputGaps++;
						if (num5 > value.InputGapMaxMs)
						{
							value.InputGapMaxMs = num5;
						}
						if (num5 > 150 && num4 - value.InputGapLogMs > 5000)
						{
							value.InputGapLogMs = num4;
							_log.Info($"sync(host): ClientId {value.Player?.ClientId}: no input from this joiner for {num5} ms (diagnostic; {value.InputGaps} gaps > 50 ms so far)");
						}
					}
				}
				value.InputLastArrivalMs = num4;
				value.InputMessages++;
				foreach (InputEntry item4 in list3)
				{
					value.InputEntries++;
					(uint, ushort) key = (item4.NetId, item4.Key);
					if (!value.InputData.TryGetValue(key, out (uint, byte[], bool) value2) || Seq.Newer(num3, value2.Item1))
					{
						bool flag2 = value2.Item2 == null || !value2.Item2.AsSpan().SequenceEqual(item4.Bytes);
						value.InputData[key] = (num3, item4.Bytes, flag2 || value2.Item3);
					}
				}
				break;
			}
		case SyncMsg.ClientLook:
		{
			var (seq, num, num2, x, y, z) = ClientLookMessage.Decode(data);
			if (!_o.LookTotals)
			{
				if (value.LookIgnored++ == 0L)
				{
					_log.Info($"sync(host): ClientId {value.Player.ClientId}: ignoring the joiner's look totals (-y51online:predict:off on the host): " + "its turning is applied from the newest ControlData per tick as in 0.7.0, its prediction will correct more often");
				}
				break;
			}
			bool flag;
			lock (value.Input)
			{
				RemoteClient remoteClient = value;
				if (remoteClient.Look == null)
				{
					remoteClient.Look = new LookAccumulator();
				}
				flag = value.Look.Offer(seq, num, num2, x, y, z);
			}
			if (flag)
			{
				_log.Info($"sync(host): ClientId {value.Player.ClientId}: look totals for NetworkId {num} (epoch {num2}): the joiner's turning is applied " + "exactly as it predicted it (total minus applied, once per tick)" + ((num != value.Player.CharacterNetId) ? $"; its character here is NetworkId {value.Player.CharacterNetId}, waiting" : ""));
			}
			break;
		}
		case SyncMsg.ClientCommands:
			var (lastCommandSeq, list4) = ClientSignalsMessage.Decode(data);
			lock (value.Input)
			{
				value.LastCommandSeq = lastCommandSeq;
				foreach (WireCommand item5 in list4)
				{
					value.CommandsIn++;
					if (value.Signals.TryGetValue(item5.Key, out (SignalKeyDef, string) value3) && value3.Item2 == null && (value3.Item1.PayloadBytes == ushort.MaxValue || item5.Payload.Length == value3.Item1.PayloadBytes))
					{
						if (value.InputCommands.Count < 4096)
						{
							value.InputCommands.Add((value3.Item1.Name, item5));
						}
					}
					else
					{
						value.CommandsRejected++;
					}
				}
				break;
			}
		case SyncMsg.StoryMove:
		{
			StoryMove storyMove = StoryMove.Decode(data);
			value.P2.StoriesIn++;
			if (value.Status != ClientStatus.Synced)
			{
				SendStoryFinish(peer, new StoryFinish
				{
					StoryId = storyMove.StoryId,
					Status = 2,
					Text = "not in the world yet"
				});
			}
			else
			{
				_world.OnClientStory(peer, value.Player.ClientId, storyMove);
			}
			break;
		}
		case SyncMsg.ObservableEdits:
		{
			List<MemberEdit> item = ObservableEditsMessage.Decode(data).Edits;
			value.P2.ObservableEditsIn += item.Count;
			if (value.Status == ClientStatus.Synced && item.Count != 0)
			{
				_world.OnObservableEdits(peer, value.Player.ClientId, item);
			}
			break;
		}
		case SyncMsg.CharacterRequest:
		{
			var (b, choice) = CharacterRequestMessage.Decode(data);
			PlayerRecord player = value.Player;
			if (value.Status != ClientStatus.Synced)
			{
				_log.Warn($"sync(host): ClientId {player.ClientId} asked for a character before its world was captured; ignored");
				break;
			}
			if (player.CharacterNetId != 0)
			{
				_t.Send(peer, Channel.Control, PlayerAssignMessage.Encode(player.ClientId, 0, player.CharacterNetId, "already assigned"));
				break;
			}
			player.CharacterRequested = true;
			_log.Info($"sync(host): ClientId {player.ClientId} '{player.Name}' requests a character ({((b == 1) ? "respawn" : "first spawn")})");
			_world.RequestCharacter(peer, player.ClientId, player.Name, choice, b == 1);
			break;
		}
		}
	}

	public void CharacterAssigned(PeerId peer, uint netId, string detail)
	{
		RemoteClient value;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out value);
		}
		if (value?.Player != null)
		{
			value.Player.CharacterNetId = netId;
			value.Player.CharacterRequested = false;
			lock (value.Input)
			{
				value.InputData.Clear();
				value.Look = null;
			}
			_t.Send(peer, Channel.Control, PlayerAssignMessage.Encode(value.Player.ClientId, 0, netId, detail));
			_log.Info($"sync(host): CHARACTER ASSIGNED: ClientId {value.Player.ClientId} '{value.Player.Name}' controls NetworkId {netId} ({detail})");
		}
	}

	public void SendSpawnMenu(PeerId peer, SpawnMenu menu) => _t.Send(peer, Channel.Control, SocialMessage.Encode(SyncMsg.SpawnMenu, menu));

	public void CharacterFailed(PeerId peer, string why)
	{
		RemoteClient value;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out value);
		}
		if (value?.Player != null)
		{
			value.Player.CharacterRequested = false;
			_t.Send(peer, Channel.Control, PlayerAssignMessage.Encode(value.Player.ClientId, 1, 0u, why));
			_log.Error($"sync(host): no character for ClientId {value.Player.ClientId} '{value.Player.Name}': {why}");
		}
	}

	public void CharacterLost(PeerId peer, string why)
	{
		RemoteClient value;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out value);
		}
		if (value?.Player != null && value.Player.CharacterNetId != 0)
		{
			uint characterNetId = value.Player.CharacterNetId;
			value.Player.CharacterNetId = 0u;
			lock (value.Input)
			{
				value.InputData.Clear();
				value.Look = null;
			}
			_t.Send(peer, Channel.Control, PlayerAssignMessage.Encode(value.Player.ClientId, 2, characterNetId, why));
			_log.Info($"sync(host): ClientId {value.Player.ClientId} '{value.Player.Name}' lost its character NetworkId {characterNetId}: {why}");
		}
	}

	private static bool IsControlKey(RemoteClient c, LookAccumulator look, ushort key)
	{
		ushort? controlKey = look.ControlKey;
		if (controlKey.HasValue)
		{
			ushort valueOrDefault = controlKey.GetValueOrDefault();
			return valueOrDefault == key;
		}
		if (c.InputTypes.Name(key) != "ControlData" || !c.InputTypes.TryResolve(key, out var _, out var size) || size != 24)
		{
			return false;
		}
		look.ControlKey = key;
		return true;
	}

	public List<ClientInputBatch> TakeInputs()
	{
		List<ClientInputBatch> list = new List<ClientInputBatch>();
		List<RemoteClient> list2;
		lock (_sync)
		{
			list2 = new List<RemoteClient>(_clients.Values);
		}
		foreach (RemoteClient item in list2)
		{
			PlayerRecord player = item.Player;
			if (player == null || item.Status != ClientStatus.Synced || player.CharacterNetId == 0)
			{
				continue;
			}
			ClientInputBatch clientInputBatch = null;
			lock (item.Input)
			{
				if (item.InputMessages == item.InputMessagesAtTake)
				{
					item.TicksWithoutInput++;
				}
				else
				{
					item.TicksWithInput++;
				}
				item.InputMessagesAtTake = item.InputMessages;
				if (item.InputCommands.Count == 0 && item.InputData.Count == 0)
				{
					continue;
				}
				object obj;
				if (_o.LookTotals)
				{
					LookAccumulator look = item.Look;
					if (look != null && look.NetId == player.CharacterNetId)
					{
						obj = look;
						goto IL_0118;
					}
				}
				obj = null;
				goto IL_0118;
				IL_0118:
				LookAccumulator lookAccumulator = (LookAccumulator)obj;
				bool flag = false;
				foreach (var item2 in new List<(uint, ushort)>(item.InputData.Keys))
				{
					(uint, byte[], bool) tuple = item.InputData[item2];
					if (lookAccumulator != null && item2.Item1 == lookAccumulator.NetId && IsControlKey(item, lookAccumulator, item2.Item2))
					{
						if (tuple.Item3 || lookAccumulator.Control == null)
						{
							item.InputData[item2] = (tuple.Item1, tuple.Item2, false);
							lookAccumulator.Control = tuple.Item2;
							flag |= tuple.Item3;
							NoteApplied(item, tuple.Item1);
						}
					}
					else
					{
						if (!tuple.Item3)
						{
							continue;
						}
						item.InputData[item2] = (tuple.Item1, tuple.Item2, false);
						NoteApplied(item, tuple.Item1);
						if (!item.InputTypes.TryResolve(item2.Item2, out var localTypeId, out var size) || (tuple.Item2.Length != size && tuple.Item2.Length != 0))
						{
							item.InputRejected++;
							continue;
						}
						if (clientInputBatch == null)
						{
							clientInputBatch = new ClientInputBatch
							{
								Peer = item.Peer,
								ClientId = player.ClientId,
								CharacterNetId = player.CharacterNetId
							};
						}
						if (tuple.Item2.Length == 0)
						{
							clientInputBatch.Removes.Add((item2.Item1, localTypeId));
							item.InputRemoved++;
						}
						else
						{
							clientInputBatch.Data.Add((item2.Item1, localTypeId, tuple.Item2));
						}
					}
				}
				if (lookAccumulator != null)
				{
					ushort? controlKey = lookAccumulator.ControlKey;
					if (controlKey.HasValue)
					{
						ushort valueOrDefault = controlKey.GetValueOrDefault();
						byte[] control = lookAccumulator.Control;
						if (control != null && control.Length == 24 && item.InputTypes.TryResolve(valueOrDefault, out var localTypeId2, out var size2) && size2 == 24 && (lookAccumulator.Take(out var x, out var y, out var z) | flag))
						{
							if (clientInputBatch == null)
							{
								clientInputBatch = new ClientInputBatch
								{
									Peer = item.Peer,
									ClientId = player.ClientId,
									CharacterNetId = player.CharacterNetId
								};
							}
							clientInputBatch.Data.Add((lookAccumulator.NetId, localTypeId2, LookAccumulator.WithRotation(control, x, y, z)));
							item.LookControlWrites++;
						}
					}
				}
				if (item.InputCommands.Count > 0)
				{
					if (clientInputBatch == null)
					{
						clientInputBatch = new ClientInputBatch
						{
							Peer = item.Peer,
							ClientId = player.ClientId,
							CharacterNetId = player.CharacterNetId
						};
					}
					clientInputBatch.Commands.AddRange(item.InputCommands);
					item.InputCommands.Clear();
				}
				goto IL_042b;
			}
			IL_042b:
			if (clientInputBatch != null)
			{
				list.Add(clientInputBatch);
			}
		}
		SendInputAcks(list2);
		return list;
	}

	private void NoteApplied(RemoteClient c, uint seq)
	{
		if (c.AckSeq == 0 || Seq.Newer(seq, c.AckSeq))
		{
			c.AckSeq = seq;
			c.AckHostMs = (uint)_clock();
		}
	}

	// The joiner's prediction starts where this input left off (see MovePredictor.SetAck). Sent before this tick's
	// state, on the state channel, and repeated so a lost ack heals.
	private void SendInputAcks(List<RemoteClient> clients)
	{
		long now = _clock();
		foreach (RemoteClient c in clients)
		{
			if (c.AckSeq == 0 || (c.AckSentSeq == c.AckSeq && now - c.AckSentMs < 250)) continue;
			c.AckSentSeq = c.AckSeq;
			c.AckSentMs = now;
			_t.Send(c.Peer, Channel.State, InputAckMessage.Encode(c.AckSeq, c.AckHostMs));
		}
	}

	public void AddHostStrings(IReadOnlyCollection<string> names)
	{
		if (names.Count == 0)
		{
			return;
		}
		lock (_sync)
		{
			_hostStrings.AddRange(names);
		}
	}

	public void PublishSignals(uint hostTick, IReadOnlyList<WireCommand> commands)
	{
		if (commands.Count == 0 || _syncedCount == 0)
		{
			return;
		}
		AnnounceSignalKeys();
		byte[] array = SignalBatchMessage.Encode(0u, hostTick, commands);
		lock (_sync)
		{
			foreach (RemoteClient value in _clients.Values)
			{
				if (value.Status == ClientStatus.Synced && value.P2.StringsSent < _hostStrings.Count)
				{
					int num = _hostStrings.Count - value.P2.StringsSent;
					byte[] array2 = StringTableMessage.Encode(value.NextLifeSeq++, _hostStrings, value.P2.StringsSent, num);
					_t.Send(value.Peer, (array2.Length > 8192) ? Channel.Bulk : Channel.Control, array2);
					if (value.P2.StringsSent == 0)
					{
						_log.Info($"sync(host): {value.Peer}: sent {num} StringHash name(s) ({MpLogExtensions.Bytes(array2.Length)}) before its first signals");
					}
					value.P2.StringsSent += num;
					value.P2.StringsOut += num;
					value.LifecycleMessages++;
					value.LifecycleBytes += array2.Length;
				}
			}
		}
		PublishLifecycle(array);
		lock (_sync)
		{
			foreach (RemoteClient value2 in _clients.Values)
			{
				if (value2.Status == ClientStatus.Synced)
				{
					value2.P2.SignalBatches++;
					value2.P2.SignalsOut += commands.Count;
					value2.P2.SignalBytesOut += array.Length;
				}
			}
		}
	}

	private void AnnounceSignalKeys()
	{
		List<SignalKeyDef> list = SignalKeys.TakeUnannounced();
		if (list.Count == 0)
		{
			return;
		}
		byte[] array = SignalKeysMessage.Encode(list);
		lock (_sync)
		{
			foreach (RemoteClient value in _clients.Values)
			{
				if (value.Status == ClientStatus.Synced)
				{
					_t.Send(value.Peer, Channel.Control, array);
				}
			}
		}
	}

	public void SendStoryMove(PeerId peer, StoryMove move)
	{
		byte[] array = move.Encode();
		_t.Send(peer, (array.Length > 8192) ? Channel.Bulk : Channel.Control, array);
		lock (_sync)
		{
			if (_clients.TryGetValue(peer, out RemoteClient value))
			{
				value.P2.StoryMovesOut++;
			}
		}
	}

	public void SendStoryFinish(PeerId peer, StoryFinish finish)
	{
		byte[] array = finish.Encode();
		_t.Send(peer, (array.Length > 8192) ? Channel.Bulk : Channel.Control, array);
		lock (_sync)
		{
			if (_clients.TryGetValue(peer, out RemoteClient value))
			{
				value.P2.StoryFinishesOut++;
			}
		}
	}

	public void InputApplied(PeerId peer, int data, int commands, int rejected)
	{
		RemoteClient value;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out value);
		}
		if (value == null)
		{
			return;
		}
		lock (value.Input)
		{
			value.InputApplied += data;
			value.CommandsApplied += commands;
			value.InputRejected += rejected;
		}
	}

	private void RequestCapture(RemoteClient c)
	{
		c.Status = ClientStatus.CaptureRequested;
		PlayerRecord player = c.Player;
		if (player != null && _o.Settings.Identities)
		{
			player.IdentityId = 0L;
			player.PlayerId = 0L;
			_log.Info($"sync(host): registering ClientId {player.ClientId} '{player.Name}' (key {player.Key}) as its own player, then capturing world '{_world.WorldName}' for {c.Peer}");
			_world.RegisterPlayer(c.Peer, player.ClientId, player.Key, player.Name);
		}
		else
		{
			_log.Info($"sync(host): capturing world '{_world.WorldName}' for {c.Peer} at the next server tick");
		}
		_world.RequestCapture(c.Peer);
	}

	private void Reject(PeerId peer, string why, byte code = JoinRejectCode.Generic)
	{
		_log.Error($"sync(host): rejecting {peer}: {why}");
		_t.Send(peer, Channel.Control, TextMessage.Encode(SyncMsg.JoinReject, code, why));
	}

	public bool BeginCapture(PeerId peer, out uint worldLifeSeq)
	{
		return BeginCapture(peer, 1, out worldLifeSeq);
	}

	public bool BeginCapture(PeerId peer, int parts, out uint firstLifeSeq)
	{
		firstLifeSeq = 0u;
		string name;
		lock (_sync)
		{
			if (!_clients.TryGetValue(peer, out RemoteClient value) || value.Status != ClientStatus.CaptureRequested)
			{
				return false;
			}
			value.Status = ClientStatus.Synced;
			value.SyncedMs = _clock();
			firstLifeSeq = value.NextLifeSeq;
			value.NextLifeSeq += (uint)Math.Max(1, parts);
			_syncedCount++;
			name = value.Name;
		}
		Announce($"{name} joined the server.");
		return true;
	}

	public bool WantsCapture(PeerId peer)
	{
		lock (_sync)
		{
			RemoteClient value;
			return _clients.TryGetValue(peer, out value) && value.Status == ClientStatus.CaptureRequested;
		}
	}

	public void CompleteCapture(PeerId peer, byte[] snapshot, EntityBundleMessage world, string summary, bool hostMissions = false, CaptureBlobs? blobs = null)
	{
		CompleteCapture(peer, snapshot, new List<EntityBundleMessage> { world }, summary, hostMissions, blobs);
	}

	public void CompleteCapture(PeerId peer, byte[] snapshot, IReadOnlyList<EntityBundleMessage> parts, string summary, bool hostMissions = false, CaptureBlobs? blobs = null)
	{
		RemoteClient value;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out value);
		}
		if (value == null || value.Status != ClientStatus.Synced)
		{
			_log.Warn($"sync(host): capture for {peer} finished but the client is gone");
			return;
		}
		List<byte[]> list = new List<byte[]>(parts.Count);
		int num = 0;
		int num2 = 0;
		long num3 = 0L;
		foreach (EntityBundleMessage part in parts)
		{
			byte[] array = part.Encode();
			list.Add(array);
			num3 += array.Length;
			num += part.Entities.Count;
			num2 += part.Data.Count;
		}
		JoinAccept joinAccept = new JoinAccept
		{
			WorldName = _world.WorldName,
			HostTick = ((parts.Count > 0) ? parts[0].HostTick : 0u),
			SnapshotBytes = snapshot.Length,
			WorldBytes = (int)Math.Min(2147483647L, num3),
			WorldEntities = num,
			WorldParts = parts.Count,
			HostMissions = hostMissions,
			WorldBlobs = (blobs?.Data.Count ?? 0),
			WorldBlobBytes = (blobs?.Bytes ?? 0),
			ClientId = (value.Player?.ClientId ?? 0),
			Settings = _o.Settings,
			IdentityId = (value.Player?.IdentityId ?? 0),
			PlayerId = (value.Player?.PlayerId ?? 0),
			Players = PlayerTable()
		};
		_t.Send(peer, Channel.Control, joinAccept.Encode());
		List<TypeKeyDef> list2 = Keys.All();
		if (list2.Count > 0)
		{
			_t.Send(peer, Channel.Control, TypeKeysMessage.Encode(list2));
		}
		List<SignalKeyDef> list3 = SignalKeys.All();
		if (list3.Count > 0)
		{
			_t.Send(peer, Channel.Control, SignalKeysMessage.Encode(list3));
		}
		int num4;
		long num5;
		if (blobs != null)
		{
			(num4, num5) = SendBlobs(value, blobs.Snapshot, blobs.Data);
		}
		else
		{
			num4 = 0;
			num5 = 0L;
		}
		_t.Send(peer, Channel.Bulk, SnapshotMessage.Encode(snapshot, blobs?.Snapshot));
		for (int i = 0; i < list.Count; i++)
		{
			if (blobs != null)
			{
				(int Count, long Bytes) tuple2 = SendBlobs(value, parts[i].RequiredBlobs, blobs.Data);
				int item = tuple2.Count;
				long item2 = tuple2.Bytes;
				num4 += item;
				num5 += item2;
			}
			_t.Send(peer, Channel.Bulk, list[i]);
		}
		lock (_sync)
		{
			value.LifecycleMessages += list.Count;
			value.LifecycleBytes += num3 + snapshot.Length;
		}
		int num6 = 0;
		if (blobs != null)
		{
			foreach (byte[] value2 in blobs.Data.Values)
			{
				num6 += ((value2 == null) ? 1 : 0);
			}
		}
		_log.Info($"sync(host): world for {peer} '{value.Name}' queued: snapshot {MpLogExtensions.Bytes(snapshot.Length)}, world {parts.Count} part(s) {MpLogExtensions.Bytes(num3)} ({num} entities, {num2} with captured data), {list2.Count} type keys, {num4} offloaded blobs {MpLogExtensions.Bytes(num5)} ({blobs?.Snapshot.Count ?? 0} for the snapshot, {num6} missing on this PC); {summary}");
	}

	private (int Count, long Bytes) SendBlobs(RemoteClient c, IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, byte[]?> data)
	{
		if (ids.Count == 0)
		{
			return (Count: 0, Bytes: 0L);
		}
		List<KeyValuePair<Guid, byte[]>> list = new List<KeyValuePair<Guid, byte[]>>();
		long num = 0L;
		lock (_sync)
		{
			foreach (Guid id in ids)
			{
				if (data.TryGetValue(id, out byte[] value) && c.SentBlobs.Add(id))
				{
					list.Add(new KeyValuePair<Guid, byte[]>(id, value));
					num += value?.Length ?? 0;
				}
			}
			c.BlobsSent += list.Count;
			c.BlobBytes += num;
		}
		if (list.Count == 0)
		{
			return (Count: 0, Bytes: 0L);
		}
		List<byte[]> list2 = new List<byte[]>();
		BlobDataMessage.Pack(list, 1048576, list2);
		foreach (byte[] item in list2)
		{
			_t.Send(c.Peer, Channel.Bulk, item);
		}
		return (Count: list.Count, Bytes: num);
	}

	public int PublishBlobs(IReadOnlyDictionary<Guid, byte[]?> data)
	{
		if (data.Count == 0)
		{
			return 0;
		}
		List<RemoteClient> list;
		lock (_sync)
		{
			list = new List<RemoteClient>();
			foreach (RemoteClient value in _clients.Values)
			{
				if (value.Status == ClientStatus.Synced)
				{
					list.Add(value);
				}
			}
		}
		List<Guid> ids = new List<Guid>(data.Keys);
		int num = 0;
		foreach (RemoteClient item in list)
		{
			num += SendBlobs(item, ids, data).Count;
		}
		return num;
	}

	public void FailCapture(PeerId peer, string reason)
	{
		lock (_sync)
		{
			if (_clients.TryGetValue(peer, out RemoteClient value))
			{
				if (value.Status == ClientStatus.Synced)
				{
					_syncedCount--;
				}
				value.Status = ClientStatus.Connected;
			}
		}
		Reject(peer, "the host could not capture its world: " + reason);
	}

	public int PublishLifecycle(byte[] encoded)
	{
		int num = 0;
		lock (_sync)
		{
			foreach (RemoteClient value in _clients.Values)
			{
				if (value.Status == ClientStatus.Synced)
				{
					byte[] array = (byte[])encoded.Clone();
					EntityBundleMessage.SetLifeSeq(array, value.NextLifeSeq++);
					Channel channel = ((array.Length > 8192) ? Channel.Bulk : Channel.Control);
					_t.Send(value.Peer, channel, array);
					value.LifecycleMessages++;
					value.LifecycleBytes += array.Length;
					num++;
				}
			}
			return num;
		}
	}

	public bool PublishLifecycleTo(PeerId peer, byte[] encoded)
	{
		lock (_sync)
		{
			if (!_clients.TryGetValue(peer, out RemoteClient value) || value.Status != ClientStatus.Synced)
			{
				return false;
			}
			EntityBundleMessage.SetLifeSeq(encoded, value.NextLifeSeq++);
			Channel channel = ((encoded.Length > 8192) ? Channel.Bulk : Channel.Control);
			_t.Send(value.Peer, channel, encoded);
			value.LifecycleMessages++;
			value.LifecycleBytes += encoded.Length;
			value.P2.ObservableBatches++;
			value.P2.ObservableBytesOut += encoded.Length;
			return true;
		}
	}

	public void PublishDespawn(IReadOnlyList<uint> netIds, byte mode = 0)
	{
		if (netIds.Count == 0 || _syncedCount == 0)
		{
			return;
		}
		PublishLifecycle(DespawnMessage.Encode(0u, mode, netIds));
		foreach (uint netId in netIds)
		{
			_despawnedIn.Enqueue(netId);
		}
	}

	public void PublishDataRemove(IReadOnlyList<(uint NetId, ushort Key)> items)
	{
		if (items.Count != 0 && _syncedCount != 0)
		{
			PublishLifecycle(DataRemoveMessage.Encode(0u, items));
		}
	}

	public void WorldChanged(string reason)
	{
		List<PeerId> list = new List<PeerId>();
		lock (_sync)
		{
			foreach (RemoteClient value in _clients.Values)
			{
				ClientStatus status = value.Status;
				if ((status == ClientStatus.CaptureRequested || status == ClientStatus.Synced) ? true : false)
				{
					list.Add(value.Peer);
				}
			}
		}
		foreach (PeerId item in list)
		{
			_log.Warn($"sync(host): disconnecting {item}: {reason}");
			_t.Disconnect(item, 1000, reason);
		}
	}

	public void PublishState(in StateEntry e)
	{
		if (_syncedCount == 0)
		{
			Interlocked.Increment(ref StateDroppedNoClient);
			return;
		}
		Interlocked.Increment(ref StateIn);
		_stateIn.Enqueue(e);
	}

	public void Tick()
	{
		long num = _clock();
		var sw = System.Diagnostics.Stopwatch.StartNew();
		TickAdmin(num);
		long admin = sw.ElapsedMilliseconds;
		List<RemoteClient> list;
		lock (_sync)
		{
			list = new List<RemoteClient>(_clients.Count);
			foreach (RemoteClient value in _clients.Values)
			{
				if (value.Status == ClientStatus.Synced)
				{
					list.Add(value);
				}
			}
		}
		uint result;
		while (_despawnedIn.TryDequeue(out result))
		{
			foreach (RemoteClient item in list)
			{
				item.State.Remove(result);
			}
		}
		long locked = sw.ElapsedMilliseconds;
		StateEntry result2;
		while (_stateIn.TryDequeue(out result2))
		{
			foreach (RemoteClient item2 in list)
			{
				item2.State.Put(in result2, num);
			}
		}
		List<TypeKeyDef> list2 = Keys.TakeUnannounced();
		if (list2.Count > 0)
		{
			byte[] array = TypeKeysMessage.Encode(list2);
			foreach (RemoteClient item3 in list)
			{
				_t.Send(item3.Peer, Channel.Control, array);
			}
		}
		bool flag = num - _lastEmitMs >= _o.StateEmitIntervalMs;
		if (flag)
		{
			_lastEmitMs = num;
		}
		foreach (RemoteClient item4 in list)
		{
			if (flag)
			{
				_out.Clear();
				item4.State.Emit(num, 1100, _out);
				foreach (byte[] item5 in _out)
				{
					_t.Send(item4.Peer, Channel.State, item5);
					item4.StateBytes += item5.Length;
				}
			}
			if (num - item4.LastStatusMs >= _o.StatusIntervalMs)
			{
				LogStatus(item4, num);
			}
		}
		if (sw.ElapsedMilliseconds > 100)
		{
			_log.Warn($"sync(host): tick took {sw.ElapsedMilliseconds} ms (admin {admin}, clients lock {locked - admin}, state drain + send {sw.ElapsedMilliseconds - locked})");
		}
		if (num - _lastWaitCheckMs < 1000)
		{
			return;
		}
		_lastWaitCheckMs = num;
		if (!_world.WorldReady)
		{
			return;
		}
		List<RemoteClient> list3 = new List<RemoteClient>();
		lock (_sync)
		{
			foreach (RemoteClient value2 in _clients.Values)
			{
				if (value2.Status == ClientStatus.WaitingForWorld)
				{
					list3.Add(value2);
				}
			}
		}
		foreach (RemoteClient item6 in list3)
		{
			RequestCapture(item6);
		}
	}

	private void LogStatus(RemoteClient c, long now)
	{
		double num = ((c.LastStatusMs == 0L) ? Math.Max(1.0, (double)(now - c.SyncedMs) / 1000.0) : ((double)(now - c.LastStatusMs) / 1000.0));
		List<(string, double)> list = new List<(string, double)>();
		foreach (var (key, num4) in c.State.SentByKey)
		{
			c.SentByKeyAtLastStatus.TryGetValue(key, out var value);
			if (num4 > value)
			{
				list.Add((KeyName(key), (double)(num4 - value) / num));
			}
			c.SentByKeyAtLastStatus[key] = num4;
		}
		list.Sort(((string Name, double PerSec) a, (string Name, double PerSec) b) => b.PerSec.CompareTo(a.PerSec));
		string value2 = ((list.Count == 0) ? "none" : string.Join(", ", list.Take(8).Select(((string Name, double PerSec) r) => $"{r.Name} {r.PerSec:0}/s")));
		long num5 = c.StateBytes - c.StateBytesAtLastStatus;
		long num6 = c.State.Sent - c.SentAtLastStatus;
		c.LastStatusMs = now;
		c.StateBytesAtLastStatus = c.StateBytes;
		c.SentAtLastStatus = c.State.Sent;
		StateCoalescer state = c.State;
		_log.Info($"sync(host) {c.Peer} '{c.Name}': state {(double)num5 / 1024.0 / num:0.0} KiB/s ({(double)num6 / num:0}/s entries, {state.Messages} msgs total), in {state.Puts} coalesced {state.Coalesced} capped {state.DeferredByCap} budget-deferred {state.DeferredByBudget} dropped(cap 0) {state.DroppedByCap}, dirty {state.DirtyCount}/{state.EntryCount}; lifecycle {c.LifecycleMessages} msgs {MpLogExtensions.Bytes(c.LifecycleBytes)}; blobs {c.BlobsSent} ({MpLogExtensions.Bytes(c.BlobBytes)}); type keys {Keys.Count}; client phase {c.LastPhase}");
		_log.Info($"sync(host) {c.Peer} send rates: {value2}");
		PlayerRecord player = c.Player;
		if (player == null)
		{
			return;
		}
		lock (c.Input)
		{
			IMpLog log = _log;
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(337, 28);
			defaultInterpolatedStringHandler.AppendLiteral("sync(host) ");
			defaultInterpolatedStringHandler.AppendFormatted(c.Peer);
			defaultInterpolatedStringHandler.AppendLiteral(" ClientId ");
			defaultInterpolatedStringHandler.AppendFormatted(player.ClientId);
			defaultInterpolatedStringHandler.AppendLiteral(" '");
			defaultInterpolatedStringHandler.AppendFormatted(player.Name);
			defaultInterpolatedStringHandler.AppendLiteral("' (");
			defaultInterpolatedStringHandler.AppendFormatted((player.IdentityId != 0L) ? $"IdentityId {player.IdentityId}" : "host's identity");
			(int, int)? lastStatus = c.LastStatus;
			string value3;
			if (lastStatus.HasValue)
			{
				(int, int) valueOrDefault = lastStatus.GetValueOrDefault();
				value3 = $", PCU {valueOrDefault.Item1}/{valueOrDefault.Item2}";
			}
			else
			{
				value3 = "";
			}
			defaultInterpolatedStringHandler.AppendFormatted(value3);
			defaultInterpolatedStringHandler.AppendLiteral("): character ");
			string? value4;
			if (player.CharacterNetId != 0)
			{
				value4 = $"NetworkId {player.CharacterNetId}";
			}
			else
			{
				value4 = (player.CharacterRequested ? "requested" : "none");
			}
			defaultInterpolatedStringHandler.AppendFormatted(value4);
			defaultInterpolatedStringHandler.AppendLiteral("; ");
			defaultInterpolatedStringHandler.AppendLiteral("input ");
			defaultInterpolatedStringHandler.AppendFormatted(c.InputMessages);
			defaultInterpolatedStringHandler.AppendLiteral(" msgs / ");
			defaultInterpolatedStringHandler.AppendFormatted(c.InputEntries);
			defaultInterpolatedStringHandler.AppendLiteral(" values in, ");
			defaultInterpolatedStringHandler.AppendFormatted(c.InputApplied);
			defaultInterpolatedStringHandler.AppendLiteral(" applied, ");
			defaultInterpolatedStringHandler.AppendFormatted(c.InputRejected);
			defaultInterpolatedStringHandler.AppendLiteral(" rejected, ");
			defaultInterpolatedStringHandler.AppendFormatted(c.InputRemoved);
			defaultInterpolatedStringHandler.AppendLiteral(" removals, ");
			defaultInterpolatedStringHandler.AppendLiteral("arrival gaps > 50 ms ");
			defaultInterpolatedStringHandler.AppendFormatted(c.InputGaps);
			defaultInterpolatedStringHandler.AppendLiteral(" (largest ");
			defaultInterpolatedStringHandler.AppendFormatted(c.InputGapMaxMs);
			defaultInterpolatedStringHandler.AppendLiteral(" ms), host ticks with / without a fresh input message ");
			defaultInterpolatedStringHandler.AppendFormatted(c.TicksWithInput);
			defaultInterpolatedStringHandler.AppendLiteral(" / ");
			defaultInterpolatedStringHandler.AppendFormatted(c.TicksWithoutInput);
			defaultInterpolatedStringHandler.AppendLiteral("; ");
			defaultInterpolatedStringHandler.AppendLiteral("commands ");
			defaultInterpolatedStringHandler.AppendFormatted(c.CommandsIn);
			defaultInterpolatedStringHandler.AppendLiteral(" in, ");
			defaultInterpolatedStringHandler.AppendFormatted(c.CommandsApplied);
			defaultInterpolatedStringHandler.AppendLiteral(" applied, ");
			defaultInterpolatedStringHandler.AppendFormatted(c.CommandsRejected);
			defaultInterpolatedStringHandler.AppendLiteral(" rejected; ");
			defaultInterpolatedStringHandler.AppendLiteral("signals out ");
			defaultInterpolatedStringHandler.AppendFormatted(c.P2.SignalsOut);
			defaultInterpolatedStringHandler.AppendLiteral(" in ");
			defaultInterpolatedStringHandler.AppendFormatted(c.P2.SignalBatches);
			defaultInterpolatedStringHandler.AppendLiteral(" batches (");
			defaultInterpolatedStringHandler.AppendFormatted(MpLogExtensions.Bytes(c.P2.SignalBytesOut));
			defaultInterpolatedStringHandler.AppendLiteral("), names ");
			defaultInterpolatedStringHandler.AppendFormatted(c.P2.StringsOut);
			defaultInterpolatedStringHandler.AppendLiteral("; ");
			defaultInterpolatedStringHandler.AppendLiteral("stories in ");
			defaultInterpolatedStringHandler.AppendFormatted(c.P2.StoriesIn);
			defaultInterpolatedStringHandler.AppendLiteral(", moves out ");
			defaultInterpolatedStringHandler.AppendFormatted(c.P2.StoryMovesOut);
			defaultInterpolatedStringHandler.AppendLiteral(", finished ");
			defaultInterpolatedStringHandler.AppendFormatted(c.P2.StoryFinishesOut);
			defaultInterpolatedStringHandler.AppendLiteral("; ");
			defaultInterpolatedStringHandler.AppendLiteral("observables out ");
			defaultInterpolatedStringHandler.AppendFormatted(c.P2.ObservableBatches);
			defaultInterpolatedStringHandler.AppendLiteral(" batches (");
			defaultInterpolatedStringHandler.AppendFormatted(MpLogExtensions.Bytes(c.P2.ObservableBytesOut));
			defaultInterpolatedStringHandler.AppendLiteral("), edits in ");
			defaultInterpolatedStringHandler.AppendFormatted(c.P2.ObservableEditsIn);
			defaultInterpolatedStringHandler.AppendLiteral("; ");
			IMpLog mpLog = log;
			string text = defaultInterpolatedStringHandler.ToStringAndClear();
			string? text2;
			if (c.Look != null)
			{
				text2 = $"{c.Look.Describe()}, {c.LookControlWrites} ControlData written";
			}
			else
			{
				text2 = (_o.LookTotals ? "look totals: none yet" : $"look totals ignored ({c.LookIgnored}, -y51online:predict:off)");
			}
			mpLog.Info(text + text2);
		}
	}

	private string KeyName(ushort key)
	{
		foreach (TypeKeyDef item in Keys.All())
		{
			if (item.Key == key)
			{
				return item.ShortName;
			}
		}
		return $"key#{key}";
	}

	public ClientStatus? StatusOf(PeerId peer)
	{
		lock (_sync)
		{
			RemoteClient value;
			return _clients.TryGetValue(peer, out value) ? new ClientStatus?(value.Status) : ((ClientStatus?)null);
		}
	}
}
