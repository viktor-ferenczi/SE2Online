using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Toolbars;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class ClientStories
{
	public const uint ServerOrigin = 0x80000000u;

	private sealed class ServerStoryNarrator : INetworkStoryNarrator
	{
		public static readonly ServerStoryNarrator Instance = new ServerStoryNarrator();

		public void Start<TResult>(NetworkStoryContext context, IAsyncStateMachine stateMachine) => stateMachine.MoveNext();

		public void MoveToClient(NetworkStoryContext context, IAsyncStateMachine stateMachine, ClientRequest client) => stateMachine.MoveNext();

		// ponytail: a host-started story that moves back to the server ends here; the forwarded notification stories never do.
		public void MoveToServer(NetworkStoryContext context, IAsyncStateMachine stateMachine, ServerRequest server)
		{
		}

		public void Finish<TResult>(NetworkStoryContext context, TResult result, Exception? exception)
		{
		}
	}

	private sealed class Entry
	{
		public uint Id;

		public NetworkStoryContext Context;

		public Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? Comp;

		public string Name;

		public long LastMs;

		public bool AtHost;
	}

	public enum Keep : byte
	{
		Succeed,
		Hold,
		LocalOnly
	}

	private readonly IMpLog _log;

	private readonly Dictionary<uint, Entry> _byId = new Dictionary<uint, Entry>();

	private readonly Dictionary<object, uint> _byContext = new Dictionary<object, uint>(ReferenceEqualityComparer.Instance);

	private readonly Dictionary<string, long> _counts = new Dictionary<string, long>();

	private uint _next = 1u;

	public const int TimeoutMs = 60000;

	public const int IdleForgetMs = 300000;

	public static readonly Dictionary<string, long> LocalResults = new Dictionary<string, long>(StringComparer.Ordinal) { ["InProcessGameServerSessionComponent.TrySaveGame"] = 128L };

	public static readonly Dictionary<string, Keep> Denied = new Dictionary<string, Keep>(StringComparer.Ordinal)
	{
		["CameraStatePerPlayerData.StoreCameraData"] = Keep.Succeed,
		["CameraPerPlayerData.StoreCameraEntity"] = Keep.Succeed,
		["ControlPerPlayerData.StoreControlledEntity"] = Keep.Succeed,
		["PossessionHelper.PossessInitialEntityAsync"] = Keep.Hold,
		["RespawnSessionComponent.RequestRespawn"] = Keep.Hold,
		["InProcessGameServerSessionComponent.TrySaveGame"] = Keep.LocalOnly
	};

	public long Sent;

	public long Local;

	public long DeniedCount;

	public long CannotTravel;

	public long Resumed;

	public long Finished;

	public long TimedOut;

	public long Failed;

	public long CameraCoalesced;

	public long ReleasedNoOps;

	public long ServerStarted;

	private readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

	public bool PerPlayer;

	public const int CameraIntervalMs = 100;

	private long _lastCameraMs = -4611686018427387904L;

	private readonly List<(object Item, long FirstMs)> _waiting = new List<(object, long)>();

	public const int ObservableWaitMs = 10000;

	private readonly List<Action> _local = new List<Action>();

	public const int EntityWaitMs = 3000;

	public int InFlight => _byId.Count;

	private bool FirstOf(string key)
	{
		return _logged.Add(key);
	}

	public ClientStories(IMpLog log)
	{
		_log = log;
	}

	private void Count(string key)
	{
		_counts[key] = (_counts.TryGetValue(key, out var value) ? (value + 1) : 1);
	}

	public void OnMove(Delegate d, ClientSession session, ObjectSpace space, Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? comp)
	{
		if (!ReadClosure(d, out NetworkStoryContext context, out object box, out Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp2))
		{
			Count("unreadable closure");
			_log.WarnOnce("story-unreadable", "client: a network-story closure has an unexpected shape (" + ClosureShape(d) + "); such stories are dropped");
			return;
		}
		StoryMove storyMove = Prepare(context, box, comp2 ?? comp, space);
		if (storyMove != null)
		{
			session.SendStoryMove(storyMove);
		}
	}

	internal static bool ReadClosure(Delegate d, out NetworkStoryContext? context, out object? box, out Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? comp)
	{
		context = null;
		box = null;
		comp = null;
		object target = d.Target;
		if (target == null)
		{
			return false;
		}
		FieldInfo[] source = ObjectCodec.Fields(target.GetType());
		context = source.FirstOrDefault((FieldInfo f) => typeof(NetworkStoryContext).IsAssignableFrom(f.FieldType))?.GetValue(target) as NetworkStoryContext;
		box = source.FirstOrDefault((FieldInfo f) => f.FieldType == typeof(IAsyncStateMachine))?.GetValue(target);
		comp = source.FirstOrDefault((FieldInfo f) => typeof(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent).IsAssignableFrom(f.FieldType))?.GetValue(target) as Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent;
		if (context != null)
		{
			return box != null;
		}
		return false;
	}

	private static string ClosureShape(Delegate d)
	{
		if (d.Target != null)
		{
			return d.Target.GetType().Name + ": " + string.Join(", ", from f in ObjectCodec.Fields(d.Target.GetType())
				select f.FieldType.Name + " " + f.Name);
		}
		return d.Method.Name + ", no target";
	}

	internal StoryMove? Prepare(NetworkStoryContext context, object box, Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? comp, ObjectSpace space)
	{
		string stateMachineType = ObjectCodec.TypeName(box.GetType());
		string name = StoryState.Name(stateMachineType);
		if (Denied.TryGetValue(name, out var value) && value == Keep.LocalOnly)
		{
			DeniedCount++;
			Count(name + ": answered here (never sent to the host)");
			object result = null;
			if (LocalResults.TryGetValue(name, out var value2))
			{
				Type type = StoryState.ResultType(context);
				if ((object)type != null && type.IsEnum)
				{
					result = Enum.ToObject(type, value2);
				}
			}
			_log.Info($"client: STORY {name} answered here{((result != null) ? $" ({result})" : "")}: joiners do not save the host's world");
			_local.Add(() =>
			{
				FinishLocal(comp, context, result, null);
			});
			return null;
		}
		if (Denied.TryGetValue(name, out value))
		{
			bool flag = PerPlayer && value == Keep.Succeed;
			if (flag && name == "CameraStatePerPlayerData.StoreCameraData")
			{
				long num = MpClock.NowMs();
				if (num - _lastCameraMs < 100)
				{
					CameraCoalesced++;
					_local.Add(() =>
					{
						FinishLocal(comp, context, null, null);
					});
					return null;
				}
				_lastCameraMs = num;
			}
			if (!flag)
			{
				DeniedCount++;
				Count(name + ((value == Keep.Succeed) ? ": kept local (shared identity)" : ": held (the world sync's character flow does this)"));
				if (value == Keep.Succeed)
				{
					_local.Add(() =>
					{
						FinishLocal(comp, context, null, null);
					});
				}
				return null;
			}
		}
		if (name.StartsWith("PlayerToolbarServerSessionComponent.", StringComparison.Ordinal) && comp != null)
		{
			Local++;
			if (!PerPlayer)
			{
				Count(name + ": applied to this player's own toolbar");
				_local.Add(() =>
				{
					RunToolbarLocally(name, box, comp, context);
				});
				return null;
			}
			Count(name + ": applied locally and sent to the host");
			_local.Add(() =>
			{
				ApplyToolbarLocally(name, box, comp);
			});
		}
		bool flag2 = false;
		if (!_byContext.TryGetValue(context, out var value3))
		{
			value3 = _next++;
			_byContext[context] = value3;
			_byId[value3] = new Entry
			{
				Id = value3,
				Context = context,
				Comp = comp,
				Name = name
			};
			flag2 = true;
		}
		Entry entry = _byId[value3];
		entry.LastMs = MpClock.NowMs();
		space.ClearLocalIds();
		List<(string, WireValue)> list = StoryState.Encode(box, space, out string fail);
		if (list == null || space.LocalIds.Count > 0)
		{
			if (fail == null)
			{
				fail = "refers to an entity the host does not have";
			}
			if (fail.Contains("was already released by the host", StringComparison.Ordinal))
			{
				bool isVoid = StoryState.ResultType(context) == typeof(DBNull);
				ReleasedNoOps++;
				Count(name + ": names a model already released (no-op here)");
				if (FirstOf("released " + name))
				{
					_log.Info($"client: STORY {name} names a model the host already released ({fail}); {(isVoid ? "done here as a no-op" : "skipped here like Keen does")} (further ones only counted)");
				}
				Forget(value3);
				_local.Add(() =>
				{
					FinishLocal(comp, context, null, isVoid ? null : new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException());
				});
				return null;
			}
			CannotTravel++;
			Count(name + ": cannot travel (" + fail + ")");
			_log.WarnOnce("story-travel-" + name, $"client: story {name} cannot travel to the host ({fail}); it is skipped here");
			Forget(value3);
			_local.Add(() =>
			{
				FinishLocal(comp, context, null, new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException());
			});
			return null;
		}
		Sent++;
		entry.AtHost = true;
		if (flag2 && AudioRouting.IsBuildStory(name))
		{
			AudioRouting.Instance.NoteOwnBuild(MpClock.NowMs());
		}
		Count(name + ": sent");
		if (FirstOf("sent " + name) | (Sent <= 20))
		{
			_log.Info($"client: STORY {name} #{value3} -> host ({list.Count} fields){((Sent > 20) ? "; first of its kind" : "")}");
		}
		return new StoryMove
		{
			StoryId = value3,
			StateMachineType = stateMachineType,
			Fields = list
		};
	}

	private static bool IsWaitable(string fail)
	{
		return fail.Contains("is not on this side", StringComparison.Ordinal);
	}

	private static int WaitMs(string fail)
	{
		if (!fail.Contains("observ", StringComparison.Ordinal))
		{
			return 3000;
		}
		return 10000;
	}

	public void ProcessInbox(ConcurrentQueue<object> inbox, ObjectSpace space, Action<Action> post)
	{
		foreach (Action item in _local)
		{
			post(item);
		}
		_local.Clear();
		List<object> list = new List<object>();
		long num = MpClock.NowMs();
		foreach (var item2 in _waiting)
		{
			list.Add(item2);
		}
		_waiting.Clear();
		object result;
		while (inbox.TryDequeue(out result))
		{
			list.Add(result);
		}
		foreach (object item3 in list)
		{
			object obj = item3;
			long num2 = num;
			if (item3 is ValueTuple<object, long> tuple)
			{
				(obj, num2) = tuple;
			}
			if (!(obj is StoryMove storyMove))
			{
				if (!(obj is StoryFinish storyFinish))
				{
					continue;
				}
				if (!_byId.TryGetValue(storyFinish.StoryId, out Entry value))
				{
					Count("finish for an unknown story");
					continue;
				}
				object result2 = null;
				Exception ex = null;
				if (storyFinish.Status == 0)
				{
					string fail = null;
					Type type = StoryState.ResultType(value.Context);
					space.ClearLocalIds();
					space.LocalStubs.Clear();
					result2 = ((type == typeof(DBNull)) ? DBNull.Value : ObjectCodec.Decode(storyFinish.Result, type, space, translated: false, ref fail));
					if (fail != null && IsWaitable(fail) && num - num2 < WaitMs(fail))
					{
						_waiting.Add((storyFinish, num2));
						continue;
					}
					if (fail != null)
					{
						ex = new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException();
						Count(value.Name + ": result unusable here (" + fail + ")");
					}
				}
				else
				{
					ex = new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException();
					Count($"{value.Name}: {((storyFinish.Status == 2) ? "skipped" : "failed")} on the host ({storyFinish.Text})");
				}
				Forget(value.Id);
				Finished++;
				string text;
				if (ex == null)
				{
					text = "ok" + StoryState.Describe(result2);
				}
				else
				{
					text = ((storyFinish.Text.Length > 0) ? storyFinish.Text : "result unusable here");
				}
				if (FirstOf("finished " + value.Name + ((ex == null) ? "" : (" " + StoryState.Shape(text)))) | (Finished <= 20))
				{
					_log.Info($"client: STORY {value.Name} #{value.Id} finished on the host ({text})");
				}
				NetworkStoryContext ctx = value.Context;
				Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp = value.Comp;
				post(() =>
				{
					FinishLocal(comp, ctx, result2, ex);
				});
				continue;
			}
			if (!_byId.TryGetValue(storyMove.StoryId, out Entry value2))
			{
				if ((storyMove.StoryId & ServerOrigin) != 0)
				{
					StartServerStory(storyMove, space, post, num, num2);
					continue;
				}
				Count("move for an unknown story");
				continue;
			}
			value2.LastMs = MpClock.NowMs();
			space.ClearLocalIds();
			space.LocalStubs.Clear();
			object obj2 = StoryState.Decode(storyMove, space, value2.Context, null, out string fail2);
			if (obj2 == null && fail2 != null && IsWaitable(fail2) && num - num2 < WaitMs(fail2))
			{
				_waiting.Add((storyMove, num2));
				continue;
			}
			value2.AtHost = false;
			if (obj2 == null)
			{
				Failed++;
				_log.Warn($"client: story {value2.Name} #{value2.Id} came back but cannot continue here ({fail2}); skipped");
				Forget(value2.Id);
				NetworkStoryContext ctx2 = value2.Context;
				Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp2 = value2.Comp;
				post(() =>
				{
					FinishLocal(comp2, ctx2, null, new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException());
				});
				continue;
			}
			Resumed++;
			if (FirstOf("resumed " + value2.Name) | (Resumed <= 20))
			{
				_log.Info($"client: STORY {value2.Name} #{value2.Id} back from the host, continuing here");
			}
			IAsyncStateMachine sm = (IAsyncStateMachine)obj2;
			string n = value2.Name;
			post(() =>
			{
				try
				{
					sm.MoveNext();
				}
				catch (Exception ex2)
				{
					_log.Warn($"client: story {n} threw on resume: {ex2.GetType().Name}: {ex2.Message}");
				}
			});
		}
		long now = MpClock.NowMs();
		foreach (Entry item4 in _byId.Values.Where((Entry x) => !x.AtHost && now - x.LastMs > 300000).ToList())
		{
			Forget(item4.Id);
		}
		foreach (Entry item5 in _byId.Values.Where((Entry x) => x.AtHost && now - x.LastMs > 60000).ToList())
		{
			TimedOut++;
			_log.Warn($"client: story {item5.Name} #{item5.Id} got no answer from the host in {60} s; skipped");
			Forget(item5.Id);
			NetworkStoryContext ctx3 = item5.Context;
			Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp3 = item5.Comp;
			post(() =>
			{
				FinishLocal(comp3, ctx3, null, new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException());
			});
		}
	}

	private void StartServerStory(StoryMove move, ObjectSpace space, Action<Action> post, long now, long since)
	{
		string name = StoryState.Name(move.StateMachineType);
		Type? type = ObjectCodec.FindType(move.StateMachineType);
		FieldInfo? builder = (type == null) ? null : ObjectCodec.Fields(type).FirstOrDefault((FieldInfo f) => StoryState.IsBuilder(f.FieldType));
		if (builder == null)
		{
			Count(name + ": host story type not here");
			return;
		}
		NetworkStoryContext context = (NetworkStoryContext)Activator.CreateInstance(builder.FieldType);
		context.Narrator = ServerStoryNarrator.Instance;
		space.ClearLocalIds();
		space.LocalStubs.Clear();
		object? sm = StoryState.Decode(move, space, context, null, out string? fail);
		if (sm == null && fail != null && IsWaitable(fail) && now - since < WaitMs(fail))
		{
			_waiting.Add((move, since));
			return;
		}
		if (sm == null)
		{
			Failed++;
			_log.WarnOnce("client-host-story-" + name, $"client: host story {name} cannot run here ({fail}); skipped");
			return;
		}
		ServerStarted++;
		if (FirstOf("host-started " + name))
		{
			_log.Info($"client: STORY {name} started by the host runs here");
		}
		post(() =>
		{
			try { ((IAsyncStateMachine)sm).MoveNext(); }
			catch (Exception ex) { _log.Warn($"client: host story {name} threw: {ex.GetType().Name}: {ex.Message}"); }
		});
	}

	private void Forget(uint id)
	{
		if (_byId.Remove(id, out Entry value))
		{
			_byContext.Remove(value.Context);
		}
	}

	public static void FinishLocal(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? comp, NetworkStoryContext context, object? result, Exception? ex)
	{
		try
		{
			comp?.SetStoryInProgress(context, false);
		}
		catch (Exception)
		{
		}
		try
		{
			NetworkStory story = context.Story;
			if (story != null)
			{
				Type type = StoryState.ResultType(context);
				if (result == null && type.IsValueType)
				{
					result = Activator.CreateInstance(type);
				}
				story.GetType().GetMethod("SetResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(story, new object[2] { result, ex });
			}
		}
		catch (Exception)
		{
		}
	}

	private void RunToolbarLocally(string name, object box, Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp, NetworkStoryContext context)
	{
		Exception ex = (ApplyToolbarLocally(name, box, comp) ? null : new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException());
		FinishLocal(comp, context, null, ex);
	}

	internal static IdentityId LocalIdentity(Session session)
	{
		return session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerIdentity ?? InProcessPerPlayerDataSessionComponent.HotfixIdentity;
	}

	private bool ApplyToolbarLocally(string name, object box, Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		try
		{
			Session session = comp.Session;
			ToolbarPerPlayerData perPlayerData = (session.SessionComponents.Components.OfType<IPerPlayerData>().FirstOrDefault() ?? throw new InvalidOperationException("no IPerPlayerData on the client")).GetPerPlayerData<ToolbarPerPlayerData>(LocalIdentity(session));
			_log.Info("client: " + ApplyToolbarStory(name, box, perPlayerData));
			return true;
		}
		catch (Exception ex)
		{
			_log.WarnOnce("toolbar-local-" + name, $"client: applying {name} to the local toolbar failed: {ex.GetType().Name}: {ex.Message}");
			return false;
		}
	}

	internal static string ApplyToolbarStory(string name, object box, ToolbarPerPlayerData data)
	{
		FieldInfo[] source = ObjectCodec.Fields(box.GetType());
		if (name.EndsWith(".RequestSetTile", StringComparison.Ordinal))
		{
			ToolbarSelection toolbarSelection = (ToolbarSelection)source.First((FieldInfo x) => x.FieldType == typeof(ToolbarSelection)).GetValue(box);
			object value = source.First((FieldInfo x) => x.FieldType.Name.StartsWith("ReleaseToServer", StringComparison.Ordinal)).GetValue(box);
			ToolbarTile toolbarTile = value.GetType().GetProperty("Value").GetValue(value) as ToolbarTile;
			if (toolbarSelection.ToolbarIndexIndex >= 0 && toolbarSelection.ToolbarIndexIndex < data.ToolbarTiles.Count)
			{
				ObservableCollection<ToolbarTile> observableCollection = data.ToolbarTiles[toolbarSelection.ToolbarIndexIndex];
				if (toolbarSelection.TileIndex >= 0 && toolbarSelection.TileIndex < observableCollection.Count)
				{
					observableCollection[toolbarSelection.TileIndex] = toolbarTile;
					return $"toolbar tile {toolbarSelection.ToolbarIndexIndex}/{toolbarSelection.TileIndex} set to {toolbarTile?.GetType().Name ?? "empty"} on this player's own toolbar";
				}
			}
			return $"toolbar tile {toolbarSelection.ToolbarIndexIndex}/{toolbarSelection.TileIndex} is outside this player's toolbar ({data.ToolbarTiles.Count} rows); nothing set";
		}
		if (name.EndsWith(".RequestResetToolbar", StringComparison.Ordinal))
		{
			(source.First((FieldInfo x) => x.FieldType == typeof(DefaultToolbarDefinition)).GetValue(box) as DefaultToolbarDefinition)?.ResetToolbar(data);
			return "toolbar reset to its defaults on this player's own toolbar";
		}
		return name + ": nothing to do on this player's own toolbar";
	}

	public string Describe()
	{
		return $"stories: sent {Sent}, host-started {ServerStarted}, resumed {Resumed}, finished {Finished}, local toolbar {Local}, kept local {DeniedCount}, camera coalesced {CameraCoalesced}, cannot travel {CannotTravel}, released-model no-ops {ReleasedNoOps}, failed {Failed}, timed out {TimedOut}, in flight {_byId.Count}" + ((_counts.Count == 0) ? "" : ("; " + string.Join(", ", from kv in _counts.OrderByDescending((KeyValuePair<string, long> kv) => kv.Value).Take(8)
			select $"{kv.Key} x{kv.Value}")));
	}
}
