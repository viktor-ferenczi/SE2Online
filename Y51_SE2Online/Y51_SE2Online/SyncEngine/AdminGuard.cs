using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2 2.4 never runs its ValidateAdminAccess validators, so a joiner's debug menu (F12) could spawn, delete and
/// teleport on the host. The host refuses those stories and signals unless the joiner is an admin.
/// </summary>
internal static class AdminGuard
{
    private static readonly ConcurrentDictionary<Type, bool> Stories = new();
    private static readonly string[] AdminTypes =
    {
        "Keen.Game2.Simulation.GameSystems.AdminTools.EntityAdmin",
        "Keen.Game2.Simulation.WorldObjects.SafeZones.SafeZonesAdmin",
        "Keen.Game2.Simulation.GameSystems.AdminTools.SpaceProbeAdmin"
    };
    private static readonly string[] AdminMethods = { "RequestSpawnEntity", "SetCreative" };
    private static readonly string[] AdminSignals =
    {
        "GameModeSessionComponent+CreativeGlobalProperty", "GameModeSessionComponent+IgnoreSafeZonesProperty",
        "GameModeSessionComponent+CreativeChangedSignal", "SafeZoneComponent+RadiusProperty", "SafeZoneComponent+ZoneNameProperty"
    };

    public static long Refused;

    public static bool IsAdminSignal(string name) => name != null && AdminSignals.Any(s => name.EndsWith(s, StringComparison.Ordinal));

    public static bool IsAdminStory(Type box) => box != null && Stories.GetOrAdd(box, Compute);

    private static bool Compute(Type box)
    {
        try
        {
            Type declaring = box.DeclaringType;
            if (declaring == null) return false;
            if (AdminTypes.Contains(declaring.FullName)) return true;
            int close = box.Name.IndexOf('>');
            string method = box.Name.StartsWith('<') && close > 1 ? box.Name.Substring(1, close - 1) : null;
            if (method == null) return false;
            if (AdminMethods.Contains(method)) return true;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            if (declaring.GetMethods(all).Where(m => m.Name == method).Any(m => m.CustomAttributes.Any(a => a.ConstructorArguments.Any(c => c.Value as string == "ValidateAdminAccess"))))
                return true;
            return CallsValidator(box.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), 1);
        }
        catch (Exception) { return false; }
    }

    // MoveTo.Server(CommonValidators.ValidateAdminAccess) loads the validator with ldftn, a lambda calls it.
    private static bool CallsValidator(MethodBase method, int depth)
    {
        byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
        if (il == null) return false;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            bool ldftn = il[i] == 0xFE && il[i + 1] == 0x06 && i + 5 < il.Length;
            bool call = il[i] == 0x28 || il[i] == 0x6F;
            if (!ldftn && !call) continue;
            int token = BitConverter.ToInt32(il, ldftn ? i + 2 : i + 1);
            MethodBase target;
            try { target = method.Module.ResolveMethod(token, method.DeclaringType?.GetGenericArguments(), null); }
            catch (Exception) { continue; }
            if (target?.Name == "ValidateAdminAccess") return true;
            if (ldftn && depth > 0 && target?.DeclaringType != null && target.DeclaringType.Name.StartsWith("<>c", StringComparison.Ordinal) && CallsValidator(target, depth - 1)) return true;
        }
        return false;
    }
}
