using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Game.GameComponents;
using Keen.Game2.OutOfProcess;
using Keen.Game2.InProcess;
using Keen.Game2.Simulation.GameSystems.Missions;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Y51_SE2Online.Sync;
using Keen.Game2.Simulation.RuntimeSystems.GameData;
using Keen.Game2.Simulation.RuntimeSystems;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.VRage.Core.Worlds;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Threading;
using Keen.VRage.Multiplayer.NetworkStories;
using Task = Keen.VRage.Library.Threading.Task;

namespace Y51_SE2Online.Dedicated;

internal static class HeadlessWorldLoading
{
    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(GameAppComponent), "Init"),
            prefix: new HarmonyMethod(typeof(HeadlessWorldLoading), nameof(InitPrefix)));
        harmony.Patch(AccessTools.Method(typeof(GameAppComponent), "TransitionToGame"),
            prefix: new HarmonyMethod(typeof(HeadlessWorldLoading), nameof(TransitionPrefix)));
        harmony.Patch(AccessTools.Method(typeof(PlayersSessionComponent), "Init", new[] { typeof(PlayersSessionObjectBuilder) }),
            postfix: new HarmonyMethod(typeof(HeadlessWorldLoading), nameof(PlayersInitPostfix)));
        harmony.Patch(AccessTools.Method(typeof(SaveSessionComponent), "CreateSaveThumbnailAsync"),
            prefix: new HarmonyMethod(typeof(HeadlessWorldLoading), nameof(ClientSavePrefix)));
        harmony.Patch(AccessTools.Method(typeof(SaveSessionComponent), "NotifyClientSaveCompletedAsync"),
            prefix: new HarmonyMethod(typeof(HeadlessWorldLoading), nameof(ClientSavePrefix)));
        harmony.Patch(AccessTools.Method(typeof(ColonizationProgressServerSessionComponent), "GetGlobalProgressFor"),
            prefix: new HarmonyMethod(typeof(HeadlessWorldLoading), nameof(EmptyColonizationPrefix)));
    }

    private static bool EmptyColonizationPrefix(ColonizationProgressServerSessionComponent __instance, SectorComponent sector, ref SectorColonizationProgress __result)
    {
        if (!DedicatedRuntime.Headless || __instance._players.GetPlayerIdentityIds().Any()) return true;
        var saved = __instance._data.GetIdentityIds().FirstOrDefault(id => PlayerIdentities.IsHuman(id));
        __result = __instance.GetProgressFor((long)saved == 0 ? OwnershipSessionComponent.NpcIdentity : saved, sector);
        return false;
    }

    private sealed class CompletedStory : NetworkStory
    {
        protected override Task GetVoidTask() => Task.FromResult(true).StripResult();
    }

    private static bool ClientSavePrefix(ref NetworkStory __result)
    {
        if (!DedicatedRuntime.Headless) return true;
        __result = new CompletedStory();
        return false;
    }

    private static bool InitPrefix(GameAppComponent __instance, GameAppComponentObjectBuilder ob)
    {
        if (!DedicatedRuntime.Headless) return true;
        __instance._instanceIndex = ob.InstanceIndex.GetValueOrDefault();
        __instance.EnableTransitionDelay = false;
        return false;
    }

    public static async Task Load(GameAppComponent app, WorldLoadArgs args)
    {
        await Task.SetLifetime(app.Scene);
        if (!app._isTransitioning.Set()) throw new InvalidOperationException("A world transition is already running.");
        try
        {
            bool mission = await MissionScreenSessionComponent.IsMissionWorld(args, app);
            var server = app.Scene.Dynamic();
            server.WithComponent<DedicatedServerWorldTickMockComponent>();
            server.WithComponent<DedicatedServerReplicationMockComponent>();
            var ob = GameWorldBuilder.CreateBaseGame(ref server, true, app._instanceIndex, mission);
            GameWorldBuilder.BuildGameServer(ref server);
            if (args.SaveId.HasValue)
                ob.WorldContainerToLoad = new WorldSessionComponentObjectBuilder.ContainerLoadData
                {
                    WorldContainerToLoad = args.SaveId, ContainerBackupId = args.SaveBackupId,
                    DetachFromContainerAfterLoad = args.PlayMission == true
                };
            ob.OverrideModList = args.OverrideModList;
            await Transition(app, server);
            var world = app.Server.Entity.Get<WorldSessionComponent>();
            var session = world.OwnedSession;
            if (args.EnableAutosave.HasValue) session.SessionComponents.Get<SaveSessionComponent>().EnableAutosave = args.EnableAutosave.Value;
            if (args.OverrideDisplayName != null) await world.SetDisplayName(args.OverrideDisplayName);
            bool content = false;
            if (args.SaveId.HasValue)
            {
                var source = await SaveHelper.TryGetSaveAsync(args.SaveId.Value);
                content = source.HasValue && source.Value.IsExternal;
            }
            session.SessionComponents.TryGet<MissionSessionComponent>()?.Configure(args.PlayMission, content);
        }
        finally { app._isTransitioning.Clear(); }
    }

    private static bool TransitionPrefix(GameAppComponent __instance, EntityBuilder? server,
        EntityBuilder? client, ref Task __result)
    {
        if (!DedicatedRuntime.Headless) return true;
        if (client.HasValue) throw new InvalidOperationException("A headless server cannot create a local game client.");
        __result = Transition(__instance, server);
        return false;
    }

    private static void PlayersInitPostfix(PlayersSessionComponent __instance)
    {
        if (!DedicatedRuntime.Headless) return;
        foreach (var player in __instance._playerData.Values.Where(p => p.ClientId.Id == 1).ToArray())
        {
            __instance._playerData.Remove(player.Player);
            __instance._identities.Remove(player.Identity);
            __instance._clientIds.Remove(player.ClientId);
        }
    }

    private static async Task Transition(GameAppComponent app, EntityBuilder? server)
    {
        await Task.SetLifetime(app.Scene);
        await app.WaitForSaveToFinish();
        app.NotifySessionLoadFinished(new System.Threading.Tasks.TaskCanceledException());
        var data = app.Scene.Single<GameDataLoaderComponent>();
        if (server.HasValue)
        {
            var world = server.Value.OBsOfType<WorldSessionComponentObjectBuilder>().Single();
            if (world.OverrideModList != null) data.SetRequiredProjects(world.OverrideModList);
            else if (world.WorldContainerToLoad?.WorldContainerToLoad is { } save)
                data.SetRequiredProjects(await SaveHelper.TryGetModList(save, app));
        }
        if (!data.CurrentProjectsMatchRequired)
        {
            app.Scene.UnloadAllScenes();
            await data.PreloadWorldDataIfNeeded();
        }
        app.Scene.UnloadAllScenes();
        await data.LoadWorldDataIfNeeded();
        if (server.HasValue) HeadlessEngine.Prepare(server.Value);
        await app.Scene.TransitionToGame(server, null);
    }
}
