using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.GameSystems.Camera;
using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.Game2.Simulation.GameSystems.Contracts;
using Keen.Game2.Simulation.GameSystems.Discoveries;
using Keen.Game2.Simulation.GameSystems.Economy;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.PCU;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Progression;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.GameSystems.Toolbars;
using Keen.Game2.Simulation.GameSystems.Toolbars.VoxelHand;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Y51_SE2Online.Core;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class HostIdentities
{
	public readonly record struct Result(long Identity, long Player, bool NewPlayer, string Detail);

	private readonly IMpLog _log;

	public readonly HostSettings Settings;

	private readonly ConcurrentDictionary<long, string?> _known = new ConcurrentDictionary<long, string>();

	private readonly ConcurrentDictionary<string, long> _skippedStories = new ConcurrentDictionary<string, long>();

	public long StoriesSkipped;

	public long StoriesForwarded;

	public long Registered;

	public long RestoredAtLoad;

	internal static readonly Type[] ResearchTypes = new Type[4]
	{
		typeof(PlayerProgressionData),
		typeof(ColonizationProgressData),
		typeof(ContractPerPlayerData),
		typeof(PlayerCreditsData)
	};

	// Welder/grinder mode and the paint palette are changed on the host (the mode request signal, the palette
	// story); without the live copy the joiner never saw a mode change, and a palette-less paint tool crashed it.
	internal static readonly Type[] LiveTypes = ResearchTypes.Append(typeof(GPSMarkersData)).Append(typeof(DiscoveriesPlayerData))
		.Append(typeof(IntegrityToolPerPlayerData)).Append(typeof(PaintToolPerPlayerData)).ToArray();

	// Server stories that only notify the addressed player's UI; others (saves, inventory) stay skipped.
	private static readonly HashSet<string> ForwardedStoryOwners = new HashSet<string>(StringComparer.Ordinal) { "DiscoveriesServerSessionComponent", "ColonizationProgressServerSessionComponent", "EncounterNotificationSessionComponent" };

	public static HostIdentities? Current { get; private set; }

	public static IdentityId Hotfix => InProcessPerPlayerDataSessionComponent.HotfixIdentity;

	public int KnownCount => _known.Count;

	private HostIdentities(IMpLog log, HostSettings settings)
	{
		_log = log;
		Settings = settings;
	}

	public static void Reset()
	{
		// Dedicated player data is restored before networking starts and survives network resets.
		if (!DedicatedRuntime.Headless) Current = null;
	}

	public static HostIdentities Install(IMpLog log, HostSettings settings)
	{
		return Current ?? (Current = new HostIdentities(log, settings));
	}

	public bool IsJoiner(long identity)
	{
		return _known.ContainsKey(identity);
	}

	public Result Register(Session server, ushort clientId, string key, string name)
	{
		//IL_0163: Unknown result type (might be due to invalid IL or missing references)
		//IL_0168: Unknown result type (might be due to invalid IL or missing references)
		//IL_016b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0235: Unknown result type (might be due to invalid IL or missing references)
		//IL_023a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0254: Unknown result type (might be due to invalid IL or missing references)
		//IL_0256: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c4: Unknown result type (might be due to invalid IL or missing references)
		Entity sessionComponents = server.SessionComponents;
		PlayersSessionComponent val = sessionComponents.TryGet<PlayersSessionComponent>(default(StringId)) ?? throw new InvalidOperationException("the server session has no PlayersSessionComponent");
		InProcessPerPlayerDataSessionComponent inProcessPerPlayerDataSessionComponent = sessionComponents.TryGet<InProcessPerPlayerDataSessionComponent>() ?? throw new InvalidOperationException("the server session has no InProcessPerPlayerDataSessionComponent");
		OwnershipSessionComponent ownershipSessionComponent = sessionComponents.TryGet<OwnershipSessionComponent>();
		long num = PlayerIdentities.Derive(key, (long id) => _known.TryGetValue(id, out string value3) && value3 != null && value3 != key);
		IdentityId identityId = new IdentityId(num);
		bool num2 = _known.TryGetValue(num, out string value) && value == null;
		_known[num] = key;
		if (ownershipSessionComponent != null)
		{
			ownershipSessionComponent._identities.Add(Hotfix);
			ownershipSessionComponent._identities.Add(identityId);
		}
		EnsureWrapper(inProcessPerPlayerDataSessionComponent);
		List<string> list = new List<string>();
		if (num2)
		{
			list.Add("per-player data from the save");
		}
		if (Settings.Research == ResearchMode.Shared)
		{
			list.Add($"research shared with the host ({ShareResearch(inProcessPerPlayerDataSessionComponent, identityId)} records)");
		}
		int value2 = PrecreateData(inProcessPerPlayerDataSessionComponent, identityId);
		PlayerIdentity val2;
		bool newPlayer;
		if (val.IsPlayerIdentity(identityId))
		{
			val2 = val.GetPlayerIdentity(identityId);
			PlayerData playerData = val.GetPlayerData(val2);
			if (playerData.ClientId.Id != clientId)
			{
				throw new InvalidOperationException($"identity {num} is registered for ClientId {playerData.ClientId.Id}, not {clientId}");
			}
			newPlayer = false;
		}
		else
		{
			if (val._clientIds.ContainsKey(new ClientId(clientId)))
			{
				throw new InvalidOperationException($"ClientId {clientId} already belongs to another player in this world session");
			}
			val2 = val.AllocatePlayer();
			val.PropagateCreatedPlayer(new PlayerData
			{
				ClientId = new ClientId(clientId),
				Player = val2,
				Identity = identityId
			});
			newPlayer = true;
		}
		ToolbarPerPlayerData perPlayerData = inProcessPerPlayerDataSessionComponent.GetPerPlayerData<ToolbarPerPlayerData>(identityId);
		if (StartingToolbar.IsBlank(perPlayerData))
		{
			var (defaultToolbarDefinition, text) = StartingToolbar.For(server, identityId, sessionComponents.TryGet<PlayerToolbarServerSessionComponent>()?._config?.DefaultToolbar);
			if (defaultToolbarDefinition != null)
			{
				defaultToolbarDefinition.ResetToolbar(perPlayerData);
				list.Add("starting toolbar (" + text + ")");
			}
		}
		PlayerCharacterData perPlayerData2 = inProcessPerPlayerDataSessionComponent.GetPerPlayerData<PlayerCharacterData>(identityId);
		if (!string.IsNullOrWhiteSpace(name))
		{
			perPlayerData2.PlayerName = name;
		}
		if (Settings.Grids == GridPermissionMode.Team)
		{
			RespawnPerPlayerData perPlayerData3 = inProcessPerPlayerDataSessionComponent.GetPerPlayerData<RespawnPerPlayerData>(identityId);
			if (perPlayerData3.KnownPoints.Count == 0)
			{
				List<Entity> knownPoints = inProcessPerPlayerDataSessionComponent.GetPerPlayerData<RespawnPerPlayerData>(Hotfix).KnownPoints;
				perPlayerData3.KnownPoints.AddRange(knownPoints);
				if (knownPoints.Count > 0)
				{
					list.Add($"{knownPoints.Count} respawn point(s) of the host known");
				}
			}
		}
		list.Add($"{value2} per-player record(s) created");
		Registered++;
		return new Result(num, val2._id, newPlayer, string.Join(", ", list));
	}

	internal static int PrecreateData(InProcessPerPlayerDataSessionComponent ppd, IdentityId identity)
	{
		List<object> source = default;
		int count = (((CollectionDictionary<IdentityId, List<object>, object>)(object)ppd._perPlayerData).TryGet(identity, out source) ? source.Select((object o) => o.GetType()).ToHashSet() : new HashSet<Type>()).Count;
		ppd.GetPerPlayerData<PaintToolPerPlayerData>(identity);
		ppd.GetPerPlayerData<ToolbarPerPlayerData>(identity);
		ppd.GetPerPlayerData<GPSMarkersData>(identity);
		ppd.GetPerPlayerData<DiscoveriesPlayerData>(identity);
		ppd.GetPerPlayerData<ContractPerPlayerData>(identity);
		ppd.GetPerPlayerData<PlayerProgressionData>(identity);
		ppd.GetPerPlayerData<ColonizationProgressData>(identity);
		ppd.GetPerPlayerData<CameraStatePerPlayerData>(identity);
		ppd.GetPerPlayerData<PlayerCharacterData>(identity);
		ppd.GetPerPlayerData<IntegrityToolPerPlayerData>(identity);
		ppd.GetPerPlayerData<BlockPlacerPerPlayerData>(identity);
		ppd.GetPerPlayerData<VoxelHandData>(identity);
		ppd.GetPerPlayerData<PlayerCreditsData>(identity);
		List<object> source2 = default;
		if (((CollectionDictionary<IdentityId, List<object>, object>)(object)ppd._perPlayerData).TryGet(Hotfix, out source2))
		{
			MethodInfo method = typeof(InProcessPerPlayerDataSessionComponent).GetMethod("GetPerPlayerData");
			List<object> source3 = default;
			foreach (Type item in source2.Select((object o) => o.GetType()).Distinct().ToList())
			{
				if (typeof(PerPlayerData).IsAssignableFrom(item) && (!((CollectionDictionary<IdentityId, List<object>, object>)(object)ppd._perPlayerData).TryGet(identity, out source3) || !source3.Any(item.IsInstanceOfType)))
				{
					method.MakeGenericMethod(item).Invoke(ppd, new object[1] { identity });
				}
			}
		}
		List<object> list = default;
		return (((CollectionDictionary<IdentityId, List<object>, object>)(object)ppd._perPlayerData).TryGet(identity, out list) ? list.Count : 0) - count;
	}

	internal static int ShareResearch(InProcessPerPlayerDataSessionComponent ppd, IdentityId identity)
	{
		return 0 + Share<PlayerProgressionData>(ppd, identity) + Share<ColonizationProgressData>(ppd, identity) + Share<ContractPerPlayerData>(ppd, identity) + Share<PlayerCreditsData>(ppd, identity);
	}

	private static int Share<T>(InProcessPerPlayerDataSessionComponent ppd, IdentityId identity) where T : PerPlayerData
	{
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Expected Obj, but got Unknown
		T perPlayerData = ppd.GetPerPlayerData<T>(Hotfix);
		List<object> orCreateCollectionForIdentity = ppd.GetOrCreateCollectionForIdentity(identity);
		int num = orCreateCollectionForIdentity.FindIndex((object o) => o is T);
		if (num >= 0 && orCreateCollectionForIdentity[num] == (object)perPlayerData)
		{
			return 0;
		}
		if (num >= 0)
		{
			PerPlayerData item = (PerPlayerData)orCreateCollectionForIdentity[num];
			orCreateCollectionForIdentity[num] = perPlayerData;
			ppd._clientVisibleData.Data.FirstOrDefault((InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData d) => (long)d.IdentityId == (long)identity)?.Data.Remove(item);
		}
		else
		{
			orCreateCollectionForIdentity.Add(perPlayerData);
		}
		return 1;
	}

	internal bool EnsureWrapper(InProcessPerPlayerDataSessionComponent ppd)
	{
		INpcIdentityProvider npc = ppd._npc;
		if (npc is JoinerAwareNpcProvider)
		{
			return true;
		}
		if (npc == null)
		{
			return false;
		}
		Unsafe.AsRef(in ppd._npc) = (INpcIdentityProvider)(object)new JoinerAwareNpcProvider(npc, IsJoiner);
		_log.Info("host identities: per-player data keeps joiner identities separate from now on (NPC provider wrapped)");
		return true;
	}

	public static (int Pcu, int Max)? ReadPcu(Session server, long identity)
	{
		PCUSessionComponent pCUSessionComponent = server.SessionComponents.TryGet<PCUSessionComponent>();
		if (pCUSessionComponent == null)
		{
			return null;
		}
		return (pCUSessionComponent.GetPCU(new IdentityId(identity)), pCUSessionComponent.MaxPerPlayerPCU);
	}

	public static void InitPrefix(PerPlayerDataSessionComponentObjectBuilder ob, out List<(IdentityId Id, List<object> Items)>? __state)
	{
		__state = null;
		HostIdentities current = Current;
		if (current == null || !current.Settings.Identities)
		{
			return;
		}
		try
		{
			foreach (KeyValuePair<IdentityId, List<object>> item2 in (CollectionDictionary<IdentityId, List<object>, object>)(object)ob.PerPlayerData)
			{
				long num = item2.Key;
				if (num != -1559595548 && !PlayerIdentities.IsNpc(num))
				{
					(__state ?? (__state = new List<(IdentityId, List<object>)>())).Add((item2.Key, new List<object>(item2.Value)));
				}
			}
			if (__state == null)
			{
				return;
			}
			foreach (var item3 in __state)
			{
				IdentityId item = item3.Id;
				((CollectionDictionary<IdentityId, List<object>, object>)(object)ob.PerPlayerData).Remove(item);
			}
		}
		catch (Exception ex)
		{
			__state = null;
			current._log.Error($"host identities: keeping joiners' saved per-player data failed at load ({ex.GetType().Name}: {ex.Message}); the game folds it into the host's as without the plugin");
		}
	}

	public static void InitPostfix(InProcessPerPlayerDataSessionComponent __instance, List<(IdentityId Id, List<object> Items)>? __state)
	{
		HostIdentities current = Current;
		if (current == null || __state == null)
		{
			return;
		}
		try
		{
			List<string> list = new List<string>();
			foreach (var item in __state)
			{
				var (id, list2) = item;
				current._known.TryAdd(id, null);
				List<object> orCreateCollectionForIdentity = __instance.GetOrCreateCollectionForIdentity(id);
				InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData perPlayerData = __instance._clientVisibleData.Data.First((InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData d) => (long)d.IdentityId == (long)id);
				foreach (object item2 in list2)
				{
					orCreateCollectionForIdentity.Add(item2);
					__instance.ObserveAndReplicateData(item2, perPlayerData);
				}
				list.Add($"{id} ({list2.Count} records)");
			}
			current.RestoredAtLoad += __state.Count;
			bool flag = current.EnsureWrapper(__instance);
			current._log.Info($"host identities: per-player data of {__state.Count} joiner identit{((__state.Count == 1) ? "y" : "ies")} kept at world load: {string.Join(", ", list)}" + (flag ? "" : " (NPC provider not injected yet; wrapped at the first registration)"));
		}
		catch (Exception ex)
		{
			current._log.Error($"host identities: restoring joiners' saved per-player data failed ({ex.GetType().Name}: {ex.Message}); they start fresh");
		}
	}

	public static bool MoveToClientPrefix(NetworkStoryContext context, IAsyncStateMachine stateMachine, ClientRequest client)
	{
		HostIdentities current = Current;
		if (current == null || !current.Settings.Identities)
		{
			return true;
		}
		int id = client.ClientId.Id;
		if (id <= 1)
		{
			return true;
		}
		string text = "?";
		try
		{
			text = StoryState.Name(ObjectCodec.TypeName(stateMachine.GetType()));
			if (ForwardedStoryOwners.Contains(stateMachine.GetType().DeclaringType?.Name ?? "") && HostWorld.Current?.ForwardServerStory((ushort)id, stateMachine) == true)
			{
				current.StoriesForwarded++;
			}
			else
			{
				current.StoriesSkipped++;
				if (current._skippedStories.AddOrUpdate(text, 1L, (string _, long c) => c + 1) == 1)
				{
					current._log.Info($"host identities: server story {text} addressed to ClientId {id} skipped (stories the server starts for a joiner cannot run on its PC yet; Keen would have run it on the host's own client)");
				}
			}
			if (!context.Skipped)
			{
				context.SkipStory();
			}
		}
		catch (Exception ex)
		{
			current._log.WarnOnce("host-skip-" + text, $"host identities: skipping server story {text} for ClientId {id} threw {ex.GetType().Name}: {ex.Message}");
		}
		return false;
	}

	public string Describe()
	{
		return $"identities: {Settings}; registered {Registered}, joiner identities {_known.Count} ({RestoredAtLoad} restored at load), server stories for joiners forwarded {StoriesForwarded}, skipped {StoriesSkipped}" + (_skippedStories.IsEmpty ? "" : (" (" + string.Join(", ", from kv in _skippedStories.OrderByDescending((KeyValuePair<string, long> kv) => kv.Value).Take(6)
			select $"{kv.Key} x{kv.Value}") + ")"));
	}
}
