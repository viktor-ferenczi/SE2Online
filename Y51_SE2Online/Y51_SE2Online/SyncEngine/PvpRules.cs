using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Explosions;
using Keen.Game2.Simulation.GameSystems.ProjectileSystems;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Characters.Damage;
using Keen.Game2.Simulation.WorldObjects.Characters.Stats;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage;
using Keen.Game2.Simulation.WorldObjects.Tools.ToolDamage;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Server-side damage rules for multiplayer: with PvP off, players cannot hurt each other's characters or grids;
/// characters of players who are offline cannot die at all, except to another player's weapons when PvP is on.
/// With PvP on, other players' weapons hurt characters even in a creative world.
/// </summary>
internal static class PvpRules
{
    /// <summary>True on the host or dedicated server while a multiplayer session runs.</summary>
    public static volatile bool Active;
    public static volatile bool Pvp;
    /// <summary>Grids whose owner and whole faction are offline take no damage from other players.</summary>
    public static volatile bool OfflineRaid = true;
    public static long Blocked, OfflineProtected, PlayerHits, RaidsBlocked;
    private static volatile string _lastBlocked = "", _lastHit = "";

    private static volatile HashSet<long> _players = new();
    private static volatile HashSet<long> _online = new();
    private static volatile HashSet<long> _undefended = new();
    private static readonly List<(Vector3D Center, long Attacker, long Ms)> Explosions = new();

    [ThreadStatic] private static long t_attacker;
    [ThreadStatic] private static bool t_pvpDamage;

    private static FieldInfo _spawnSource, _simulatedSource;
    private static PropertyInfo _handleEmpty, _handleValue;
    private static FieldInfo _statGameMode;
    private static MethodInfo _updateStat;

    public static void Install(Harmony harmony)
    {
        var projectiles = typeof(ProjectileSystemSessionComponent);
        _spawnSource = AccessTools.Field(AccessTools.Inner(projectiles, "ProjectileSpawnInfo"), "SourceEntity");
        _simulatedSource = AccessTools.Field(AccessTools.Inner(projectiles, "SimulatedProjectileData"), "SourceEntity");
        _handleEmpty = _simulatedSource.FieldType.GetProperty("IsEmpty");
        _handleValue = _simulatedSource.FieldType.GetProperty("Value");
        HarmonyMethod Own(string name) => new(typeof(PvpRules), name);
        harmony.Patch(AccessTools.Method(projectiles, "SpawnHitscanProjectile"), prefix: Own(nameof(HitscanPrefix)), finalizer: Own(nameof(ClearFinalizer)));
        harmony.Patch(AccessTools.Method(projectiles, "ProcessHit"), prefix: Own(nameof(SimulatedPrefix)), finalizer: Own(nameof(ClearFinalizer)));
        harmony.Patch(AccessTools.Method(projectiles, "ExplodeProjectile"), prefix: Own(nameof(ExplodePrefix)));
        foreach (var receiver in new[] { typeof(CharacterHitReceiverComponent), typeof(GridHitReceiverComponent) })
        {
            harmony.Patch(AccessTools.Method(receiver, "TryReceiveProjectileHit"), prefix: Own(nameof(ProjectileHitPrefix)));
            harmony.Patch(AccessTools.Method(receiver, "ReceiveExplosionHit"), prefix: Own(nameof(ExplosionHitPrefix)), finalizer: Own(nameof(ClearFinalizer)));
        }
        foreach (var tool in new[] { typeof(CharacterToolDamageAdapterComponent), typeof(GridToolDamageAdapterComponent) })
            harmony.Patch(AccessTools.Method(tool, "TryDamage"), prefix: Own(nameof(ToolPrefix)), finalizer: Own(nameof(ClearFinalizer)));
        harmony.Patch(AccessTools.Method(typeof(CharacterLifeComponent), nameof(CharacterLifeComponent.DoDamage)), prefix: Own(nameof(DoDamagePrefix)), finalizer: Own(nameof(DoDamageFinalizer)));
        _statGameMode = AccessTools.Field(typeof(StatCollectionComponent), "_gameMode");
        _updateStat = AccessTools.Method(typeof(StatCollectionComponent), "UpdateStat");
        harmony.Patch(AccessTools.Method(typeof(StatCollectionComponent), nameof(StatCollectionComponent.TrySetValue)), prefix: Own(nameof(TrySetValuePrefix)));
    }

    /// <summary>Every player identity of this world, and those currently connected (server thread snapshots).</summary>
    public static void SetPlayers(IEnumerable<long> players, IEnumerable<long> online, IEnumerable<IReadOnlyCollection<long>> factions)
    {
        var all = players.ToHashSet();
        var present = online.ToHashSet();
        var defended = new HashSet<long>(present);
        foreach (var members in factions)
            if (members.Any(present.Contains)) defended.UnionWith(members);
        _players = all;
        _online = present;
        _undefended = all.Where(p => !defended.Contains(p)).ToHashSet();
    }

    private static bool Raid(Component receiver, long victim)
        => OfflineRaid && (receiver is GridHitReceiverComponent || receiver is GridToolDamageAdapterComponent)
            && IsPlayer(t_attacker) && t_attacker != victim && _undefended.Contains(victim);

    public static void Reset()
    {
        Active = false; Pvp = false;
        _players = new(); _online = new(); _undefended = new();
        lock (Explosions) Explosions.Clear();
    }

    private static bool IsPlayer(long identity) => identity != 0 && _players.Contains(identity);

    /// <summary>Whether damage from <paramref name="attacker"/> to something owned by <paramref name="victim"/> is allowed.</summary>
    public static bool Allowed(long attacker, long victim, bool pvp, Func<long, bool> isPlayer)
        => pvp || !isPlayer(attacker) || !isPlayer(victim) || attacker == victim;

