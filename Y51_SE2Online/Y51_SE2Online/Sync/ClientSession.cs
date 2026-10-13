using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class ClientSession
{
	public enum Phase
	{
		Connecting,
		JoinSent,
		WaitingForHostWorld,
		Accepted,
		Rejected,
		Disconnected
	}

	private readonly ITransport _t;

	private readonly IMpLog _log;

	private readonly ClientSyncOptions _o;

	private readonly Func<string, (int TypeId, int Size)?> _resolveType;

	private readonly Func<long> _clock;

	private readonly ConcurrentQueue<(ClientPhase Phase, string Text)> _reports = new ConcurrentQueue<(ClientPhase, string)>();

	private byte[]? _snapshot;

	private long _snapshotTaken;

	private volatile Phase _phase;

	public long LifecycleReceived;

	public long StateMessages;

	public long StateEntries;

	public long BlobMessages;

	private long _blobLogBytes;

	private readonly HashSet<ushort> _transformKeys = new HashSet<ushort>();

	public long SmoothedEntries;

	public long SmoothedRefused;

	private long _assignVersion;

	private uint _upSeq;

	public long InputSent;

	public long CommandsSent;

	public long LookSent;

	private uint _lookSeq;

	public long StoryMovesIn;

	public long StoryFinishesIn;

	public long StoryMovesOut;

	public long ObservableOpsIn;

	public long ObservableEditsOut;

	private bool _detached;

	private uint _editSeq;

	public PeerId? Host { get; private set; }

	public Phase CurrentPhase => _phase;

	public JoinAccept? Accept { get; private set; }

	public string? RejectReason { get; private set; }

	public byte RejectCode { get; private set; }

	/// <summary>The refusing host's plugin version, from the connection handshake.</summary>
	public string RejectHostVersion { get; private set; } = "";

	public long RejectVersion { get; private set; }

	public AdminState Admin { get; private set; } = new();

	private long _adminMessageMs = long.MinValue / 2;

	public void SendChat(string text)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted)
			_t.Send(Host.Value, Channel.Control, SocialMessage.Encode(SyncMsg.ChatSend, new ChatSend(text)));
	}

	public void SendAdmin(AdminRequest request)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted)
			_t.Send(Host.Value, Channel.Control, SocialMessage.Encode(SyncMsg.AdminRequest, request));
	}

	public RemoteTypeTable Types { get; } = new RemoteTypeTable();

	public ClientStateTable State { get; } = new ClientStateTable();

	public LifecycleSequencer<LifecycleItem> Lifecycle { get; } = new LifecycleSequencer<LifecycleItem>();

	public ClientBlobStore Blobs { get; } = new ClientBlobStore();

	public IReadOnlyList<Guid> SnapshotBlobs { get; private set; } = Array.Empty<Guid>();

	public TransformSmoother Smoother { get; } = new TransformSmoother();

	public ushort ClientId => Accept?.ClientId ?? 0;

	public (byte Status, uint NetId, string Text, long Version) Assignment { get; private set; }

	public PlayerNamesInbox Names { get; } = new PlayerNamesInbox();

	public RemoteSignalTable RemoteSignals { get; } = new RemoteSignalTable();

	public ConcurrentQueue<object> StoryInbox { get; } = new ConcurrentQueue<object>();

	public IReadOnlyList<PlayerInfo> Players { get; private set; } = Array.Empty<PlayerInfo>();

	public long PlayersVersion { get; private set; }
	public SocialSnapshot Social { get; private set; } = new();
	public void SendSocial(SocialRequest request)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted && IdentityId != 0)
			_t.Send(Host.Value, Channel.Control, SocialMessage.Encode(SyncMsg.SocialRequest, request));
	}

	public long IdentityId => Accept?.IdentityId ?? 0;

	public (int Pcu, int MaxPcu, long Version) Status { get; private set; }

	public bool IsDetached => _detached;

	public bool HasSnapshot => Volatile.Read(in _snapshot) != null;

	public bool LeftOnPurpose { get; private set; }

	public event Action? SnapshotReady;

	public ClientSession(ITransport transport, IMpLog log, ClientSyncOptions options, Func<string, (int TypeId, int Size)?> resolveType, Func<long>? clock = null)
	{
		_t = transport;
		_log = log;
		_o = options;
		_resolveType = resolveType;
		_clock = clock ?? new Func<long>(MpClock.NowMs);
		_t.PeerConnected += OnConnected;
		_t.PeerDisconnected += OnDisconnected;
		_t.MessageReceived += OnMessage;
	}

	public void Detach()
	{
		if (!_detached)
		{
			_detached = true;
			_t.PeerConnected -= OnConnected;
			_t.PeerDisconnected -= OnDisconnected;
			_t.MessageReceived -= OnMessage;
		}
	}

	public byte[]? TryTakeSnapshot()
	{
		byte[] array = Volatile.Read(in _snapshot);
		if (array == null || Interlocked.Exchange(ref _snapshotTaken, 1L) != 0L)
		{
			return null;
		}
		return array;
	}

	public void Report(ClientPhase phase, string text)
	{
		string message = $"client: {phase}: {text}";
		if ((int)phase >= 10)
		{
			_log.Error(message);
		}
		else if ((int)phase >= 9)
		{
			_log.Warn(message);
		}
		else
		{
			_log.Info(message);
		}
		_reports.Enqueue((phase, text));
	}

	private void OnConnected(PeerId peer)
	{
		if (Host.HasValue)
		{
			_log.Warn($"sync(client): second host connection {peer} ignored");
			return;
		}
		Host = peer;
		_phase = Phase.JoinSent;
		JoinProgress progress = _o.Progress;
		if (progress != null && _t.TryGetStats(peer, out PeerStats stats2) && !string.IsNullOrWhiteSpace(stats2.RemoteName))
		{
			progress.SetHost(stats2.RemoteName);
		}
		_o.Progress?.Set(JoinStage.Connecting, _clock(), "Connected, asking to join...");
		JoinRequest joinRequest = new JoinRequest
		{
			PluginVersion = _o.PluginVersion,
			GameVersion = _o.GameVersion,
			PlayerName = _o.PlayerName,
			Password = _o.Password ?? ""
		};
		_t.Send(peer, Channel.Control, joinRequest.Encode());
		_log.Info($"sync(client): join request sent to {peer}");
		_reports.Enqueue((ClientPhase.JoinSent, "plugin " + _o.PluginVersion + ", game " + _o.GameVersion));
	}

	private void OnDisconnected(PeerId peer, DisconnectInfo info)
	{
		if (Host == peer)
		{
			_phase = Phase.Disconnected;
			_log.Warn($"sync(client): disconnected from host ({info})");
		}
	}

	private void OnMessage(PeerId peer, Channel ch, byte[] data)
	{
		if (!SyncProtocol.IsSyncMessage(data))
		{
			return;
		}
		PeerId? host = Host;
		if (!host.HasValue || peer != host.GetValueOrDefault())
		{
			return;
		}
		try
		{
			Handle(ch, data);
		}
		catch (Exception ex) when ((ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException || ex is ArgumentException) ? true : false)
		{
			_log.Warn($"sync(client): malformed {(SyncMsg)data[0]} ({data.Length} B on {ch}): {ex.Message}");
		}
	}

	private void Handle(Channel ch, byte[] data)
	{
		long num = _clock();
		long? num3;
		long? bulkBase;
		switch ((SyncMsg)data[0])
		{
		case SyncMsg.JoinAccept:
		{
			JoinAccept joinAccept = JoinAccept.Decode(data);
			Accept = joinAccept;
			Players = Accept.Players;
			PlayersVersion++;
			_phase = Phase.Accepted;
			PeerId? host = Host;
			if (host.HasValue)
			{
				PeerId valueOrDefault = host.GetValueOrDefault();
				if (_t.TryGetStats(valueOrDefault, out PeerStats stats))
				{
					num3 = stats.Channels[1].BytesReceived;
					goto IL_0163;
				}
			}
			num3 = null;
			goto IL_0163;
		}
		case SyncMsg.JoinReject:
			var (b, text2) = TextMessage.Decode(data);
			RejectReason = text2;
			RejectCode = b;
			RejectHostVersion = Host is PeerId rejectedBy && _t.TryGetStats(rejectedBy, out PeerStats rejectStats) ? rejectStats.RemotePluginVersion : "";
			RejectVersion++;
			if (b == JoinRejectCode.Removed)
			{
				text2 = System.Text.Json.JsonSerializer.Deserialize<RemovalNotice>(text2)?.Describe(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ?? "Removed by an admin.";
			}
			_phase = Phase.Rejected;
			_o.Progress?.Set(JoinStage.Rejected, num, text2);
			_log.Error("sync(client): JOIN REJECTED by host: " + text2);
			break;
		case SyncMsg.JoinWait:
		{
			string item5 = TextMessage.Decode(data).Text;
			_phase = Phase.WaitingForHostWorld;
			_o.Progress?.Set(JoinStage.WaitingForHostWorld, num, item5);
			_log.Info("sync(client): host says wait: " + item5);
			break;
		}
		case SyncMsg.TypeKeys:
		{
			List<TypeKeyDef> list3 = TypeKeysMessage.Decode(data);
			List<string> list4 = Types.Add(list3, _resolveType);
			foreach (TypeKeyDef item8 in list3)
			{
				if (item8.ShortName == "WorldTransform")
				{
					lock (_transformKeys)
					{
						_transformKeys.Add(item8.Key);
					}
				}
			}
			_log.Info($"sync(client): {list3.Count} type key(s) received ({Types.Count} total), {list4.Count} unusable");
			{
				foreach (string item9 in list4)
				{
					_log.Warn("sync(client): " + item9);
				}
				break;
			}
		}
		case SyncMsg.Snapshot:
		{
			byte[] array = SnapshotMessage.Decode(data, out List<Guid> requiredBlobs);
			SnapshotBlobs = requiredBlobs;
			Volatile.Write(ref _snapshot, array);
			Blobs.Check(requiredBlobs, num, out var absent);
			_log.Info($"sync(client): session snapshot received ({MpLogExtensions.Bytes(array.Length)}; references {requiredBlobs.Count} offloaded blobs, {absent} not here; blobs so far: {Blobs.Describe()})");
			_reports.Enqueue((ClientPhase.SnapshotReceived, MpLogExtensions.Bytes(array.Length)));
			try
			{
				SnapshotReady?.Invoke();
				break;
			}
			catch (Exception value2)
			{
				_log.Error($"sync(client): SnapshotReady handler threw: {value2}");
				break;
			}
		}
		case SyncMsg.EntityBundle:
		{
			EntityBundleMessage entityBundleMessage = EntityBundleMessage.Decode(data);
			Offer(entityBundleMessage.LifeSeq, new LifecycleItem
			{
				Type = SyncMsg.EntityBundle,
				Bundle = entityBundleMessage,
				WireBytes = data.Length
			});
			if (entityBundleMessage.Kind == 0)
			{
				_o.Progress?.PartReceived();
			}
			if (entityBundleMessage.Kind == 0)
			{
				_log.Info($"sync(client): world bundle received: {entityBundleMessage.Entities.Count} entities, {MpLogExtensions.Bytes(entityBundleMessage.Bundle.Length)} engine bundle, {entityBundleMessage.Data.Count} entities with data ({MpLogExtensions.Bytes(entityBundleMessage.DataBytes)}), lifeSeq {entityBundleMessage.LifeSeq}");
			}
			break;
		}
		case SyncMsg.Despawn:
			var (seq5, despawnMode, netIds) = DespawnMessage.Decode(data);
			Offer(seq5, new LifecycleItem
			{
				Type = SyncMsg.Despawn,
				NetIds = netIds,
				DespawnMode = despawnMode,
				WireBytes = data.Length
			});
			break;
		case SyncMsg.DataRemove:
			var (seq4, removes) = DataRemoveMessage.Decode(data);
			Offer(seq4, new LifecycleItem
			{
				Type = SyncMsg.DataRemove,
				Removes = removes,
				WireBytes = data.Length
			});
			break;
		case SyncMsg.SignalKeys:
		{
			List<SignalKeyDef> list6 = SignalKeysMessage.Decode(data);
			List<string> list7 = RemoteSignals.Add(list6, _o.CheckSignal);
			_log.Info($"sync(client): {list6.Count} host signal key(s) ({RemoteSignals.Count} total), {list7.Count} unusable");
			{
				foreach (string item10 in list7)
				{
					_log.WarnOnce("client-sigkey-" + item10, "sync(client): " + item10 + "; ignored");
				}
				break;
			}
		}
		case SyncMsg.SignalBatch:
			var (seq3, hostTick, signals) = SignalBatchMessage.Decode(data);
			Offer(seq3, new LifecycleItem
			{
				Type = SyncMsg.SignalBatch,
				Signals = signals,
				HostTick = hostTick,
				WireBytes = data.Length
			});
			break;
		case SyncMsg.StringTable:
			var (seq2, strings) = StringTableMessage.Decode(data);
			Offer(seq2, new LifecycleItem
			{
				Type = SyncMsg.StringTable,
				Strings = strings,
				WireBytes = data.Length
			});
			break;
		case SyncMsg.ObservableBatch:
			var (seq, list5) = ObservableBatchMessage.Decode(data);
			ObservableOpsIn += list5.Count;
			Offer(seq, new LifecycleItem
			{
				Type = SyncMsg.ObservableBatch,
				Observables = list5,
				WireBytes = data.Length
			});
			break;
		case SyncMsg.PlayerTable:
			Players = PlayerTableMessage.Decode(data);
			PlayersVersion++;
			_log.Info("sync(client): player table: " + DescribePlayers(Players));
			break;
		case SyncMsg.SocialSnapshot:
		{
			var snapshot = SocialMessage.Decode<SocialSnapshot>(data);
			if (snapshot.Actor != IdentityId || snapshot.Players == null || snapshot.Factions == null || snapshot.Grids == null) break;
			try { new SocialState { Players = snapshot.Players.ToList(), Factions = snapshot.Factions.ToList() }.Validate(); }
			catch (InvalidOperationException error) { throw new ArgumentException("Invalid social snapshot.", error); }
			Social = snapshot;
			break;
		}
		case SyncMsg.ChatLine:
		{
			var line = SocialMessage.Decode<ChatLine>(data);
			ChatLog.Add(line with { From = SocialState.Clean(line.From, 80), Text = SocialState.Clean(line.Text, ChatLog.MaxText) }, num);
			break;
		}
		case SyncMsg.InputAck:
		{
			var (ackSeq, appliedHostMs) = InputAckMessage.Decode(data);
			var sent = _inputSent[ackSeq % (uint)_inputSent.Length];
			if (sent.Seq == ackSeq && ackSeq != InputAck.Seq && (InputAck.Seq == 0 || Seq.Newer(ackSeq, InputAck.Seq)))
			{
				long rtt = num - sent.LocalMs;
				InputAck = (ackSeq, sent.LocalMs, appliedHostMs, rtt);
				if (rtt >= 0 && rtt < 2000) InputRttMs = InputRttMs < 0 ? rtt : InputRttMs + 0.1 * (rtt - InputRttMs);
				if (num - _ackWorstSinceMs > 5000) { _ackWorstSinceMs = num; InputAckWorstMs = 0; }
				if (rtt > InputAckWorstMs) InputAckWorstMs = rtt;
			}
			break;
		}
		case SyncMsg.SpawnMenu:
		{
			var menu = SocialMessage.Decode<SpawnMenu>(data);
			if (menu.Options == null || menu.Options.Length is 0 or > 64) break;
			SpawnMenu = menu with { Message = SocialState.Clean(menu.Message ?? "", 200), Options = menu.Options.Select(o => o with { Name = SocialState.Clean(o.Name ?? "", 100), Detail = SocialState.Clean(o.Detail ?? "", 120) }).ToArray() };
			break;
		}
		case SyncMsg.AdminState:
		{
			var state = SocialMessage.Decode<AdminState>(data);
			// Roster updates carry no message; keep the last action's feedback readable for a while.
			if (!string.IsNullOrEmpty(state.Message)) _adminMessageMs = num;
			else if (num - _adminMessageMs < 10000) state.Message = Admin.Message;
			Admin = state;
			break;
		}
		case SyncMsg.PlayerStatus:
		{
			(ushort ClientId, int Pcu, int MaxPcu) tuple4 = PlayerStatusMessage.Decode(data);
			int item6 = tuple4.Pcu;
			int item7 = tuple4.MaxPcu;
			Status = (Pcu: item6, MaxPcu: item7, Version: Status.Version + 1);
			break;
		}
		case SyncMsg.StoryMove:
			StoryMovesIn++;
			StoryInbox.Enqueue(StoryMove.Decode(data));
			break;
		case SyncMsg.StoryFinish:
			StoryFinishesIn++;
			StoryInbox.Enqueue(StoryFinish.Decode(data));
			break;
		case SyncMsg.BlobData:
		{
			List<BlobPiece> list2 = BlobDataMessage.Decode(data);
			BlobMessages++;
			int num4 = 0;
			foreach (BlobPiece item11 in list2)
			{
				if (!Blobs.Accept(item11, num))
				{
					num4++;
				}
			}
			if (num4 > 0)
			{
				_log.Warn($"sync(client): {num4} inconsistent blob piece(s) ignored");
			}
			if (Blobs.BytesReceived - _blobLogBytes >= 16777216 || BlobMessages == 1)
			{
				_blobLogBytes = Blobs.BytesReceived;
				_log.Info($"sync(client): offloaded blobs: {Blobs.Describe()}, {MpLogExtensions.Bytes(Blobs.BytesReceived)} received");
			}
			break;
		}
		case SyncMsg.StateBatch:
		{
			List<StateEntry> list = StateBatchMessage.Decode(data);
			StateMessages++;
			StateEntries += list.Count;
			{
				foreach (StateEntry item12 in list)
				{
					StateEntry e = item12;
					if (e.NetId == StateCoalescer.WatchNetId && StateCoalescer.WatchLog.Count < 20000)
					{
						StateCoalescer.WatchLog.Enqueue($"arrived,{num},{e.Key},{e.HostMs}");
					}
					if (Smoother.Enabled && IsTransformKey(e.Key) && !State.IsTombstoned(e.NetId) && !Smoother.IsRaw(e.NetId))
					{
						if (Smoother.Add(e.NetId, e.HostMs, e.Bytes, num))
						{
							SmoothedEntries++;
						}
						else
						{
							SmoothedRefused++;
						}
					}
					else
					{
						if (Smoother.Enabled && IsTransformKey(e.Key))
						{
							Smoother.NoteClock(e.HostMs, num);
						}
						State.Put(in e, num);
					}
				}
				break;
			}
		}
		case SyncMsg.PlayerAssign:
		{
			(ushort ClientId, byte Status, uint NetId, string Text) tuple2 = PlayerAssignMessage.Decode(data);
			ushort item = tuple2.ClientId;
			byte item2 = tuple2.Status;
			uint item3 = tuple2.NetId;
			string item4 = tuple2.Text;
			Assignment = (Status: item2, NetId: item3, Text: item4, Version: ++_assignVersion);
			SpawnMenu = null;
			string value = item2 switch
			{
				0 => $"CHARACTER ASSIGNED: NetworkId {item3}", 
				2 => $"character NetworkId {item3} lost", 
				_ => "no character", 
			};
			_log.Info($"sync(client): ClientId {item}: {value} ({item4})");
			break;
		}
		case SyncMsg.PlayerNames:
			var (num2, tags) = PlayerNamesMessage.Decode(data);
			if (Names.Accept(num2, tags))
			{
				_log.Info($"nametags(client): player list v{num2} from the host: {PlayerNamesMessage.Describe(tags)}");
			}
			break;
		default:
			{
				_log.WarnOnce($"client-unexpected-{data[0]}", $"sync(client): unexpected {(SyncMsg)data[0]} on {ch}");
				break;
			}
			IL_0163:
			bulkBase = num3;
			_o.Progress?.Accepted(Accept.WorldName, Accept.SnapshotBytes + Accept.WorldBytes + Accept.WorldBlobBytes, Accept.WorldParts, num, bulkBase);
			_o.Progress?.SetWorldEntities(Accept.WorldEntities);
			_log.Info($"sync(client): JOIN ACCEPTED: world '{Accept.WorldName}', snapshot {MpLogExtensions.Bytes(Accept.SnapshotBytes)}, world {MpLogExtensions.Bytes(Accept.WorldBytes)} in {Accept.WorldParts} part(s) ({Accept.WorldEntities} entities), {Accept.WorldBlobs} offloaded blobs ({MpLogExtensions.Bytes(Accept.WorldBlobBytes)}) on the way; this player is ClientId {Accept.ClientId}");
			_log.Info($"sync(client): host settings: {Accept.Settings}; " + ((Accept.IdentityId != 0L) ? $"this player is IdentityId {Accept.IdentityId} (PlayerIdentity {Accept.PlayerId}); players: {DescribePlayers(Accept.Players)}" : "this player plays as the host's identity (identities off on the host)"));
			break;
		}
	}

	public static string DescribePlayers(IReadOnlyList<PlayerInfo> players)
	{
		if (players.Count != 0)
		{
			return string.Join(", ", players.Select((PlayerInfo p) => $"ClientId {p.ClientId} '{p.Name}' = IdentityId {p.Identity}{(p.Connected ? "" : " (away)")}"));
		}
		return "none";
	}

	public void Leave(string reason)
	{
		LeftOnPurpose = true;
		PeerId? host = Host;
		if (host.HasValue)
		{
			PeerId valueOrDefault = host.GetValueOrDefault();
			_t.Disconnect(valueOrDefault, 1000, reason);
		}
	}

	private void Offer(uint seq, LifecycleItem item)
	{
		LifecycleReceived++;
		if (!Lifecycle.Offer(seq, item))
		{
			if (Lifecycle.Overflow > 0 && Host.HasValue)
			{
				_log.Error($"sync(client): lifecycle buffer overflow waiting for #{Lifecycle.NextExpected}; disconnecting");
				_t.Disconnect(Host.Value, 1001, "lifecycle buffer overflow");
			}
			else
			{
				_log.Warn($"sync(client): duplicate lifecycle #{seq} ignored");
			}
		}
		else if (seq != Lifecycle.NextExpected - 1 && Lifecycle.Buffered > 0 && Lifecycle.Buffered % 100 == 1)
		{
			_log.Info($"sync(client): lifecycle #{seq} arrived early, waiting for #{Lifecycle.NextExpected} ({Lifecycle.Buffered} buffered)");
		}
	}

	public bool TryGetTransformType(out int typeId, out int size)
	{
		lock (_transformKeys)
		{
			foreach (ushort transformKey in _transformKeys)
			{
				if (Types.TryResolve(transformKey, out typeId, out size) && size == 40)
				{
					return true;
				}
			}
		}
		typeId = -1;
		size = 0;
		return false;
	}

	public bool IsTransformKey(ushort key)
	{
		lock (_transformKeys)
		{
			if (!_transformKeys.Contains(key))
			{
				return false;
			}
		}
		if (Types.TryResolve(key, out var _, out var size))
		{
			return size == 40;
		}
		return false;
	}

	public void RequestCharacter(byte reason, int choice = -1)
	{
		if (Host.HasValue)
		{
			_t.Send(Host.Value, Channel.Control, CharacterRequestMessage.Encode(reason, choice));
		}
	}

	/// <summary>The host's spawn choices while we wait for a character; null once chosen.</summary>
	public SpawnMenu? SpawnMenu { get; private set; }

	public void ChooseSpawn(int index)
	{
		var menu = SpawnMenu;
		if (menu == null || index < 0 || index >= menu.Options.Length || !menu.Options[index].Available) return;
		SpawnMenu = null;
		RequestCharacter(menu.Respawn ? CharacterRequestMessage.Respawn : CharacterRequestMessage.Initial, index);
	}

	public void AnnounceInputTypes(IReadOnlyList<TypeKeyDef> defs)
	{
		if (Host.HasValue && defs.Count > 0)
		{
			_t.Send(Host.Value, Channel.Control, TypeKeysMessage.Encode(defs));
		}
	}

	public void AnnounceSignals(IReadOnlyList<SignalKeyDef> defs)
	{
		if (Host.HasValue && defs.Count > 0)
		{
			_t.Send(Host.Value, Channel.Control, SignalKeysMessage.Encode(defs));
		}
	}

	public void SendObservableEdits(IReadOnlyList<MemberEdit> edits)
	{
		if (Host.HasValue && edits.Count != 0)
		{
			for (int i = 0; i < edits.Count; i += 256)
			{
				List<MemberEdit> edits2 = edits.Skip(i).Take(256).ToList();
				byte[] array = ObservableEditsMessage.Encode(++_editSeq, edits2);
				_t.Send(Host.Value, (array.Length > 8192) ? Channel.Bulk : Channel.Control, array);
			}
			ObservableEditsOut += edits.Count;
		}
	}

	public void SendStoryMove(StoryMove move)
	{
		if (Host.HasValue)
		{
			byte[] array = move.Encode();
			_t.Send(Host.Value, (array.Length > 8192) ? Channel.Bulk : Channel.Control, array);
			StoryMovesOut++;
		}
	}

	public void SendLook(uint netId, ushort epoch, double x, double y, double z)
	{
		if (Host.HasValue)
		{
			_t.Send(Host.Value, Channel.State, ClientLookMessage.Encode(++_lookSeq, netId, epoch, x, y, z));
			LookSent++;
		}
	}

	private readonly (uint Seq, long LocalMs)[] _inputSent = new (uint, long)[512];

	/// <summary>Diagnostics: the newest input the host applied, when it was sent, and how long until its ack came back.</summary>
	public (uint Seq, long SentLocalMs, uint AppliedHostMs, long RoundTripMs) InputAck { get; private set; }

	public long InputAckWorstMs { get; private set; }

	/// <summary>Smoothed time from sending an input to the host's ack of it; -1 until the first ack.</summary>
	public double InputRttMs { get; private set; } = -1;

	private long _ackWorstSinceMs;

	public void SendUpstream(IReadOnlyList<InputEntry> input, IReadOnlyList<WireCommand> commands)
	{
		if (!Host.HasValue)
		{
			return;
		}
		uint seq = ++_upSeq;
		if (input.Count > 0)
		{
			_inputSent[seq % (uint)_inputSent.Length] = (seq, _clock());
			List<InputEntry> list = new List<InputEntry>();
			int num = 7;
			foreach (InputEntry item in input)
			{
				if (list.Count > 0 && num + 8 + item.Bytes.Length > 1100)
				{
					_t.Send(Host.Value, Channel.State, ClientInputMessage.Encode(seq, list));
					list = new List<InputEntry>();
					num = 7;
				}
				list.Add(item);
				num += 8 + item.Bytes.Length;
			}
			_t.Send(Host.Value, Channel.State, ClientInputMessage.Encode(seq, list));
			InputSent += input.Count;
		}
		if (commands.Count > 0)
		{
			byte[] array = ClientSignalsMessage.Encode(seq, commands);
			_t.Send(Host.Value, (array.Length > 8192) ? Channel.Bulk : Channel.Control, array);
			CommandsSent += commands.Count;
		}
	}

	public void Tick()
	{
		if (!Host.HasValue || _detached)
		{
			return;
		}
		if (_o.Progress != null && Accept != null && _phase == Phase.Accepted && _t.TryGetStats(Host.Value, out PeerStats stats))
		{
			_o.Progress.BulkBytes(stats.Channels[1].BytesReceived);
		}
		(ClientPhase, string) result;
		while (_reports.TryDequeue(out result))
		{
			_t.Send(Host.Value, Channel.Control, TextMessage.Encode(SyncMsg.ClientStatus, (byte)result.Item1, result.Item2));
		}
	}
}
