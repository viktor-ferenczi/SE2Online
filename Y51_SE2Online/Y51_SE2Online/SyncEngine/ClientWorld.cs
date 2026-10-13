using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Keen.Game2;
using Keen.Game2.Client.GameSystems.PCU;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.RuntimeSystems;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.Game2.Client.WorldObjects.Character.Effects;
using Keen.Game2.Client.WorldObjects.Spectators;
using Keen.Game2.OutOfProcess;
using Keen.Game2.Simulation.GameSystems.Control;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.PCU;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Toolbars;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.Game2.Simulation.Utils;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.DCS.Signals;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.Data;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.SecureIds;
using Keen.VRage.Multiplayer.Sessions;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.Probe;
using Y51_SE2Online.Sync;
using VRage.Multiplayer.Replication.CB;

using Component = Keen.VRage.DCS.Components.Component;

namespace Y51_SE2Online.SyncEngine;

internal sealed class ClientWorld
{
	private sealed class Attempt
	{
		public bool Built;

		public string Summary = "";

		public int Paired;

		public int Missing;

		public int Duplicates;

		public int DataSet;

		public int DataSkipped;

		public int BuiltRoots;

		public int Excluded;

		public Exception? Error;

		public List<Entity> FailedRoots = new List<Entity>();

		public string FailureDetail = "";
	}

	private long _foreignRefused;

	private int _f6Logged;

	private readonly IMpLog _log;

	private readonly MpOptions _o;

	private readonly ProxyMap _proxies = new ProxyMap();

	private readonly List<StateEntry> _drain = new List<StateEntry>();

	private volatile ClientSession? _session;

	private volatile bool _startRequested;

	private volatile bool _startIssued;

	private volatile bool _configured;

	private bool _mainThreadSeen;

	private InProcessReplication? _replication;

	private Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? _comp;

	private Entity? _sessionProxy;

	private bool _dcsLogged;

	private bool _worldApplied;

	private bool _spectatorQueued;

	private bool _failed;

	private bool _left;

	private int _worldPartsApplied;

	private int _worldEntitiesBuilt;

	private SessionBuilder? _configuredBuilder;

	private bool _snapshotApplied;

	private long _ticks;

	private long _commands;

	private long _stateApplied;

	private long _stateBadSize;

	private long _lifecycleApplied;

	private long _c2sDropped;

	private long _lastStatusMs;

	private long _startRequestedMs;

	private int _entitiesBuilt;

	private object? _blobTarget;

	private long _blobsRegistered;

	private long _blobBytesRegistered;

	private uint _gateSeq;

	private long _gateSinceMs;

	private long _purgedDead;

	private long _despawnSkipped;

	private long _rootsDropped;

	private long _bundlesRetried;

	private ClientUpstream? _upstream;

	private bool _upstreamFailed;

	private bool _charRequested;

	private bool _charFailed;

	private bool _characterFlowFailed;

	private bool _smoothingFailed;

	private long _charRequestMs;

	private long _assignSeen;

	private long _respawnAtMs;

	private long _smoothApplied;

	private uint _pendingNetId;

	private uint _ownNetId;

	private double? _ownHostLocalMs;


	private long _bodyDetailMs;

	// Test diagnostics lower it to exercise the worker path with everyday spawns.
	internal static int BackgroundBuildEntities = 2000;

	private sealed class BackgroundBuild
	{
		public volatile bool IsCompleted;

		public Entity[]? Result;

		public Exception? Error;
	}

	private (BackgroundBuild Job, Action<Entity[]> Finish, Action<Exception> Fail, string Summary, Stopwatch Watch)? _backgroundBuild;

	// SE2's own tasks: a thread-pool GetResult() does not wait for them. This is what its entity spawner does:
	// hop onto an engine worker, build there (Scene.OpenNoSyncSection lets the game tick meanwhile), report back.
	private static async void BuildOnWorker(GameEntitySerializer serializer, GameEntitySerializer.BundleInitArgs args, BackgroundBuild job)
	{
		try
		{
			await Keen.VRage.Library.Threading.Parallel.MoveToIfNeeded();
			job.Result = await serializer.BuildFromBundleAsync(args, allowAsyncSpawn: true);
		}
		catch (Exception ex)
		{
			job.Error = ex;
		}
		finally
		{
			job.IsCompleted = true;
		}
	}

