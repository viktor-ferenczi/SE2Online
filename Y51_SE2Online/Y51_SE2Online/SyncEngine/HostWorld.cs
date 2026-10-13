using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Keen.Game2;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.GameSystems.Control;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.Game2.Simulation.Utils;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Annotations.Signals;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.Net;
using Y51_SE2Online.Probe;
using Y51_SE2Online.Sync;
using VRage.Multiplayer.Replication.CB;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld : IHostWorld, IS2CTap
{
	private sealed class Joiner
	{
		public PeerId Peer;

		public ushort ClientId;

		public string Name;

		public Entity? Character;

		public uint NetId;

		public int Choice = -1;

		public bool Respawn;

		public List<SpawnTarget>? Menu;

		public volatile bool ShipPending;

		public volatile Entity[]? ShipRoots;

		public volatile List<Entity>? ShipSettle;

		public long ShipSettleUntil;

		public long ShipDeadline;

		public volatile bool ShipQueued;

		public bool SeatLogged;

		public readonly ConcurrentQueue<uint> Candidates = new ConcurrentQueue<uint>();

		public Entity? Seat;

		public bool SeatMismatchLogged;

		public readonly Dictionary<Entity, long> ScannedRoots = new Dictionary<Entity, long>(ReferenceEqualityComparer.Instance);

		public Entity? SeatRoot;

		public long SeatedAtMs;

		public bool Driving = true;

		public volatile uint[]? LeftGridIds;

		public long LeftAtMs;

		public uint StopGridId;
	}

	private sealed record Registration(PeerId Peer, ushort ClientId, string Key, string Name);

	private sealed class Part
	{
		public readonly EntityBundle Bundle = new EntityBundle();

		public readonly Dictionary<Entity, EntityObjectBuilder> Eobs = new Dictionary<Entity, EntityObjectBuilder>();

		public int Roots;
	}

	private readonly IMpLog _log;

	private readonly NetIdRegistry<Entity> _ids = new NetIdRegistry<Entity>();

	private readonly ConcurrentQueue<PeerId> _captureRequests = new ConcurrentQueue<PeerId>();

	private readonly HashSet<PeerId> _cancelled = new HashSet<PeerId>();

	private volatile HostSession? _host;

	private object? _serverComp;

	private long _lastServerTickMs;

	private long _version;

	private uint _hostTick;

	private bool _disabled;

	private readonly ConcurrentDictionary<Type, FieldInfo?> _closureEntityField = new ConcurrentDictionary<Type, FieldInfo>();

	private volatile IReadOnlyDataOffloading? _offloading;

	private readonly ConcurrentDictionary<ushort, Joiner> _joiners = new ConcurrentDictionary<ushort, Joiner>();

	private readonly ConcurrentDictionary<ushort, HashSet<uint>> _allowed = new ConcurrentDictionary<ushort, HashSet<uint>>();

	private readonly ConcurrentQueue<Joiner> _spawnRequests = new ConcurrentQueue<Joiner>();

	private readonly ConcurrentQueue<Joiner> _releaseRequests = new ConcurrentQueue<Joiner>();
	private int _pendingReleases;

	private volatile Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? _hostClientComp;

	private bool _upstreamFailed;

	private readonly List<(int, Segment)> _scratch = new List<(int, Segment)>();

	private readonly List<(int, Segment)> _tapScratch = new List<(int, Segment)>();

	private readonly object _signalSync = new object();

	private static readonly FieldInfo? HashToStringField = typeof(StringHash).GetField(Name("sh-hash2str"), BindingFlags.Static | BindingFlags.NonPublic);

	private static readonly FieldInfo? HashLockField = typeof(StringHash).GetField(Name("sh-lock"), BindingFlags.Static | BindingFlags.NonPublic);

	private readonly HashSet<int> _sentHashes = new HashSet<int>();

	private int _lastHashCount = -1;

	private bool _namesFailed;

	private List<WireCommand> _pendingSignals = new List<WireCommand>();

	private readonly ConcurrentDictionary<string, long> _s2cDrops = new ConcurrentDictionary<string, long>();

	private readonly ConcurrentDictionary<string, long> _c2sDrops = new ConcurrentDictionary<string, long>();

	private readonly ConcurrentDictionary<string, long> _s2cSent = new ConcurrentDictionary<string, long>();

	private readonly ConcurrentDictionary<string, long> _c2sApplied = new ConcurrentDictionary<string, long>();

	private HostStories? _stories;

	private int _serverStoryIds;

	public long SignalsForwarded;

	public long SignalsDropped;

	private long _lastAllowedMs;

	public bool SpawnNearHost;

	public bool OnePilot = true;

	public long InputDataApplied;

	public long InputCommandsApplied;

	public long InputRejected;

	public long CharactersSpawned;

	public long CharactersReleased;

	public long CharactersLost;

	public long StateUnknownEntity;

	public long SpawnBundles;

	public long SpawnBytes;

	public long Despawns;

	public long Forgets;

	public long SpawnBlobs;

	public long SpawnBlobBytes;

	public long InputRemovals;

	private readonly HashSet<int> _removalLogged = new HashSet<int>();

	private static readonly TimeSpan BlobLoadTimeout = TimeSpan.FromSeconds(60L);

	private readonly HostObservables _observables;

	private HostIdentities? _identities;

	private PermissionGate? _gate;
	internal WorldSocial Social => _worldSession == null ? null : WorldSocial.For(_worldSession);
	internal long IdentityOf(ushort clientId) => _identityOf.TryGetValue(clientId, out var identity) ? identity : 0;
	private readonly ConcurrentQueue<(PeerId? Peer, ushort ClientId, SocialRequest Request)> _socialRequests = new();
	private readonly Dictionary<long, SocialRequest> _socialViews = new();
	private readonly Dictionary<long, string> _socialMessages = new();
	private readonly ConcurrentDictionary<ushort, long> _socialRate = new();

	/// <summary>Players with their suit broadcast off: other players get no name tag for them.</summary>
	private readonly ConcurrentDictionary<ushort, bool> _silentClients = new();
	private long _socialNextPublish;
	private string Tagged(long identity, string name)
	{
		string tag = FactionTagOf(identity);
		return string.IsNullOrEmpty(tag) ? name : "[" + tag + "] " + name;
	}

	// Read from the net thread while HostSession holds its own lock, so it must not take social.Sync.
	private volatile Dictionary<long, string> _factionTags = new();

	internal string? FactionTagOf(long identity) => identity != 0 && _factionTags.TryGetValue(identity, out string tag) ? tag : null;

	internal void QueueSocial(SocialRequest request) => _socialRequests.Enqueue((null, 1, request));
	public void OnSocialRequest(PeerId peer, ushort clientId, SocialRequest request)
	{
		long now = MpClock.NowMs();
		if (_socialRequests.Count >= 128 || (_socialRate.TryGetValue(clientId, out long last) && now - last < 200)) return;
		_socialRate[clientId] = now;
		_socialRequests.Enqueue((peer, clientId, request));
	}

	private void ApplyPcuLimit(Session session)
	{
		var host = _host;
		var pcu = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.PCU.PCUSessionComponent>();
		if (host == null || pcu == null) return;
		host.Access.RememberWorldPcu(pcu.MaxPerPlayerPCU);
		int want = host.PcuLimit is int limit ? (limit == 0 ? int.MaxValue : limit) : host.Access.WorldPcu ?? pcu.MaxPerPlayerPCU;
		if (pcu.MaxPerPlayerPCU != want)
		{
			pcu.MaxPerPlayerPCU = want;
			_log.Info($"host world sync: per-player PCU limit {(want == int.MaxValue ? "removed" : want.ToString("N0"))}");
		}
		host.PcuInForce = want;
	}

	private void ApplySun(Session session)
	{
		var host = _host;
		var sun = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Sun.SunSessionComponent>();
		if (host == null || sun == null) return;
		bool rotate = host.SunRotation ?? sun.SunRotation;
		var period = host.SunPeriodMinutes is double minutes ? TimeSpan.FromMinutes(minutes) : sun.SunPeriod;
		double nudge = host.TakeSunNudge() / 360.0;
		if (rotate != sun.SunRotation || period != sun.SunPeriod || nudge != 0)
		{
			// The sun's angle is (game time of day / period + offset) turns, with the time term dropped while it is
			// stopped: carry the change into the offset so stopping or changing the day length leaves the sun put.
			double offset = sun.TimeOfDayOffset + Phase(sun.SunRotation, sun.SunPeriod) - Phase(rotate, period) + nudge;
			sun.TimeOfDayOffset = (float)(offset - Math.Floor(offset));
			sun.SunRotation = rotate;
			sun.SunPeriod = period;
			_log.Info($"host world sync: sun rotation {(rotate ? "on" : "off")}, one turn every {period.TotalMinutes:0.#} min, offset {sun.TimeOfDayOffset:0.###}");
		}
		host.SunInForce = (sun.SunRotation, sun.SunPeriod.TotalMinutes);

		double Phase(bool rotating, TimeSpan turn) => rotating && turn.TotalSeconds > 0 ? sun._time.CurrentGameTime.TimeOfDay.TotalSeconds / turn.TotalSeconds % 1.0 : 0;
	}

	private void UpdateSocial(Session session)
	{
		long socialStarted = Stopwatch.GetTimestamp();
		try { UpdateSocialCore(session); }
		finally { _socialMs += Stopwatch.GetElapsedTime(socialStarted).TotalMilliseconds; }
	}

	private void UpdateSocialCore(Session session)
	{
		ApplyPcuLimit(session);
		ApplySun(session);
		BlockSizeFilter.EnsureHost();
		var social = Social;
		if (social == null || _host == null) return;
		lock (social.Sync)
		{
			_factionTags = social.State.Factions.SelectMany(f => f.Members.Select(m => (m, f.Tag))).GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.First().Tag);
			PvpRules.SetPlayers(social.State.Players.Select(p => p.Identity),
				_joiners.Values.Select(j => IdentityOf(j.ClientId)).Append(Dedicated.DedicatedRuntime.Headless ? 0 : PlayerIdentities.HotfixIdentity),
				social.State.Factions.Select(f => (IReadOnlyCollection<long>)f.Members));
			if (TurretRelations.SetFactions(social.State.Factions.SelectMany(f => f.Members.Select(m => (m, f.Id)))) | TurretRelations.TakeRefresh())
				_log.Info($"turrets: factions or PvP changed; {TurretRelations.Refresh(session)} turret grid(s) re-evaluated their targets; {PvpRules.Describe()}");
			social.InitializeGrids(_worldSession);
			var roster = _host.PlayerTable();
			foreach (var player in roster) social.State.Register(player.Identity, player.Name);
			bool changed = false;
			while (_socialRequests.TryDequeue(out var command))
			{
				long actor = command.Peer.HasValue ? IdentityOf(command.ClientId) : PlayerIdentities.HotfixIdentity;
				if (command.Peer.HasValue && !_host.Players().Any(p => p.ClientId == command.ClientId && p.Peer == command.Peer)) continue;
				if (command.Request.Action == SocialAction.Broadcast)
				{
					if (command.Request.Offset == 0) _silentClients[command.ClientId] = true;
					else _silentClients.TryRemove(command.ClientId, out _);
					continue;
				}
				if (!roster.Any(p => p.Identity == actor)) continue;
				if (command.Request.Action == SocialAction.Refresh) _socialViews[actor] = command.Request;
				else _socialMessages[actor] = social.Apply(actor, command.Request, id => _ids.TryGet(id, out Entity entity) && !EngineCodec.IsDead(entity) ? entity : null);
				changed = true;
			}
			long now = MpClock.NowMs();
			if (!changed && now < _socialNextPublish) return;
			_socialNextPublish = now + 1000;
			var grids = session.Entities.Where(e => e.TryGet<CubeGridComponent>() != null && e.TryGet<OwnershipComponent>() != null && !EngineCodec.IsDead(e))
				.Select(e => new SocialGrid(_ids.GetOrAssign(e), SocialState.Clean(e.TryGet<CubeGridComponent>().DisplayName.ToString(), 100), TeamPermissions.OwnerOf(e), WorldSocial.SharingOf(e))).ToArray();
			foreach (var player in roster)
			{
				var view = _socialViews.TryGetValue(player.Identity, out var current) ? current : new SocialRequest(SocialAction.Refresh);
				var message = _socialMessages.TryGetValue(player.Identity, out var text) ? text : "";
				var snapshot = social.Snapshot(player.Identity, grids, view, message);
				var peer = _host.Players().FirstOrDefault(p => p.ClientId == player.ClientId)?.Peer;
				if (peer.HasValue) _host.SendSocial(peer.Value, snapshot);
			}
		}
	}

	private readonly ConcurrentQueue<Registration> _registrations = new ConcurrentQueue<Registration>();

	private readonly ConcurrentDictionary<PeerId, (long Since, ushort ClientId)> _pendingRegistration = new ConcurrentDictionary<PeerId, (long, ushort)>();

	private readonly ConcurrentDictionary<ushort, long> _identityOf = new ConcurrentDictionary<ushort, long>();

	private long _lastStatusMs;

	public const int RegistrationWaitMs = 10000;

	private volatile WorldSessionComponent? _worldSession;

	public const int WorldPartEntities = 1500;

	private static readonly HashSet<string> ShipActionStories = new HashSet<string>(StringComparer.Ordinal) { "CubeGridComponent.TogglePower", "CubeGridComponent.ToggleDampeners", "CubeGridComponent.ToggleLights", "CubeGridComponent.ToggleParkingBrakes", "GridSpeedLimitComponent.SetBoost" };

	public long PassengerActionsRefused;

	private readonly Dictionary<Entity, long> _hostSeatedSince = new Dictionary<Entity, long>(ReferenceEqualityComparer.Instance);

	private readonly HashSet<Entity> _hostDriveLogged = new HashSet<Entity>(ReferenceEqualityComparer.Instance);

	private readonly Dictionary<Entity, (long Ms, bool HostPilots)> _hostPilotScan = new Dictionary<Entity, (long, bool)>(ReferenceEqualityComparer.Instance);

	private bool _menuPauseOff;

	private bool _menuPauseFailed;

	private int _joinerCharTick;

	private int _joinerCharCount = -1;

	private readonly ConcurrentDictionary<string, long> _echoedProps = new ConcurrentDictionary<string, long>();

	private static readonly ConcurrentDictionary<Type, bool> ClientSettable = new ConcurrentDictionary<Type, bool>();

	private static readonly int[] DataRefOffsets = new int[2] { 0, 4 };

	private static readonly MethodInfo? CreateCharacterMethod = typeof(RespawnSessionComponent).GetMethod("CreateCharacter", BindingFlags.Instance | BindingFlags.NonPublic);

	private readonly ConditionalWeakTable<ReplicationCommandBuffer, StrongBox<uint>> _bufferStamp = new ConditionalWeakTable<ReplicationCommandBuffer, StrongBox<uint>>();

	private double _lastStampMs = -1.0;

	public long StampedBuffers;

	public long UnstampedPostData;

	private readonly StringIdTable _sids = new StringIdTable();

	public const int ContactCapMs = 100;

	private readonly Dictionary<(int Stub, object Target), long> _contactLast = new Dictionary<(int, object), long>();

	public long ContactCapped;

	private static readonly string[] HostPlayerEventPrefixes = new string[10] { "ClientRespawnSessionComponent+", "ClientPlayersSessionComponent+", "CameraAnalyticsSessionComponent+", "SessionInGameUISessionComponent+", "DatapadClientSessionComponent+", "FastTravelClientSessionComponent+", "TradeNotificationsSessionComponent+", "TargetingSessionComponent+", "PlayersSessionComponent+PropagateCreatedPlayerSignal", "Streamed" };

	private ObjectSpace? _tapSpace;

	private readonly object _despawnSync = new object();

	private List<(uint Id, byte Mode, Entity? Forgotten)> _pendingDespawns = new List<(uint, byte, Entity)>();

	private readonly Dictionary<uint, (long Ms, string What)> _forgotten = new Dictionary<uint, (long, string)>();

	private const int RosterIntervalMs = 500;

	private const int HostTagResolveTicks = 30;

	private long _lastRosterMs;

	private bool _rosterFailed;

	private NametagOverlay? _hostTags;

	private int _hostTagTick;

	private readonly List<(ushort ClientId, string Name, Entity? Target)> _hostTagList = new List<(ushort, string, Entity)>();

	private volatile Entity? _hostCharacterServer;

	private bool _hostCharacterLogged;

	public static HostWorld? Current { get; private set; }

	public TypeKeyTable Keys { get; } = new TypeKeyTable();

	public bool WorldReady
	{
		get
		{
			if (!_disabled && Volatile.Read(in _serverComp) != null)
			{
				return MpClock.NowMs() - Volatile.Read(in _lastServerTickMs) < 3000;
			}
			return false;
		}
	}

	public string WorldName
	{
		get
		{
			try
			{
				return _worldSession?.DisplayName ?? "?";
			}
			catch (Exception)
			{
				return "?";
			}
		}
	}

	internal Session? HostClientSession => _hostClientComp?.Session;

	public bool WorldLoaded
	{
		get
		{
			if (!_disabled && Volatile.Read(in _serverComp) is Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent inProcessReplicationSessionComponent)
			{
				return !inProcessReplicationSessionComponent.IsUnloading;
			}
			return false;
		}
	}

	private static string Name(string id)
	{
		return HookTargets.Reflected.Single((EngineMember m) => m.Id == id).Name;
	}

	private HostWorld(IMpLog log)
	{
		_log = log;
		_observables = new HostObservables(log);
	}

	public static bool CleanupPending => Current != null && Volatile.Read(ref Current._pendingReleases) != 0;

	public static void Reset()
	{
		if (Current != null)
		{
			Current._disabled = true;
			var ticks = Current._worldSession?.Entity?.TryGet<Keen.Game2.WorldTickComponent>()?._tickManagement;
			if (ticks != null) ticks.PauseWhenInMenu = true;
		}
		Current = null;
		ReplicationProbe.Tap = null;
	}

	public static HostWorld Install(IMpLog log)
	{
		if (Current == null)
		{
			Current = new HostWorld(log);
		}
		ReplicationProbe.Tap = Current;
		return Current;
	}

	public void ConfigureIdentities(HostSettings settings)
	{
		if (settings.Identities)
		{
			_identities = HostIdentities.Install(_log, settings);
		}
		TeamPermissions.Enabled = settings.Identities && settings.Grids == GridPermissionMode.Team;
		_gate = settings.Identities ? new PermissionGate() : null;
		_log.Info($"host world sync: {settings}" + (settings.Identities ? "" : " (every joiner plays as the host's identity, as in 0.5)"));
	}

	private IdentityId IdentityFor(ushort clientId)
	{
		if (!_identityOf.TryGetValue(clientId, out var value))
		{
			return InProcessPerPlayerDataSessionComponent.HotfixIdentity;
		}
		return new IdentityId(value);
	}

	private ushort SourceFor(ushort clientId)
	{
		if (!_identityOf.ContainsKey(clientId))
		{
			return 1;
		}
		return clientId;
	}

	public void RegisterPlayer(PeerId client, ushort clientId, string identityKey, string name)
	{
		if (_identities != null)
		{
			_pendingRegistration[client] = (MpClock.NowMs(), clientId);
			_registrations.Enqueue(new Registration(client, clientId, identityKey, name));
		}
	}

	private void ServerRegister(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent server, Registration r)
	{
		HostSession host = _host;
		try
		{
			if (_identities != null && host != null)
			{
				if (!_pendingRegistration.ContainsKey(r.Peer))
				{
					_log.Warn($"host world sync: registration of ClientId {r.ClientId} '{r.Name}' came after its world capture; it plays as the host's identity until it rejoins");
					return;
				}
				HostIdentities.Result result = _identities.Register(server.Session, r.ClientId, r.Key, r.Name);
				_identityOf[r.ClientId] = result.Identity;
				_log.Info($"host world sync: PLAYER REGISTERED: ClientId {r.ClientId} '{r.Name}' -> IdentityId {result.Identity} (PlayerIdentity {result.Player}, key {r.Key}; {(result.NewPlayer ? "new in this world session" : "already known in this world session")}; {result.Detail})");
				host.PlayerIdentified(r.ClientId, result.Identity, result.Player);
			}
		}
		catch (Exception value)
		{
			_identityOf.TryRemove(r.ClientId, out var _);
			_log.Error($"host world sync: registering ClientId {r.ClientId} '{r.Name}' as its own player failed; it plays as the host's identity (as in 0.5): {value}");
			host?.PlayerIdentified(r.ClientId, 0L, 0L);
		}
		finally
		{
			_pendingRegistration.TryRemove(r.Peer, out (long, ushort) _);
		}
	}

	private void PublishPlayerStatus(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		HostSession host = _host;
		long num = MpClock.NowMs();
		if (host == null || _identityOf.IsEmpty || num - _lastStatusMs < 1000)
		{
			return;
		}
		_lastStatusMs = num;
		try
		{
			foreach (var (clientId, identity) in _identityOf)
			{
				(int, int)? tuple = HostIdentities.ReadPcu(comp.Session, identity);
				if (tuple.HasValue)
				{
					(int, int) valueOrDefault = tuple.GetValueOrDefault();
					host.PublishPlayerStatus(clientId, valueOrDefault.Item1, valueOrDefault.Item2);
				}
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("host-pcu-status", $"host world sync: reading joiners' PCU failed ({ex.GetType().Name}: {ex.Message}); their PCU readout stays as it is");
		}
	}

	private bool IsAdminClient(ushort clientId) => _host?.IsAdminClient(clientId) ?? clientId == SyncProtocol.HostClientId;

	private string? StoryGate(ushort clientId, object box, Session? server)
	{
		if (!IsAdminClient(clientId) && AdminGuard.IsAdminStory(box.GetType()))
		{
			Interlocked.Increment(ref AdminGuard.Refused);
			return "Only admins can do that on this server.";
		}
		if (box.GetType().DeclaringType?.Namespace?.Contains(".Ownership", StringComparison.Ordinal) == true || box.GetType().DeclaringType == typeof(GridPermissionsComponent))
			return "Use the grid ownership controls to change ownership or sharing.";
		if (_identityOf.TryGetValue(clientId, out long actor))
		{
			string denied = SocialGuard.CheckObject(box, actor);
			if (denied != null) return denied;
		}
		if (OnePilot)
		{
			string text = PassengerGate(clientId, box);
			if (text != null)
			{
				return text;
			}
		}
		PermissionGate gate = _gate;
		if (gate == null || server == null || !_identityOf.TryGetValue(clientId, out var value))
		{
			return null;
		}
		string text2 = gate.StoryValidator(box.GetType());
		if (text2 == null)
		{
			return null;
		}
		return gate.Check(text2, (ObjectCodec.Fields(box.GetType()).FirstOrDefault((FieldInfo f) => f.Name == "<>4__this")?.GetValue(box) as Component)?.Entity, value, server.SessionComponents.TryGet<PermissionsSessionComponent>());
	}

	public void Bind(HostSession host)
	{
		_host = host;
	}

	public void SetWorldSession(WorldSessionComponent world)
	{
		_worldSession = world;
	}

	public void RequestCapture(PeerId client)
	{
		lock (_cancelled)
		{
			_cancelled.Remove(client);
		}
		_captureRequests.Enqueue(client);
	}

	public void CancelCapture(PeerId client)
	{
		lock (_cancelled)
		{
			_cancelled.Add(client);
		}
	}

	private void Fail(string where, Exception e)
	{
		_disabled = true;
		_log.Error($"host world sync: {where} failed, world sync disabled for this session (game unaffected): {e}");
	}

	public static void CommitPostfix(object __instance)
	{
		HostWorld current = Current;
		if (current == null || current._disabled)
		{
			return;
		}
		try
		{
			Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent inProcessReplicationSessionComponent = (Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent)__instance;
			if (inProcessReplicationSessionComponent.IsServer)
			{
				long started = Stopwatch.GetTimestamp();
				current._socialMs = 0;
				current.OnServerCommit(inProcessReplicationSessionComponent);
				current.NoteFrameWork(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
			}
		}
		catch (Exception e)
		{
			current.Fail("CommitFrame postfix", e);
		}
	}

	private double _socialMs;
	private long _slowWorkLogMs;

	// Joiners see a stalled server frame as everyone popping; this says whether the plugin's own frame work was it.
	private void NoteFrameWork(double ms)
	{
		if (ms < 25.0) return;
		long now = MpClock.NowMs();
		if (now - _slowWorkLogMs < 2000) return;
		_slowWorkLogMs = now;
		_log.Warn($"host world sync: slow frame work {ms:0} ms (factions/ownership refresh {_socialMs:0} ms)");
	}

	private void OnServerCommit(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		if (comp.IsUnloading)
		{
			if (_serverComp == comp)
			{
				Volatile.Write(ref _serverComp, null);
				_log.Info("host world sync: server scene unloading; joined clients are disconnected");
				_host?.WorldChanged("the host left the world");
			}
			return;
		}
		if (_serverComp != comp)
		{
			bool flag = _serverComp != null;
			_identityOf.Clear();
			_ids.Clear();
			_ids.Bind(comp.Entity, 1u);
			try
			{
				_offloading = comp.Session.TryGet<IReadOnlyDataOffloading>() ?? comp.Session.ExternalServices?.TryGet<IReadOnlyDataOffloading>();
			}
			catch (Exception ex)
			{
				_offloading = null;
				_log.Warn($"host world sync: no data offloading service ({ex.GetType().Name}: {ex.Message}); voxel data cannot be sent");
			}
			if (_offloading == null)
			{
				_log.Warn("host world sync: the server session has no IReadOnlyDataOffloading; planets/asteroids will fail on clients");
			}
			Volatile.Write(ref _serverComp, comp);
			_log.Info($"host world sync: server scene attached (world '{WorldName}', data offloading {_offloading?.GetType().Name ?? "MISSING"}); friends can join now");
			if (flag)
			{
				_host?.WorldChanged("the host loaded another world");
			}
		}
		Volatile.Write(ref _lastServerTickMs, MpClock.NowMs());
		_hostTick++;
		if (_joiners.IsEmpty && _menuPauseOff)
		{
			SyncMenuPause();
		}
		if (!_joiners.IsEmpty && !_upstreamFailed)
		{
			try
			{
				UpdateJoiners(comp);
			}
			catch (Exception value)
			{
				_upstreamFailed = true;
				_log.Error($"host world sync: joining players' characters failed and are switched off for this session (world sync continues): {value}");
			}
		}
		if (!_identityOf.IsEmpty)
		{
			PublishPlayerStatus(comp);
		}
		UpdateNametagRoster(comp);
		List<PeerId> list = null;
		PeerId result;
		while (_captureRequests.TryDequeue(out result))
		{
			lock (_cancelled)
			{
				if (_cancelled.Remove(result))
				{
					continue;
				}
			}
			if (_pendingRegistration.TryGetValue(result, out (long, ushort) value2))
			{
				if (MpClock.NowMs() - value2.Item1 < 10000)
				{
					(list ?? (list = new List<PeerId>())).Add(result);
					continue;
				}
				_pendingRegistration.TryRemove(result, out (long, ushort) _);
				_identityOf.TryRemove(value2.Item2, out var _);
				_log.Warn($"host world sync: {result}'s player registration did not run within {10} s; capturing its world anyway (it plays as the host's identity)");
			}
			Capture(comp, result);
		}
		if (list == null)
		{
			return;
		}
		foreach (PeerId item in list)
		{
			_captureRequests.Enqueue(item);
		}
	}

	internal static bool HasMissions(SessionComponentsSnapshot snapshot)
	{
		return snapshot.Components?.Any((SessionComponentsSnapshot.ComponentInfo c) => c.Type?.Name == "MissionSessionComponent") ?? false;
	}

	private void Capture(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp, PeerId peer)
	{
		HostSession host = _host;
		if (host == null || !host.WantsCapture(peer))
		{
			return;
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			Session session = comp.Session;
			Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext serverSceneContext = (Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext)comp._context;
			ReplicationStrategy replicationStrategy = ((Keen.VRage.Multiplayer.Replications.InProcess.SceneContext)serverSceneContext).Replication.ReplicationStrategy;
			ReplayContext replayContext = comp.ReplayContext;
			SessionComponentsSnapshot snapshot = SessionComponentsSnapshot.Capture(session, (Entity _, bool __) => false, null);
			bool missions = HasMissions(snapshot);
			HashSet<Type> clientObTypes = null;
			try
			{
				Session session2 = ((Keen.VRage.Multiplayer.Replications.InProcess.SceneContext)serverSceneContext).Other?.SessionComponent?.Session;
				if (session2 != null)
				{
					clientObTypes = EngineCodec.SessionObjectBuilderTypes(session2);
				}
			}
			catch (Exception ex)
			{
				_log.Warn("host world sync: reading the local client's session components failed (" + ex.GetType().Name + "); sending every blob the snapshot references");
			}
			IReadOnlyDataOffloading offloading = _offloading;
			List<Part> parts = new List<Part>
			{
				new Part()
			};
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			int num5 = 0;
			HashSet<Entity> hashSet = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
			foreach (Entity entity in session.Entities)
			{
				if (entity == comp.Entity)
				{
					continue;
				}
				if (entity.Data.Has<ParentData>())
				{
					num3++;
				}
				else if (!replayContext.HasPairingLocal(entity))
				{
					num2++;
				}
				else
				{
					if (!hashSet.Add(entity))
					{
						continue;
					}
					List<Part> list = parts;
					Part part = list[list.Count - 1];
					if (part.Eobs.Count >= 1500)
					{
						parts.Add(part = new Part());
					}
					int count = part.Eobs.Count;
					try
					{
						replicationStrategy.SerializeForClientAsBundle(entity, part.Bundle, part.Eobs);
						part.Roots++;
						num++;
						num5 += part.Eobs.Count - count;
					}
					catch (Exception ex2)
					{
						if (++num4 <= 3)
						{
							_log.Warn($"host world sync: serializing root entity {entity} failed: {ex2.GetType().Name}: {ex2.Message}");
						}
					}
				}
			}
			List<Part> list2 = parts;
			if (list2[list2.Count - 1].Eobs.Count == 0 && parts.Count > 1)
			{
				parts.RemoveAt(parts.Count - 1);
			}
			List<EntityBundleMessage> msgs = new List<EntityBundleMessage>(parts.Count);
			int num6 = 0;
			int num7 = 0;
			foreach (Part item in parts)
			{
				foreach (Entity key in item.Eobs.Keys)
				{
					_ids.GetOrAssign(key);
				}
				EntityBundleMessage entityBundleMessage = new EntityBundleMessage
				{
					Kind = 0,
					HostTick = _hostTick
				};
				foreach (KeyValuePair<Entity, EntityObjectBuilder> eob in item.Eobs)
				{
					DEntityBuilder dataStorage = default;
					replicationStrategy.CaptureEntityData(eob.Key, eob.Value.Definition, ref dataStorage);
					List<DataBlob> list3 = EngineCodec.Blobs(ref dataStorage, Keys);
					if (list3.Count > 0)
					{
						entityBundleMessage.Data.Add((_ids.GetOrAssign(eob.Key), list3));
						num6 += list3.Count;
					}
				}
				entityBundleMessage.Entities = EngineCodec.Locate(item.Bundle, item.Eobs, (Entity e) => _ids.GetOrAssign(e), out var unlocated);
				num7 += unlocated;
				msgs.Add(entityBundleMessage);
			}
			if (!host.BeginCapture(peer, msgs.Count, out var firstLifeSeq))
			{
				_log.Info($"host world sync: {peer} left before its world was captured");
				return;
			}
			for (int num8 = 0; num8 < msgs.Count; num8++)
			{
				msgs[num8].LifeSeq = firstLifeSeq + (uint)num8;
			}
			double totalMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
			string pre = $"captured on the server thread in {totalMilliseconds:0} ms: {num} roots / {num5} entities in {msgs.Count} part(s) ({num3} child entities in scene, {num2} roots not on the local client skipped, {num4} roots failed, {num7} unlocated), {num6} data blobs";
			_log.Info($"host world sync: world for {peer} {pre}");
			Task.Run(() =>
			{
				Stopwatch stopwatch2 = Stopwatch.StartNew();
				try
				{
					NetIdProxyContext netIdProxyContext = new NetIdProxyContext((Entity e) => (int)_ids.GetOrAssign(e), null);
					HashSet<Blob> hashSet2;
					byte[] snapshot2;
					string value;
					if (clientObTypes != null)
					{
						hashSet2 = EngineCodec.SnapshotBlobs(snapshot, (Type t) => clientObTypes.Contains(t), out int _, out List<string> owners);
						snapshot2 = EngineCodec.SerializeSnapshot(snapshot, netIdProxyContext);
						value = ((owners.Count == 0) ? "none" : string.Join(", ", owners));
					}
					else
					{
						hashSet2 = new HashSet<Blob>();
						snapshot2 = EngineCodec.SerializeSnapshot(snapshot, netIdProxyContext, hashSet2);
						value = $"{hashSet2.Count} (all components)";
					}
					int written = netIdProxyContext.Written;
					HashSet<Blob> hashSet3 = new HashSet<Blob>(hashSet2);
					for (int num9 = 0; num9 < msgs.Count; num9++)
					{
						HashSet<Blob> hashSet4 = new HashSet<Blob>();
						msgs[num9].Bundle = EngineCodec.SerializeBundle(parts[num9].Bundle, netIdProxyContext, hashSet4);
						msgs[num9].RequiredBlobs = EngineCodec.Ids(hashSet4);
						hashSet3.UnionWith(hashSet4);
					}
					double totalMilliseconds2 = stopwatch2.Elapsed.TotalMilliseconds;
					CaptureBlobs captureBlobs = new CaptureBlobs
					{
						Snapshot = EngineCodec.Ids(hashSet2)
					};
					int failed = 0;
					string firstError = null;
					if (hashSet3.Count > 0)
					{
						if (offloading != null)
						{
							captureBlobs.Data = EngineCodec.LoadBlobs(offloading, hashSet3, BlobLoadTimeout, out failed, out firstError);
						}
						else
						{
							foreach (Blob item2 in hashSet3)
							{
								captureBlobs.Data[EngineCodec.Id(item2)] = null;
							}
							failed = hashSet3.Count;
							firstError = "no data offloading service on the host";
						}
					}
					string value2 = $"{captureBlobs.Data.Count} offloaded blobs {MpLogExtensions.Bytes(captureBlobs.Bytes)} (snapshot: {value}; parts: {string.Join("/", msgs.Select((EntityBundleMessage m) => m.RequiredBlobs.Count))}), loaded in {stopwatch2.Elapsed.TotalMilliseconds - totalMilliseconds2:0} ms";
					_log.Info($"host world sync: world for {peer}: {value2}");
					if (failed > 0)
					{
						_log.Error($"host world sync: {failed} offloaded blob(s) could not be loaded on this PC (first: {firstError}); the entities using them will be missing on {peer}");
					}
					host.CompleteCapture(peer, snapshot2, msgs, $"{pre}; serialized in {totalMilliseconds2:0} ms ({written} entity refs in snapshot, {netIdProxyContext.Written - written} in bundles); {value2}; missions {(missions ? "on" : "off")}", missions, captureBlobs);
				}
				catch (Exception ex4)
				{
					_log.Error($"host world sync: serializing the world for {peer} failed: {ex4}");
					host.FailCapture(peer, ex4.GetType().Name + ": " + ex4.Message);
				}
			});
		}
		catch (Exception ex3)
		{
			_log.Error($"host world sync: capturing the world for {peer} failed: {ex3}");
			host.FailCapture(peer, ex3.GetType().Name + ": " + ex3.Message);
		}
	}

	private void Assign(HostSession host, Joiner j, Entity? entity2, IdentityId identityId, Entity? entity, string text)
	{
		if (entity2 == null)
		{
			host.CharacterFailed(j.Peer, "spawning through " + text + " returned nothing");
			return;
		}
		j.Character = entity2;
		j.NetId = _ids.GetOrAssign(entity2);
		CharactersSpawned++;
		string text2 = "?";
		try
		{
			WorldTransform worldTransform = entity2.Data.GetWorldTransform();
			text2 = $"at {worldTransform.Position.X:0.0}, {worldTransform.Position.Y:0.0}, {worldTransform.Position.Z:0.0}";
			if (entity != null && entity.Data.TryGet<WorldTransform>(out var data2))
			{
				text2 += $", {Vector3D.Distance(in data2.Position, in worldTransform.Position):0.0} m from the host's character";
			}
		}
		catch (Exception)
		{
		}
		_log.Info($"host world sync: SPAWNED character for ClientId {j.ClientId} '{j.Name}': NetworkId {j.NetId}, owner IdentityId {identityId}{(((long)identityId == -1559595548) ? " (the host's identity)" : "")}, via {text}, {text2}");
		host.CharacterAssigned(j.Peer, j.NetId, "spawned via " + text + ", " + text2);
	}

	public void RequestCharacter(PeerId client, ushort clientId, string name, int choice, bool respawn)
	{
		Joiner item = _joiners.AddOrUpdate(clientId, (ushort _) => new Joiner
		{
			Peer = client,
			ClientId = clientId,
			Name = name,
			Choice = choice,
			Respawn = respawn
		}, (ushort _, Joiner old) =>
		{
			old.Peer = client;
			old.Choice = choice;
			old.Respawn = respawn;
			return old;
		});
		_spawnRequests.Enqueue(item);
	}

	public void ReleaseCharacter(PeerId client, ushort clientId)
	{
		_silentClients.TryRemove(clientId, out _);
		if (_joiners.TryRemove(clientId, out Joiner value))
		{
			_allowed.TryRemove(clientId, out HashSet<uint> _);
			Interlocked.Increment(ref _pendingReleases);
			_releaseRequests.Enqueue(value);
		}
	}

	internal string DescribeActor(Entity character)
	{
		foreach (Joiner value in _joiners.Values)
		{
			if (value.Character == character)
			{
				return $"ClientId {value.ClientId} '{value.Name}'";
			}
		}
		if (!_ids.TryGet(character, out var id))
		{
			return "the host's player";
		}
		return $"the host's player (character NetworkId {id})";
	}

	internal string DescribeNetId(Entity e)
	{
		if (!_ids.TryGet(e, out var id))
		{
			return "(no NetworkId)";
		}
		return $"NetworkId {id}";
	}

	internal bool IsJoinerCharacter(Entity? serverEntity)
	{
		if (serverEntity == null)
		{
			return false;
		}
		foreach (Joiner value in _joiners.Values)
		{
			if (value.Character == serverEntity)
			{
				return true;
			}
		}
		return false;
	}

	private string? PassengerGate(ushort clientId, object box)
	{
		Type type = box.GetType();
		Type declaringType = type.DeclaringType;
		int num = type.Name.IndexOf('<');
		int num2 = type.Name.IndexOf('>');
		if (declaringType == null || num != 0 || num2 < 2 || !ShipActionStories.Contains(declaringType.Name + "." + type.Name.Substring(1, num2 - 1)))
		{
			return null;
		}
		if (!_joiners.TryGetValue(clientId, out Joiner value) || value.Seat == null || value.SeatRoot == null || value.Driving)
		{
			return null;
		}
		Entity entity = (ObjectCodec.Fields(type).FirstOrDefault((FieldInfo f) => f.Name == "<>4__this")?.GetValue(box) as Component)?.Entity;
		if (entity == null)
		{
			return null;
		}
		for (int num3 = 0; num3 < 16; num3++)
		{
			Entity entity2 = entity.TryGet<HierarchyComponent>()?.Parent;
			if (entity2 == null)
			{
				break;
			}
			entity = entity2;
		}
		if (entity != value.SeatRoot)
		{
			return null;
		}
		Interlocked.Increment(ref PassengerActionsRefused);
		return "passenger seat: only the ship's pilot switches its power, dampeners, lights, brakes and boost";
	}

	private bool MayDriveGrid(Joiner j, Entity root, long now)
	{
		if (j.SeatRoot != root)
		{
			j.SeatRoot = root;
			j.SeatedAtMs = now;
			j.Driving = true;
		}
		if (!OnePilot)
		{
			return true;
		}
		string text = null;
		if (HostPilots(root, now) && _hostSeatedSince.TryGetValue(root, out var value) && value <= j.SeatedAtMs)
		{
			text = "the host's player sat down first and flies it";
		}
		else
		{
			foreach (Joiner value2 in _joiners.Values)
			{
				if (value2 != j && value2.Seat != null && value2.SeatRoot == root && (value2.SeatedAtMs < j.SeatedAtMs || (value2.SeatedAtMs == j.SeatedAtMs && value2.ClientId < j.ClientId)))
				{
					text = $"ClientId {value2.ClientId} '{value2.Name}' sat down first and flies it";
					break;
				}
			}
		}
		bool flag = text == null;
		if (flag != j.Driving)
		{
			j.Driving = flag;
			_log.Info(flag ? $"host world sync: ClientId {j.ClientId} '{j.Name}' now flies its ship (NetworkId {(_ids.TryGet(root, out var id) ? id : 0)})" : ($"host world sync: ClientId {j.ClientId} '{j.Name}' sits in another seat of ship NetworkId {(_ids.TryGet(root, out var id2) ? id2 : 0)}: {text}; " + "its flight input is ignored until then (two pilots made the ship swing between their inputs)"));
		}
		return flag;
	}

	private bool HostPilots(Entity root, long now)
	{
		if (_hostPilotScan.TryGetValue(root, out (long, bool) value) && now - value.Item1 < 500)
		{
			return value.Item2;
		}
		bool flag = false;
		Stack<Entity> stack = new Stack<Entity>();
		stack.Push(root);
		int num = 0;
		while (stack.Count > 0 && num < 20000 && !flag)
		{
			Entity entity = stack.Pop();
			num++;
			SeatComponent seatComponent = entity.TryGet<SeatComponent>();
			if (seatComponent != null)
			{
				Entity pilot = seatComponent.Pilot;
				if (pilot != null && entity.TryGet<CockpitComponent>() != null && !IsJoinerCharacter(pilot))
				{
					flag = true;
				}
			}
			PooledList<Entity> pooledList = entity.TryGet<HierarchyComponent>()?.Children;
			if (pooledList == null)
			{
				continue;
			}
			foreach (Entity item in pooledList)
			{
				stack.Push(item);
			}
		}
		if (_hostPilotScan.Count > 64)
		{
			_hostPilotScan.Clear();
		}
		_hostPilotScan[root] = (now, flag);
		if (flag)
		{
			_hostSeatedSince.TryAdd(root, now);
		}
		else
		{
			_hostSeatedSince.Remove(root);
		}
		if (_hostSeatedSince.Count > 64)
		{
			_hostSeatedSince.Clear();
		}
		return flag;
	}

	private static Entity? FindPilotedSeat(Entity root, Entity pilot, out int scanned, out string seats)
	{
		scanned = 0;
		List<string> list = new List<string>();
		Stack<Entity> stack = new Stack<Entity>();
		stack.Push(root);
		Entity entity = null;
		while (stack.Count > 0 && scanned < 20000)
		{
			Entity entity2 = stack.Pop();
			scanned++;
			SeatComponent seatComponent = entity2.TryGet<SeatComponent>();
			if (seatComponent != null)
			{
				if (seatComponent.Pilot == pilot)
				{
					if (entity == null)
					{
						entity = entity2;
					}
				}
				else if (list.Count < 4)
				{
					list.Add((seatComponent.Pilot == null) ? "empty" : "other pilot");
				}
			}
			PooledList<Entity> pooledList = entity2.TryGet<HierarchyComponent>()?.Children;
			if (pooledList == null)
			{
				continue;
			}
			foreach (Entity item in pooledList)
			{
				stack.Push(item);
			}
		}
		object obj;
		if (list.Count != 0)
		{
			obj = string.Join(", ", list);
		}
		else
		{
			obj = ((entity != null) ? "ours" : "none");
		}
		seats = (string)obj;
		return entity;
	}

	internal void SyncMenuPause()
	{
		if (_menuPauseFailed)
		{
			return;
		}
		bool flag = !_disabled;
		if (flag == _menuPauseOff)
		{
			return;
		}
		try
		{
			var ticks = _worldSession?.Entity?.TryGet<Keen.Game2.WorldTickComponent>()?._tickManagement;
			if (ticks == null) return;
			ticks.PauseWhenInMenu = !flag;
			_menuPauseOff = flag;
			_log.Info(flag ? "host world sync: hosting keeps the world running while menus are open" : "host world sync: the ESC menu pauses the world again");
		}
		catch (Exception ex)
		{
			_menuPauseFailed = true;
			_log.Warn($"host world sync: cannot switch off pause-in-menu ({ex.GetType().Name}: {ex.Message}); the host's ESC menu will freeze joiners");
		}
	}

	private void UpdateJoiners(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		long num = MpClock.NowMs();
		bool flag = num - _lastAllowedMs >= 250;
		if (flag)
		{
			_lastAllowedMs = num;
		}
		if (flag)
		{
			SyncMenuPause();
		}
		foreach (Joiner value3 in _joiners.Values)
		{
			Entity character = value3.Character;
			if (character == null)
			{
				continue;
			}
			bool flag2 = EngineCodec.IsDead(character);
			string text = "removed from the world";
			if (!flag2)
			{
				try
				{
					CharacterLifeComponent characterLifeComponent = character.TryGet<CharacterLifeComponent>();
					if (characterLifeComponent != null && !characterLifeComponent.IsAlive)
					{
						flag2 = true;
						text = "died";
					}
				}
				catch (Exception)
				{
				}
			}
			if (flag2)
			{
				value3.Character = null;
				value3.NetId = 0u;
				_allowed.TryRemove(value3.ClientId, out HashSet<uint> _);
				CharactersLost++;
				_log.Info($"host world sync: ClientId {value3.ClientId} '{value3.Name}': character {text}");
				_host?.CharacterLost(value3.Peer, text);
			}
			else
			{
				if (!flag && _allowed.ContainsKey(value3.ClientId))
				{
					continue;
				}
				HashSet<uint> hashSet = new HashSet<uint>();
				Collect(character, hashSet, 0);
				try
				{
					uint result;
					while (value3.Candidates.TryDequeue(out result))
					{
						if (value3.Seat != null || !_ids.TryGet(result, out Entity obj))
						{
							continue;
						}
						Entity entity = obj;
						for (int i = 0; i < 16; i++)
						{
							Entity entity2 = entity.TryGet<HierarchyComponent>()?.Parent;
							if (entity2 == null)
							{
								break;
							}
							entity = entity2;
						}
						long num2 = MpClock.NowMs();
						if (!value3.ScannedRoots.TryGetValue(entity, out var value2) || num2 - value2 >= 2000)
						{
							value3.ScannedRoots[entity] = num2;
							Entity entity3 = FindPilotedSeat(entity, character, out int scanned, out string seats);
							if (entity3 != null)
							{
								value3.Seat = entity3;
								_log.Info($"host world sync: ClientId {value3.ClientId} '{value3.Name}' is seated (seat {(_ids.TryGet(entity3, out var id) ? id : 0)}, grid scan {scanned} entities); its input may drive the seat and its grid");
							}
							else if (!value3.SeatMismatchLogged)
							{
								value3.SeatMismatchLogged = true;
								_log.Warn($"host world sync: ClientId {value3.ClientId} '{value3.Name}' sends input for NetworkId {result} ({obj.GetComposition()?.Guid}) but no seat in that grid ({scanned} entities scanned; seats: {seats}) is piloted by its character; refused");
							}
						}
					}
					Entity seat = value3.Seat;
					if (seat != null)
					{
						if (EngineCodec.IsDead(seat) || seat.TryGet<SeatComponent>()?.Pilot != character)
						{
							_log.Info($"host world sync: ClientId {value3.ClientId} '{value3.Name}' left its seat");
							if (value3.Driving && value3.SeatRoot != null)
							{
								List<uint> list = new List<uint>();
								if (_ids.TryGet(value3.SeatRoot, out var id2))
								{
									list.Add(id2);
								}
								Entity entity4 = seat.TryGet<CubeBlockComponent>()?.Grid?.Entity;
								if (entity4 != null && _ids.TryGet(entity4, out var id3))
								{
									list.Add(id3);
									value3.StopGridId = id3;
								}
								value3.LeftAtMs = num;
								value3.LeftGridIds = list.ToArray();
							}
							value3.Seat = null;
							value3.SeatRoot = null;
						}
						else
						{
							Collect(seat, hashSet, 0);
							Entity entity5 = seat;
							for (int j = 0; j < 16; j++)
							{
								Entity entity6 = entity5.TryGet<HierarchyComponent>()?.Parent;
								if (entity6 == null)
								{
									break;
								}
								entity5 = entity6;
							}
							if (MayDriveGrid(value3, entity5, num))
							{
								if (_ids.TryGet(entity5, out var id4))
								{
									hashSet.Add(id4);
								}
								Entity entity7 = seat.TryGet<CubeBlockComponent>()?.Grid?.Entity;
								if (entity7 != null && _ids.TryGet(entity7, out var id5))
								{
									hashSet.Add(id5);
								}
							}
						}
					}
				}
				catch (Exception ex2)
				{
					_log.WarnOnce("host-seat", $"host world sync: seat check failed ({ex2.GetType().Name}: {ex2.Message})");
				}
				_allowed[value3.ClientId] = hashSet;
			}
		}
		void Collect(Entity e, HashSet<uint> set, int depth)
		{
			if (_ids.TryGet(e, out var id6))
			{
				set.Add(id6);
			}
			if (depth <= 8)
			{
				PooledList<Entity> pooledList = e.TryGet<HierarchyComponent>()?.Children;
				if (pooledList != null)
				{
					foreach (Entity item in pooledList)
					{
						Collect(item, set, depth + 1);
					}
				}
			}
		}
	}

	public static void CommitPrefix(object __instance)
	{
		HostWorld current = Current;
		if (current == null || current._disabled || !(__instance is Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent inProcessReplicationSessionComponent))
		{
			return;
		}
		if (inProcessReplicationSessionComponent.IsServer)
		{
			try
			{
				if (inProcessReplicationSessionComponent == Volatile.Read(in current._serverComp))
				{
					current.StampServerBuffer(inProcessReplicationSessionComponent);
				}
				return;
			}
			catch (Exception ex)
			{
				current._log.WarnOnce("host-stamp", $"host world sync: stamping server ticks failed ({ex.GetType().Name}: {ex.Message}); state uses the replay time");
				return;
			}
		}
		if (current._upstreamFailed)
		{
			return;
		}
		try
		{
			current.OnHostClientCommit(inProcessReplicationSessionComponent);
		}
		catch (Exception value)
		{
			current._upstreamFailed = true;
			current._log.Error($"host world sync: joining players' input/characters failed and are switched off for this session (world sync continues): {value}");
		}
	}

	private void CollectHostStrings(HostSession host)
	{
		if (_namesFailed)
		{
			return;
		}
		try
		{
			if (HashToStringField?.GetValue(null) is Dictionary<StringHash, string> dictionary)
			{
				object obj = HashLockField?.GetValue(null);
				if (obj != null)
				{
					List<string> list = null;
					lock (obj)
					{
						if (dictionary.Count == _lastHashCount)
						{
							return;
						}
						_lastHashCount = dictionary.Count;
						foreach (KeyValuePair<StringHash, string> item in dictionary)
						{
							if (_sentHashes.Add(item.Key.Hash))
							{
								(list ?? (list = new List<string>())).Add(item.Value);
							}
						}
					}
					if (list != null)
					{
						host.AddHostStrings(list);
					}
					return;
				}
			}
			throw new MissingFieldException("StringHash._hashToString/_lock");
		}
		catch (Exception ex)
		{
			_namesFailed = true;
			_log.Error($"host world sync: cannot read the game's StringHash names ({ex.GetType().Name}: {ex.Message}); joiners may crash on host animation signals");
		}
	}

	private void OnHostClientCommit(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent server = Volatile.Read(in _serverComp) as Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent;
		if (server == null || comp._context.Other?.SessionComponent != server)
		{
			return;
		}
		_hostClientComp = comp;
		UpdateHostNametags(comp);
		UpdateJoinerCharacters(comp);
		DrainHostWork(comp, server, false);
	}

	internal void HeadlessServerWork(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent server)
	{
		if (!HeadlessReplication.Active || _disabled || _upstreamFailed || server.IsUnloading || _serverComp != server) return;
		try { DrainHostWork(server, server, true); }
		catch (Exception ex)
		{
			_upstreamFailed = true;
			_log.Error("dedicated: server input/character processing failed: " + ex);
			throw;
		}
	}

	private void DrainHostWork(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp,
		Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent server, bool headless)
	{
		void OnServer(Action action)
		{
			if (headless) action();
			else comp.PostInvoke(_ => action());
		}
		OnServer(() => UpdateSocial(server.Session));
		OnServer(() => UpdateAdmin(server.Session));
		Registration result;
		while (_registrations.TryDequeue(out result))
		{
			Registration r = result;
			OnServer(delegate
			{
				ServerRegister(server, r);
			});
		}
		List<WireCommand> list = null;
		lock (_signalSync)
		{
			if (_pendingSignals.Count > 0)
			{
				list = _pendingSignals;
				_pendingSignals = new List<WireCommand>();
			}
		}
		if (list != null && _host != null)
		{
			CollectHostStrings(_host);
			List<string> list2 = _sids.TakePending();
			if (list2 != null)
			{
				_host.AddHostStrings(list2);
			}
			_host.PublishSignals(_hostTick, list);
		}
		if (_host != null)
		{
			FlushDespawns(_host);
		}
		HostSession obsHost = _host;
		if (obsHost != null)
		{
			try
			{
				_observables.Flush((PeerId peer, byte[] msg) => obsHost.PublishLifecycleTo(peer, msg), comp._exchangeSync);
			}
			catch (Exception ex)
			{
				_log.WarnOnce("host-observables-flush", $"host world sync: observables flush failed ({ex.GetType().Name}: {ex.Message}); joiners' screens may stop updating");
			}
		}
		HostStories stories = _stories;
		if (stories != null && stories.Pending)
		{
			Session serverSession = server.Session;
			stories.SourceFor = SourceFor;
			stories.Gate = StoryGate;
			stories.RootsFor = _observables.AddRoots;
			stories.Drain(comp, () => _host, () => ServerSpace(server), serverSession, headless);
		}
		Joiner result2;
		while (_releaseRequests.TryDequeue(out result2))
		{
			Joiner rel = result2;
			OnServer(delegate
			{
				try { ServerRelease(server, rel); }
				finally { Interlocked.Decrement(ref _pendingReleases); }
			});
		}
		Joiner result3;
		while (_spawnRequests.TryDequeue(out result3))
		{
			Joiner req = result3;
			OnServer(delegate
			{
				ServerSpawn(server, req);
			});
		}
		foreach (Joiner pending in _joiners.Values)
		{
			if (!pending.ShipPending && pending.ShipSettle == null || pending.ShipQueued) continue;
			pending.ShipQueued = true;
			OnServer(delegate
			{
				pending.ShipQueued = false;
				ServerShip(server, pending);
			});
		}
		HostSession host = _host;
		if (host == null || !host.AnySynced)
		{
			return;
		}
		List<ClientInputBatch> list3 = host.TakeInputs();
		if (list3.Count == 0)
		{
			return;
		}
		object exchangeSync = comp._exchangeSync;
		if (exchangeSync == null)
		{
			return;
		}
		lock (exchangeSync)
		{
			foreach (ClientInputBatch item in list3)
			{
				Inject(comp, item, host);
			}
		}
	}

	private void UpdateJoinerCharacters(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		if (!ControlGuard.Enabled && !ControlGuard.HostFlightGate)
		{
			return;
		}
		int num = 0;
		foreach (Joiner value in _joiners.Values)
		{
			num += ((value.Character != null) ? 1 : 0);
		}
		if (_joinerCharTick++ % 5 != 0 && num == _joinerCharCount)
		{
			return;
		}
		_joinerCharCount = num;
		try
		{
			List<Entity> list = new List<Entity>(num);
			List<Entity> list2 = new List<Entity>();
			foreach (Joiner value2 in _joiners.Values)
			{
				Entity entity = Local(value2.Character);
				if (entity != null)
				{
					list.Add(entity);
				}
				if (!OnePilot)
				{
					continue;
				}
				Entity seat = value2.Seat;
				if (seat != null && value2.Driving && value2.SeatRoot != null)
				{
					Entity entity2 = Local(value2.SeatRoot);
					if (entity2 != null)
					{
						list2.Add(entity2);
					}
					Entity entity3 = Local(seat.TryGet<CubeBlockComponent>()?.Grid?.Entity);
					if (entity3 != null && entity3 != list2.LastOrDefault())
					{
						list2.Add(entity3);
					}
				}
			}
			foreach (Entity parked in _parked.Keys)
			{
				if (EngineCodec.IsDead(parked)) _parked.TryRemove(parked, out _);
				else if (Local(parked) is Entity local) list.Add(local);
			}
			ControlGuard.SetJoinerCharacters(list.ToArray());
			ControlGuard.SetJoinerDriven(list2.ToArray());
		}
		catch (Exception ex)
		{
			_log.WarnOnce("host-control-guard", $"host: resolving the joiners' characters / ships for the control guard failed ({ex.GetType().Name}: {ex.Message}); retrying");
		}
		Entity? Local(Entity? server)
		{
			if (server == null || EngineCodec.IsDead(server))
			{
				return null;
			}
			bool skip = false;
			return comp.ReplayContext.TranslateObject(server, ReplicationFailureStrategy.NullOnAnyFail, ref skip) as Entity;
		}
	}

	private static bool IsClientSettableProperty(CommandLayout l)
	{
		Type signalType = l.SignalType;
		if ((object)signalType != null)
		{
			return ClientSettable.GetOrAdd(signalType, (Type tt) => tt.GetCustomAttribute<SignalSignature>()?.SyncProperty ?? false);
		}
		return false;
	}

	internal static void WriteMarkedData(ReplicationCommandBuffer cb, int stub, Entity sessionEntity, Entity target, int typeId, byte[]? bytes)
	{
		int num = ((bytes == null) ? 12 : (16 + bytes.Length));
		Span<byte> span = ((num > 512) ? ((Span<byte>)new byte[num]) : stackalloc byte[num]);
		Span<byte> span2 = span;
		BinaryPrimitives.WriteInt32LittleEndian(span2.Slice(8), typeId);
		if (bytes != null)
		{
			BinaryPrimitives.WriteInt32LittleEndian(span2.Slice(12), bytes.Length);
			bytes.CopyTo(span2.Slice(16));
		}
		CommandBuffers.Append(cb, stub, span2, (int off) => cb.StoreObject((object)((off == 0) ? sessionEntity : target)), DataRefOffsets, 1580056798);
	}

	private void Inject(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp, ClientInputBatch b, HostSession host)
	{
		HashSet<uint> hashSet = (_allowed.TryGetValue(b.ClientId, out HashSet<uint> value) ? value : null);
		ReplayContext replay = comp.ReplayContext;
		bool headless = HeadlessReplication.Active && comp.IsServer;
		ReplicationCommandBuffer buffer = headless ? Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.AllocateCB() : ((ReplicationCommandBufferSessionComponent)(object)comp).Buffer;
		new VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader(buffer);
		int assemblyOffset = VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader.AssemblyOffset;
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		foreach (var datum in b.Data)
		{
			uint item = datum.NetId;
			int item2 = datum.TypeId;
			byte[] item3 = datum.Bytes;
			Entity entity = ((hashSet != null && hashSet.Contains(item)) ? Local(item) : null);
			if (entity == null || WorldSocial.Access(entity, IdentityOf(b.ClientId)) == false)
			{
				num3++;
				if (_joiners.TryGetValue(b.ClientId, out Joiner value2) && value2.Seat == null && value2.Candidates.Count < 64)
				{
					value2.Candidates.Enqueue(item);
				}
			}
			else
			{
				WriteMarkedData(buffer, assemblyOffset + 1, comp.Entity, entity, item2, item3);
				num++;
			}
		}
		_joiners.TryGetValue(b.ClientId, out Joiner value3);
		uint stopGrid = (value3 != null) ? Interlocked.Exchange(ref value3.StopGridId, 0u) : 0u;
		if (stopGrid != 0 && Local(stopGrid) is Entity stopped)
		{
			// The joiner's own zero flight input on leaving is dropped as "not ours" / not allowed, so the server kept the last thrust/turn.
			foreach (Type flightType in new[] { typeof(Keen.Game2.Simulation.WorldObjects.Movement.ControlData), typeof(Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData) })
			{
				Keen.VRage.DCS.Internal.RuntimeDataInfo.Info flightInfo = Keen.VRage.DCS.Internal.RuntimeDataInfo.Of(flightType);
				WriteMarkedData(buffer, assemblyOffset + 1, comp.Entity, stopped, flightInfo.TypeId, new byte[flightInfo.Size]);
				num++;
			}
		}
		foreach (var remove in b.Removes)
		{
			uint item4 = remove.NetId;
			int item5 = remove.TypeId;
			uint[] array = ((value3 != null) ? value3.LeftGridIds : null);
			bool flag = array != null && Array.IndexOf(array, item4) >= 0 && MpClock.NowMs() - value3.LeftAtMs < 3000;
			Entity entity2 = (((hashSet?.Contains(item4) ?? false) | flag) ? Local(item4) : null);
			if (entity2 == null || WorldSocial.Access(entity2, IdentityOf(b.ClientId)) == false)
			{
				num3++;
				continue;
			}
			WriteMarkedData(buffer, assemblyOffset + 2, comp.Entity, entity2, item5, null);
			num++;
			InputRemovals++;
			if (InputRemovals <= 3 || _removalLogged.Add(item5))
			{
				_log.Info($"host world sync: ClientId {b.ClientId}: removed {EngineCodec.DescribeType(item5).Name} on NetworkId {item4} like its game did (first ones / first of each type)");
			}
		}
		ObjectSpace sp = new ObjectSpace
		{
			Name = "host-client",
			IdOf = (Entity e) => (!_ids.TryGet(e, out var id)) ? ((uint?)null) : new uint?(id),
			Explain = ExplainNetId,
			Resolve = (uint id, bool _) => Local(id),
			Session = comp.Session,
			TranslatedSession = comp.Session
		};
		object serverComp = _serverComp;
		bool flag2 = serverComp != null && ReplicationProbe.IsGuarded(serverComp);
		PermissionGate gate = _gate;
		long num4 = ((gate != null && _identityOf.TryGetValue(b.ClientId, out var value4)) ? value4 : 0);
		PermissionsSessionComponent permissions = ((num4 != 0L) ? comp.Session.SessionComponents.TryGet<PermissionsSessionComponent>() : null);
		ReplicationCommandBuffer replicationCommandBuffer = null;
		int num5 = 0;
		ObjectSpace objectSpace = null;
		foreach (var command in b.Commands)
		{
			string item6 = command.Signal;
			WireCommand item7 = command.Command;
			CommandLayout commandLayout = CommandLayouts.ByName(item6);
			string fail = null;
			if (!flag2)
			{
				fail = "replay guard not armed (see the probe:CommitFrame line)";
			}
			else if (commandLayout == null || !commandLayout.Forwardable)
			{
				fail = "not forwardable here";
			}
			else if (AdminGuard.IsAdminSignal(item6) && !IsAdminClient(b.ClientId))
			{
				fail = "admin only";
				Interlocked.Increment(ref AdminGuard.Refused);
			}
			else if (SocialGuard.IsOwnershipSignal(item6))
			{
				fail = "ownership changes require the authenticated grid ownership controls";
			}
			else if (item7.Refs.Any(r => r.Kind == WireKind.Entity && WorldSocial.Access(Local(r.Id), num4) == false))
			{
				fail = "grid access denied by its owner";
			}
			else if (ControlGuard.Enabled && ControlGuard.IsControllerOnly(commandLayout) && item7.Refs.Count > 0 && item7.Refs[0].Kind == WireKind.Entity && (hashSet == null || !hashSet.Contains(item7.Refs[0].Id)) && Local(item7.Refs[0].Id)?.TryGet<CharacterControllableComponent>() != null)
			{
				fail = "not allowed: another player's character (controller-only signal)";
				Interlocked.Increment(ref ControlGuard.HostInjectDropped);
			}
			else
			{
				if (num4 != 0L && item7.Refs.Count > 0 && item7.Refs[0].Kind == WireKind.Entity)
				{
					string text = gate.Check(gate.SignalValidator(item6), Local(item7.Refs[0].Id), num4, permissions);
					if (text != null)
					{
						fail = "not allowed: " + text;
						goto IL_04eb;
					}
				}
				if (!SignalCodec.Write(buffer, commandLayout, item7, sp, _scratch, ref fail) && fail == null)
				{
					fail = "?";
				}
			}
			goto IL_04eb;
			IL_04eb:
			if (fail != null)
			{
				num3++;
				NoteC2SDrop(item6, fail);
				continue;
			}
			if (_c2sApplied.AddOrUpdate(ShortName(item6), 1L, (string _, long c) => c + 1) == 1)
			{
				_log.Info($"host world sync: ClientId {b.ClientId}: joiner signal {ShortName(item6)} handed to the server (first one)");
			}
			num2++;
			if (!IsClientSettableProperty(commandLayout))
			{
				continue;
			}
			if (!headless)
			{
				string fail2 = null;
				if (replicationCommandBuffer == null)
				{
					replicationCommandBuffer = Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.AllocateCB();
				}
				if (objectSpace == null)
				{
					objectSpace = new ObjectSpace
					{
						Name = "host-client-inbound",
						IdOf = (Entity e) => (!_ids.TryGet(e, out var id)) ? ((uint?)null) : new uint?(id),
						Explain = ExplainNetId,
						Resolve = (uint id, bool _) => (id == 0 || !_ids.TryGet(id, out Entity obj)) ? null : obj,
						Session = (serverComp as Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent)?.Session,
						TranslatedSession = (serverComp as Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent)?.Session
					};
				}
				if (SignalCodec.Write(replicationCommandBuffer, commandLayout, item7, objectSpace, _scratch, ref fail2))
				{
					num5++;
				}
			}
			SignalKeyDef orAdd = host.SignalKeys.GetOrAdd(commandLayout.Name, commandLayout.Def(0).PayloadBytes, commandLayout.Signature);
			lock (_signalSync)
			{
				_pendingSignals.Add(new WireCommand
				{
					Key = orAdd.Key,
					Payload = item7.Payload,
					Refs = item7.Refs
				});
			}
			if (_echoedProps.AddOrUpdate(ShortName(item6), 1L, (string _, long c) => c + 1) == 1)
			{
				_log.Info($"host world sync: ClientId {b.ClientId}: {ShortName(item6)} is a client-set property: shown on the host and sent to every joiner (first one)");
			}
		}
		if (headless)
		{
			if (num + num2 > 0) comp.ReplayBuffer(buffer);
			else Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.ReturnCB(buffer);
		}
		if (replicationCommandBuffer != null)
		{
			if (num5 > 0)
			{
				comp.ReplayBuffer(replicationCommandBuffer);
			}
			else
			{
				Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.ReturnCB(replicationCommandBuffer);
			}
		}
		InputDataApplied += num;
		InputCommandsApplied += num2;
		InputRejected += num3;
		host.InputApplied(b.Peer, num, num2, num3);
		Entity? Local(uint netId)
		{
			if (netId == 0 || !_ids.TryGet(netId, out Entity obj))
			{
				return null;
			}
			if (headless) return EngineCodec.IsDead(obj) ? null : obj;
			bool skip = false;
			return replay.TranslateObject(obj, ReplicationFailureStrategy.NullOnAnyFail, ref skip) as Entity;
		}
	}

	private void ServerSpawn(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent server, Joiner j)
	{
		HostSession host = _host;
		if (host == null)
		{
			return;
		}
		try
		{
			if (!_joiners.TryGetValue(j.ClientId, out Joiner value) || value != j)
			{
				return;
			}
			RootPerPlayerData(server, j);
			if (j.Character != null && !EngineCodec.IsDead(j.Character))
			{
				host.CharacterAssigned(j.Peer, j.NetId, "existing character");
				return;
			}
			Session session = server.Session;
			IRespawn respawn = session.TryGet<IRespawn>();
			if (respawn == null)
			{
				host.CharacterFailed(j.Peer, "the host world has no respawn service (IRespawn)");
				return;
			}
			IdentityId identityId = IdentityFor(j.ClientId);
			HashSet<Entity> others = new HashSet<Entity>(from x in _joiners.Values
				select x.Character into x
				where x != null
				select x, ReferenceEqualityComparer.Instance);
			List<Entity> list = new List<Entity>();
			Entity entity = null;
			if (session.TryFillAliveCharactersHotfix(list, preserveOrder: true))
			{
				entity = list.FirstOrDefault((Entity x) => !others.Contains(x) && (long?)x.TryGet<OwnershipComponent>()?.Owner == -1559595548) ?? list.FirstOrDefault((Entity x) => !others.Contains(x));
			}
			Entity entity2 = null;
			Entity entity3 = (((long)identityId != -1559595548) ? SavedCharacter(session, identityId, list, others) : null);
			string text;
			if (entity3 != null)
			{
				_parked.TryRemove(entity3, out _);
				entity2 = entity3;
				text = "its saved character (the host's save kept it; same identity)";
			}
			else if (j.ShipPending || !ChosenSpawn(server.Session, host, j, respawn, identityId, entity, out entity2, out text))
			{
				return;
			}
			Assign(host, j, entity2, identityId, entity, text);
		}
		catch (Exception ex2)
		{
			_log.Error($"host world sync: spawning a character for ClientId {j.ClientId} failed: {ex2}");
			host.CharacterFailed(j.Peer, ex2.GetType().Name + ": " + ex2.Message);
		}
	}

	private readonly System.Collections.Concurrent.ConcurrentDictionary<Entity, byte> _parked = new(ReferenceEqualityComparer.Instance);

	/// <summary>Clears held movement/flight input so a left-behind character or ship does not keep moving.</summary>
	private static void StopInput(Entity entity)
	{
		if (entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ControlData>(out _)) entity.Data.Set(default(Keen.Game2.Simulation.WorldObjects.Movement.ControlData));
		if (entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData>(out _)) entity.Data.Set(default(Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData));
		if (entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.AdvancedCharacterControlData>(out _)) entity.Data.Set(default(Keen.Game2.Simulation.WorldObjects.Characters.AdvancedCharacterControlData));
	}

	private static Entity? SavedCharacter(Session session, IdentityId identity, List<Entity> alive, HashSet<Entity> others)
	{
		Entity entity = null;
		try
		{
			entity = session.TryGet<IPerPlayerData>()?.GetPerPlayerData<ControlPerPlayerData>(identity).LastControlledEntity;
		}
		catch (Exception)
		{
		}
		Entity entity2 = null;
		foreach (Entity item in alive)
		{
			if (others.Contains(item) || EngineCodec.IsDead(item) || EngineCodec.IsClosing(session, item))
			{
				continue;
			}
			OwnershipComponent ownershipComponent = item.TryGet<OwnershipComponent>();
			if (ownershipComponent != null && (long)ownershipComponent.Owner == (long)identity)
			{
				if (item == entity)
				{
					return item;
				}
				if (entity2 == null)
				{
					entity2 = item;
				}
			}
		}
		return entity2;
	}

	// A leaving player's character stays in the world (with its position, inventory, skin and seat) and is saved with
	// it; their next join takes it over again through SavedCharacter instead of respawning at a medbay.
	private void ServerRelease(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent server, Joiner j)
	{
		try
		{
			Entity character = j.Character;
			j.Character = null;
			if (character != null && !EngineCodec.IsDead(character) && !EngineCodec.IsClosing(server.Session, character))
			{
				StopInput(character);
				Entity seatGrid = j.Seat?.TryGet<CubeBlockComponent>()?.Grid?.Entity;
				if (seatGrid != null) StopInput(seatGrid);
				if (j.SeatRoot != null && j.SeatRoot != seatGrid) StopInput(j.SeatRoot);
				_parked[character] = 0;
				CharactersReleased++;
				_log.Info($"host world sync: ClientId {j.ClientId} '{j.Name}' left; their character (NetworkId {j.NetId}) stays where it is for their next join");
			}
		}
		catch (Exception ex)
		{
			_log.Warn($"host world sync: releasing the character of ClientId {j.ClientId} failed: {ex.GetType().Name}: {ex.Message}");
		}
	}

	public static void StagedBundlePrefix(ServerSceneContext.ServerToClientEntityReplicationData data) => BundlePostfix(data);

	public static void BundlePostfix(object __result)
	{
		HostWorld current = Current;
		if (current == null || current._disabled)
		{
			return;
		}
		HostSession? host = current._host;
		if (host == null || !host.AnySynced)
		{
			return;
		}
		ServerSceneContext.ServerToClientEntityReplicationData val = (ServerSceneContext.ServerToClientEntityReplicationData)((__result is ServerSceneContext.ServerToClientEntityReplicationData) ? __result : null);
		if (val == null)
		{
			return;
		}
		try
		{
			current.ForwardSpawn(val);
		}
		catch (Exception e)
		{
			current.Fail("BuildAndReset postfix", e);
		}
	}

	private void ForwardSpawn(ServerSceneContext.ServerToClientEntityReplicationData data)
	{
		if (data.EntityObjectBuilders.Count == 0 && data.SkippedEntities.Count == 0)
		{
			return;
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		foreach (Entity key in data.EntityObjectBuilders.Keys)
		{
			_ids.GetOrAssign(key);
		}
		NetIdProxyContext proxies = new NetIdProxyContext((Entity e) => (int)_ids.GetOrAssign(e), null);
		HashSet<Blob> hashSet = new HashSet<Blob>();
		EntityBundleMessage entityBundleMessage = new EntityBundleMessage
		{
			Kind = 1,
			HostTick = _hostTick,
			Bundle = ((data.ServerBundle.Entities.Count > 0) ? EngineCodec.SerializeBundle(data.ServerBundle, proxies, hashSet) : Array.Empty<byte>()),
			Entities = EngineCodec.Locate(data.ServerBundle, data.EntityObjectBuilders, (Entity e) => _ids.GetOrAssign(e), out var unlocated)
		};
		entityBundleMessage.RequiredBlobs = EngineCodec.Ids(hashSet);
		foreach (KeyValuePair<Entity, DEntityBuilder> entityDatum in data.EntityData)
		{
			DEntityBuilder builder = entityDatum.Value;
			List<DataBlob> list = EngineCodec.Blobs(ref builder, Keys);
			if (list.Count > 0)
			{
				entityBundleMessage.Data.Add((_ids.GetOrAssign(entityDatum.Key), list));
			}
		}
		foreach (KeyValuePair<Entity, Entity> entityParent in data.EntityParents)
		{
			entityBundleMessage.Parents.Add((_ids.GetOrAssign(entityParent.Key), _ids.GetOrAssign(entityParent.Value)));
		}
		foreach (Entity stagedEntity in data.StagedEntities)
		{
			entityBundleMessage.Staged.Add(_ids.GetOrAssign(stagedEntity));
		}
		foreach (Entity skippedEntity in data.SkippedEntities)
		{
			entityBundleMessage.Skipped.Add(_ids.GetOrAssign(skippedEntity));
		}
		byte[] array = entityBundleMessage.Encode();
		HostSession host = _host;
		host?.PublishLifecycle(array);
		SpawnBundles++;
		SpawnBytes += array.Length;
		if (hashSet.Count > 0 && host != null)
		{
			SendSpawnBlobs(host, hashSet);
		}
		if (data.EntityObjectBuilders.Count >= 20 || SpawnBundles <= 3 || hashSet.Count > 0)
		{
			_log.Info($"host world sync: spawn bundle {data.EntityObjectBuilders.Count} entities ({data.StagedEntities.Count} staged, {data.SkippedEntities.Count} skipped, {unlocated} unlocated, {hashSet.Count} offloaded blobs), {MpLogExtensions.Bytes(array.Length)} on the wire ({MpLogExtensions.Bytes(entityBundleMessage.RawBodyBytes)} raw: engine bundle {MpLogExtensions.Bytes(entityBundleMessage.Bundle.Length)}, entity data {MpLogExtensions.Bytes(entityBundleMessage.DataBytes)}) in {stopwatch.Elapsed.TotalMilliseconds:0.0} ms");
		}
	}

	private void SendSpawnBlobs(HostSession host, HashSet<Blob> blobs)
	{
		IReadOnlyDataOffloading offloading = _offloading;
		List<Blob> list = blobs.ToList();
		Task.Run(() =>
		{
			try
			{
				Dictionary<Guid, byte[]> dictionary;
				int failed;
				string firstError;
				if (offloading != null)
				{
					dictionary = EngineCodec.LoadBlobs(offloading, list, BlobLoadTimeout, out failed, out firstError);
				}
				else
				{
					dictionary = list.ToDictionary(EngineCodec.Id, (Blob _) => (byte[])null);
					failed = list.Count;
					firstError = "no data offloading service on the host";
				}
				host.PublishBlobs(dictionary);
				long value = ((IEnumerable<byte[]>)dictionary.Values).Sum((Func<byte[], long>)((byte[] d) => d?.Length ?? 0));
				Interlocked.Add(ref SpawnBlobs, dictionary.Count);
				Interlocked.Add(ref SpawnBlobBytes, value);
				if (failed > 0)
				{
					_log.Error($"host world sync: {failed} offloaded blob(s) of a spawn bundle could not be loaded (first: {firstError})");
				}
			}
			catch (Exception value2)
			{
				_log.Error($"host world sync: sending a spawn bundle's offloaded blobs failed: {value2}");
			}
		});
	}

	private void StampServerBuffer(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent server)
	{
		ReplicationCommandBuffer buffer = ((ReplicationCommandBufferSessionComponent)(object)server).Buffer;
		if (buffer != null)
		{
			double num = MpClock.NowMs();
			double num2 = _lastStampMs + 16.666666666666668;
			double num3 = ((_lastStampMs < 0.0 || Math.Abs(num - num2) > 250.0) ? num : (num2 + 0.1 * (num - num2)));
			if (_lastStampMs >= 0.0)
			{
				num3 = Math.Max(num3, _lastStampMs + 1.0);
			}
			_lastStampMs = num3;
			_bufferStamp.AddOrUpdate(buffer, new StrongBox<uint>((uint)(long)Math.Round(num3)));
			StampedBuffers++;
		}
	}

	public void OnPostData(Entity? source, int typeId, ReadOnlySpan<byte> data, ReplicationCommandBuffer cb)
	{
		HostSession host = _host;
		if (host == null || !host.AnySynced || _disabled)
		{
			return;
		}
		if (source == null || !_ids.TryGet(source, out var id))
		{
			StateUnknownEntity++;
			return;
		}
		TypeKeyDef orAdd = Keys.GetOrAdd(typeId, EngineCodec.DescribeType);
		uint hostMs;
		if (_bufferStamp.TryGetValue(cb, out StrongBox<uint> value))
		{
			hostMs = value.Value;
		}
		else
		{
			hostMs = (uint)MpClock.NowMs();
			UnstampedPostData++;
		}
		host.PublishState(new StateEntry(id, orAdd.Key, (uint)Interlocked.Increment(ref _version), data.ToArray(), hostMs));
	}

	public void OnRemoveData(Entity? source, int typeId)
	{
		HostSession host = _host;
		if (host != null && host.AnySynced && !_disabled && source != null && _ids.TryGet(source, out var id))
		{
			TypeKeyDef orAdd = Keys.GetOrAdd(typeId, EngineCodec.DescribeType);
			host.PublishDataRemove(new (uint, ushort)[1] { (id, orAdd.Key) });
		}
	}

	private bool CappedContactSignal(CommandLayout l, ReplicationCommandBuffer cb, ReadOnlySpan<byte> payload)
	{
		if ((!l.Name.EndsWith("+SendContactImpulseRawDataSignal", StringComparison.Ordinal) && !l.Name.EndsWith("+OnContactSignal", StringComparison.Ordinal)) || payload.Length < 4)
		{
			return false;
		}
		int num = BinaryPrimitives.ReadInt32LittleEndian(payload);
		if (num >= 0)
		{
			object obj = cb.ReadObject(num);
			if (obj != null)
			{
				long num2 = MpClock.NowMs();
				(int, object) key = (l.Stub, obj);
				if (_contactLast.TryGetValue(key, out var value) && num2 - value < 100)
				{
					ContactCapped++;
					return true;
				}
				if (_contactLast.Count > 4096)
				{
					_contactLast.Clear();
				}
				_contactLast[key] = num2;
				return false;
			}
		}
		return false;
	}

	public void OnSignal(ReplicationCommandBuffer cb, int stub, ReadOnlySpan<byte> payload)
	{
		CommandLayout commandLayout = CommandLayouts.ByStub(stub);
		if (commandLayout == null)
		{
			return;
		}
		if (commandLayout.Name.StartsWith("Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+", StringComparison.Ordinal))
		{
			string name = commandLayout.Name;
			bool flag = ((name == "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReleaseReplicatedObjectSignal" || name == "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReleaseReplicatedCollectionSignal") ? true : false);
			if (!flag || payload.Length < 12 || _disabled)
			{
				return;
			}
			try
			{
				int num = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8));
				_observables.OnRelease((num >= 0) ? cb.ReadObject(num) : null, commandLayout.Name == "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReleaseReplicatedCollectionSignal", HeadlessReplication.Active ? (_serverComp as Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent)?.ReplayContext : _hostClientComp?.ReplayContext);
				return;
			}
			catch (Exception ex)
			{
				_log.WarnOnce("host-observable-release", $"host world sync: reading an observable release failed ({ex.GetType().Name}: {ex.Message})");
				return;
			}
		}
		HostSession host = _host;
		if (host == null || !host.AnySynced || _disabled)
		{
			return;
		}
		if (!commandLayout.Forwardable)
		{
			NoteS2CDrop(commandLayout.Name, commandLayout.Why ?? "not forwardable");
			return;
		}
		if (IsHostPlayerEvent(commandLayout.Name))
		{
			NoteS2CDrop(commandLayout.Name, "the host player's own event");
			return;
		}
		if (CappedContactSignal(commandLayout, cb, payload))
		{
			NoteS2CDrop(commandLayout.Name, $"rate-capped (one per {100} ms per entity)");
			return;
		}
		SignalKeyDef orAdd = host.SignalKeys.GetOrAdd(commandLayout.Name, commandLayout.Def(0).PayloadBytes, commandLayout.Signature);
		ObjectSpace objectSpace = _tapSpace;
		if (objectSpace == null)
		{
			ObjectSpace objectSpace2 = new ObjectSpace
			{
				Name = "server",
				IdOf = (Entity e) => (!_ids.TryGet(e, out var id)) ? ((uint?)null) : new uint?(id),
				Explain = ExplainNetId,
				Resolve = (uint _, bool _) => (Entity?)null
			};
			ObjectSpace objectSpace3 = objectSpace2;
			_tapSpace = objectSpace2;
			objectSpace = objectSpace3;
		}
		ObjectSpace objectSpace4 = objectSpace;
		objectSpace4.ClearLocalIds();
		string fail = null;
		WireCommand wireCommand = SignalCodec.Encode(commandLayout, orAdd.Key, payload, (int i) => cb.ReadObject(i), objectSpace4, _tapScratch, out var _, ref fail);
		if (wireCommand == null)
		{
			NoteS2CDrop(commandLayout.Name, fail ?? "?");
			return;
		}
		if (commandLayout.StringIdOffsets.Length != 0)
		{
			_sids.NoteHost(wireCommand.Payload, commandLayout.StringIdOffsets);
		}
		lock (_signalSync)
		{
			_pendingSignals.Add(wireCommand);
		}
		SignalsForwarded++;
		if (_s2cSent.AddOrUpdate(ShortName(commandLayout.Name), 1L, (string _, long c) => c + 1) == 1)
		{
			_log.Info("host world sync: forwarding S2C signal " + ShortName(commandLayout.Name) + " to joiners (first one)");
		}
	}

	internal static bool IsHostPlayerEvent(string fullName)
	{
		string text = ShortName(fullName);
		if (text.Contains("LocalPlayer", StringComparison.Ordinal))
		{
			return true;
		}
		string[] hostPlayerEventPrefixes = HostPlayerEventPrefixes;
		foreach (string value in hostPlayerEventPrefixes)
		{
			if (text.StartsWith(value, StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	private void NoteS2CDrop(string name, string why)
	{
		SignalsDropped++;
		_s2cDrops.AddOrUpdate(ShortName(name) + ": " + why, 1L, (string _, long c) => c + 1);
	}

	private void NoteC2SDrop(string name, string why)
	{
		_c2sDrops.AddOrUpdate(ShortName(name) + ": " + why, 1L, (string _, long c) => c + 1);
	}

	private static string ShortName(string full)
	{
		int num = full.LastIndexOf('.');
		if (num < 0)
		{
			return full;
		}
		int num2 = num + 1;
		return full.Substring(num2, full.Length - num2);
	}

	private static string Top(ConcurrentDictionary<string, long> d, int n)
	{
		if (!d.IsEmpty)
		{
			return string.Join(", ", from kv in d.OrderByDescending((KeyValuePair<string, long> kv) => kv.Value).Take(n)
				select $"{kv.Key} x{kv.Value}");
		}
		return "none";
	}

	private ObjectSpace ServerSpace(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent server)
	{
		ReplayContext replay = server.ReplayContext;
		return new ObjectSpace
		{
			Name = "server",
			IdOf = (Entity e) => (!_ids.TryGet(e, out var id)) ? ((uint?)null) : new uint?(id),
			Explain = ExplainNetId,
			Resolve = (uint id, bool _) => (!_ids.TryGet(id, out Entity obj)) ? null : obj,
			Session = server.Session,
			ObservableId = (object o) => _observables.OidOnServer(o, replay),
			ResolveObservable = (uint oid, bool inCollection) => _observables.ResolveOnServer(oid, inCollection, replay)
		};
	}

	/// <summary>Sends a server story addressed to a joiner (discovery/encounter/sector notifications) to run on their PC.</summary>
	internal bool ForwardServerStory(ushort clientId, IAsyncStateMachine stateMachine)
	{
		HostSession host = _host;
		if (host == null || !_joiners.TryGetValue(clientId, out Joiner j) || Volatile.Read(ref _serverComp) is not Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent server)
		{
			return false;
		}
		ObjectSpace space = ServerSpace(server);
		List<(string, WireValue)>? fields = StoryState.Encode(stateMachine, space, out string? fail);
		if (fields == null || space.LocalIds.Count > 0)
		{
			_log.WarnOnce("server-story-" + stateMachine.GetType().FullName, $"host world sync: server story {StoryState.Name(ObjectCodec.TypeName(stateMachine.GetType()))} for ClientId {clientId} cannot travel ({fail ?? "refers to an entity the joiner does not have"}); skipped");
			return false;
		}
		if (space.Observed.Count > 0)
		{
			_observables.AddRoots(j.Peer, space.Observed.ToList());
		}
		host.SendStoryMove(j.Peer, new StoryMove
		{
			StoryId = ClientStories.ServerOrigin | (uint)Interlocked.Increment(ref _serverStoryIds),
			StateMachineType = ObjectCodec.TypeName(stateMachine.GetType()),
			Fields = fields
		});
		return true;
	}

	/// <summary>Streams a joiner's own research, colonization, contracts, credits, GPS, discoveries, tool modes and paint palette live instead of only at join.</summary>
	private void RootPerPlayerData(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent server, Joiner j)
	{
		if (!_identityOf.TryGetValue(j.ClientId, out long identity))
		{
			return;
		}
		InProcessPerPlayerDataSessionComponent? ppd = server.Session.SessionComponents.TryGet<InProcessPerPlayerDataSessionComponent>();
		if (ppd == null || !((CollectionDictionary<IdentityId, List<object>, object>)(object)ppd._perPlayerData).TryGet(new IdentityId(identity), out List<object> items))
		{
			return;
		}
		List<uint> roots = new List<uint>();
		foreach (object item in items)
		{
			if (!HostIdentities.LiveTypes.Any((Type t) => t.IsInstanceOfType(item)))
			{
				continue;
			}
			try
			{
				if (_observables.OidOnServer(item, server.ReplayContext) is uint oid)
				{
					roots.Add(oid);
				}
			}
			catch (Exception ex)
			{
				_log.WarnOnce("host-ppd-root-" + item.GetType().Name, $"host world sync: {item.GetType().Name} of ClientId {j.ClientId} cannot stream ({ex.GetType().Name}: {ex.Message}); it stays as copied at join");
			}
		}
		if (roots.Count > 0)
		{
			_observables.AddRoots(j.Peer, roots);
		}
	}

	public void OnClientStory(PeerId client, ushort clientId, StoryMove move)
	{
		(_stories ?? (_stories = new HostStories(_log))).Enqueue(client, clientId, move);
	}

	public void ForgetStories(PeerId client)
	{
		_stories?.Forget(client);
		_observables.Forget(client);
	}

	public void OnObservableEdits(PeerId client, ushort clientId, IReadOnlyList<MemberEdit> edits)
	{
		_observables.QueueEdits(client, clientId, edits);
	}

	public void OnInvoke(Delegate callback)
	{
		if (_host == null || _disabled)
		{
			return;
		}
		string name = callback.Method.Name;
		bool flag = name.Contains("OnBeforeEntityRemovedFromScene", StringComparison.Ordinal);
		bool flag2 = !flag && name.Contains("OnBeforeEntityDestroyed", StringComparison.Ordinal);
		if (!flag && !flag2)
		{
			return;
		}
		Entity entity = CapturedEntity(callback.Target);
		if (entity == null || !_ids.TryGet(entity, out var id))
		{
			return;
		}
		lock (_despawnSync)
		{
			_pendingDespawns.Add((id, (!flag) ? ((byte)1) : ((byte)0), flag ? null : entity));
		}
	}

	private void FlushDespawns(HostSession host)
	{
		List<(uint, byte, Entity)> pendingDespawns;
		lock (_despawnSync)
		{
			if (_pendingDespawns.Count == 0)
			{
				return;
			}
			pendingDespawns = _pendingDespawns;
			_pendingDespawns = new List<(uint, byte, Entity)>();
		}
		foreach (var (num, b, entity) in pendingDespawns)
		{
			if (b == 0)
			{
				Despawns++;
				host.PublishDespawn(new uint[1] { num }, 0);
				continue;
			}
			Forgets++;
			host.PublishDespawn(new uint[1] { num }, 1);
			if (entity != null)
			{
				_ids.Remove(entity, out var _);
				RememberForgotten(num, entity);
			}
		}
	}

	private void RememberForgotten(uint id, Entity entity)
	{
		string item;
		try
		{
			item = (entity.GetComposition()?.Guid.ToString() ?? "?") ?? "";
		}
		catch (Exception)
		{
			item = "?";
		}
		lock (_forgotten)
		{
			if (_forgotten.Count >= 8192)
			{
				_forgotten.Clear();
			}
			_forgotten[id] = (MpClock.NowMs(), item);
		}
	}

	internal string ExplainNetId(uint id)
	{
		if (id == 0)
		{
			return " (null)";
		}
		if (id >= _ids.NextId)
		{
			return " (never assigned by this host)";
		}
		lock (_forgotten)
		{
			if (_forgotten.TryGetValue(id, out (long, string) value))
			{
				return $" (destroyed on the server {(double)(MpClock.NowMs() - value.Item1) / 1000.0:0.0} s ago: {value.Item2})";
			}
		}
		return " (assigned earlier, no longer mapped on the host)";
	}

	private Entity? CapturedEntity(object? target)
	{
		if (target == null)
		{
			return null;
		}
		return _closureEntityField.GetOrAdd(target.GetType(), (Type t) =>
		{
			FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (FieldInfo fieldInfo in fields)
			{
				if (fieldInfo.FieldType == typeof(Entity))
				{
					return fieldInfo;
				}
			}
			return (FieldInfo?)null;
		})?.GetValue(target) as Entity;
	}

	public string SignalStats()
	{
		return $"S2C signals top: {Top(_s2cSent, 8)}; S2C dropped: {Top(_s2cDrops, 8)}; C2S applied: {Top(_c2sApplied, 8)}; C2S rejected: {Top(_c2sDrops, 6)}; " + ReplayGuard.Describe() + $"; 0.7.6: {AudioGate.Describe()}; {ToolShadowGuard.Describe()}; {ToolTargetDiagnostics.Describe()}; joiner data removals applied {InputRemovals}; {BuildGuards.Describe()}; {ControlGuard.Describe()}; 0.8.2: state stamped by server tick {StampedBuffers} ticks ({UnstampedPostData} unstamped), passenger ship actions refused {PassengerActionsRefused}; 0.8.3: contact signals rate-capped {ContactCapped}, StringIds announced {_sids.Announced}";
	}

	public string Stats()
	{
		return $"NetIds {_ids.Count}, type keys {Keys.Count}, spawn bundles {SpawnBundles} ({MpLogExtensions.Bytes(SpawnBytes)}; blobs {SpawnBlobs} {MpLogExtensions.Bytes(SpawnBlobBytes)}), despawns {Despawns}, forgets {Forgets}, joiners {_joiners.Count} (characters spawned {CharactersSpawned}, released {CharactersReleased}, lost {CharactersLost}), joiner input applied {InputDataApplied} data / {InputCommandsApplied} commands, rejected {InputRejected}, S2C signals forwarded {SignalsForwarded} dropped {SignalsDropped}, {_stories?.Describe() ?? "stories: none"}, state for unknown entities {StateUnknownEntity}; {_observables.Describe()}; " + ((_identities != null) ? (_identities.Describe() + ", players " + string.Join(", ", _identityOf.Select((KeyValuePair<ushort, long> kv) => $"ClientId {kv.Key} = {kv.Value}")) + (TeamPermissions.Enabled ? $", team-grid overrides {TeamPermissions.Overrides}" : "") + ((_gate != null) ? $", strict-grid checks {_gate.Checked} refused {_gate.Refused}" : "")) : "identities off");
	}

	private void UpdateNametagRoster(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		if (_rosterFailed || !NametagOverlay.Enabled)
		{
			return;
		}
		long num = MpClock.NowMs();
		if (num - _lastRosterMs < 500)
		{
			return;
		}
		_lastRosterMs = num;
		try
		{
			List<PlayerTag> list = new List<PlayerTag>();
			if (!_joiners.IsEmpty)
			{
				HashSet<Entity> hashSet = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
				foreach (Joiner value2 in _joiners.Values)
				{
					Entity character = value2.Character;
					if (character != null && value2.NetId != 0 && !EngineCodec.IsDead(character))
					{
						hashSet.Add(character);
						if (_silentClients.ContainsKey(value2.ClientId)) continue;
						list.Add(new PlayerTag(value2.ClientId, value2.NetId, Tagged(IdentityOf(value2.ClientId), value2.Name)));
					}
				}
				Entity hostCharacterServer = _hostCharacterServer;
				if (hostCharacterServer != null && !EngineCodec.IsDead(hostCharacterServer) && !hashSet.Contains(hostCharacterServer) && !_silentClients.ContainsKey(SyncProtocol.HostClientId) && _ids.TryGet(hostCharacterServer, out var id))
				{
					list.Add(new PlayerTag(1, id, Tagged(PlayerIdentities.HotfixIdentity, HostNametags.HostName)));
				}
			}
			HostNametags.Roster.Set(list);
		}
		catch (Exception value)
		{
			_rosterFailed = true;
			_log.Error($"nametags(host): building the player list failed, joiners get no nametags this session (game unaffected): {value}");
		}
	}

	private void ResolveHostCharacter(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		try
		{
			Entity entity = (comp.Session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController)?.GetTopControllable<CharacterControllableComponent>()?.Entity;
			if (entity != null && comp.ReplayContext.HasPairingLocal(entity) && comp.ReplayContext.TranslateLocalToRemote(entity) is Entity entity2 && entity2 != _hostCharacterServer)
			{
				_hostCharacterServer = entity2;
				if (!_hostCharacterLogged)
				{
					_hostCharacterLogged = true;
					_log.Info("nametags(host): the host's character is the one its own client controls (" + (_ids.TryGet(entity2, out var id) ? $"NetworkId {id}" : "not synced yet") + ")");
				}
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("host-character-resolve", $"nametags(host): finding the host's own character failed ({ex.GetType().Name}: {ex.Message}); its tag may be missing");
		}
	}

	private void UpdateHostNametags(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		if (!NametagOverlay.Enabled)
		{
			return;
		}
		NametagOverlay nametagOverlay = _hostTags ?? (_hostTags = new NametagOverlay(_log, "host"));
		if (_hostTagTick++ % 30 == 0 || _hostTagList.Count != _joiners.Count)
		{
			ResolveHostCharacter(comp);
			_hostTagList.Clear();
			try
			{
				foreach (Joiner item2 in _joiners.Values.OrderBy((Joiner x) => x.ClientId))
				{
					Entity item = null;
					Entity character = item2.Character;
					if (character != null && !EngineCodec.IsDead(character) && !_silentClients.ContainsKey(item2.ClientId))
					{
						bool skip = false;
						item = comp.ReplayContext.TranslateObject(character, ReplicationFailureStrategy.NullOnAnyFail, ref skip) as Entity;
					}
					_hostTagList.Add((item2.ClientId, item2.Name, item));
				}
			}
			catch (Exception ex)
			{
				_log.WarnOnce("host-nametag-resolve", $"nametags(host): resolving joiners' characters failed ({ex.GetType().Name}: {ex.Message}); retrying");
				_hostTagList.Clear();
			}
		}
		nametagOverlay.Update(comp.Session, comp.Entity, _hostTagList);
	}
}
