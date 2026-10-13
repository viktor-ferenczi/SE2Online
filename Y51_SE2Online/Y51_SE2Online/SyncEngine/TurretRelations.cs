using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Relations;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Weapons;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Weapons;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Player-to-player relations for turrets and sensors in multiplayer: with PvP on, players outside your faction are
/// hostile; you and your faction members (and your grids) are not. With PvP off, no player is hostile to another.
/// </summary>
internal static class TurretRelations
{
    /// <summary>Set per multiplayer session from the host's "turrets attack players outside your faction" setting.</summary>
    public static volatile bool PlayersHostile;

    // identity -> faction id; read on the simulation thread, replaced whole when membership changes.
    private static volatile Dictionary<long, string> _factions = new();
    private static string _signature;

    public static void Install(Harmony harmony)
        => harmony.Patch(AccessTools.Method(typeof(RelationsSessionComponent), nameof(RelationsSessionComponent.GetRelationFromOwners)),
            postfix: new HarmonyMethod(typeof(TurretRelations), nameof(RelationPostfix)));

    public static RelationState Relation(long a, long b, bool playersHostile, IReadOnlyDictionary<long, string> factions)
    {
        if (!playersHostile || a == b) return RelationState.NonHostile;
        return factions.TryGetValue(a, out var fa) && factions.TryGetValue(b, out var fb) && fa == fb ? RelationState.NonHostile : RelationState.Hostile;
    }

    private static void RelationPostfix(RelationsSessionComponent __instance, IdentityId ownerA, IdentityId ownerB, ref RelationState __result)
    {
        long a = (long)ownerA, b = (long)ownerB;
        if (!PvpRules.Active || a == 0 || b == 0 || __instance._npcIdentity.IsNpc(ownerA) || __instance._npcIdentity.IsNpc(ownerB)) return;
        __result = Relation(a, b, PlayersHostile, _factions);
    }

    /// <summary>Publishes faction membership; returns true when it changed so turrets need re-evaluating.</summary>
    public static bool SetFactions(IEnumerable<(long Member, string Faction)> members)
    {
        var map = members.GroupBy(m => m.Member).ToDictionary(g => g.Key, g => g.First().Faction);
        string signature = string.Join(";", map.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
        if (signature == _signature) return false;
        _signature = signature;
        _factions = map;
        return true;
    }

    /// <summary>Makes every turret grid rebuild its target list with the current relations (server thread).</summary>
    public static int Refresh(Session session)
    {
        int count = 0;
        foreach (Entity grid in session.GetEntitiesOfType<TargetingGridComponent>().ToList())
        {
            var targeting = grid.Get<TargetingGridComponent>();
            var owner = grid.TryGet<OwnershipComponent>()?.Owner ?? default;
            targeting.OnOwnerChanged(owner);
            count++;
        }
        // A turret keeps shooting its current target until it leaves range or sight; drop targets that are no longer hostile.
        foreach (Entity turret in session.GetEntitiesOfType<AutoTargetingComponent>().ToList())
        {
            var auto = turret.Get<AutoTargetingComponent>();
            Entity target = auto._target.Entity;
            if (target != null && auto._gridTargeting != null && !auto._gridTargeting.PotentialTargets.Contains(target)) auto.Untarget(setIdle: true);
        }
        return count;
    }

    private static volatile bool _refresh;

    /// <summary>Applies a PvP change to turrets, sensors and damage rules; turret target lists rebuild on the server thread.</summary>
    public static void SetPvp(bool on)
    {
        PvpRules.Pvp = PlayersHostile = on;
        _refresh = true;
    }

    public static bool TakeRefresh()
    {
        bool refresh = _refresh;
        _refresh = false;
        return refresh;
    }

    public static void Reset()
    {
        PlayersHostile = false;
        _factions = new();
        _signature = null;
    }
}