    private static void HitscanPrefix(object[] __args)
    {
        t_attacker = Active && __args[0] != null ? TeamPermissions.OwnerOf(_spawnSource.GetValue(__args[0]) as Entity) : 0;
    }

    private static void SimulatedPrefix(object[] __args)
    {
        t_attacker = 0;
        if (!Active || __args[2] == null) return;
        object handle = _simulatedSource.GetValue(__args[2]);
        if (handle != null && !(bool)_handleEmpty.GetValue(handle)) t_attacker = TeamPermissions.OwnerOf(_handleValue.GetValue(handle) as Entity);
    }

    private static void ExplodePrefix(Vector3D explosionCenter)
    {
        if (!Active || t_attacker == 0) return;
        long now = MpClock.NowMs();
        lock (Explosions)
        {
            Explosions.RemoveAll(e => now - e.Ms > 5000);
            Explosions.Add((explosionCenter, t_attacker, now));
        }
    }

    private static bool ProjectileHitPrefix(Component __instance, ref Vector3D? __result)
    {
        if (!Active) return true;
        long victim = TeamPermissions.OwnerOf(__instance.Entity);
        if (Allowed(t_attacker, victim, Pvp, IsPlayer) && !Raid(__instance, victim))
        {
            if (IsPlayer(t_attacker) && IsPlayer(victim) && t_attacker != victim) Note(ref PlayerHits, ref _lastHit, "projectile", victim, __instance);
            return true;
        }
        if (Raid(__instance, victim)) System.Threading.Interlocked.Increment(ref RaidsBlocked);
        Note(ref Blocked, ref _lastBlocked, "projectile", victim, __instance);
        __result = null;
        return false;
    }

    private static void Note(ref long counter, ref string last, string kind, long victim, Component receiver)
    {
        if (System.Threading.Interlocked.Increment(ref counter) % 50 == 1) last = $"{kind} {t_attacker} on {victim} ({receiver.GetType().Name})";
    }

    private static bool ExplosionHitPrefix(Component __instance, ExplosionHit explosion)
    {
        if (!Active) return true;
        long now = MpClock.NowMs();
        lock (Explosions) t_attacker = Explosions.LastOrDefault(e => now - e.Ms <= 5000 && e.Center == explosion.Center).Attacker;
        long victim = TeamPermissions.OwnerOf(__instance.Entity);
        if (Allowed(t_attacker, victim, Pvp, IsPlayer) && !Raid(__instance, victim))
        {
            if (IsPlayer(t_attacker) && IsPlayer(victim) && t_attacker != victim) Note(ref PlayerHits, ref _lastHit, "explosion", victim, __instance);
            return true;
        }
        if (Raid(__instance, victim)) System.Threading.Interlocked.Increment(ref RaidsBlocked);
        Note(ref Blocked, ref _lastBlocked, "explosion", victim, __instance);
        return false;
    }

    private static bool ToolPrefix(Component __instance, Span<Entity> entities, Entity sourceTopEntity, ref bool __result)
    {
        t_attacker = Active ? TeamPermissions.OwnerOf(sourceTopEntity) : 0;
        if (!Active) return true;
        foreach (Entity target in entities)
        {
            long victim = target == null ? 0 : TeamPermissions.OwnerOf(target);
            if (target != null && (!Allowed(t_attacker, victim, Pvp, IsPlayer) || Raid(__instance, victim)))
            {
                if (Raid(__instance, victim)) System.Threading.Interlocked.Increment(ref RaidsBlocked);
                if (System.Threading.Interlocked.Increment(ref Blocked) % 50 == 1) _lastBlocked = $"tool {t_attacker} on {victim}";
                __result = false;
                return false;
            }
        }
        return true;
    }

    private static Exception ClearFinalizer(Exception __exception)
    {
        t_attacker = 0;
        return __exception;
    }

    // Offline players' characters stay in the world; nothing kills them unless PvP is on and another player attacks.
    private static bool DoDamagePrefix(CharacterLifeComponent __instance, float damage)
    {
        t_pvpDamage = false;
        if (!Active || damage <= 0) return true;
        long owner = TeamPermissions.OwnerOf(__instance.Entity);
        if (!IsPlayer(owner)) return true;
        bool byPlayer = IsPlayer(t_attacker) && t_attacker != owner;
        if (byPlayer && !Pvp)
        {
            System.Threading.Interlocked.Increment(ref Blocked);
            return false;
        }
        t_pvpDamage = byPlayer;
        if (t_pvpDamage || _online.Contains(owner)) return true;
        System.Threading.Interlocked.Increment(ref OfflineProtected);
        return false;
    }

    private static Exception DoDamageFinalizer(Exception __exception)
    {
        t_pvpDamage = false;
        return __exception;
    }

    // A creative world locks character health; PvP damage from another player still lands there, unless the victim
    // is in per-player creative (an admin), which stays invulnerable.
    private static bool TrySetValuePrefix(StatCollectionComponent __instance, StatDefinition stat, float value, ref bool __result)
    {
        if (!t_pvpDamage || stat.ModifiableInCreative || _statGameMode.GetValue(__instance) is not GameModeSessionComponent mode || !mode.CreativeGlobal) return true;
        _updateStat.Invoke(__instance, new object[] { stat, stat.Clamp(value) });
        __result = true;
        return false;
    }

    public static string Describe() => $"pvp {(Pvp ? "on" : "off")}: player-on-player hits {PlayerHits} (last {_lastHit}), blocked {Blocked} (last {_lastBlocked}), offline characters protected {OfflineProtected}, offline raid protection {(OfflineRaid ? "on" : "off")} ({RaidsBlocked} blocked, {_undefended.Count} players undefended)";
}
