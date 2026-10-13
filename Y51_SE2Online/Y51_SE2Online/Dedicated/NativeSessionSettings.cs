using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Keen.Game2.Game.GameComponents;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Builders;
using Keen.VRage.Library.Serialization;
using HarmonyLib;

namespace Y51_SE2Online.Dedicated;

internal static class NativeSessionSettings
{
    private static readonly MethodInfo Deserialize = typeof(SerializationHelper).GetMethod(nameof(SerializationHelper.DeserializeFromJson));
    private static readonly MethodInfo Serialize = typeof(SerializationHelper).GetMethod(nameof(SerializationHelper.SerializeToJson));
    private static readonly Dictionary<string, object> Schema = new();
    private static readonly HashSet<string> Seen = new();

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(GameWorldBuilder), nameof(GameWorldBuilder.CreateBaseGame)),
            postfix: new HarmonyMethod(typeof(NativeSessionSettings), nameof(BaseGamePostfix)));
        harmony.Patch(AccessTools.Method(typeof(SessionComponentsSnapshot), nameof(SessionComponentsSnapshot.Apply)),
            prefix: new HarmonyMethod(typeof(NativeSessionSettings), nameof(SnapshotPrefix)));
    }

    internal static void Validate(Dictionary<string, Dictionary<string, JsonElement>> settings)
    {
        foreach (var entry in settings)
        {
            var type = FindBuilder(entry.Key);
            object builder = Activator.CreateInstance(type) ?? throw new ArgumentException("Cannot construct " + entry.Key);
            if (entry.Value == null) throw new ArgumentException(entry.Key + " settings cannot be null.");
            foreach (var value in entry.Value) Write(builder, value.Key.Split('.'), 0, value.Value, apply: false);
        }
    }

    private static Type FindBuilder(string name)
    {
        var matches = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).Where(t => t != null).ToArray();
        if (matches.Length != 1 || !typeof(IAccessible).IsAssignableFrom(matches[0]) || !typeof(ISerializable).IsAssignableFrom(matches[0]))
            throw new ArgumentException("SessionSettings key must be an exact native object builder full type name: " + name);
        return matches[0];
    }

    private static void BaseGamePostfix(bool isServer, WorldSessionComponentObjectBuilder __result)
    {
        if (!DedicatedRuntime.Active || !isServer) return;
        var previous = __result.SessionBuilderInspector;
        __result.SessionBuilderInspector = builder =>
        {
            previous?.Invoke(builder);
            foreach (var component in builder.SessionComponents.Components) Apply(component.ObjectBuilder);
        };
    }

    private static void SnapshotPrefix(SessionComponentsSnapshot __instance)
    {
        if (!DedicatedRuntime.Active) return;
        foreach (var component in __instance.Components ?? Array.Empty<SessionComponentsSnapshot.ComponentInfo>()) Apply(component.ObjectBuilder);
    }

    private static void Apply(object builder)
    {
        if (builder is not IAccessible accessible) return;
        string name = builder.GetType().FullName;
        lock (Seen) Seen.Add(name);
        if (DedicatedRuntime.Config.SessionSettings.TryGetValue(name, out var settings))
            foreach (var entry in settings) Write(builder, entry.Key.Split('.'), 0, entry.Value, apply: true);
        var schema = Describe(accessible);
        lock (Schema) Schema[name] = schema;
    }

    internal static Dictionary<string, object> Describe(IAccessible accessible)
    {
        var schema = new Dictionary<string, object>();
        foreach (var pair in accessible.GetTypeInfo().Members)
        {
            var member = pair.Value;
            if (!member.CanRead || !member.IsSerializable(SerializerFormat.Json)) continue;
            object value;
            try
            {
                string serialized = (string)Serialize.MakeGenericMethod(member.Type).Invoke(null, new[] { member.Get(accessible) });
                value = JsonSerializer.Deserialize<JsonElement>(serialized);
            }
            catch (Exception ex) { value = new { SerializationError = ex.GetBaseException().Message }; }
            schema[pair.Key] = new { Type = member.Type.FullName, Writable = member.CanWrite, Value = value };
        }
        return schema;
    }

    internal static void Write(object parent, string[] path, int index, JsonElement value, bool apply)
    {
        if (parent is not IAccessible accessible || index >= path.Length || string.IsNullOrWhiteSpace(path[index]))
            throw new ArgumentException("Invalid native setting path: " + string.Join(".", path));
        var info = accessible.GetTypeInfo();
        if (!info.Members.TryGetValue(path[index], out var member) || !member.CanWrite || !member.IsSerializable(SerializerFormat.Json))
            throw new ArgumentException(info.Type.FullName + "." + path[index] + " is not a writable native JSON setting.");
        if (index == path.Length - 1)
        {
            object parsed;
            try { parsed = Deserialize.MakeGenericMethod(member.Type).Invoke(null, new object[] { value.GetRawText() }); }
            catch (Exception ex) { throw new ArgumentException(info.Type.FullName + "." + path[index] + ": " + ex.GetBaseException().Message, ex); }
            if (apply) member.Set(parent, parsed);
            return;
        }
        var child = member.Get(parent) ?? Activator.CreateInstance(member.Type);
        if (child == null) throw new ArgumentException("Cannot traverse native setting " + string.Join(".", path));
        Write(child, path, index + 1, value, apply);
        if (apply) member.Set(parent, child);
    }

    internal static void Finish()
    {
        lock (Seen)
        {
            var missing = DedicatedRuntime.Config.SessionSettings.Keys.Where(k => !Seen.Contains(k)).ToArray();
            if (missing.Length != 0) throw new ArgumentException("Configured native components are absent from this world: " + string.Join(", ", missing));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(DedicatedRuntime.Config.DumpPath));
        lock (Schema) File.WriteAllText(DedicatedRuntime.Config.DumpPath, JsonSerializer.Serialize(Schema, DedicatedConfig.Json));
    }
}
