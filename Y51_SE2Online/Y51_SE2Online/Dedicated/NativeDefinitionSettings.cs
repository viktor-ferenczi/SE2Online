using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Keen.Game2.Game.SessionComponents;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Utils;

namespace Y51_SE2Online.Dedicated;

internal static class NativeDefinitionSettings
{
    private sealed record Selector(string Key, Type Type, Guid? Id, Dictionary<string, JsonElement> Values);
    private static readonly HashSet<string> Applied = new();
    private static Selector[] _selectors = Array.Empty<Selector>();
    private static bool _dumpWritten;

    internal static void Validate(Dictionary<string, Dictionary<string, JsonElement>> settings)
    {
        foreach (var selector in Selectors(settings))
        {
            var runtime = Activator.CreateInstance(selector.Type);
            var init = Initializer(selector.Type);
            var builder = Activator.CreateInstance(init.GetParameters()[0].ParameterType);
            foreach (var entry in selector.Values)
            {
                if (entry.Key.Split('.')[0] == nameof(Definition.Guid))
                    throw new ArgumentException("Native definition registry identity Guid cannot be changed: " + selector.Key);
                NativeSessionSettings.Write(builder, entry.Key.Split('.'), 0, entry.Value, false);
                NativeSessionSettings.Write(runtime, entry.Key.Split('.'), 0, entry.Value, false);
            }
        }
    }

    internal static void Install(Harmony harmony)
    {
        _selectors = Selectors(DedicatedRuntime.Config.DefinitionSettings).ToArray();
        lock (Applied) Applied.Clear();
        _dumpWritten = false;
        foreach (var type in _selectors.Select(s => s.Type).Distinct())
            harmony.Patch(Initializer(type), prefix: new HarmonyMethod(typeof(NativeDefinitionSettings), nameof(InitPrefix)));
        harmony.Patch(AccessTools.Method(typeof(WorldSessionComponent), "InitAsync"),
            prefix: new HarmonyMethod(typeof(NativeDefinitionSettings), nameof(WorldInitPrefix)));
    }

    private static IEnumerable<Selector> Selectors(Dictionary<string, Dictionary<string, JsonElement>> settings)
    {
        foreach (var entry in settings)
        {
            string[] parts = entry.Key.Split('#');
            if (parts.Length > 2 || string.IsNullOrWhiteSpace(parts[0])) throw new ArgumentException("Invalid native definition selector: " + entry.Key);
            Guid? id = null;
            if (parts.Length == 2)
            {
                if (!Guid.TryParse(parts[1], out var parsed)) throw new ArgumentException("Invalid native definition GUID selector: " + entry.Key);
                id = parsed;
            }
            var types = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(parts[0], false)).Where(t => t != null).ToArray();
            if (types.Length != 1 || !typeof(Definition).IsAssignableFrom(types[0]) || types[0].IsAbstract)
                throw new ArgumentException("DefinitionSettings requires an exact concrete native definition type name: " + entry.Key);
            if (entry.Value == null) throw new ArgumentException("Definition settings cannot be null: " + entry.Key);
            yield return new Selector(entry.Key, types[0], id, entry.Value);
        }
    }

    private static MethodInfo Initializer(Type type)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .SingleOrDefault(m => m.Name == "Init" && m.GetParameters().Length == 1
                && typeof(DefinitionObjectBuilder).IsAssignableFrom(m.GetParameters()[0].ParameterType))
            ?? throw new ArgumentException("Native definition has no unique typed initializer: " + type.FullName);
    }

    private static void InitPrefix(object __instance, object[] __args)
    {
        if (!DedicatedRuntime.Active || __args[0] is not DefinitionObjectBuilder builder) return;
        foreach (var selector in _selectors.Where(s => s.Type == __instance.GetType() && (!s.Id.HasValue || s.Id == builder.Guid)).OrderBy(s => s.Id.HasValue))
        {
            foreach (var entry in selector.Values) NativeSessionSettings.Write(builder, entry.Key.Split('.'), 0, entry.Value, true);
            lock (Applied) Applied.Add(selector.Key);
        }
    }

    private static void WorldInitPrefix(WorldSessionComponentObjectBuilder ob)
    {
        if (!DedicatedRuntime.Active || !ob.IsServer) return;
        var definitions = new List<Definition>();
        foreach (var definition in Singleton<DefinitionManager>.Instance.GetDefinitionsOfTypeAndDescendants<Definition>()) definitions.Add(definition);
        foreach (var selector in _selectors.OrderBy(s => s.Id.HasValue))
        {
            var selected = definitions.Where(d => d.GetType() == selector.Type && (!selector.Id.HasValue || d.Guid == selector.Id)).ToArray();
            if (selected.Length == 0) throw new ArgumentException("Configured native definition is absent from loaded game/mod content: " + selector.Key);
            foreach (var definition in selected)
                foreach (var entry in selector.Values) NativeSessionSettings.Write(definition, entry.Key.Split('.'), 0, entry.Value, true);
            lock (Applied) Applied.Add(selector.Key);
        }
    }

    internal static void Finish()
    {
        lock (Applied)
        {
            string[] missing = _selectors.Where(s => !Applied.Contains(s.Key)).Select(s => s.Key).ToArray();
            if (missing.Length != 0) throw new ArgumentException("Native definition overrides were not applied: " + string.Join(", ", missing));
        }
        if (_dumpWritten) return;
        var schema = new SortedDictionary<string, object>();
        foreach (var definition in Singleton<DefinitionManager>.Instance.GetDefinitionsOfTypeAndDescendants<Definition>())
        {
            var accessible = (IAccessible)definition;
            var fields = NativeSessionSettings.Describe(accessible);
            fields.Remove(nameof(Definition.Guid));
            schema[definition.GetType().FullName + "#" + definition.Guid] = new { definition.DebugName, Settings = fields };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(DedicatedRuntime.Config.DefinitionDumpPath));
        File.WriteAllText(DedicatedRuntime.Config.DefinitionDumpPath, JsonSerializer.Serialize(schema, DedicatedConfig.Json));
        _dumpWritten = true;
    }
}