	private void FinishBackgroundBuild((BackgroundBuild Job, Action<Entity[]> Finish, Action<Exception> Fail, string Summary, Stopwatch Watch) pending)
	{
		long built = pending.Watch.ElapsedMilliseconds;
		try
		{
			if (pending.Job.Error != null) throw new InvalidOperationException("the worker build threw", pending.Job.Error);
			pending.Finish(pending.Job.Result ?? throw new InvalidOperationException("the worker build returned nothing"));
			_log.Info($"client: {pending.Summary}; worker build done after {built} ms, added to the scene in {pending.Watch.ElapsedMilliseconds - built} ms");
		}
		catch (Exception ex)
		{
			pending.Fail(ex);
			_log.Error($"client: {pending.Summary}: the worker-thread build failed and was rolled back: {ex}");
			Report(ClientPhase.Error, "background bundle build failed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private const double StallLeadMs = 100.0;

	private static volatile bool _sprintIntent;

	private static long _sprintIntentMs;

	private static long _crouchToggleMs;

	private long _crouchToggleSeen;

	private bool _crouchIntent;

	// Postfixes on the local character's requests (CharacterMovementControlComponent), installed for joiners.
	internal static void SprintRequested(bool sprint)
	{
		_sprintIntent = sprint;
		Interlocked.Exchange(ref _sprintIntentMs, MpClock.NowMs());
	}

	internal static void CrouchToggleRequested() => Interlocked.Exchange(ref _crouchToggleMs, MpClock.NowMs());

	private Entity? _ownCharacter;

	private IController? _controller;

	private readonly HashSet<Entity> _controlled = new HashSet<Entity>(ReferenceEqualityComparer.Instance);

	private readonly List<(uint NetId, byte[] Value)> _smoothOut = new List<(uint, byte[])>();

	private const int CharacterWaitMs = 60000;

	private const int RespawnDelayMs = 5000;

	private const int PossessRetryMs = 1000;

	private uint _assignedNetId;

	private bool _possessQueued;

	private long _possessRetryAtMs;

	private int _possessFailures;

	private Entity? _spectator;

	private ClientStories? _stories;

	private readonly List<(int, Segment)> _sigScratch = new List<(int, Segment)>();

	private readonly Dictionary<string, long> _sigDrops = new Dictionary<string, long>();

	private readonly HashSet<string> _sigFirst = new HashSet<string>(StringComparer.Ordinal);

	private long _signalsApplied;

	private long _signalsDropped;

	private long _namesRegistered;

	private long _namesKnown;

	private long _namesBad;

	private static readonly FieldInfo EntityBackingField = EngineMembers.Field(HookTargets.Reflected.Single((EngineMember m) => m.Id == "component-entity"));

	private readonly StringIdTable _sids = new StringIdTable();

	private readonly ClientObservables _observables;

	private TimeSpan? _hostGameTime0;

	private uint _hostTick0;

	private long _gameTimeCheckMs;

	private long _gameTimeSyncs;

	private readonly List<(Delegate Move, Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent Comp)> _earlyStories = new List<(Delegate, Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent)>();

	private bool _earlyStoriesSent;

	public const int MaxEarlyStories = 32;

	private ContinuationQueue? _mainQueue;

	private bool _waitingForMenuLogged;

	private bool _waitingForTransitionLogged;

	private long _menuSettledSinceMs;

	private long _startAttemptMs;

	private int _startAttempts;

	private const int MaxStartAttempts = 5;

	private volatile bool _startFailed;

	private static readonly FieldInfo? s_isTransitioning = typeof(Keen.Game2.GameAppComponent).GetField("_isTransitioning", BindingFlags.Instance | BindingFlags.NonPublic);

	private SessionComponentsSnapshot? _snapshot;

	private byte[]? _snapshotRaw;

	private int _snapshotBytes;

	private int _snapshotRefs;

	private PlayersSessionObjectBuilder? _playersOb;

	private bool _playersRepaired;

	private long _playersVersionSeen = -1L;

	private long _statusSeen;

	private Session? _sessionStandIn;

	private readonly Dictionary<Entity, long> _recentlyDriven = new Dictionary<Entity, long>(ReferenceEqualityComparer.Instance);

	private const long RecentlyDrivenMs = 2000L;

	private static readonly FieldInfo? PcuModelField = typeof(PCUClientSessionComponent).GetField("_streamedModel", BindingFlags.Instance | BindingFlags.NonPublic);

	private bool _pcuDone;

	private bool _pcuStreamedLogged;

	private PlayerPcuModel? _pcuStandIn;

	private LookPredictor? _look;

	private bool _lookFailed;

	private bool _lookDirty;

	private long _lookSentMs;

	private long _lookIntercepted;

	private (uint NetId, ushort Epoch, double X, double Y, double Z) _lookLastSent;

	public const int LookResendMs = 100;

	private OwnBodyFilter? _body;

	private MovePredictor? _move;

	private Keen.VRage.Physics.Components.CharacterPhysicsComponent? _movePhys;

	private (float X, float Z, bool Sprint, bool Crouch) _moveInput;

	private string? _bodyWhy;

	private System.Numerics.Vector3? _ownVel;

	private bool _ownVelLogged;

	private byte[]? _lastWritten;

	private long _lastWrittenMs;

	private long _otherWriterLogMs = -4611686018427387904L;

	private long _otherWriterCount;

	private long _otherWriterSuppressed;

	private double _otherWriterMaxM;

	private bool _ownSmoothed;

	private int _ownSmoothedLogged;

	public const float FastFlightOnMs = 15f;

	public const float FastFlightOffMs = 8f;

	public const int FastFlightHoldMs = 750;

	private long _fastUntilMs;

	private long _deckUntilMs;

	private uint _deckRootId;

	private bool _deckFrame;

	private System.Numerics.Quaternion _deckQ = System.Numerics.Quaternion.Identity;

	private uint _deckCheckId;

	private long _deckCheckMs;

	private long _deckJumpLogMs = -4611686018427387904L;

	private long _deckJumps;

	private double _dx;

	private double _dy;

	private double _dz;

	private double _dvx;

	private double _dvy;

	private double _dvz;

	private Entity? _seatDiagLast;

	private int _seatDiagCount;

	private const int TagResolveTicks = 30;

	private NametagOverlay? _tagOverlay;

	private long _tagChanges = -1L;

	private int _tagTick;

	private readonly List<(ushort ClientId, string Name, Entity? Target)> _tagList = new List<(ushort, string, Entity)>();

	private static JoinStatusUi? s_ui;

	public const int LostToMenuDelayMs = 4000;

	public const int RejoinDelayMs = 3000;

	private int _generation = 1;

	private volatile string? _lostReason;

	private volatile string? _rejoinAsk;

	private bool _lostHandled;

	private bool _menuReturnIssued;

	private bool _retired;

	private long _lostAtMs;

	private JoinStage _loggedStage = (JoinStage)byte.MaxValue;

	private bool _lostMenuSeen;

	private long _secureAsked;

	private long _secureResolved;

	private long _secureNull;

	private long _secureFailed;

	public static ClientWorld? Current { get; private set; }

	internal Session? GameSession => _comp?.Session;

	// Test diagnostics: the last smoothed transform written per NetId, while set.
	internal Dictionary<uint, byte[]>? WatchWritten;

	public System.Collections.Generic.IReadOnlyList<PlayerInfo> SessionPlayers => _session?.Players ?? System.Array.Empty<PlayerInfo>();

	public static JoinProgress Progress { get; } = new JoinProgress();

	public int Generation => _generation;

	internal ClientSession? BoundSession => _session;

	public bool IdleAfterJoin
	{
		get
		{
			bool flag = !_retired;
			if (flag)
			{
				bool flag2 = _left || _startFailed || _lostReason != null;
				if (!flag2)
				{
					JoinStage stage = Progress.Stage;
					bool flag3 = ((stage - 12 <= JoinStage.WaitingForSteam) ? true : false);
					flag2 = flag3;
				}
				flag = flag2;
			}
			return flag;
		}
	}

	public static event Action<string>? RejoinRequested;

	private Entity? AssignedEntity(uint netId)
	{
		if (netId != 0)
		{
			return ClientEntity(netId);
		}
		return null;
	}

	private bool OwnCharacterControlled(uint netId)
	{
		if (_ownNetId != netId || _ownCharacter == null)
		{
			return false;
		}
		IController controller = _controller ?? LocalController();
		if (controller == null || controller.ControlledEntities.Count == 0)
		{
			return false;
		}
		foreach (var controlledEntity in controller.ControlledEntities)
		{
			if (controlledEntity.Controllable is Component component && (component.Entity == _spectator || IsForeignCharacter(component.Entity)))
			{
				return false;
			}
		}
		return true;
	}

	private PlayerControllerComponent? LocalController()
	{
		try
		{
			return _comp?.Session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void CloseSpectator(string why)
	{
		Entity spectator = _spectator;
		if (spectator == null)
		{
			return;
		}
		_spectator = null;
		_spectatorQueued = false;
		try
		{
			if (!EngineCodec.IsDead(spectator) && !EngineCodec.IsClosing(_comp.Session, spectator))
			{
				_comp.Session.MarkEntityForClose(spectator);
			}
			_log.Info("client: free-camera spectator closed (" + why + ")");
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-spectator-close", $"client: closing the free-camera spectator failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	internal bool IsForeignCharacter(Entity? e)
	{
		if (e == null || e.TryGet<CharacterControllableComponent>() == null)
		{
			return false;
		}
		Entity entity = AssignedEntity(_assignedNetId) ?? _ownCharacter;
		return e != entity;
	}

	internal bool IsOwnCharacterProxy(object? o)
	{
		if (o is Entity e && _ownNetId != 0 && _proxies.TryGetId(e, out var id))
		{
			return id == _ownNetId;
		}
		return false;
	}

	public static void GetNextCharacterPostfix(PlayerControllerComponent controller, ref IControllable? __result)
	{
		ClientWorld current = Current;
		if (current?._comp == null || __result == null || AdminAccess.LocalIsAdmin)
		{
			return;
		}
		try
		{
			if (current.IsForeignCharacter(__result.Entity))
			{
				IControllable controllable = current.AssignedEntity(current._assignedNetId)?.TryGet<CharacterControllableComponent>();
				if (controllable != null && controllable.Controller != null && controllable.Controller != controller)
				{
					controllable = null;
				}
				current._foreignRefused++;
				if (current._foreignRefused <= 3)
				{
					current._log.Info($"client: F6 / spectator keys would take another player's character ({__result.Entity}); " + ((controllable != null) ? "staying on (or going back to) this player's own character" : "ignored (no own character yet)"));
				}
				__result = controllable;
			}
		}
		catch (Exception ex)
		{
			current._log.WarnOnce("client-next-character", $"client: F6 filter failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	public static bool NextCharacterFocusPrefix(SpectatingModesSessionComponent __instance)
	{
		ClientWorld current = Current;
		if (current?._comp == null || AdminAccess.LocalIsAdmin)
		{
			return true;
		}
		try
		{
			CharacterControllableComponent characterControllableComponent = (current.AssignedEntity(current._assignedNetId) ?? current._ownCharacter)?.TryGet<CharacterControllableComponent>();
			PlayerControllerComponent localPlayerController = __instance.LocalPlayerController;
			if (characterControllableComponent == null || localPlayerController == null)
			{
				// The game's F6 would pick a foreign character or ask for a debug-character spawn.
				return false;
			}
			IControllable topControllable = localPlayerController.GetTopControllable();
			if (topControllable != null && !(topControllable is SpectatorComponent) && ((IControllable)characterControllableComponent).Controller == localPlayerController)
			{
				// Already on the own character; the game's F6 would RequestUnpossessionAll and drop a cockpit.
				return false;
			}
			IControllable controllable = characterControllableComponent;
			while (true)
			{
				IControllable controllable2 = controllable.Entity.AsInterface<ISeatable>()?.CurrentSeat?.Entity.AsInterface<IControllable>();
				if (controllable2 == null)
				{
					break;
				}
				localPlayerController.CameraSystem.ResetCameraModeForEntity(controllable.Entity);
				controllable = controllable2;
			}
			localPlayerController.CameraSystem.ResetCameraModeForEntity(controllable.Entity);
			localPlayerController.RequestUnpossessionAll();
			localPlayerController.RequestPossession(characterControllableComponent);
			current._possessRetryAtMs = 0L;
			if (current._f6Logged++ < 3)
			{
				current._log.Info($"client: F6 from the free camera: back to this player's own character (NetworkId {current._assignedNetId})");
			}
			return false;
		}
		catch (Exception ex)
		{
			current._log.WarnOnce("client-f6-focus", $"client: F6 handling failed ({ex.GetType().Name}: {ex.Message}); the game's own F6 runs");
			return true;
		}
	}

	public static bool SetEntityTypeToPossessPrefix(InitialEntityOptions toPossess)
	{
		ClientWorld current = Current;
		if (current?._comp == null || toPossess != InitialEntityOptions.Character)
		{
			return true;
		}
		try
		{
			current._log.Info("client: the game asked to possess 'a character'; on a joiner that is only this player's own (retried every second until it is built)");
			current._possessRetryAtMs = 0L;
			return false;
		}
		catch (Exception)
		{
			return true;
		}
	}

	public static bool SpectatorKeyPrefix()
	{
		return Current?._comp == null || AdminAccess.LocalIsAdmin;
	}

	private ClientWorld(IMpLog log, MpOptions o)
	{
		_log = log;
		_o = o;
		_observables = new ClientObservables(log);
		_observables.Adopt = AdoptOwnPerPlayerData;
		StringIdTable.Client = _sids;
	}

	public static void Reset()
	{
		if (Current != null) { Current._retired = true; Current._session?.Detach(); }
		Current = null;
		StringIdTable.Client = null;
		Progress.Set(JoinStage.Idle, MpClock.NowMs());
	}

	public static ClientWorld Install(IMpLog log, MpOptions o)
	{
		if (Current == null)
		{
			Progress.SetRejoin(o.Rejoin, o.Rejoin);
			s_ui = new JoinStatusUi(log);
			Current = new ClientWorld(log, o);
			if (o.BuildFix)
			{
				DataTypeRegistry.Start(log);
			}
			ControlGuard.IsForeignCharacterHere = (Entity e) => Current?.IsForeignCharacter(e) ?? false;
		}
		return Current;
	}

	public void Bind(ClientSession session)
	{
		_session = session;
		session.SnapshotReady += () =>
		{
			if (!_o.ClientGame)
			{
				_log.Info("client: snapshot received; -y51online:noclientgame set, not starting the client game");
			}
			else
			{
				_startRequestedMs = MpClock.NowMs();
				_startRequested = true;
				_log.Info("client: snapshot received; the client-only game starts on the next main-thread frame");
			}
		};
	}

	public void NetTick(long now)
	{
		if (_startRequested && !_startIssued && !_mainThreadSeen && now - _startRequestedMs > 10000 && _startRequestedMs > 0)
		{
			_startRequestedMs = 0L;
			_log.Error("client: the client game was requested 10 s ago but GameAppComponent.GameUpdate never ran our prefix; the main-thread hook does not fire on this build (see the Harmony patch lines at startup)");
			_session?.Report(ClientPhase.Error, "main-thread hook (GameAppComponent.GameUpdate prefix) never fired; client game not started");
		}
	}

	private void Report(ClientPhase phase, string text)
	{
		_session?.Report(phase, text);
	}

	public static void GameUpdatePrefix(Keen.Game2.GameAppComponent __instance)
	{
		ClientWorld current = Current;
		if (current == null)
		{
			return;
		}
		try
		{
			current.OnMainThread(__instance);
		}
		catch (Exception value)
		{
			current._log.WarnOnce("client-main-thread", $"client: main-thread hook failed: {value}");
		}
	}

	private bool IsTransitioning(Keen.Game2.GameAppComponent app)
	{
		try
		{
			return s_isTransitioning?.GetValue(app) is AtomicFlag atomicFlag && atomicFlag.IsSet;
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-transition-flag", "client: could not read GameAppComponent._isTransitioning (" + ex.GetType().Name + "); relying on retries");
			return false;
		}
	}

	public void CaptureMainQueue(string where)
	{
		if (_mainQueue == null)
		{
			ContinuationQueue continuationQueue = Keen.VRage.Library.Threading.SynchronizationContext.Current?.Queue;
			if (continuationQueue != null)
			{
				_mainQueue = continuationQueue;
				_log.Info("client: main-thread continuation queue captured (" + where + ")");
			}
		}
	}

	private void OnMainThread(Keen.Game2.GameAppComponent app)
	{
		bool flag = Environment.CurrentManagedThreadId == MpPlugin.MainThreadId;
		if (flag && _mainQueue == null)
		{
			_mainQueue = Keen.VRage.Library.Threading.SynchronizationContext.Current?.Queue;
		}
		if (!_mainThreadSeen)
		{
			_mainThreadSeen = true;
			_log.Info($"client: frame hook active (GameAppComponent.GameUpdate) on thread '{Thread.CurrentThread.Name}' #{Environment.CurrentManagedThreadId} ({(flag ? "the main thread" : $"NOT the main thread #{MpPlugin.MainThreadId}")}); Keen sync context {((Keen.VRage.Library.Threading.SynchronizationContext.Current != null) ? "present" : "MISSING")}, main queue {((_mainQueue != null) ? "known" : "unknown")}");
		}
		long num = MpClock.NowMs();
		bool flag2 = app.MainMenu != null;
		bool flag3 = IsTransitioning(app);
		bool clientUp = app.Client != null;
		bool flag4 = MenuStep(flag2, flag3, clientUp, num, () =>
		{
			ReturnToMenu(app);
		});
		ShowProgress(app, num);
		if (flag4 || !_startRequested || _startFailed)
		{
			return;
		}
		if (_startIssued)
		{
			if (_configured || app.MainMenu == null || IsTransitioning(app) || num - _startAttemptMs < 3000)
			{
				return;
			}
			if (_startAttempts >= 5)
			{
				if (_startAttempts++ == 5)
				{
					_log.Error($"client: the client-only game did not start after {5} attempts; giving up (see the 'starting the client game' lines above)");
					Report(ClientPhase.Error, $"client game did not start after {5} attempts");
				}
				return;
			}
			_log.Warn($"client: still at the main menu {num - _startAttemptMs} ms after starting the client game; retrying");
			_startIssued = false;
		}
		if (!flag2)
		{
			if (!_waitingForMenuLogged)
			{
				_waitingForMenuLogged = true;
				_log.Warn("client: the host's world is ready, but this game is not at the main menu; it joins as soon as you return to the main menu");
				Report(ClientPhase.Warning, "client is not at the main menu; waiting");
			}
			_menuSettledSinceMs = 0L;
			return;
		}
		if (flag3)
		{
			if (!_waitingForTransitionLogged)
			{
				_waitingForTransitionLogged = true;
				_log.Info("client: the game is still in its own menu transition; waiting for it to finish");
			}
			_menuSettledSinceMs = 0L;
			return;
		}
		if (_menuSettledSinceMs == 0L)
		{
			_menuSettledSinceMs = num;
		}
		if (num - _menuSettledSinceMs >= 1000)
		{
			_startIssued = true;
			_startAttempts++;
			_startAttemptMs = num;
			ContinuationQueue continuationQueue = _mainQueue ?? ((!flag) ? null : Keen.VRage.Library.Threading.SynchronizationContext.Current?.Queue);
			if (continuationQueue != null)
			{
				continuationQueue.Enqueue(Start);
				return;
			}
			_log.Warn($"client: no main-thread continuation queue known; starting the client game directly on thread #{Environment.CurrentManagedThreadId}");
			Start();
		}
		void Start()
		{
			try
			{
				bool flag5 = _o.ClientMissions ?? (_session?.Accept?.HostMissions == true);
				_log.Info($"client: starting the client game (attempt {_startAttempts}/{5}; single player's client recipe, host missions {(flag5 ? "on" : "off")})");
				Report(ClientPhase.ClientGameStarting, $"attempt {_startAttempts}, missions {(flag5 ? "on" : "off")}");
				Progress.SetInGame(inGame: false);
				Progress.Set(JoinStage.Loading, MpClock.NowMs());
				RemoteClientGame.LoadAsync(app, flag5, _log, Report, (bool ok) =>
				{
					if (!ok)
					{
						_startAttemptMs = MpClock.NowMs() - 2000;
					}
				}, (Exception e) =>
				{
					_startFailed = true;
					Progress.Set(JoinStage.Failed, MpClock.NowMs(), RemoteClientGame.Describe(e));
				}).SkipWait();
			}
			catch (Exception ex)
			{
				_log.Error($"client: starting the client game threw: {ex}");
				Report(ClientPhase.Error, "client game start threw " + ex.GetType().Name + ": " + ex.Message);
			}
		}
	}

	public static bool ConfigurePrefix(NetworkGameClientCoreComponent __instance, SessionBuilder sessionBuilder)
	{
		ClientWorld current = Current;
		if (current == null || !current._startIssued)
		{
			return true;
		}
		try
		{
			return !current.Configure(__instance, sessionBuilder);
		}
		catch (Exception ex)
		{
			current._log.Error($"client: configuring the client session failed, falling back to Keen's empty replication (you will see nothing): {ex}");
			current.Report(ClientPhase.Error, "session configure failed: " + ex.GetType().Name + ": " + ex.Message);
			return true;
		}
	}

	private bool Configure(NetworkGameClientCoreComponent core, SessionBuilder sb)
	{
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Expected Obj, but got Unknown
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Expected Obj, but got Unknown
		NetworkGameClientComponent client = core._client;
		if (client == null || sb.IsServer)
		{
			return false;
		}
		if (_configuredBuilder == sb)
		{
			_log.Warn("client: ConfigureSession called twice for the same session; second call skipped");
			return true;
		}
		_configuredBuilder = sb;
		ImmutableArray<PrefabBindingDefinition> immutableArray = Singleton<DefinitionManager>.Instance.GetDefinitionsOfType<PrefabBindingDefinition>().ToImmutableArray();
		InProcessReplication val = new InProcessReplication(immutableArray);
		sb.ExternalServices.Add((IGameClient)client);
		((ISessionConfigurator)val.Client).ConfigureSession(sb);
		sb.ExternalServices.Add<IReplicationPairingProvider>((IReplicationPairingProvider)new Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationComponent.SimpleReplicationPairingProvider(val.Server.ReplayContext));
		Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent inProcessReplicationSessionComponent = (Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent)RuntimeHelpers.GetUninitializedObject(typeof(Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext.ServerSessionComponent));
		EntityBackingField.SetValue(inProcessReplicationSessionComponent, _proxies.GetOrCreate(1u));
		val.Server.SessionComponent = inProcessReplicationSessionComponent;
		sb.SessionComponents.WithComponent((ComponentType: typeof(SimpleReplicationMappingSessionComponent), ObjectBuilder: new SimpleReplicationMappingSessionComponentObjectBuilder
		{
			InProcessReplication = val
		}));
		_replication = val;
		_comp = null;
		_worldApplied = (_spectatorQueued = (_failed = false));
		_worldPartsApplied = (_worldEntitiesBuilt = 0);
		_configured = true;
		_log.Info($"client: client session configured with Keen's in-process client replication ({immutableArray.Length} prefab bindings, replay table {val.ReplayTable.Stubs?.Length ?? 0} commands) instead of the Empty* services");
		return true;
	}

	public static bool PullPrefix(NetworkGameClientComponent __instance, SessionBuilder clientSession, ref Task __result)
	{
		ClientWorld current = Current;
		if (current == null || !current._configured)
		{
			return true;
		}
		__result = default;
		try
		{
			current.ApplySnapshot(__instance, clientSession);
		}
		catch (Exception ex)
		{
			current._log.Error($"client: applying the session snapshot failed (session starts with default session components): {ex}");
			current.Report(ClientPhase.Error, "snapshot apply failed: " + ex.GetType().Name + ": " + ex.Message);
		}
		return false;
	}

	private SessionComponentsSnapshot EnsureSnapshot()
	{
		if (_snapshot != null)
		{
			return _snapshot;
		}
		byte[] array = _snapshotRaw ?? (_snapshotRaw = _session?.TryTakeSnapshot() ?? throw new InvalidOperationException("no snapshot received"));
		NetIdProxyContext netIdProxyContext = new NetIdProxyContext(null, (int id) => _proxies.GetOrCreate((uint)id));
		_snapshot = EngineCodec.DeserializeSnapshot(array, netIdProxyContext);
		_snapshotBytes = array.Length;
		_snapshotRefs = netIdProxyContext.Read;
		return _snapshot;
	}

	private void ApplySnapshot(NetworkGameClientComponent client, SessionBuilder clientSession)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		string text = "no blob service";
		try
		{
			RegisterBlobs(clientSession.ExternalServices.TryGet<IStreamedOutBlobs>(), "session load");
			IReadOnlyList<Guid> readOnlyList = _session?.SnapshotBlobs ?? Array.Empty<Guid>();
			int num = readOnlyList.Count((Guid id) =>
			{
				ClientSession? session = _session;
				return session == null || !session.Blobs.Has(id);
			});
			text = $"{readOnlyList.Count} offloaded blobs referenced ({num} not here), {_blobsRegistered} registered";
			if (num > 0)
			{
				_log.Warn($"client: the snapshot references {num} offloaded blob(s) this PC does not have; session components using them may misbehave");
			}
		}
		catch (Exception ex)
		{
			text = "blob registration failed: " + ex.GetType().Name;
			_log.Error($"client: registering offloaded blobs at session load failed: {ex}");
		}
		SessionComponentsSnapshot sessionComponentsSnapshot = EnsureSnapshot();
		sessionComponentsSnapshot.Apply(ref clientSession.SessionComponents);
		JoinAccept joinAccept = _session?.Accept;
		long num2 = joinAccept?.IdentityId ?? 0;
		Keen.Game2.Simulation.GameSystems.GameTimes.GameTimeSessionComponentObjectBuilder? hostTime = sessionComponentsSnapshot.Components?.Select((SessionComponentsSnapshot.ComponentInfo c) => c.ObjectBuilder).OfType<Keen.Game2.Simulation.GameSystems.GameTimes.GameTimeSessionComponentObjectBuilder>().FirstOrDefault();
		_hostGameTime0 = (hostTime != null && hostTime.Valid && joinAccept != null) ? hostTime.ElapsedTime : null;
		_hostTick0 = joinAccept?.HostTick ?? 0;
		TeamPermissions.Enabled = joinAccept != null && joinAccept.Settings.Identities && joinAccept.Settings.Grids == GridPermissionMode.Team;
		string value = "shared identity (identities off on the host)";
		string summary = "no per-player data component";
		foreach (EntityBuilder.ComponentBuildInfo item in clientSession.SessionComponents.Components ?? new List<EntityBuilder.ComponentBuildInfo>())
		{
			object? objectBuilder = item.ObjectBuilder;
			InProcessPerPlayerDataClientSessionComponentObjectBuilder val = (InProcessPerPlayerDataClientSessionComponentObjectBuilder)((objectBuilder is InProcessPerPlayerDataClientSessionComponentObjectBuilder) ? objectBuilder : null);
			if (val != null)
			{
				val.ManuallyTranslatedData = PerPlayerDataFromSnapshot(sessionComponentsSnapshot, num2, out summary);
			}
			else if (num2 != 0L && item.Type == typeof(ClientPlayersSessionComponent))
			{
				object? objectBuilder2 = item.ObjectBuilder;
				PlayersSessionObjectBuilder val2 = (PlayersSessionObjectBuilder)((objectBuilder2 is PlayersSessionObjectBuilder) ? objectBuilder2 : null);
				if (val2 != null)
				{
					value = SeedPlayers(val2, _session.Players, joinAccept.ClientId, num2, joinAccept.PlayerId);
					_playersOb = val2;
				}
			}
		}
		if (num2 != 0L && _playersOb == null)
		{
			_log.Error("client: the client session has no ClientPlayersSessionComponent builder to seed; this player will act as the host's identity locally");
		}
		_snapshotApplied = true;
		string text2 = $"session snapshot applied: {sessionComponentsSnapshot.Components?.Length ?? 0} session components, {MpLogExtensions.Bytes(_snapshotBytes)}, {_snapshotRefs} entity refs (proxies; {SnapshotRefOwners(sessionComponentsSnapshot)}), players: {value}, per-player data: {summary}, {text}, {stopwatch.ElapsedMilliseconds} ms";
		_log.Info("client: " + text2);
		Report(ClientPhase.Progress, text2);
	}

	private object EmptyClientVisibleData(out string note)
	{
		note = "";
		try
		{
			return InProcessPerPlayerDataSessionComponent.CreateEmptyClientVisibleData();
		}
		catch (Exception ex)
		{
			note = " (Keen's defaults unavailable: " + ex.GetType().Name + ")";
			Type? typeFromHandle = typeof(InProcessPerPlayerDataBaseSessionComponent);
			Type nestedType = typeFromHandle.GetNestedType("ClientVisibleData", BindingFlags.NonPublic);
			Type? nestedType2 = typeFromHandle.GetNestedType("ClientVisiblePerPlayerData", BindingFlags.NonPublic);
			object obj = Activator.CreateInstance(nestedType, nonPublic: true);
			object value = Activator.CreateInstance(nestedType2, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[2]
			{
				InProcessPerPlayerDataSessionComponent.HotfixIdentity,
				true
			}, null);
			((IList)nestedType.GetProperty("Data").GetValue(obj)).Add(value);
			return obj;
		}
	}

	private object PerPlayerDataFromSnapshot(SessionComponentsSnapshot snapshot, long own, out string summary)
	{
		try
		{
			return BuildPerPlayerData(snapshot, own, out summary);
		}
		catch (Exception ex)
		{
			_log.Warn($"client: building per-player data from the host's snapshot failed ({ex.GetType().Name}: {ex.Message}); using empty defaults");
			object result = EmptyClientVisibleData(out string note);
			summary = "empty defaults (copy failed)" + note;
			return result;
		}
	}

	internal static object BuildPerPlayerData(SessionComponentsSnapshot snapshot, long own, out string summary)
	{
		//IL_035a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0364: Expected Obj, but got Unknown
		long num = -1559595548L;
		long num2 = ((own != 0L) ? own : num);
		Dictionary<long, List<PerPlayerData>> dictionary = new Dictionary<long, List<PerPlayerData>>();
		SessionComponentsSnapshot.ComponentInfo[] array = snapshot.Components ?? Array.Empty<SessionComponentsSnapshot.ComponentInfo>();
		for (int i = 0; i < array.Length; i++)
		{
			if (!(array[i].ObjectBuilder is PerPlayerDataSessionComponentObjectBuilder perPlayerDataSessionComponentObjectBuilder))
			{
				continue;
			}
			foreach (KeyValuePair<IdentityId, List<object>> item in (CollectionDictionary<IdentityId, List<object>, object>)(object)perPlayerDataSessionComponentObjectBuilder.PerPlayerData)
			{
				long num3 = item.Key;
				if (own != 0L || num3 == num)
				{
					if (!dictionary.TryGetValue(num3, out var value))
					{
						value = (dictionary[num3] = new List<PerPlayerData>());
					}
					value.AddRange(item.Value.OfType<PerPlayerData>());
				}
			}
		}
		List<long> ids = new List<long> { num2 };
		if (!ids.Contains(num))
		{
			ids.Add(num);
		}
		ids.AddRange(dictionary.Keys.Where((long k) => !ids.Contains(k) && !PlayerIdentities.IsNpc(k)));
		InProcessPerPlayerDataBaseSessionComponent.ClientVisibleData clientVisibleData = new InProcessPerPlayerDataBaseSessionComponent.ClientVisibleData();
		List<string> list2 = new List<string>();
		string text = null;
		foreach (long item2 in ids)
		{
			InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData clientVisiblePerPlayerData = new InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData(new IdentityId(item2), true);
			try
			{
				InProcessPerPlayerDataBaseSessionComponent.ClientVisibleData clientVisibleData2 = (InProcessPerPlayerDataBaseSessionComponent.ClientVisibleData)InProcessPerPlayerDataSessionComponent.CreateEmptyClientVisibleData();
				List<PerPlayerData> list3 = clientVisibleData2.Data[0].Data.ToList();
				clientVisibleData2.Data[0].Data.Clear();
				foreach (PerPlayerData item3 in list3)
				{
					clientVisiblePerPlayerData.Data.Add(item3);
				}
			}
			catch (Exception ex)
			{
				if (text == null)
				{
					text = " (Keen's defaults unavailable: " + ex.GetType().Name + ")";
				}
			}
			int num4 = 0;
			List<string> list4 = new List<string>();
			if (dictionary.TryGetValue(item2, out var value2))
			{
				foreach (PerPlayerData p in value2)
				{
					PerPlayerData val = clientVisiblePerPlayerData.Data.FirstOrDefault((PerPlayerData x) => ((object)x).GetType() == ((object)p).GetType());
					if (val != null)
					{
						clientVisiblePerPlayerData.Data.Remove(val);
					}
					clientVisiblePerPlayerData.Data.Add(p);
					num4++;
					list4.Add(((object)p).GetType().Name);
				}
			}
			int num5 = 0;
			if (item2 == num2 && item2 != num && dictionary.TryGetValue(num, out var value3))
			{
				foreach (Type t in value3.Select((PerPlayerData r) => ((object)r).GetType()).Distinct())
				{
					if (!clientVisiblePerPlayerData.Data.Any((PerPlayerData x) => t.IsInstanceOfType(x)) && !(t.GetConstructor(Type.EmptyTypes) == null))
					{
						try
						{
							clientVisiblePerPlayerData.Data.Add((PerPlayerData)Activator.CreateInstance(t));
							num5++;
						}
						catch (Exception)
						{
						}
					}
				}
			}
			PadToolbar(clientVisiblePerPlayerData.Data.OfType<ToolbarPerPlayerData>().FirstOrDefault());
			clientVisibleData.Data.Add(clientVisiblePerPlayerData);
			string value4;
			if (item2 == num2)
			{
				value4 = ((own != 0L) ? "own" : "host player (shared)");
			}
			else
			{
				value4 = ((item2 == num) ? "host player" : "other player");
			}
			list2.Add($"{item2} ({value4}): {num4} from the host ({string.Join(", ", list4)}){((num5 > 0) ? $", {num5} defaults added" : "")}");
		}
		summary = $"{ids.Count} identit{((ids.Count == 1) ? "y" : "ies")}, local {num2}; " + string.Join("; ", list2) + text;
		return clientVisibleData;
	}

	private static void PadToolbar(ToolbarPerPlayerData? toolbar)
	{
		if (toolbar == null)
		{
			return;
		}
		ToolbarSelection selection = toolbar.Selection.Selection;
		int num = Math.Max(1, selection.ToolbarIndexIndex + 1);
		int num2 = Math.Max(10, selection.TileIndex + 1);
		while (toolbar.ToolbarTiles.Count < num)
		{
			toolbar.ToolbarTiles.Add(new ObservableCollection<ToolbarTile>());
		}
		foreach (ObservableCollection<ToolbarTile> toolbarTile in toolbar.ToolbarTiles)
		{
			while (toolbarTile.Count < num2)
			{
				toolbarTile.Add(null);
			}
		}
	}

	internal static string SeedPlayers(PlayersSessionObjectBuilder ob, IReadOnlyList<PlayerInfo> table, ushort ownClientId, long own, long ownPlayer)
	{
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		ob.PlayerData.Clear();
		ob.Identities.Clear();
		ob.ClientIds.Clear();
		List<PlayerInfo> list = (from r in table
			where r.ClientId != 1 && r.Identity != own
			orderby r.ClientId
			select r).ToList();
		if (table.Any(p => p.ClientId == 1)) Add(1, 1559595546L, -1559595548L);
		Add(ownClientId, ownPlayer, own);
		foreach (PlayerInfo item in list)
		{
			Add(item.ClientId, item.Player, item.Identity);
		}
		ob.Identities[InProcessPerPlayerDataSessionComponent.HotfixIdentity] = new PlayerIdentity(ownPlayer);
		return $"local player = IdentityId {own} (PlayerIdentity {ownPlayer}, ClientId {ownClientId}); {ob.PlayerData.Count} players seeded";
		void Add(ushort clientId, long player, long identity)
		{
			//IL_0003: Unknown result type (might be due to invalid IL or missing references)
			//IL_001f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0020: Unknown result type (might be due to invalid IL or missing references)
			//IL_003f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0058: Unknown result type (might be due to invalid IL or missing references)
			//IL_0070: Unknown result type (might be due to invalid IL or missing references)
			PlayerIdentity val = new PlayerIdentity(player);
			PlayerData value = new PlayerData
			{
				ClientId = new ClientId(clientId),
				Player = val,
				Identity = new IdentityId(identity)
			};
			ob.PlayerData.TryAdd(val, value);
			ob.ClientIds.TryAdd(value.ClientId, val);
			ob.Identities.TryAdd(value.Identity, val);
		}
	}

	internal static void RepairPlayers(PlayersSessionObjectBuilder ob)
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		if (ob.ClientIds.TryGetValue(new ClientId(1), out var host)) ob.Identities[InProcessPerPlayerDataSessionComponent.HotfixIdentity] = host;
	}

	private void UpdatePlayers(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp, ClientSession session)
	{
		//IL_015e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d7: Unknown result type (might be due to invalid IL or missing references)
		PlayersSessionObjectBuilder playersOb = _playersOb;
		if (playersOb != null && !_playersRepaired)
		{
			_playersRepaired = true;
			ClientPlayersSessionComponent clientPlayersSessionComponent = comp.Session.SessionComponents.TryGet<ClientPlayersSessionComponent>();
			RepairPlayers(playersOb);
			long num = ((clientPlayersSessionComponent != null) ? ((long)clientPlayersSessionComponent.LocalPlayerIdentity) : 0);
			string text = $"LocalPlayerIdentity = {num}" + ((num == session.IdentityId) ? " (this player's own identity)" : $" (EXPECTED {session.IdentityId}: the client acts as another identity)");
			if (num == session.IdentityId)
			{
				_log.Info("client: " + text);
			}
			else
			{
				_log.Error("client: " + text);
			}
			Report((num == session.IdentityId) ? ClientPhase.Progress : ClientPhase.Warning, text);
		}
		if (playersOb != null && session.PlayersVersion != _playersVersionSeen)
		{
			_playersVersionSeen = session.PlayersVersion;
			int num2 = 0;
			foreach (PlayerInfo player in session.Players)
			{
				PlayerIdentity val = new PlayerIdentity(player.Player);
				PlayerData value = new PlayerData
				{
					ClientId = new ClientId(player.ClientId),
					Player = val,
					Identity = new IdentityId(player.Identity)
				};
				if (playersOb.PlayerData.TryAdd(val, value))
				{
					num2++;
				}
				playersOb.ClientIds.TryAdd(value.ClientId, val);
				playersOb.Identities.TryAdd(value.Identity, val);
			}
			if (num2 > 0)
			{
				_log.Info($"client: {num2} player(s) added to this game's player table ({ClientSession.DescribePlayers(session.Players)})");
			}
		}
		(int, int, long) status = session.Status;
		if (!_pcuStreamedLogged && _ticks % 30 == 0L)
		{
			PCUClientSessionComponent pCUClientSessionComponent = comp.Session.SessionComponents.TryGet<PCUClientSessionComponent>();
			if (pCUClientSessionComponent != null && PcuModelField?.GetValue(pCUClientSessionComponent) is PlayerPcuModel playerPcuModel && playerPcuModel != _pcuStandIn && _observables.IsReplica(playerPcuModel))
			{
				_pcuStreamedLogged = true;
				_log.Info($"client: PCU readout is the host's streamed model (observable #{_observables.IdOf(playerPcuModel)}): {playerPcuModel.PCU} / {playerPcuModel.MaxPCU}" + ((_pcuStandIn != null) ? "; the stand-in is retired" : ""));
			}
		}
		if (status.Item3 == _statusSeen || !_pcuDone)
		{
			return;
		}
		_statusSeen = status.Item3;
		try
		{
			PCUClientSessionComponent pCUClientSessionComponent2 = comp.Session.SessionComponents.TryGet<PCUClientSessionComponent>();
			if (pCUClientSessionComponent2 != null && PcuModelField?.GetValue(pCUClientSessionComponent2) is PlayerPcuModel playerPcuModel2 && playerPcuModel2 == _pcuStandIn)
			{
				bool flag = playerPcuModel2.MaxPCU == 1073741823;
				(playerPcuModel2.PCU, playerPcuModel2.MaxPCU, _) = status;
				if (flag)
				{
					_log.Info($"client: PCU readout is live from the host's server: {status.Item1} / {status.Item2}");
				}
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-pcu-live", $"client: applying the host's PCU numbers failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private string SnapshotRefOwners(SessionComponentsSnapshot snapshot)
	{
		try
		{
			List<string> list = new List<string>();
			SessionComponentsSnapshot.ComponentInfo[] array = snapshot.Components ?? Array.Empty<SessionComponentsSnapshot.ComponentInfo>();
			for (int i = 0; i < array.Length; i++)
			{
				SessionComponentsSnapshot.ComponentInfo componentInfo = array[i];
				int num = CountEntities(componentInfo.ObjectBuilder, 0);
				if (num > 0)
				{
					list.Add($"{componentInfo.Type?.Name ?? "?"} {num}");
				}
			}
			return (list.Count == 0) ? "none found in fields" : ("in " + string.Join(", ", list));
		}
		catch (Exception ex)
		{
			return "owner scan failed: " + ex.GetType().Name;
		}
	}

	private static int CountEntities(object? o, int depth)
	{
		if (o == null || depth > 3)
		{
			return 0;
		}
		if (o is Entity)
		{
			return 1;
		}
		if (o is string || o.GetType().IsPrimitive || o.GetType().IsEnum)
		{
			return 0;
		}
		int num = 0;
		if (o is IEnumerable enumerable && depth < 3)
		{
			int num2 = 0;
			foreach (object item in enumerable)
			{
				num += CountEntities(item, depth + 1);
				if (++num2 > 2000)
				{
					break;
				}
			}
			return num;
		}
		if (depth >= 2)
		{
			return 0;
		}
		FieldInfo[] fields = o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		foreach (FieldInfo fieldInfo in fields)
		{
			if (!fieldInfo.FieldType.IsPrimitive && !fieldInfo.FieldType.IsEnum && !(fieldInfo.FieldType == typeof(string)))
			{
				num += CountEntities(fieldInfo.GetValue(o), depth + 1);
			}
		}
		return num;
	}

	public static bool CommitPrefix(object __instance)
	{
		ClientWorld current = Current;
		InProcessReplication val = current?._replication;
		if (val == null || !(__instance is Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent inProcessReplicationSessionComponent) || inProcessReplicationSessionComponent._context != val.Client)
		{
			return true;
		}
		if (current._failed)
		{
			return false;
		}
		try
		{
			current.OnClientCommit(inProcessReplicationSessionComponent);
		}
		catch (Exception ex)
		{
			current._failed = true;
			current._log.Error($"client: per-tick exchange failed, world sync stops (the game keeps running): {ex}");
			current.Report(ClientPhase.Error, "client tick failed: " + ex.GetType().Name + ": " + ex.Message);
		}
		return false;
	}

	private void OnClientCommit(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		_ticks++;
		if (_comp != comp)
		{
			_comp = comp;
			_sessionProxy = _proxies.GetOrCreate(1u);
			if (!comp.ReplayContext.HasPairingRemote(_sessionProxy))
			{
				comp.ReplayContext.AddObjectPairing(_sessionProxy, comp.Entity);
			}
			_proxies.MarkLive(1u);
			_log.Info("client: client scene is ticking; host session entity paired (NetworkId 1)");
			Report(ClientPhase.SessionReady, "client scene ticking, replication attached");
			Progress.SetInGame(inGame: true);
			if (!_worldApplied)
			{
				Progress.Set(JoinStage.Building, MpClock.NowMs());
			}
			if (!_snapshotApplied)
			{
				_log.Warn("client: the session snapshot was never applied (WorldSessionComponent did not call IServerDataProvider.PullDataFromServerAsync); session components run with defaults");
				Report(ClientPhase.Warning, "snapshot not applied: PullDataFromServerAsync was not called");
			}
		}
		ClientSession? session = _session;
		if (session != null && session.Blobs.HasFresh)
		{
			RegisterBlobs(BlobTarget(comp.Session), "tick");
		}
		ClientSession session2 = _session;
		if (session2 != null && session2.IdentityId != 0L)
		{
			try
			{
				UpdatePlayers(comp, session2);
			}
			catch (Exception ex)
			{
				_log.WarnOnce("client-players", "client: updating the player table failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}
		((IReplicationContext)comp._context).CommitFrame();
		ReplicationCommandBuffer buffer = Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.AllocateCB();
		object exchangeSync = comp._exchangeSync;
		ReplicationCommandBuffer buffer2;
		if (exchangeSync != null)
		{
			lock (exchangeSync)
			{
				buffer2 = ((ReplicationCommandBufferSessionComponent)(object)comp).Buffer;
				((ReplicationCommandBufferSessionComponent)(object)comp).Buffer = buffer;
			}
		}
		else
		{
			buffer2 = ((ReplicationCommandBufferSessionComponent)(object)comp).Buffer;
			((ReplicationCommandBufferSessionComponent)(object)comp).Buffer = buffer;
		}
		if (_worldApplied)
		{
			ClientSession session3 = _session;
			if (session3 != null)
			{
				LookTick(session3, MpClock.NowMs());
			}
		}
		if (_worldApplied && !_upstreamFailed)
		{
			try
			{
				SendUpstream(comp, buffer2);
			}
			catch (Exception ex2)
			{
				_upstreamFailed = true;
				_log.Error($"client: input upstream failed and is switched off (you can still watch): {ex2}");
				Report(ClientPhase.Error, "input upstream failed: " + ex2.GetType().Name + ": " + ex2.Message);
			}
		}
		else if (buffer2.GetBuffersCount() > 0)
		{
			_c2sDropped++;
			HoldEarlyStories(buffer2, comp);
		}
		Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.ReturnCB(buffer2);
		BuildInbound(comp);
		UpdateNametags(comp);
		long num = MpClock.NowMs();
		if (num - _lastStatusMs < 10000)
		{
			return;
		}
		_lastStatusMs = num;
		ClientSession session4 = _session;
		_log.Info($"client sync: ticks {_ticks}, commands injected {_commands}, lifecycle applied {_lifecycleApplied}, entities built {_entitiesBuilt}, state applied {_stateApplied} (bad size {_stateBadSize}), proxies {_proxies.LiveCount}/{_proxies.Count}, " + ((session4 != null) ? $"pending state {session4.State.DirtyCount} (stale {session4.State.Stale}, expired {session4.State.Expired}), lifecycle buffered {session4.Lifecycle.Buffered} ready {session4.Lifecycle.Ready}, blobs {session4.Blobs.Describe()} / {_blobsRegistered} registered, " : "") + $"roots dropped {_rootsDropped} (bundles retried {_bundlesRetried}), despawns skipped {_despawnSkipped}, dead entities purged {_purgedDead}, C2S ticks dropped {_c2sDropped}");
		if (session4 != null)
		{
			_log.Info($"client sync: character {((_ownNetId != 0) ? $"NetworkId {_ownNetId} possessed" : ((_pendingNetId != 0) ? $"NetworkId {_pendingNetId} assigned, not built yet" : (_charRequested ? "requested" : "none")))}; {_upstream?.Describe() ?? "upstream: idle"}, sent {session4.InputSent} input values / {session4.CommandsSent} commands; {session4.Smoother.Describe()}, applied {_smoothApplied}; {LookDescribe()}");
		}
		if (session4 != null)
		{
			_log.Info($"client sync: host signals applied {_signalsApplied}, dropped {_signalsDropped}, names registered {_namesRegistered} (known {_namesKnown}, bad {_namesBad})" + ((_sigDrops.Count == 0) ? "" : (" (" + string.Join(", ", from kv in _sigDrops.OrderByDescending((KeyValuePair<string, long> kv) => kv.Value).Take(6)
				select $"{kv.Key} x{kv.Value}") + ")")) + $"; {_stories?.Describe() ?? "stories: none"}{DescribeSecure()}; {ReplayGuard.Describe()}; 0.7.6: {AudioGate.Describe()}; {ToolShadowGuard.Describe()}; {BuildGuards.Describe()}; {ControlGuard.Describe()}; data types: {DataTypeRegistry.State}; 0.8.3: {_sids.Describe()}, {FractureGuard.Describe()}; {SlowTicks.Describe()}");
		}
		if (session4 != null && (_observables.Count > 0 || session4.ObservableOpsIn > 0))
		{
			_log.Info($"client sync: {_observables.Describe()}; {session4.ObservableOpsIn} steps received, {session4.ObservableEditsOut} edits sent");
		}
	}

	private void RegisterHostStrings(List<string> names)
	{
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		string text = null;
		foreach (string name in names)
		{
			try
			{
				if (!_sids.TryRegister(name))
				{
					if (StringHash.TryGet(name, out var _))
					{
						num2++;
						continue;
					}
					StringHash.GetOrCompute(name);
					num++;
				}
			}
			catch (Exception ex)
			{
				num3++;
				if (text == null)
				{
					text = $"'{name}': {ex.GetType().Name}: {ex.Message}";
				}
			}
		}
		_namesRegistered += num;
		_namesKnown += num2;
		_namesBad += num3;
		if (_namesRegistered + _namesKnown == num + num2)
		{
			_log.Info($"client: host StringHash names: {num} registered, {num2} already known, {num3} failed (first table)");
		}
		if (num3 > 0)
		{
			_log.WarnOnce("client-names-bad", $"client: {num3} host StringHash name(s) could not be registered (first: {text})");
		}
	}

	private ObjectSpace ClientSpace()
	{
		return _observables.Attach(new ObjectSpace
		{
			Name = "client",
			IdOf = NetIdOf,
			Resolve = (uint id, bool translated) =>
			{
				if (!translated)
				{
					return ClientEntity(id);
				}
				Entity e;
				return (ClientEntity(id) == null || !_proxies.TryGet(id, out e)) ? null : e;
			},
			Session = _comp?.Session,
			TranslatedSession = SessionStandIn()
		});
	}

	private void HoldEarlyStories(ReplicationCommandBuffer old, Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		if (_earlyStoriesSent || _earlyStories.Count >= 32)
		{
			return;
		}
		try
		{
			List<Delegate> list = new List<Delegate>();
			ClientUpstream.CollectStoryMoves(old, list, 32 - _earlyStories.Count);
			foreach (Delegate item in list)
			{
				_earlyStories.Add((item, comp));
			}
			if (list.Count > 0)
			{
				_log.Info($"client: holding {list.Count} story move(s) made during the load until the world is applied ({_earlyStories.Count} held)");
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-early-stories", $"client: reading story moves made during the load failed ({ex.GetType().Name}: {ex.Message}); they are dropped");
		}
	}

	private void SendEarlyStories(ClientSession session)
	{
		if (_earlyStoriesSent)
		{
			return;
		}
		_earlyStoriesSent = true;
		if (_earlyStories.Count == 0)
		{
			return;
		}
		List<string> list = new List<string>();
		foreach (var (d, comp) in _earlyStories)
		{
			try
			{
				if (ClientStories.ReadClosure(d, out NetworkStoryContext _, out object box, out Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent _) && box != null)
				{
					list.Add(StoryState.Name(ObjectCodec.TypeName(box.GetType())));
				}
				_stories.OnMove(d, session, ClientSpace(), comp);
			}
			catch (Exception ex)
			{
				_log.WarnOnce("client-early-story", $"client: sending a story held during the load failed ({ex.GetType().Name}: {ex.Message})");
			}
		}
		_log.Info($"client: {_earlyStories.Count} story move(s) held during the load sent to the host now: {string.Join(", ", list)}");
		_earlyStories.Clear();
	}

	private Keen.VRage.Library.UI.ObservableObject? AdoptOwnPerPlayerData(Type type)
	{
		long own = _session?.IdentityId ?? 0;
		if (own == 0 || !typeof(PerPlayerData).IsAssignableFrom(type))
		{
			return null;
		}
		InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData? mine = _comp?.Session.SessionComponents.TryGet<InProcessPerPlayerDataClientSessionComponent>()?._data?.Data.FirstOrDefault((InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData d) => (long)d.IdentityId == own);
		return mine?.Data.FirstOrDefault((PerPlayerData p) => type.IsInstanceOfType(p) && !_observables.IsReplica(p));
	}

	private void SyncGameTime(uint hostTick)
	{
		Keen.Game2.Simulation.GameSystems.GameTimes.GameTimeSessionComponent? gameTime = _comp?.Session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.GameTimes.GameTimeSessionComponent>();
		if (_hostGameTime0 is not TimeSpan hostTime0 || gameTime?._timeProvider == null)
		{
			return;
		}
		// CommitFrame is [DoNotPause]: host ticks include the host's own paused frames (video screens), which show here as a lead.
		TimeSpan drift = hostTime0 + Keen.Game2.Simulation.UpdateTime.TIME_PER_FRAME * (hostTick - _hostTick0) - gameTime.FrameTime;
		if (drift.Duration() < TimeSpan.FromSeconds(1.0))
		{
			return;
		}
		gameTime._elapsedTimeOffset += drift;
		if (++_gameTimeSyncs <= 5 || _gameTimeSyncs % 60 == 0)
		{
			_log.Info($"client: game clock moved {drift.TotalSeconds:+0.0;-0.0} s to the host's ({_gameTimeSyncs} corrections; weather and other timed events follow the host)");
		}
	}

	private void ApplyObservables(List<ObservableOp> ops)
	{
		try
		{
			ObservableObjectReplicationClientSessionComponent comp = _comp?.Session.SessionComponents.TryGet<ObservableObjectReplicationClientSessionComponent>();
			_observables.Apply(comp, ops, ClientSpace());
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-observables", $"client: applying the host's observable steps failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private Session? SessionStandIn()
	{
		if (_sessionStandIn != null || _sessionProxy == null)
		{
			return _sessionStandIn;
		}
		try
		{
			Session session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
			typeof(Session).GetField("<SessionComponents>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, _sessionProxy);
			_sessionStandIn = session;
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-session-standin", $"client: no session stand-in ({ex.GetType().Name}: {ex.Message}); host signals with a Session argument are dropped");
		}
		return _sessionStandIn;
	}

	private readonly Dictionary<uint, (bool Ragdoll, long Until)> _ragdolls = new Dictionary<uint, (bool, long)>();

	internal long RagdollWritesSkipped;

	/// <summary>
	/// SE2 simulates a dead character's ragdoll on each side and lets it drive the transform; writing the host's
	/// transform as well made the body fight between two positions every frame.
	/// </summary>
	private bool Ragdolling(uint netId, long now)
	{
		if (_ragdolls.TryGetValue(netId, out var cached) && now < cached.Until)
		{
			return cached.Ragdoll;
		}
		bool ragdoll = ClientEntity(netId) is Entity entity && RagdollGuard.DrivesTransform(entity);
		_ragdolls[netId] = (ragdoll, now + 250);
		if (_ragdolls.Count > 4096)
		{
			_ragdolls.Clear();
		}
		return ragdoll;
	}

	private Entity? ClientEntity(uint id)
	{
		if (_comp == null || !_proxies.IsLive(id) || !_proxies.TryGet(id, out Entity e))
		{
			return null;
		}
		bool skip = false;
		if (_comp.ReplayContext.TranslateObject(e, ReplicationFailureStrategy.NullOnAnyFail, ref skip) is Entity entity && !EngineCodec.IsDead(entity))
		{
			return entity;
		}
		return null;
	}

	private void WriteSignals(ClientSession session, ReplicationCommandBuffer cb, List<WireCommand> cmds)
	{
		ObjectSpace objectSpace = ClientSpace();
		bool flag = _comp != null && ReplicationProbe.IsGuarded(_comp);
		foreach (WireCommand cmd in cmds)
		{
			string text = $"key {cmd.Key}";
			string fail = null;
			SignalKeyDef def;
			if (!flag)
			{
				fail = "replay guard not armed (see the probe:CommitFrame line)";
			}
			else if (!session.RemoteSignals.TryGet(cmd.Key, out def))
			{
				fail = (session.RemoteSignals.IsKnown(cmd.Key) ? "unusable here" : "unknown key");
			}
			else
			{
				text = def.Name;
				CommandLayout commandLayout = CommandLayouts.ByName(def.Name);
				if (commandLayout == null)
				{
					fail = "not in this build";
				}
				else
				{
					uint num = ((cmd.Refs.Count > 0 && cmd.Refs[0].Kind == WireKind.Entity) ? cmd.Refs[0].Id : 0u);
					if (num == 0 || !_proxies.IsLive(num))
					{
						fail = "target not live here";
					}
					else
					{
						Entity entity = ClientEntity(num);
						if (entity == null || !EntityInternalSignalsApi.Constructed(entity))
						{
							fail = "target destroyed here";
						}
						else
						{
							WireCommand c = cmd;
							if (commandLayout.StringIdOffsets.Length != 0)
							{
								byte[] array = _sids.Map(cmd.Payload, commandLayout.StringIdOffsets, toLocal: true);
								if (array == null)
								{
									fail = "StringId the host did not announce";
								}
								else
								{
									c = new WireCommand
									{
										Key = cmd.Key,
										Payload = array,
										Refs = cmd.Refs
									};
									fail = FractureGuard.Check(commandLayout, c, entity);
								}
							}
							if (fail == null)
							{
								objectSpace.ClearLocalIds();
								SignalCodec.Write(cb, commandLayout, c, objectSpace, _sigScratch, ref fail);
							}
						}
					}
				}
			}
			if (fail == null)
			{
				_signalsApplied++;
				if (_sigFirst.Count < 80 && _sigFirst.Add(text))
				{
					_log.Info("client: host signal " + ClientUpstream.Short(text) + " written for replay (first one; the replay guard reports handler errors)");
				}
			}
			else
			{
				_signalsDropped++;
				string key = ClientUpstream.Short(text) + ": " + fail;
				_sigDrops[key] = (_sigDrops.TryGetValue(key, out var value) ? (value + 1) : 1);
			}
		}
	}

	private uint? NetIdOf(Entity? e)
	{
		if (e == null || _comp == null)
		{
			return null;
		}
		ReplayContext replayContext = _comp.ReplayContext;
		if (!replayContext.HasPairingLocal(e))
		{
			return null;
		}
		if (!(replayContext.TranslateLocalToRemote(e) is Entity e2) || !_proxies.TryGetId(e2, out var id))
		{
			return null;
		}
		return id;
	}

	private void SendUpstream(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp, ReplicationCommandBuffer old)
	{
		ClientSession session = _session;
		Entity ownCharacter = _ownCharacter;
		if (session == null)
		{
			return;
		}
		if (_upstream == null)
		{
			_upstream = new ClientUpstream(_log);
		}
		ClientUpstream upstream = _upstream;
		if (upstream.OnObservableEdit == null)
		{
			upstream.OnObservableEdit = (object? replica, string? name, object? value) =>
			{
				_observables.OnLocalEdit(replica, name, value, ClientSpace());
			};
		}
		if (_stories == null)
		{
			_stories = new ClientStories(_log);
		}
		_stories.PerPlayer = session.IdentityId != 0;
		SendEarlyStories(session);
		long nowMs = MpClock.NowMs();
		if (old.GetBuffersCount() > 0)
		{
			_controlled.Clear();
			if (ownCharacter != null)
			{
				Collect(ownCharacter, 0);
			}
			try
			{
				IController controller = _controller;
				if (controller != null && ownCharacter != null)
				{
					foreach (var (controllable, entity) in controller.ControlledEntities)
					{
						if (controllable is Component component && IsForeignCharacter(component.Entity))
						{
							continue;
						}
						if (controllable is Component component2 && component2.Entity != ownCharacter && !_controlled.Contains(component2.Entity))
						{
							Collect(component2.Entity, 0);
							Entity entity2 = component2.Entity;
							_recentlyDriven[entity2] = nowMs;
							for (int num = 0; num < 8; num++)
							{
								Entity entity3 = entity2.TryGet<HierarchyComponent>()?.Parent;
								if (entity3 == null)
								{
									break;
								}
								entity2 = entity3;
								_recentlyDriven[entity2] = nowMs;
							}
							_controlled.Add(entity2);
						}
						if (entity != null && !_controlled.Contains(entity))
						{
							Collect(entity, 0);
						}
					}
				}
			}
			catch (Exception ex)
			{
				_log.WarnOnce("client-controlled", $"client: reading what the player controls failed ({ex.GetType().Name}: {ex.Message}); only the character's input is sent");
			}
			ObjectSpace space = ClientSpace();
			if (_recentlyDriven.Count > 0)
			{
				foreach (Entity item in (from kv in _recentlyDriven
					where nowMs - kv.Value > 2000
					select kv.Key).ToList())
				{
					_recentlyDriven.Remove(item);
				}
			}
			_upstream.Capture(old, (Entity? e) => (e == null || !_controlled.Contains(e)) ? ((uint?)null) : NetIdOf(e), space, (Delegate d) =>
			{
				_stories.OnMove(d, session, ClientSpace(), comp);
			}, (Entity? e) => (e == null || (!_controlled.Contains(e) && !_recentlyDriven.ContainsKey(e))) ? ((uint?)null) : NetIdOf(e));
		}
		_upstream.Flush(session, MpClock.NowMs());
		List<MemberEdit> list = _observables.TakeEdits();
		if (list != null)
		{
			session.SendObservableEdits(list);
		}
		void Collect(Entity e, int depth)
		{
			_controlled.Add(e);
			if (depth <= 8)
			{
				PooledList<Entity> pooledList = e.TryGet<HierarchyComponent>()?.Children;
				if (pooledList != null)
				{
					foreach (Entity item2 in pooledList)
					{
						Collect(item2, depth + 1);
					}
				}
			}
		}
	}

	/// <summary>
	/// A component that throws while its entity is added leaves the entity in the scene half-initialised, and its
	/// jobs then throw every tick (a paint tool did that and crashed the game). Back to staging and closed instead.
	/// </summary>
	private int CloseHalfAdded(Session session, Entity[] built)
	{
		int closed = 0;
		foreach (Entity entity in built)
		{
			if (entity == null || EngineCodec.IsDead(entity)) continue;
			try
			{
				if (!session.IsEntityInScene(entity)) continue;
				session.MarkEntityForClose(entity);
				closed++;
			}
			catch (Exception ex)
			{
				// Its own removal listeners threw too; staging alone keeps its jobs from running.
				try { entity.Data.Set<Keen.VRage.DCS.CoreData.StagingTag>(default); } catch (Exception) { }
				_log.WarnOnce("client-half-added", "client: closing a half-added entity threw " + ex.GetType().Name + ": " + ex.Message + "; left in staging");
			}
		}
		return closed;
	}

	private void CharacterFlow(ClientSession session, VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader w, int baseIndex, ref int cmds, long now)
	{
		if (!_charRequested)
		{
			_charRequested = true;
			_charRequestMs = now;
			session.RequestCharacter(0);
			_log.Info($"client: asking the host for a character (ClientId {session.ClientId})");
			Report(ClientPhase.Progress, $"character requested (ClientId {session.ClientId})");
		}
		(byte, uint, string, long) assignment = session.Assignment;
		if (assignment.Item4 != _assignSeen)
		{
			_assignSeen = assignment.Item4;
			switch (assignment.Item1)
			{
			case 0:
				_pendingNetId = assignment.Item2;
				_assignedNetId = assignment.Item2;
				_possessQueued = false;
				_possessFailures = 0;
				_possessRetryAtMs = 0L;
				_charFailed = false;
				Progress.Set(JoinStage.WaitingForCharacter, now);
				break;
			case 1:
				_charFailed = true;
				_log.Warn("client: the host could not give us a character (" + assignment.Item3 + "); free camera instead");
				Report(ClientPhase.Warning, "no character: " + assignment.Item3);
				break;
			case 2:
				if (assignment.Item2 == _ownNetId)
				{
					ClearOwnCharacter("lost on the host: " + assignment.Item3);
				}
				_pendingNetId = 0u;
				if (assignment.Item2 == _assignedNetId)
				{
					_assignedNetId = 0u;
				}
				_respawnAtMs = now + 5000;
				_charRequestMs = now;
				_log.Info($"client: our character is gone ({assignment.Item3}); asking for a new one in {5} s");
				break;
			}
		}
		if (_respawnAtMs != 0L && now >= _respawnAtMs)
		{
			_respawnAtMs = 0L;
			session.RequestCharacter(1);
			_log.Info("client: asking the host for a new character");
		}
		uint assigned = _assignedNetId;
		if (assigned != 0 && !_possessQueued && now >= _possessRetryAtMs && _proxies.IsLive(assigned) && !OwnCharacterControlled(assigned))
		{
			_pendingNetId = 0u;
			_possessQueued = true;
			w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<Keen.VRage.Multiplayer.Replications.InProcess.SceneContext>)delegate
			{
				Possess(assigned);
			});
			cmds++;
		}
		if (_o.Spectator && !_spectatorQueued && _ownCharacter == null && _assignedNetId == 0 && session.SpawnMenu == null && (_charFailed || now - _charRequestMs > 60000))
		{
			_spectatorQueued = true;
			_log.Info("client: " + (_charFailed ? "no character" : $"no character assigned after {60} s") + "; using the free camera until one arrives");
			w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<Keen.VRage.Multiplayer.Replications.InProcess.SceneContext>)delegate
			{
				Spectate();
			});
			cmds++;
		}
	}

	private void Possess(uint netId)
	{
		_possessQueued = false;
		if (netId != _assignedNetId)
		{
			return;
		}
		try
		{
			Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp = _comp;
			if (!_proxies.TryGet(netId, out Entity e))
			{
				throw new InvalidOperationException($"NetworkId {netId} unknown");
			}
			bool skip = false;
			if (!(comp.ReplayContext.TranslateObject(e, ReplicationFailureStrategy.NullOnAnyFail, ref skip) is Entity entity) || EngineCodec.IsDead(entity))
			{
				throw new InvalidOperationException($"NetworkId {netId} has no live client entity");
			}
			IControllable controllable = entity.Components.OfType<IControllable>().FirstOrDefault() ?? throw new InvalidOperationException($"entity {entity} has no IControllable component");
			PlayerControllerComponent playerControllerComponent = (comp.Session.SessionComponents.TryGet<ClientPlayersSessionComponent>() ?? throw new InvalidOperationException("no ClientPlayersSessionComponent")).LocalPlayerController ?? throw new InvalidOperationException("no local player controller");
			bool flag = _ownNetId == netId;
			playerControllerComponent.RequestUnpossessionAll();
			playerControllerComponent.RequestPossession(controllable);
			_controller = playerControllerComponent;
			_ownCharacter = entity;
			_ownNetId = netId;
			_session?.Smoother.SetRaw(netId, raw: true);
			_ownSmoothed = false;
			_upstream?.Reset();
			if (!flag)
			{
				LookBegin(netId);
			}
			CloseSpectator("own character possessed");
			_possessRetryAtMs = MpClock.NowMs() + 1000;
			if (flag)
			{
				_log.Info($"client: control back on own character NetworkId {netId} (this game's controller had left it)");
				return;
			}
			string text = $"POSSESSED own character NetworkId {netId} ({entity.GetComposition()?.Guid.ToString() ?? "?"}, {controllable.GetType().Name}); input goes to the host from now on" + ((_possessFailures > 0) ? $" (after {_possessFailures} failed attempt(s))" : "");
			_log.Info("client: " + text);
			Report(ClientPhase.CharacterReady, text);
			Progress.Set(JoinStage.Playing, MpClock.NowMs());
			EnsureLocalPcuModel(comp);
		}
		catch (Exception ex)
		{
			_possessFailures++;
			_possessRetryAtMs = MpClock.NowMs() + 1000;
			if (_possessFailures <= 3 || _possessFailures % 30 == 0)
			{
				_log.Warn($"client: possessing our character (NetworkId {netId}) failed (attempt {_possessFailures}): {ex.GetType().Name}: {ex.Message}; retrying in {1} s");
				Report(ClientPhase.Warning, $"possess attempt {_possessFailures} failed: {ex.GetType().Name}: {ex.Message}");
			}
		}
	}

	private void EnsureLocalPcuModel(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		if (_pcuDone)
		{
			return;
		}
		_pcuDone = true;
		try
		{
			PCUClientSessionComponent pCUClientSessionComponent = comp.Session.SessionComponents.TryGet<PCUClientSessionComponent>();
			if (pCUClientSessionComponent == null || PcuModelField == null)
			{
				_log.Warn("client: no PCU session component (" + ((pCUClientSessionComponent == null) ? "component" : "field") + " missing); block placement may be refused locally");
				return;
			}
			if (PcuModelField.GetValue(pCUClientSessionComponent) is PlayerPcuModel local)
			{
				_log.Info("client: PCU model already attached (" + (_observables.IsReplica(local) ? "the host's streamed model" : "local") + "); no stand-in needed");
				return;
			}
			PlayerPcuModel playerPcuModel = new PlayerPcuModel
			{
				PCU = 0,
				MaxPCU = 1073741823
			};
			PcuModelField.SetValue(pCUClientSessionComponent, playerPcuModel);
			_pcuStandIn = playerPcuModel;
			try
			{
				MethodInfo method = typeof(PCUClientSessionComponent).GetMethod("OnStreamedModelDataChanged", BindingFlags.Instance | BindingFlags.NonPublic);
				if (method != null)
				{
					playerPcuModel.PropertyChanged += (PropertyChangedEventHandler)Delegate.CreateDelegate(typeof(PropertyChangedEventHandler), pCUClientSessionComponent, method);
				}
				else
				{
					_log.Warn("client: PCUClientSessionComponent.OnStreamedModelDataChanged not found; the PCU readout may not refresh");
				}
			}
			catch (Exception ex)
			{
				_log.Warn($"client: PCU readout refresh hook failed ({ex.GetType().Name}: {ex.Message})");
			}
			_statusSeen = 0L;
			IMpLog log = _log;
			ClientSession? session = _session;
			log.Info((session == null || session.IdentityId != 0) ? "client: PCU model stand-in installed (this player's live PCU on the host's server follows every second)" : "client: PCU model stand-in installed (the host's server enforces the real limit; the PCU readout here is not live)");
		}
		catch (Exception ex2)
		{
			_log.Warn($"client: PCU model stand-in failed ({ex2.GetType().Name}: {ex2.Message}); block placement may be refused locally");
		}
	}

	private void ClearOwnCharacter(string why)
	{
		if (_ownCharacter != null)
		{
			_log.Info($"client: our character NetworkId {_ownNetId} is no longer controlled ({why})");
			_session?.Smoother.SetRaw(_ownNetId, raw: false);
			LookEnd(why);
			_ownHostLocalMs = null;
			_ownCharacter = null;
			_ownNetId = 0u;
			_upstream?.Reset();
		}
	}

	private void ApplySmoothed(ClientSession session, VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader w, int baseIndex, ref int cmds, long now)
	{
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		if (!session.Smoother.Enabled || !session.TryGetTransformType(out var typeId, out var size))
		{
			return;
		}
		_smoothOut.Clear();
		session.Smoother.Sample(now, _smoothOut);
		NoteDeckSample(_smoothOut);
		foreach (var (num, array) in _smoothOut)
		{
			if (array.Length == size && _proxies.IsLive(num) && _proxies.TryGet(num, out Entity e) && !LookTakeSmoothed(num, array, now))
			{
				if (Ragdolling(num, now))
				{
					RagdollWritesSkipped++;
					continue;
				}
				NoteOwnWrittenElsewhere(num, array, now);
				if (WatchWritten != null) WatchWritten[num] = array;
				w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(baseIndex + 1, _sessionProxy, e, new Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.InProcessPayload(typeId, (ReadOnlySpan<byte>)array));
				_smoothApplied++;
				cmds++;
			}
		}
	}

	private void BuildInbound(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		//IL_069c: Unknown result type (might be due to invalid IL or missing references)
		ClientSession session = _session;
		if (session == null || _sessionProxy == null)
		{
			return;
		}
		ReplicationCommandBuffer replicationCommandBuffer = Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.AllocateCB();
		VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader w = new VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader(replicationCommandBuffer);
		int cmds = 0;
		int baseIndex = VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader.AssemblyOffset;
		int num = 0;
		int num2 = 0;
		long now = MpClock.NowMs();
		uint seq;
		LifecycleItem item;
		if (_backgroundBuild is { } pending)
		{
			if (!pending.Job.IsCompleted)
			{
				// Later updates may touch the entities still being built: hold them until it is done.
				num = int.MaxValue;
			}
			else
			{
				_backgroundBuild = null;
				w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<Keen.VRage.Multiplayer.Replications.InProcess.SceneContext>)delegate
				{
					FinishBackgroundBuild(pending);
				});
				cmds++;
			}
		}
		while (num < 64 && session.Lifecycle.TryPeek(out seq, out item))
		{
			if ((item.Type == SyncMsg.EntityBundle && item.Bundle.RequiredBlobs.Count > 0 && !BlobsReady(session, seq, item.Bundle, now)) || (item.Type == SyncMsg.SignalBatch && num2 > 0) || !session.Lifecycle.TryTake(out uint _, out LifecycleItem item2))
			{
				break;
			}
			if (item2.Type == SyncMsg.StringTable)
			{
				RegisterHostStrings(item2.Strings);
				_lifecycleApplied++;
				num++;
			}
			else if (item2.Type == SyncMsg.ObservableBatch)
			{
				List<ObservableOp> ops = item2.Observables;
				w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<Keen.VRage.Multiplayer.Replications.InProcess.SceneContext>)delegate
				{
					ApplyObservables(ops);
				});
				_lifecycleApplied++;
				num++;
				int num3 = cmds;
				cmds = num3 + 1;
			}
			else if (item2.Type == SyncMsg.SignalBatch)
			{
				if (_hostGameTime0.HasValue && now - _gameTimeCheckMs >= 1000)
				{
					_gameTimeCheckMs = now;
					uint hostTick = item2.HostTick;
					w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<Keen.VRage.Multiplayer.Replications.InProcess.SceneContext>)delegate
					{
						SyncGameTime(hostTick);
					});
					cmds++;
				}
				try
				{
					WriteSignals(session, replicationCommandBuffer, item2.Signals);
				}
				catch (Exception ex)
				{
					_log.WarnOnce("client-signals", $"client: writing host signals failed ({ex.GetType().Name}: {ex.Message}); this batch is skipped");
				}
				_lifecycleApplied++;
				num++;
				int num3 = cmds;
				cmds = num3 + 1;
			}
			else
			{
				LifecycleItem it = item2;
				w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<Keen.VRage.Multiplayer.Replications.InProcess.SceneContext>)delegate
				{
					Apply(it);
				});
				int num3 = cmds;
				cmds = num3 + 1;
				num2++;
				num++;
				if (it.Type == SyncMsg.EntityBundle && it.Bundle.Entities.Count >= 200)
				{
					break;
				}
			}
		}
		if (_worldApplied && _o.Character && !_characterFlowFailed)
		{
			try
			{
				CharacterFlow(session, w, baseIndex, ref cmds, now);
			}
			catch (Exception ex2)
			{
				_characterFlowFailed = true;
				_log.Error($"client: the character flow failed and is switched off (world sync continues, free camera): {ex2}");
				Report(ClientPhase.Error, "character flow failed: " + ex2.GetType().Name + ": " + ex2.Message);
				if (_o.Spectator && !_spectatorQueued)
				{
					_spectatorQueued = true;
					w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<Keen.VRage.Multiplayer.Replications.InProcess.SceneContext>)delegate
					{
						Spectate();
					});
					cmds++;
				}
			}
		}
		else if (_worldApplied && !_spectatorQueued && _o.Spectator)
		{
			_spectatorQueued = true;
			w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<Keen.VRage.Multiplayer.Replications.InProcess.SceneContext>)delegate
			{
				Spectate();
			});
			cmds++;
		}
		if (_stories != null)
		{
			try
			{
				Entity proxy = _sessionProxy;
				_stories.ProcessInbox(session.StoryInbox, ClientSpace(), (Action action) =>
				{
					w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, proxy, (Action<Keen.VRage.Multiplayer.Replications.InProcess.SceneContext>)delegate
					{
						action();
					});
					int num4 = cmds;
					cmds = num4 + 1;
				});
			}
			catch (Exception ex3)
			{
				_log.WarnOnce("client-stories", "client: processing the host's story replies failed: " + ex3.GetType().Name + ": " + ex3.Message);
			}
		}
		if (!_smoothingFailed)
		{
			try
			{
				ApplySmoothed(session, w, baseIndex, ref cmds, now);
			}
			catch (Exception value)
			{
				_smoothingFailed = true;
				session.Smoother.DelayMs = 0;
				_log.Error($"client: motion smoothing failed and is switched off (transforms are applied raw): {value}");
			}
		}
		_drain.Clear();
		session.State.Drain((uint id) => _proxies.IsLive(id), (ushort key) => (!session.Types.IsKnown(key)) ? ((bool?)null) : new bool?(session.Types.TryResolve(key, out var _, out var _)), _drain, MpClock.NowMs());
		foreach (StateEntry item3 in _drain)
		{
			if (!session.Types.TryResolve(item3.Key, out var localTypeId, out var size) || item3.Bytes.Length != size || !_proxies.TryGet(item3.NetId, out Entity e))
			{
				_stateBadSize++;
			}
			else if (!LookIntercept(session, item3, now))
			{
				if (session.IsTransformKey(item3.Key) && Ragdolling(item3.NetId, now))
				{
					RagdollWritesSkipped++;
					continue;
				}
				if (session.IsTransformKey(item3.Key))
				{
					NoteOwnWrittenElsewhere(item3.NetId, item3.Bytes, now);
				}
				w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(baseIndex + 1, _sessionProxy, e, new Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.InProcessPayload(localTypeId, (ReadOnlySpan<byte>)item3.Bytes));
				_stateApplied++;
				cmds++;
			}
		}
		LookWrite(session, w, baseIndex, ref cmds);
		if (cmds == 0)
		{
			Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.ReturnCB(replicationCommandBuffer);
			return;
		}
		_commands += cmds;
		comp.ScheduleForReplay(replicationCommandBuffer);
	}

	private void Apply(LifecycleItem item)
	{
		try
		{
			switch (item.Type)
			{
			case SyncMsg.EntityBundle:
				ReplicateBundle(item.Bundle);
				break;
			case SyncMsg.Despawn:
				Despawn(item.NetIds, item.DespawnMode);
				break;
			case SyncMsg.DataRemove:
				RemoveData(item.Removes);
				break;
			}
			_lifecycleApplied++;
		}
		catch (Exception ex)
		{
			_log.Error($"client: applying {item} failed: {ex}");
			Report(ClientPhase.Error, $"{item} failed: {ex.GetType().Name}: {ex.Message}");
			if (item.Type == SyncMsg.EntityBundle && item.Bundle.Kind == 0)
			{
				WorldPartDone(0, $"bundle #{item.Bundle.LifeSeq} FAILED ({ex.GetType().Name})");
			}
		}
	}

	private IStreamedOutBlobs? BlobTarget(Session session)
	{
		try
		{
			return session.TryGet<IStreamedOutBlobs>() ?? session.ExternalServices?.TryGet<IStreamedOutBlobs>();
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void RegisterBlobs(IStreamedOutBlobs? target, string where)
	{
		ClientBlobStore clientBlobStore = _session?.Blobs;
		if (clientBlobStore == null)
		{
			return;
		}
		if (target == null)
		{
			_log.WarnOnce("client-no-blob-target", "client: the client game has no IStreamedOutBlobs (" + where + "); planets and asteroids cannot load their voxel data");
			return;
		}
		List<(Guid, byte[])> list;
		if (target != _blobTarget)
		{
			_blobTarget = target;
			clientBlobStore.TakeFresh();
			list = (from x in clientBlobStore.All()
				select (Id: x.Id, x.Data)).ToList();
		}
		else
		{
			list = clientBlobStore.TakeFresh();
		}
		int num = 0;
		int num2 = 0;
		long num3 = 0L;
		foreach (var (id, array) in list)
		{
			if (array == null)
			{
				num2++;
				continue;
			}
			EngineCodec.Register(target, id, array);
			num++;
			num3 += array.Length;
		}
		_blobsRegistered += num;
		_blobBytesRegistered += num3;
		if (num > 0 || num2 > 0)
		{
			_log.Info($"client: {num} offloaded blob(s) ({MpLogExtensions.Bytes(num3)}) registered with the client game ({where}); {_blobsRegistered} in total ({MpLogExtensions.Bytes(_blobBytesRegistered)})" + ((num2 > 0) ? $"; {num2} the host could not load (entities using them will be missing)" : ""));
		}
	}

	private bool BlobsReady(ClientSession session, uint seq, EntityBundleMessage b, long now)
	{
		ClientBlobStore.Gate gate = session.Blobs.Check(b.RequiredBlobs, now, out var absent);
		if (gate == ClientBlobStore.Gate.Ready)
		{
			if (_gateSeq == seq)
			{
				_log.Info($"client: bundle #{seq}: its {b.RequiredBlobs.Count} offloaded blob(s) arrived after {now - _gateSinceMs} ms");
				_gateSeq = 0u;
			}
			return true;
		}
		if (_gateSeq != seq)
		{
			_gateSeq = seq;
			_gateSinceMs = now;
			session.Blobs.Touch(now);
			_log.Info($"client: holding bundle #{seq} until {absent} of its {b.RequiredBlobs.Count} offloaded blob(s) arrive");
			return false;
		}
		if (gate == ClientBlobStore.Gate.Stalled)
		{
			_log.Warn($"client: bundle #{seq}: {absent} offloaded blob(s) still missing and no blob data for {session.Blobs.StallMs / 1000} s; " + "building it anyway (entities that need them will be skipped)");
			Report(ClientPhase.Warning, $"bundle #{seq}: {absent} blobs never arrived");
			_gateSeq = 0u;
			return true;
		}
		return false;
	}

	private void ReplicateBundle(EntityBundleMessage m)
	{
		Stopwatch sw = Stopwatch.StartNew();
		Session session = _comp.Session;
		if (_o.BuildFix)
		{
			DataTypeRegistry.EnsureDone(_log);
		}
		RegisterBlobs(BlobTarget(session), $"bundle #{m.LifeSeq}");
		bool flag = m.Kind == 0;
		if (flag)
		{
			SlowTicks.Note = $"world part {_worldPartsApplied + 1} build ({m.Entities.Count} entities)";
		}
		HashSet<Entity> excluded = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		List<string> list = new List<string>();
		int num = 1;
		Attempt attempt;
		while (true)
		{
			attempt = TryReplicate(m, excluded, num, sw);
			if (attempt.Built || attempt.Error == null)
			{
				return;
			}
			list.Add(attempt.FailureDetail);
			int num2 = attempt.FailedRoots.Count((Entity r) => excluded.Add(r));
			if (num >= 3 || num2 == 0)
			{
				break;
			}
			_bundlesRetried++;
			_rootsDropped += num2;
			_log.Warn($"client: bundle #{m.LifeSeq}: {attempt.FailureDetail}; building it again without {num2} failed root(s)");
			num++;
		}
		string text = $"bundle #{m.LifeSeq} ({(flag ? "world" : "spawn")}, {m.Entities.Count} entities) could not be built after {num} attempt(s): " + string.Join(" | ", list);
		_log.Error("client: " + text + $"\n{attempt.Error}");
		Report(ClientPhase.Error, text);
		if (flag)
		{
			WorldPartDone(0, $"bundle #{m.LifeSeq} FAILED ({attempt.Error.GetType().Name})");
		}
	}

	private Attempt TryReplicate(EntityBundleMessage m, HashSet<Entity> excludedRoots, int attempt, Stopwatch sw)
	{
		Attempt a = new Attempt();
		Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp = _comp;
		Session session = comp.Session;
		ReplayContext replay = comp.ReplayContext;
		ReplicationStrategy replicationStrategy = _replication.ReplicationStrategy;
		RemoteTypeTable types = _session.Types;
		bool world = m.Kind == 0;
		double totalMilliseconds = sw.Elapsed.TotalMilliseconds;
		NetIdProxyContext proxies = new NetIdProxyContext(null, (int id2) => _proxies.GetOrCreate((uint)id2));
		HashSet<Blob> referenced = new HashSet<Blob>();
		EntityBundle entityBundle = ((m.Bundle.Length != 0) ? EngineCodec.DeserializeBundle(m.Bundle, proxies, referenced) : new EntityBundle());
		double num = sw.Elapsed.TotalMilliseconds - totalMilliseconds;
		if (attempt == 1 && referenced.Count > 0)
		{
			List<Guid> list = referenced.Select(EngineCodec.Id).Where((Guid id2) =>
			{
				ClientSession? session2 = _session;
				return session2 == null || !session2.Blobs.Has(id2);
			}).ToList();
			if (list.Count > 0)
			{
				_log.Warn($"client: bundle #{m.LifeSeq} references {referenced.Count} offloaded blob(s), {list.Count} not on this PC (first {list[0]}; host listed {m.RequiredBlobs.Count}); entities using them will fail to build");
			}
		}
		Dictionary<Entity, EntityObjectBuilder> dictionary = new Dictionary<Entity, EntityObjectBuilder>(ReferenceEqualityComparer.Instance);
		foreach (EntityLocator entity5 in m.Entities)
		{
			EntityObjectBuilder entityObjectBuilder = EngineCodec.Resolve(entityBundle, entity5);
			if (entityObjectBuilder == null)
			{
				a.Missing++;
			}
			else
			{
				dictionary[_proxies.GetOrCreate(entity5.NetId)] = entityObjectBuilder;
			}
		}
		for (int num2 = 0; num2 < entityBundle.Entities.Count && num2 < entityBundle.Builders.Count; num2++)
		{
			Entity entity = entityBundle.Entities[num2];
			if (entity != null && !dictionary.ContainsKey(entity))
			{
				dictionary[entity] = entityBundle.Builders[num2];
			}
		}
		HashSet<Entity> hashSet = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		foreach (var parent in m.Parents)
		{
			uint item = parent.Child;
			uint item2 = parent.Parent;
			Entity orCreate = _proxies.GetOrCreate(item2);
			if (!_proxies.IsLive(item2) && !dictionary.ContainsKey(orCreate))
			{
				hashSet.Add(_proxies.GetOrCreate(item));
			}
		}
		for (int num3 = Math.Min(entityBundle.Roots, entityBundle.Entities.Count) - 1; num3 >= 0; num3--)
		{
			Entity entity2 = entityBundle.Entities[num3];
			if (entity2 != null)
			{
				if (replay.HasPairingRemote(entity2))
				{
					entityBundle.RemoveAt(num3);
					a.Duplicates++;
				}
				else if (excludedRoots.Contains(entity2) || hashSet.Contains(entity2))
				{
					entityBundle.RemoveAt(num3);
					a.Excluded++;
				}
			}
		}
		if (hashSet.Count > 0 && attempt == 1)
		{
			_log.Warn($"client: bundle #{m.LifeSeq}: {hashSet.Count} root(s) skipped, their parent entity is not on this PC");
		}
		if (entityBundle.Builders.Count == 0 || entityBundle.Roots == 0)
		{
			string text = $"bundle #{m.LifeSeq}: nothing to build ({a.Duplicates} already present, {a.Excluded} excluded, {a.Missing} unlocated)";
			if (world)
			{
				WorldPartDone(0, text);
			}
			else if (a.Excluded > 0)
			{
				_log.Info("client: " + text);
			}
			return a;
		}
		Dictionary<EntityObjectBuilder, Entity> dictionary2 = new Dictionary<EntityObjectBuilder, Entity>(ReferenceEqualityComparer.Instance);
		foreach (KeyValuePair<Entity, EntityObjectBuilder> item7 in dictionary)
		{
			dictionary2[item7.Value] = item7.Key;
		}
		Dictionary<Entity, DEntityBuilder> dictionary3 = new Dictionary<Entity, DEntityBuilder>(ReferenceEqualityComparer.Instance);
		foreach (var datum in m.Data)
		{
			uint item3 = datum.NetId;
			List<DataBlob> item4 = datum.Data;
			DEntityBuilder value = default;
			foreach (DataBlob item8 in item4)
			{
				if (item8.Bytes.Length != 0 && types.TryResolve(item8.Key, out var localTypeId, out var size) && item8.Bytes.Length == size)
				{
					value.Set(localTypeId, in item8.Bytes[0], size);
					a.DataSet++;
				}
				else
				{
					a.DataSkipped++;
				}
			}
			dictionary3[_proxies.GetOrCreate(item3)] = value;
		}
		HashSet<Entity> hashSet2 = new HashSet<Entity>(m.Staged.Select((uint id2) => _proxies.GetOrCreate(id2)), ReferenceEqualityComparer.Instance);
		HashSet<Entity> hashSet3 = new HashSet<Entity>(m.Skipped.Select((uint id2) => _proxies.GetOrCreate(id2)), ReferenceEqualityComparer.Instance);
		EntityTranslationContext tctx = new EntityTranslationContext(replay);
		EntityBundle clientBundle = entityBundle.CloneBundle(tctx);
		Dictionary<Entity, DEntityBuilder> dictionary4 = new Dictionary<Entity, DEntityBuilder>();
		replicationStrategy.TranslateBundle(clientBundle, entityBundle, tctx, dictionary2, dictionary4, hashSet3, dictionary3, hashSet2, false);
		Dictionary<Entity, Entity> dictionary5 = new Dictionary<Entity, Entity>();
		foreach (var parent2 in m.Parents)
		{
			uint item5 = parent2.Child;
			uint item6 = parent2.Parent;
			Entity orCreate2 = _proxies.GetOrCreate(item5);
			if (!hashSet.Contains(orCreate2) && !excludedRoots.Contains(orCreate2))
			{
				dictionary5[tctx.AddEntity(orCreate2)] = tctx.AddEntity(_proxies.GetOrCreate(item6));
			}
		}
		List<Entity> pairedProxies = new List<Entity>();
		List<uint> paired = new List<uint>();
		Dictionary<Entity, Entity> clientToServer = new Dictionary<Entity, Entity>(ReferenceEqualityComparer.Instance);
		foreach (KeyValuePair<Entity, (Entity, bool)> translation in tctx.Translations)
		{
			Entity key = translation.Key;
			var (entity3, flag) = translation.Value;
			clientToServer[entity3] = key;
			if (!flag && dictionary.ContainsKey(key) && !hashSet3.Contains(key) && !replay.HasPairingRemote(key))
			{
				replay.AddObjectPairing(key, entity3);
				pairedProxies.Add(key);
				if (_proxies.TryGetId(key, out var id))
				{
					paired.Add(id);
				}
			}
		}
		double value2 = sw.Elapsed.TotalMilliseconds - totalMilliseconds - num;
		a.Paired = paired.Count;
		a.Summary = $"bundle #{m.LifeSeq} ({(world ? "world" : "spawn")}): {m.Entities.Count} entities sent, {paired.Count} paired, {a.Missing} unlocated, {a.Duplicates} duplicates, {a.Excluded} roots excluded, data {a.DataSet} set / {a.DataSkipped} skipped, {referenced.Count} blobs; deserialize {num:0} ms, translate {value2:0} ms" + ((attempt > 1) ? $", attempt {attempt}" : "");
		// A big pasted or spawned grid (40k blocks took 3.1 s) is built on a worker thread while the game keeps
		// running: Scene.OpenNoSyncSection exists for allocating entities concurrently with Tick, and SE2's own
		// spawner does the same. Only adding it to the scene stays on this thread; later updates wait for it.
		bool background = !world && m.Entities.Count >= BackgroundBuildEntities && dictionary5.Count == 0 && _sessionProxy != null;
		if (_o.BuildFix && dictionary5.Count == 0 && _sessionProxy != null && !background)
		{
			dictionary5[_sessionProxy] = _sessionProxy;
		}
		if (!_dcsLogged)
		{
			_dcsLogged = true;
			bool flag2 = false;
			try
			{
				flag2 = session.Scene.IsCurrentThreadDCS();
			}
			catch (Exception)
			{
			}
			_log.Info($"client: entity bundles are built on thread '{Thread.CurrentThread.Name}' (scene-synchronized: {(flag2 ? "yes" : "no")}); " + (_o.BuildFix ? "roots one by one (0.8.2 build fix)" : "roots in parallel (buildfix off)"));
		}
		GameEntitySerializer.BundleInitArgs args = new GameEntitySerializer.BundleInitArgs
		{
			Bundle = clientBundle,
			EntityParents = dictionary5,
			EntityBuilders = dictionary4
		};
		Task<Entity[]> task;
		Entity[] halfAdded = null;
		if (background)
		{
			args.EntityParents = null;
			var job = new BackgroundBuild();
			_backgroundBuild = (job, Finish, Fail, a.Summary, sw);
			BuildOnWorker(session.EntitySerializer, args, job);
			_log.Info($"client: {a.Summary}; building {m.Entities.Count} entities on a worker thread, the game keeps running");
			a.Built = true;
			return a;
		}
		try
		{
			task = session.EntitySerializer.BuildFromBundleAsync(args, allowAsyncSpawn: false);
		}
		catch (Exception e)
		{
			Fail(e);
			return a;
		}
		if (!task.IsCompleted)
		{
			_log.Warn("client: " + a.Summary + ": BuildFromBundleAsync did not complete synchronously; adding to scene when it does");
			task.GetAwaiter().OnCompleted(() =>
			{
				try
				{
					Finish(task.GetAwaiter().GetResult());
				}
				catch (Exception ex2)
				{
					Fail(ex2);
					_log.Error($"client: async {a.Summary} failed and was rolled back: {a.FailureDetail}\n{ex2}");
					Report(ClientPhase.Error, $"bundle #{m.LifeSeq} build failed: {a.FailureDetail}");
					if (world)
					{
						WorldPartDone(0, $"bundle #{m.LifeSeq} FAILED ({ex2.GetType().Name})");
					}
				}
			});
			a.Built = true;
			return a;
		}
		try
		{
			Finish(task.GetAwaiter().GetResult());
			a.Built = true;
		}
		catch (Exception e2)
		{
			Fail(e2);
		}
		return a;
		void Fail(Exception ex2)
		{
			a.Error = ex2;
			int value3 = RollBackPairings(replay, pairedProxies, out string firstError);
			if (firstError != null)
			{
				_log.WarnOnce("client-unpair", "client: removing a pairing after a failed bundle threw " + firstError);
			}
			int num4 = 0;
			try
			{
				List<Entity> entities = (from v in tctx.Translations.Values
					where !v.IsTranslated
					select v.Entity).ToList();
				num4 = EngineCodec.RemoveFromCloseQueues(session, entities);
				if (num4 > 0)
				{
					_purgedDead += num4;
					_log.Warn($"client: bundle #{m.LifeSeq}: {num4} of its unrolled entities were queued for close; removed from the session's close queue");
				}
			}
			catch (Exception ex3)
			{
				_log.WarnOnce("client-unqueue", "client: clearing a failed bundle's entities from the close queue threw " + ex3.GetType().Name + ": " + ex3.Message);
			}
			num4 += PurgeDead(session, $"failed bundle #{m.LifeSeq}");
			GameEntitySerializer.EntityBundleInitException ex4 = Unwrap<GameEntitySerializer.EntityBundleInitException>(ex2);
			List<string> list2 = new List<string>();
			if (ex4?.Failures != null && ex4.Bundle != null)
			{
				for (int num5 = 0; num5 < ex4.Failures.Length && num5 < ex4.Bundle.Entities.Count; num5++)
				{
					Exception ex5 = ex4.Failures[num5];
					if (ex5 != null)
					{
						Entity entity4 = ex4.Bundle.Entities[num5];
						if (entity4 != null && clientToServer.TryGetValue(entity4, out Entity value4))
						{
							a.FailedRoots.Add(value4);
						}
						if (list2.Count < 3)
						{
							list2.Add($"{Innermost(ex5).GetType().Name} '{Short(Innermost(ex5).Message)}' in {Short(ex5.Message)}");
						}
					}
				}
			}
			a.FailureDetail = ((ex4 != null) ? $"{a.FailedRoots.Count} of {clientBundle.Roots} root(s) failed ({string.Join("; ", list2)})" : (ex2.GetType().Name + ": " + Short(ex2.Message))) + $"; rolled back {value3} pairing(s), purged {num4} dead entities";
			if (halfAdded != null)
			{
				a.FailureDetail += $"; closed {CloseHalfAdded(session, halfAdded)} half-added root(s)";
				halfAdded = null;
			}
		}
		void Finish(Entity[] built)
		{
			try
			{
				session.AddEntityBundleToScene(built);
			}
			catch
			{
				halfAdded = built;
				throw;
			}
			foreach (uint item9 in paired)
			{
				_proxies.MarkLive(item9);
			}
			_entitiesBuilt += built.Length;
			a.BuiltRoots = built.Length;
			string text2 = $"{a.Summary}; built {built.Length} root entities in {sw.ElapsedMilliseconds} ms total";
			if (world)
			{
				WorldPartDone(paired.Count, text2);
			}
			else if (m.Entities.Count >= 20 || _lifecycleApplied < 5 || attempt > 1 || referenced.Count > 0)
			{
				_log.Info("client: " + text2);
			}
		}
	}

	internal static int RollBackPairings(ReplayContext replay, IEnumerable<Entity> proxies, out string? firstError)
	{
		int num = 0;
		firstError = null;
		foreach (Entity proxy in proxies)
		{
			try
			{
				if (replay.HasPairingRemote(proxy))
				{
					replay.RemoveObjectPairing(proxy);
					num++;
				}
			}
			catch (Exception ex)
			{
				if (firstError == null)
				{
					firstError = ex.GetType().Name + ": " + ex.Message;
				}
			}
		}
		return num;
	}

	private static T? Unwrap<T>(Exception e) where T : Exception
	{
		for (Exception ex = e; ex != null; ex = ex.InnerException)
		{
			if (ex is T result)
			{
				return result;
			}
			if (ex is AggregateException ex2 && !(ex2 is T))
			{
				foreach (Exception innerException in ex2.InnerExceptions)
				{
					T val = Unwrap<T>(innerException);
					if (val != null)
					{
						return val;
					}
				}
			}
		}
		return null;
	}

	private static Exception Innermost(Exception e)
	{
		while (e.InnerException != null)
		{
			e = e.InnerException;
		}
		return e;
	}

	private static string Short(string s)
	{
		if (s.Length > 160)
		{
			return s.Substring(0, 157) + "...";
		}
		return s;
	}

	private int PurgeDead(Session session, string where)
	{
		try
		{
			int num = EngineCodec.PurgeDeadFromCloseQueues(session, out string sample);
			if (num > 0)
			{
				_purgedDead += num;
				_log.Warn($"client: dropped {num} dead entit{((num == 1) ? "y" : "ies")} from the session's close queue ({where}; first: {sample}); " + "Session.Update would have crashed on them");
			}
			return num;
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-purge", $"client: checking the session's close queue failed ({where}): {ex.GetType().Name}: {ex.Message}");
			return 0;
		}
	}

	public static void SessionUpdatePrefix(Session __instance, bool doEntityLifetimeUpdates)
	{
		ClientWorld current = Current;
		Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent inProcessReplicationSessionComponent = current?._comp;
		if (inProcessReplicationSessionComponent == null || !doEntityLifetimeUpdates)
		{
			return;
		}
		try
		{
			if (inProcessReplicationSessionComponent.Session == __instance)
			{
				SlowTicks.Begin();
				current.PurgeDead(__instance, "Session.Update");
			}
		}
		catch (Exception ex)
		{
			current._log.WarnOnce("client-session-update", "client: Session.Update prefix failed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void SessionUpdatePostfix()
	{
		try
		{
			SlowTicks.End();
		}
		catch (Exception)
		{
			SlowTicks.Enabled = false;
		}
	}

	private void WorldPartDone(int entities, string detail)
	{
		_worldPartsApplied++;
		_worldEntitiesBuilt += entities;
		Progress.PartBuilt(entities);
		int num = Math.Max(1, _session?.Accept?.WorldParts ?? 1);
		_log.Info($"client: world part {_worldPartsApplied}/{num}: {detail}");
		Report(ClientPhase.Progress, $"world part {_worldPartsApplied}/{num}: {detail}");
		if (_worldPartsApplied >= num && !_worldApplied)
		{
			_worldApplied = true;
			string text = $"WORLD APPLIED: {_worldEntitiesBuilt} entities paired in {num} part(s), {_proxies.LiveCount} live NetworkIds";
			_log.Info("client: " + text);
			Report(ClientPhase.WorldApplied, text);
			Progress.Set(_o.Character ? JoinStage.WaitingForCharacter : JoinStage.Playing, MpClock.NowMs(), _o.Character ? null : "Free camera (no character).");
		}
	}

	private void Despawn(List<uint> ids, byte mode)
	{
		Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp = _comp;
		ReplayContext replayContext = comp.ReplayContext;
		foreach (uint id in ids)
		{
			if (!_proxies.TryGet(id, out Entity e))
			{
				_despawnSkipped++;
				continue;
			}
			_session?.Smoother.Remove(id);
			if (id == _ownNetId)
			{
				ClearOwnCharacter((mode == 0) ? "removed on the host" : "forgotten by the host");
			}
			if (mode == 0)
			{
				_session?.State.Despawn(id, MpClock.NowMs());
				bool flag = _proxies.IsLive(id);
				_proxies.MarkDead(id);
				if (!flag || !replayContext.HasPairingRemote(e))
				{
					_despawnSkipped++;
					continue;
				}
				bool skip = false;
				if (!(replayContext.TranslateObject(e, ReplicationFailureStrategy.NullOnAnyFail, ref skip) is Entity entity) || EngineCodec.IsDead(entity) || EngineCodec.IsClosing(comp.Session, entity))
				{
					_despawnSkipped++;
					continue;
				}
				try
				{
					comp.Session.MarkEntityForClose(entity);
				}
				catch (Exception ex)
				{
					_log.WarnOnce("client-despawn", $"client: closing despawned entity (NetworkId {id}) threw {ex.GetType().Name}: {ex.Message}");
				}
				continue;
			}
			try
			{
				if (replayContext.HasPairingRemote(e))
				{
					replayContext.RemoveObjectPairing(e);
				}
			}
			catch (Exception ex2)
			{
				_log.WarnOnce("client-forget", $"client: forgetting NetworkId {id} threw {ex2.GetType().Name}: {ex2.Message}");
			}
			_proxies.Forget(id);
		}
	}

	private void RemoveData(List<(uint NetId, ushort Key)> items)
	{
		ReplayContext replayContext = _comp.ReplayContext;
		RemoteTypeTable types = _session.Types;
		foreach (var (id, key) in items)
		{
			if (_proxies.IsLive(id) && _proxies.TryGet(id, out Entity e) && types.TryResolve(key, out var localTypeId, out var _))
			{
				bool skip = false;
				if (replayContext.TranslateObject(e, ReplicationFailureStrategy.NullOnAnyFail, ref skip) is Entity entity && !EngineCodec.IsDead(entity))
				{
					entity.Scene.TryRemoveData(entity.Data, localTypeId);
				}
			}
		}
	}

	private void Spectate()
	{
		if (_ownCharacter != null)
		{
			_log.Info("client: free camera not needed any more (own character possessed)");
			return;
		}
		try
		{
			Session session = _comp.Session;
			EntityObjectBuilder serverObjectBuilder = (Singleton<DefinitionManager>.Instance.GetDefinitionsOfType<SpectatingModesComponentDefinition>().FirstOrDefault() ?? throw new InvalidOperationException("no SpectatingModesComponentDefinition")).SpectatorPrefab.Get();
			EntityObjectBuilder clientObjectBuilder = _replication.ReplicationStrategy.GetClientObjectBuilder(serverObjectBuilder);
			ClientPlayersSessionComponent clientPlayersSessionComponent = session.SessionComponents.TryGet<ClientPlayersSessionComponent>();
			if (clientPlayersSessionComponent != null)
			{
				OwnershipSessionComponent.ChangeOwnership(clientObjectBuilder, clientPlayersSessionComponent.LocalPlayerIdentity);
			}
			string value = "at the origin";
			List<Entity> list = new List<Entity>();
			if (session.TryFillAliveCharactersHotfix(list, preserveOrder: true) && list.Count > 0 && list[0].Data.TryGet<WorldTransform>(out var data))
			{
				Vector3D position = data.Position + Vector3D.Up * 3.0 + Vector3D.Backward * 6.0;
				clientObjectBuilder.OB<WorldTransformComponent, WorldTransformComponentObjectBuilder>().Transform = new WorldTransform(position, data.Orientation);
				value = $"next to character {list[0]} ({list.Count} characters in the client world)";
			}
			Entity value2 = (_spectator = (session.SessionComponents.TryGet<SimpleEntitySpawnerSessionComponent>() ?? throw new InvalidOperationException("no entity spawner in the client session")).SpawnEntity(clientObjectBuilder));
			SpectatingModesSessionComponent spectatingModesSessionComponent = session.SessionComponents.TryGet<SpectatingModesSessionComponent>();
			spectatingModesSessionComponent?.SetEntityTypeToPossess(InitialEntityOptions.Spectator);
			string text = $"free camera: local spectator {value2} spawned {value}; possession requested via {((spectatingModesSessionComponent != null) ? "SpectatingModesSessionComponent" : "nothing (component missing)")}";
			_log.Info("client: " + text);
			Report(ClientPhase.CameraReady, text);
			JoinStage stage = Progress.Stage;
			if (stage - 7 <= JoinStage.WaitingForSteam)
			{
				Progress.Set(JoinStage.Playing, MpClock.NowMs(), "No character from the host: free camera.");
			}
		}
		catch (Exception ex)
		{
			_log.Error($"client: free camera failed (world is still synced; camera may stay at the origin): {ex}");
			Report(ClientPhase.Warning, "free camera failed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private void LookBegin(uint netId)
	{
		if (_o.Predict && !_lookFailed)
		{
			if (_look == null)
			{
				_look = new LookPredictor(_log);
			}
			_look.Begin(netId, MpClock.NowMs());
			_deckFrame = false;
			_look.ExtraLagMs = 0.0;
			_fastUntilMs = 0L;
			_lookDirty = false;
			_lookLastSent = default;
			if (_body == null)
			{
				_body = new OwnBodyFilter(_log);
			}
			_body.Enabled = _o.BodyFilter;
			_body.Reset();
			(_move ?? (_move = new MovePredictor())).Reset();
			_movePhys = null;
			_lastWritten = null;
			_bodyWhy = null;
		}
	}

	private void NoteBodyWhy(string what)
	{
		_bodyWhy = ((_bodyWhy == null) ? what : (_bodyWhy + "; " + what));
	}

	private void NoteOwnWrittenElsewhere(uint netId, byte[] value, long now)
	{
		if (_body != null && netId != 0 && netId == _ownNetId && value.Length == 40)
		{
			var (x, y, z, quaternion) = LookMath.ReadTransform(value);
			if (_deckFrame)
			{
				quaternion = System.Numerics.Quaternion.Conjugate(_deckQ) * quaternion;
			}
			_body.NoteShown(now, x, y, z, quaternion);
			_lastWritten = null;
		}
	}

	private void CheckOwnSecondWriter(Entity own, long now)
	{
		byte[] lastWritten = _lastWritten;
		if (lastWritten == null || now - _lastWrittenMs > 200 || !own.Data.TryGet<WorldTransform>(out var data))
		{
			return;
		}
		byte[] array = new byte[40];
		Unsafe.WriteUnaligned(ref array[0], data);
		(double X, double Y, double Z, System.Numerics.Quaternion O) tuple = LookMath.ReadTransform(array);
		double item = tuple.X;
		double item2 = tuple.Y;
		double item3 = tuple.Z;
		System.Numerics.Quaternion item4 = tuple.O;
		(double X, double Y, double Z, System.Numerics.Quaternion O) tuple2 = LookMath.ReadTransform(lastWritten);
		double item5 = tuple2.X;
		double item6 = tuple2.Y;
		double item7 = tuple2.Z;
		System.Numerics.Quaternion item8 = tuple2.O;
		double num = Math.Sqrt((item - item5) * (item - item5) + (item2 - item6) * (item2 - item6) + (item3 - item7) * (item3 - item7));
		float num2 = LookMath.AngleDeg(item4, item8);
		if (!(num < 0.05) || !(num2 < 1f))
		{
			_otherWriterCount++;
			if (num > _otherWriterMaxM)
			{
				_otherWriterMaxM = num;
			}
			if (now - _otherWriterLogMs < 2000)
			{
				_otherWriterSuppressed++;
				return;
			}
			_log.Info($"client: own body: the game shows a transform Y51_SE2Online did not write ({num:0.00} m / {num2:0.0} deg off the one written {now - _lastWrittenMs} ms ago): a second writer (Keen's replay of host data / client physics){((_otherWriterSuppressed > 0) ? $" (+{_otherWriterSuppressed} more)" : "")}");
			_otherWriterSuppressed = 0L;
			_otherWriterLogMs = now;
		}
	}

	private void LookEnd(string why)
	{
		_look?.End(why);
		_move?.Reset();
		_ownSmoothed = false;
	}

	private void LookTick(ClientSession session, long now)
	{
		LookPredictor look = _look;
		Entity ownCharacter = _ownCharacter;
		if (look == null || _lookFailed || ownCharacter == null || look.NetId == 0 || look.NetId != _ownNetId)
		{
			return;
		}
		try
		{
			CheckOwnSecondWriter(ownCharacter, now);
			_ownVel = (ownCharacter.Data.TryGet<RigidBodyData>(out var data) ? new System.Numerics.Vector3?(new System.Numerics.Vector3(data.LinearVelocity.X, data.LinearVelocity.Y, data.LinearVelocity.Z)) : ((System.Numerics.Vector3?)null));
			if (!_ownVelLogged)
			{
				_ownVelLogged = true;
				_log.Info("client: own-body blend " + (_o.BodyFilter ? "on" : "off (-y51online:bodyfilter:off)") + ": velocity from " + (_ownVel.HasValue ? "the character's RigidBodyData" : "the host samples (no RigidBodyData on the own character)") + "; fast-jetpack smoothing " + (_o.FastSmooth ? "ON (-y51online:fastsmooth:on)" : "off (0.8.2 behaviour)") + ", deck frame " + (_o.DeckFrame ? "on" : "off"));
			}
			// Host samples are stamped at the end of the tick the input was applied in, the ack at its start.
			look.MeasuredLagMs = session.InputRttMs >= 0 ? session.InputRttMs + 8.0 : null;
			LookMode lookMode = LookModeOf(ownCharacter, out string detail);
			if (lookMode != look.Mode)
			{
				NoteBodyWhy($"movement mode {look.Mode} -> {lookMode}");
			}
			look.SetMode(lookMode, now, detail);
			bool flag = lookMode == LookMode.Off && detail.StartsWith("controlling", StringComparison.Ordinal);
			bool flag2 = lookMode == LookMode.Ground && OnMovingGrid(ownCharacter, session, now);
			bool flag3 = _o.FastSmooth && lookMode == LookMode.Jetpack && FastOwnFlight(ownCharacter, now);
			bool flag4 = flag | flag2 | flag3;
			bool flag5 = flag2 && session.Smoother.Enabled && _o.DeckFrame;
			if (flag5 != _deckFrame)
			{
				_deckFrame = flag5;
				System.Numerics.Quaternion delta = (flag5 ? System.Numerics.Quaternion.Conjugate(_deckQ) : _deckQ);
				look.Rebase(delta);
				_body?.Rebase(delta);
				NoteBodyWhy(flag5 ? "prediction now in the deck's frame" : "prediction back in the world frame");
			}
			if (flag4 != _ownSmoothed && session.Smoother.Enabled)
			{
				_ownSmoothed = flag4;
				NoteBodyWhy(flag4 ? "own body source: raw -> smoothed timeline" : "own body source: smoothed timeline -> raw");
				session.Smoother.SetRaw(look.NetId, !flag4);
				if (_ownSmoothedLogged++ < 12)
				{
					IMpLog log = _log;
					string message;
					if (flag)
					{
						message = "client: seated: the own character follows the ship's smoothed timeline (no shaking in the seat)";
					}
					else if (flag2)
					{
						message = "client: standing on a moving grid: the own character follows that grid's smoothed timeline (no sliding on the deck)";
					}
					else
					{
						message = (flag3 ? "client: fast jetpack flight: the own character follows the smoothed timeline (no shaking at speed)" : "client: on foot again: the own character is shown raw (look prediction)");
					}
					log.Info(message);
				}
			}
			look.ExtraLagMs = (((_deckFrame || ((_o.FastSmooth & flag3) && _ownSmoothed)) && session.Smoother.Enabled) ? session.Smoother.CurrentDelayMs : 0.0);
			OwnBodyFilter body = _body;
			if (body != null && body.Enabled && lookMode != LookMode.Off && look.Intercepts)
			{
				_lookDirty = true;
			}
			if (lookMode == LookMode.Off)
			{
				if (look.Intercepts)
				{
					_lookDirty = true;
				}
			}
			else
			{
				System.Numerics.Vector3 rotation = (ownCharacter.Data.TryGet<ControlData>(out var data2) ? new System.Numerics.Vector3(data2.Rotation.X, data2.Rotation.Y, data2.Rotation.Z) : System.Numerics.Vector3.Zero);
				float maxTorque = ((ownCharacter.Data.TryGet<CharacterMovementComponent.JetpackModeData>(out var data3) && data3.MaxTorque > 0f) ? data3.MaxTorque : 65f);
				System.Numerics.Vector3 centerOfMass = (ownCharacter.Data.TryGet<RigidBodyMassProperties>(out var data4) ? new System.Numerics.Vector3(data4.CenterOfMass.X, data4.CenterOfMass.Y, data4.CenterOfMass.Z) : System.Numerics.Vector3.Zero);
				if (look.Step(now, rotation, maxTorque, centerOfMass))
				{
					_lookDirty = true;
				}
			}
			MoveStep(ownCharacter, lookMode, look, now);
			if (_move != null && _move.Active && look.Intercepts)
			{
				_lookDirty = true;
			}
			(uint, ushort, double, double, double) tuple = (look.NetId, look.Epoch, look.TotalX, look.TotalY, look.TotalZ);
			var (num, num2, num3, num4, num5) = tuple;
			var (num6, num7, num8, num9, num10) = _lookLastSent;
			if (num != num6 || num2 != num7 || num3 != num8 || num4 != num9 || num5 != num10 || now - _lookSentMs >= 100)
			{
				session.SendLook(look.NetId, look.Epoch, look.TotalX, look.TotalY, look.TotalZ);
				_lookLastSent = tuple;
				_lookSentMs = now;
			}
		}
		catch (Exception e)
		{
			LookFail(e);
		}
	}

	private void MoveStep(Entity own, LookMode mode, LookPredictor look, long now)
	{
		MovePredictor move = _move;
		if (move == null)
		{
			return;
		}
		if (_ownSmoothed || mode == LookMode.Off || look.Disabled || (look.LagSamples == 0 && !look.MeasuredLagMs.HasValue))
		{
			move.Reset();
			return;
		}
		System.Numerics.Vector3 hostVel = _ownVel.GetValueOrDefault();
		if (mode != LookMode.Ground || !own.Data.TryGet<ControlData>(out var control) || !own.Data.TryGet<CharacterMovementComponent.GroundModeData>(out var ground))
		{
			move.Step(now, System.Numerics.Vector3.Zero, hostVel, inputChanged: false);
			return;
		}
		Keen.VRage.Physics.Components.CharacterPhysicsComponent phys = _movePhys ?? (_movePhys = own.Components.OfType<Keen.VRage.Physics.Components.CharacterPhysicsComponent>().FirstOrDefault());
		bool sprint = own.Data.TryGet<AdvancedCharacterControlData>(out var advanced) && advanced.IsSprinting;
		bool crouch = own.Data.TryGet<CharacterCrouchTag>(out _);
		// Sprint and crouch are requests the host answers: its state reaches us a round trip after the key, and the
		// model ran at the old speed for that long, a lead error that snapped back (0.25-0.45 m jumps while walking).
		// For one round trip after a request the model uses what was pressed; then the host's state rules again
		// (stamina, no room to stand up).
		double window = look.LagMs + 250.0;
		if (_sprintIntentMs != 0 && now - _sprintIntentMs < window) sprint = _sprintIntent;
		if (_crouchToggleMs != _crouchToggleSeen)
		{
			_crouchToggleSeen = _crouchToggleMs;
			_crouchIntent = !crouch;
		}
		if (_crouchToggleMs != 0 && now - _crouchToggleMs < window) crouch = _crouchIntent;
		System.Numerics.Quaternion shown = look.Shown;
		System.Numerics.Vector3 up = System.Numerics.Vector3.Transform(LookMath.Up, shown);
		System.Numerics.Vector3 hostVertical = up * System.Numerics.Vector3.Dot(hostVel, up);
		System.Numerics.Vector3 target = MovePredictor.GroundVelocity(shown, control.Movement.X, control.Movement.Z, sprint, crouch, ground.NonSprintingSpeedMultiplier, ground.CrouchSpeedMultiplier, phys?.CharacterPhysicsDefinition.BaseGroundVelocity ?? 10f);
		(float, float, bool, bool) input = (control.Movement.X, control.Movement.Z, sprint, crouch);
		move.StepGround(now, target, hostVel - hostVertical, hostVertical, phys?.CharacterPhysicsDefinition.MaxAcceleration ?? 200f, input != _moveInput);
		_moveInput = input;
	}

	private bool FastOwnFlight(Entity own, long now)
	{
		if (own.Data.TryGet<RigidBodyData>(out var data))
		{
			Keen.VRage.Library.Mathematics.Vector3 linearVelocity = data.LinearVelocity;
			double num = Math.Sqrt((double)linearVelocity.X * (double)linearVelocity.X + (double)linearVelocity.Y * (double)linearVelocity.Y + (double)linearVelocity.Z * (double)linearVelocity.Z);
			bool flag = now < _fastUntilMs;
			if (num >= 15.0 || (flag && num >= 8.0))
			{
				_fastUntilMs = now + 750;
			}
		}
		return now < _fastUntilMs;
	}

	private void NoteDeckSample(List<(uint NetId, byte[] Value)> samples)
	{
		if (_deckRootId == 0)
		{
			return;
		}
		foreach (var (num, array) in samples)
		{
			if (num == _deckRootId && array.Length == 40)
			{
				(double, double, double, System.Numerics.Quaternion) tuple2 = LookMath.ReadTransform(array);
				_deckQ = System.Numerics.Quaternion.Normalize(tuple2.Item4);
				CheckDeckContinuity(num, tuple2.Item1, tuple2.Item2, tuple2.Item3);
				break;
			}
		}
	}

	private void CheckDeckContinuity(uint id, double x, double y, double z)
	{
		long num = MpClock.NowMs();
		double num2 = (double)(num - _deckCheckMs) / 1000.0;
		if (id == _deckCheckId && num2 > 0.002 && num2 < 0.2)
		{
			double num3 = _dx + _dvx * num2;
			double num4 = _dy + _dvy * num2;
			double num5 = _dz + _dvz * num2;
			double num6 = Math.Sqrt((x - num3) * (x - num3) + (y - num4) * (y - num4) + (z - num5) * (z - num5));
			double num7 = Math.Sqrt(_dvx * _dvx + _dvy * _dvy + _dvz * _dvz);
			if (num6 > 0.3 + 0.05 * num7 * num2 * 60.0)
			{
				_deckJumps++;
				if (num - _deckJumpLogMs > 2000)
				{
					_deckJumpLogMs = num;
					_log.Info($"client: deck NetworkId {id}: smoothed track jumped {num6:0.00} m against its velocity ({num7:0} m/s) this frame (diagnostic, {_deckJumps} so far; {_session?.Smoother.Describe()})");
				}
			}
			_dvx = (x - _dx) / num2;
			_dvy = (y - _dy) / num2;
			_dvz = (z - _dz) / num2;
		}
		else
		{
			_dvx = (_dvy = (_dvz = 0.0));
		}
		_deckCheckId = id;
		_deckCheckMs = num;
		_dx = x;
		_dy = y;
		_dz = z;
	}

	private bool OnMovingGrid(Entity own, ClientSession session, long now)
	{
		Entity entity = own.TryGet<CharacterMovementEffectsComponent>()?._supportingEntity?.Entity;
		if (entity != null && !EngineCodec.IsDead(entity))
		{
			Entity entity2 = entity;
			for (int i = 0; i < 16; i++)
			{
				Entity entity3 = entity2.TryGet<HierarchyComponent>()?.Parent;
				if (entity3 == null)
				{
					break;
				}
				entity2 = entity3;
			}
			uint? num = NetIdOf(entity2);
			if (num.HasValue)
			{
				uint valueOrDefault = num.GetValueOrDefault();
				if (session.Smoother.SpeedOf(valueOrDefault) > 1.0 || session.Smoother.TurnRateOf(valueOrDefault) > 1.0)
				{
					_deckUntilMs = now + 500;
					_deckRootId = valueOrDefault;
				}
			}
		}
		return now < _deckUntilMs;
	}

	private bool LookTakeSmoothed(uint netId, byte[] value, long now)
	{
		LookPredictor look = _look;
		if (look == null || _lookFailed || !_ownSmoothed || netId == 0 || netId != look.NetId || !look.Intercepts || value.Length != 40)
		{
			return false;
		}
		try
		{
			var (x, y, z, quaternion) = LookMath.ReadTransform(value);
			if (_deckFrame)
			{
				quaternion = System.Numerics.Quaternion.Conjugate(_deckQ) * quaternion;
			}
			look.OnHost(now, x, y, z, quaternion);
			_lookDirty = true;
			_lookIntercepted++;
			return true;
		}
		catch (Exception e)
		{
			LookFail(e);
			return false;
		}
	}

	private LookMode LookModeOf(Entity own, out string detail)
	{
		CharacterMovementComponent characterMovementComponent = own.TryGet<CharacterMovementComponent>();
		if (characterMovementComponent == null)
		{
			detail = "the character has no CharacterMovementComponent here";
			return LookMode.Off;
		}
		IController controller = _controller;
		if (controller != null)
		{
			foreach (var controlledEntity in controller.ControlledEntities)
			{
				if (controlledEntity.Controllable is Component component && !IsSelfOrBelow(component.Entity, own))
				{
					SeatDiagnostics(component.Entity, own);
					detail = "controlling " + component.GetType().Name;
					return LookMode.Off;
				}
			}
		}
		CharacterMovementState currentMovementState = characterMovementComponent.CurrentMovementState;
		detail = $"movement state {currentMovementState}";
		return currentMovementState switch
		{
			CharacterMovementState.Ground => LookMode.Ground, 
			CharacterMovementState.Jetpack => LookMode.Jetpack, 
			_ => LookMode.Off, 
		};
	}

	private void SeatDiagnostics(Entity controlled, Entity own)
	{
		if (controlled == _seatDiagLast || _seatDiagCount >= 20)
		{
			return;
		}
		_seatDiagLast = controlled;
		_seatDiagCount++;
		try
		{
			SeatComponent seatComponent = controlled.TryGet<SeatComponent>();
			CubeBlockComponent cubeBlockComponent = controlled.TryGet<CubeBlockComponent>();
			string value;
			if (seatComponent == null)
			{
				value = "no SeatComponent";
			}
			else if (seatComponent.Pilot == null)
			{
				value = "Pilot NULL";
			}
			else
			{
				value = ((seatComponent.Pilot == own) ? "Pilot = own character" : $"Pilot = {seatComponent.Pilot}");
			}
			string value2;
			if (cubeBlockComponent == null)
			{
				value2 = "no CubeBlockComponent";
			}
			else
			{
				value2 = ((cubeBlockComponent.Grid == null) ? "CubeBlockComponent.Grid NULL" : $"grid {cubeBlockComponent.Grid.Entity}");
			}
			_log.Info($"client: seated in {controlled} ({value}; {value2}) - the control panel from this seat needs both (diagnostic)");
		}
		catch (Exception ex)
		{
			_log.Info("client: seat diagnostic failed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static bool IsSelfOrBelow(Entity e, Entity own)
	{
		Entity entity = e;
		for (int i = 0; i < 8; i++)
		{
			if (entity == null)
			{
				break;
			}
			if (entity == own)
			{
				return true;
			}
			entity = entity.TryGet<HierarchyComponent>()?.Parent;
		}
		return false;
	}

	private bool LookIntercept(ClientSession session, StateEntry e, long now)
	{
		LookPredictor look = _look;
		if (look == null || _lookFailed || e.NetId == 0 || e.NetId != look.NetId || !look.Intercepts || e.Bytes.Length != 40 || !session.IsTransformKey(e.Key))
		{
			return false;
		}
		try
		{
			var (x, y, z, o) = LookMath.ReadTransform(e.Bytes);
			if (look.OnHost(now, x, y, z, o))
			{
				_lookDirty = true;
			}
			_ownHostLocalMs = session.Smoother.LocalOf(e.HostMs);
			MovePredictor move = _move;
			if (move != null)
			{
				move.OnHost(now, e.HostMs);
				if (look.Mode == LookMode.Ground && _ownVel.HasValue)
				{
					System.Numerics.Vector3 hostVel = _ownVel.GetValueOrDefault();
					System.Numerics.Vector3 up = System.Numerics.Vector3.Transform(LookMath.Up, look.Shown);
					move.Observe(hostVel - up * System.Numerics.Vector3.Dot(hostVel, up), look.LagMs);
				}
			}
			_lookIntercepted++;
			return true;
		}
		catch (Exception e2)
		{
			LookFail(e2);
			return false;
		}
	}

	private void LookWrite(ClientSession session, VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader w, int baseIndex, ref int cmds)
	{
		//IL_0190: Unknown result type (might be due to invalid IL or missing references)
		LookPredictor look = _look;
		if (look == null || _lookFailed || !_lookDirty)
		{
			return;
		}
		_lookDirty = false;
		try
		{
			if (Ragdolling(look.NetId, MpClock.NowMs()))
			{
				RagdollWritesSkipped++;
				return;
			}
			byte[] array = look.Display();
			if (array == null || !session.TryGetTransformType(out var typeId, out var size) || size != array.Length || !_proxies.IsLive(look.NetId) || !_proxies.TryGet(look.NetId, out Entity e))
			{
				return;
			}
			string lastEvent = look.LastEvent;
			if (lastEvent != null)
			{
				NoteBodyWhy("prediction " + lastEvent);
				look.LastEvent = null;
			}
			MovePredictor move = _move;
			bool flag = move != null && move.Active && !_ownSmoothed;
			if (_body != null || _deckFrame || flag)
			{
				var (x, y, z, quaternion) = LookMath.ReadTransform(array);
				System.Numerics.Vector3 leadShown = default;
				double ageShown = -1;
				if (flag)
				{
					// A server stall pauses the host's simulation and never catches up: predicting through it put the body
					// ahead of where the host resumed, and it was pulled back (the pop). Past 100 ms without a sample the lead
					// stops growing, so the body waits with the server and carries on from there.
					long leadNow = MpClock.NowMs();
					if (_ownHostLocalMs is double sampleAt && leadNow - sampleAt > StallLeadMs) leadNow = (long)(sampleAt + StallLeadMs);
					System.Numerics.Vector3 vector = move.Lead(leadNow, look.LagMs);
					leadShown = vector;
					if (_ownHostLocalMs is double sampleLocal) ageShown = MpClock.NowMs() - sampleLocal;
					x += (double)vector.X;
					y += (double)vector.Y;
					z += (double)vector.Z;
				}
				else if (!_ownSmoothed && !_deckFrame && _ownVel is System.Numerics.Vector3 hostVel && _ownHostLocalMs is double hostLocal)
				{
					// Nothing local predicts this motion (drifting, pushed, carried): carry the newest host sample to now
					// along the host's velocity, or the body stands still on frames without a sample and doubles up after.
					// Never with the input lead: its window already runs to now, and adding the sample's age on top
					// wobbled the body by speed x 0-16 ms (0.8 m at 100 m/s).
					double age = Math.Clamp(MpClock.NowMs() - hostLocal, 0.0, 100.0) / 1000.0;
					ageShown = age * 1000.0;
					double seconds = age;
					x += hostVel.X * seconds;
					y += hostVel.Y * seconds;
					z += hostVel.Z * seconds;
				}
				if (_body != null)
				{
					string source;
					if (_ownSmoothed)
					{
						source = (_deckFrame ? "deck's smoothed timeline" : "smoothed timeline");
					}
					else
					{
						source = (flag ? "host's newest sample + movement lead" : "host's newest sample (raw)");
					}
					long bigBefore = _body.BlendedBig;
					(double X, double Y, double Z, System.Numerics.Quaternion Q) tuple2 = _body.Filter(MpClock.NowMs(), x, y, z, quaternion, flag ? new System.Numerics.Vector3?(move.Velocity) : _ownVel, look.LastStep, source, _bodyWhy, _ownSmoothed);
					x = tuple2.X;
					y = tuple2.Y;
					z = tuple2.Z;
					quaternion = tuple2.Q;
					_bodyWhy = null;
					long nowMs = MpClock.NowMs();
					if (_body.BlendedBig != bigBefore && nowMs - _bodyDetailMs >= 1000)
					{
						// What the jump was made of, so rubberbanding in a real session can be traced to its cause.
						_bodyDetailMs = nowMs;
						var hostSpeed = _ownVel is System.Numerics.Vector3 hv ? hv.Length() : -1f;
						_log.Info($"client: own body jump detail: lead {leadShown.Length():0.00} m ({(look.MeasuredLagMs.HasValue ? "measured" : "estimated")} lag {look.LagMs:0} ms), newest host sample {ageShown:0} ms old, model speed {(move?.Velocity.Length() ?? 0f):0.0} m/s, host speed {hostSpeed:0.0} m/s, last input applied by the host {(session.InputAck.Seq == 0 ? "(no ack yet)" : $"#{session.InputAck.Seq}, {session.InputAck.RoundTripMs} ms from sending to its ack (worst of the last 5 s {session.InputAckWorstMs} ms)")}, mode {look.Mode}");
					}
				}
				array = LookMath.WriteTransform(x, y, z, System.Numerics.Quaternion.Normalize(_deckFrame ? (_deckQ * quaternion) : quaternion));
			}
			w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(baseIndex + 1, _sessionProxy, e, new Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent.InProcessPayload(typeId, (ReadOnlySpan<byte>)array));
			cmds++;
			_lastWritten = array;
			_lastWrittenMs = MpClock.NowMs();
		}
		catch (Exception e2)
		{
			LookFail(e2);
		}
	}

	private void LookFail(Exception e)
	{
		if (!_lookFailed)
		{
			_lookFailed = true;
			_log.Error($"client: look prediction failed and is switched off (the host's orientation is applied as is from now on): {e}");
			Report(ClientPhase.Warning, "look prediction failed: " + e.GetType().Name + ": " + e.Message);
		}
	}

	private string LookDescribe()
	{
		if (_o.Predict)
		{
			if (!_lookFailed)
			{
				if (_look != null)
				{
					return $"{_look.Describe()}, host transforms taken {_lookIntercepted}, totals sent {_session?.LookSent ?? 0}, movement lead {((_move != null && _move.Active) ? $"on (wall gain {_move.Gain:0.00})" : "off")}, input round trip {_session?.InputAck.RoundTripMs ?? 0} ms (worst of 5 s {_session?.InputAckWorstMs ?? 0} ms)" + ((_body != null) ? $"; {_body.Describe()}; second writer on the own body {_otherWriterCount}x (largest {_otherWriterMaxM:0.00} m)" : "");
				}
				return "look prediction: no character yet";
			}
			return "look prediction FAILED (off)";
		}
		return "look prediction off (-y51online:predict:off)";
	}

	private void UpdateNametags(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		ClientSession session = _session;
		if (session == null || !_worldApplied || !NametagOverlay.Enabled)
		{
			return;
		}
		try
		{
			long changes = session.Names.Changes;
			if (changes != _tagChanges || _tagTick++ % 30 == 0)
			{
				_tagChanges = changes;
				_tagList.Clear();
				foreach (PlayerTag item2 in session.Names.Others(session.ClientId))
				{
					if (item2.NetId != _ownNetId)
					{
						Entity item = null;
						if (_proxies.IsLive(item2.NetId) && _proxies.TryGet(item2.NetId, out Entity e))
						{
							bool skip = false;
							item = comp.ReplayContext.TranslateObject(e, ReplicationFailureStrategy.NullOnAnyFail, ref skip) as Entity;
						}
						_tagList.Add((item2.ClientId, item2.Name, item));
					}
				}
			}
			(_tagOverlay ?? (_tagOverlay = new NametagOverlay(_log, "client"))).Update(comp.Session, comp.Entity, _tagList);
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-nametags", $"nametags(client): update failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	public void OnConnectionLost(string reason)
	{
		bool flag = _left || _retired || _lostReason != null;
		if (!flag)
		{
			JoinStage stage = Progress.Stage;
			bool flag2 = ((stage == JoinStage.Rejected) ? true : false);
			flag = flag2;
		}
		if (!flag)
		{
			_lostReason = (string.IsNullOrWhiteSpace(reason) ? "connection closed" : reason);
		}
	}

	public static void ConnectionLost(ClientSession session, string reason)
	{
		ClientWorld current = Current;
		if (current != null && current._session == session)
		{
			current.OnConnectionLost(reason);
		}
	}

	public void AskRejoin(string why)
	{
		_rejoinAsk = why;
	}

	internal bool MenuStep(bool atMenu, bool transitioning, bool clientUp, long now, Action? returnToMenu)
	{
		if (_retired)
		{
			return true;
		}
		if (Progress.Stage == JoinStage.Rejected && clientUp)
		{
			// Kicked or banned while playing: leave the host's world without trying to rejoin.
			if (!transitioning && !_menuReturnIssued)
			{
				_menuReturnIssued = true;
				_log.Info("client: removed by the host; returning to the main menu");
				returnToMenu?.Invoke();
			}
			return true;
		}
		string lostReason = _lostReason;
		if (lostReason != null && !_left)
		{
			if (!_lostHandled)
			{
				_lostHandled = true;
				_lostAtMs = now;
				Progress.SetInGame(clientUp);
				Progress.Set(JoinStage.Lost, now, lostReason);
				IMpLog log = _log;
				string text = lostReason;
				string? text2;
				if (_o.Rejoin)
				{
					text2 = ((clientUp | transitioning) ? $"back to the main menu in {4} s, then joining again (fresh world sync)" : $"joining again in {3} s");
				}
				else
				{
					text2 = "the join dialog offers Retry";
				}
				log.Warn("client: LOST THE HOST (" + text + "); " + text2);
				Report(ClientPhase.Warning, "lost the host: " + lostReason);
			}
			if (!_o.Rejoin)
			{
				return true;
			}
			if (clientUp)
			{
				if (!transitioning && !_menuReturnIssued && now - _lostAtMs >= 4000)
				{
					_menuReturnIssued = true;
					_log.Info("client: returning to the main menu (the host's world is gone from this game)");
					returnToMenu?.Invoke();
				}
				return true;
			}
			if (atMenu && !transitioning)
			{
				if (!_lostMenuSeen)
				{
					_lostMenuSeen = true;
					if (_menuReturnIssued || _configured)
					{
						_lostAtMs = now;
					}
					Progress.SetInGame(inGame: false);
					Progress.Set(JoinStage.Lost, now, lostReason);
				}
				if (now - _lostAtMs >= 3000)
				{
					Rejoin("the connection was lost (" + lostReason + ")", now);
				}
			}
			return true;
		}
		if (((!_left && _configured) & atMenu) && !transitioning && !clientUp)
		{
			_left = true;
			if (_startFailed)
			{
				_log.Warn("client: the client game failed to load and the game is back at the main menu; " + (_o.Rejoin ? "press F3 to join again" : "restart the game to join again"));
			}
			else
			{
				_log.Info("client: back at the main menu: leaving the host's world" + (_o.Rejoin ? " (press F3 at the main menu, or 'Join Game' in Steam, to join again)" : " (restart the game to join again)"));
				Report(ClientPhase.Warning, "left the world (quit to the main menu)");
				Progress.SetInGame(inGame: false);
				Progress.Set(JoinStage.Left, now);
			}
			_session?.Leave("quit to the main menu");
		}
		bool flag = _left || _startFailed;
		if (!flag)
		{
			JoinStage stage = Progress.Stage;
			bool flag2 = ((stage - 12 <= JoinStage.WaitingForSteam) ? true : false);
			flag = flag2;
		}
		bool flag3 = flag;
		if (((flag3 && _o.Rejoin) & atMenu) && !transitioning && !clientUp)
		{
			string text3 = Interlocked.Exchange(ref _rejoinAsk, null);
			if (text3 == null && RejoinKey.Pressed())
			{
				text3 = "F3 pressed at the main menu";
			}
			if (text3 != null)
			{
				Rejoin(text3, now);
			}
			return true;
		}
		if (!flag3)
		{
			_rejoinAsk = null;
		}
		return flag3;
	}

	private void Rejoin(string why, long now)
	{
		if (_retired)
		{
			return;
		}
		_retired = true;
		ClientWorld clientWorld = (Current = new ClientWorld(_log, _o)
		{
			_mainQueue = _mainQueue,
			_mainThreadSeen = true,
			_generation = _generation + 1
		});
		AudioRouting.Instance.Reset();
		Progress.NewJoin(now);
		_log.Info($"client: JOINING AGAIN (join #{clientWorld._generation} in this game): {why}; the world sync starts from scratch " + "(NetworkIds, proxies, pairings, lifecycle, state, blobs, signal/type keys, stories, observables, look prediction, nametags)");
		try
		{
			RejoinRequested?.Invoke(why);
		}
		catch (Exception value)
		{
			_log.Error($"client: asking the net thread to reconnect failed: {value}");
		}
	}

	private void ReturnToMenu(Keen.Game2.GameAppComponent app)
	{
		if (_mainQueue != null)
		{
			_mainQueue.Enqueue(Go);
		}
		else
		{
			Go();
		}
		void Go()
		{
			try
			{
				if (!IsTransitioning(app) && app.Client != null)
				{
					app.Disconnect();
				}
			}
			catch (Exception ex)
			{
				_log.Error($"client: returning to the main menu failed ({ex.GetType().Name}: {ex.Message}); use ESC > exit to the main menu");
			}
		}
	}

	private void ShowProgress(Keen.Game2.GameAppComponent app, long now)
	{
		JoinStage stage = Progress.Stage;
		if (stage != _loggedStage)
		{
			_loggedStage = stage;
			JoinText joinText = Progress.Describe(now);
			if (stage != JoinStage.Idle)
			{
				_log.Info($"client: join progress ({stage}): {joinText.Toast ?? joinText.Loading ?? "-"}");
			}
		}
		s_ui?.Update(app, Progress, now);
	}

	internal static string UiDescribe()
	{
		return s_ui?.Describe() ?? "on-screen progress: none";
	}

	public static bool AwaitReplicationPrefix(object __instance, EntityId __0, ref Task<Entity> __result)
	{
		ClientWorld current = Current;
		InProcessReplication val = current?._replication;
		if (val == null || !(__instance is Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent inProcessReplicationSessionComponent) || inProcessReplicationSessionComponent._context != val.Client)
		{
			return true;
		}
		try
		{
			__result = current.AwaitSecureEntity(inProcessReplicationSessionComponent, __0);
			return false;
		}
		catch (Exception ex)
		{
			current._secureFailed++;
			current._log.WarnOnce("client-await-replication", $"client: resolving a host EntityId failed ({ex.GetType().Name}: {ex.Message}); Keen's own call runs (it never completes on a joiner)");
			return true;
		}
	}

	private Task<Entity> AwaitSecureEntity(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp, EntityId id)
	{
		TaskCompletionSource<Entity> tcs = TaskCompletionSource<Entity>.Create("Y51_SE2Online.ClientWorld.AwaitSecureEntity");
		if (id == default(EntityId))
		{
			tcs.SetResult(null);
			return tcs.Task;
		}
		long n = ++_secureAsked;
		Keen.VRage.Library.Threading.TaskAwaiter<Entity?> awaiter = SecureEntities.ResolveSecureEntity(comp.Session, id).Task.GetAwaiter();
		awaiter.OnCompleted(() =>
		{
			Entity entity = null;
			string text = null;
			try
			{
				ExceptionDispatchInfo exceptionResult = awaiter.GetExceptionResult();
				if (exceptionResult != null)
				{
					text = exceptionResult.SourceException.GetType().Name;
				}
				else
				{
					entity = awaiter.GetResult();
				}
			}
			catch (Exception ex)
			{
				text = ex.GetType().Name;
			}
			if (entity != null)
			{
				_secureResolved++;
			}
			else
			{
				_secureNull++;
			}
			if (n <= 3 || (entity == null && _secureNull == 1))
			{
				_log.Info($"client: AwaitReplication #{n} (a host model's EntityId {id.EntityGuid}) -> " + ((entity != null) ? ("NetworkId " + (NetIdOf(entity)?.ToString() ?? "?") + " via the host") : ("nothing (" + (text ?? "the host does not know it, or it never arrived here") + ")")));
			}
			tcs.SetResult(entity);
		});
		return tcs.Task;
	}

	private string DescribeSecure()
	{
		if (_secureAsked != 0L)
		{
			return $"; host EntityIds asked {_secureAsked}: resolved {_secureResolved}, none {_secureNull}, failed {_secureFailed}";
		}
		return "";
	}
}
