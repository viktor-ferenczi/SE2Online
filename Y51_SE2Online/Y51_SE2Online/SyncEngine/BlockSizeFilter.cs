using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Block type names read "{Loc:...} {BLOCK_SIZE:guid}". The control panel evaluates them on the server
/// (ControlPanelEntityModelServer), and a headless dedicated server has no BlockSizeTextFilterComponent, so the raw
/// token reached every joiner. Hosts get this filter when the game's own is missing; joiners use it while joined
/// (SE2's own size table can be empty there). SE2's AddFilter throws on a duplicate key, so ours steps aside first.
/// </summary>
internal sealed class BlockSizeFilter : ITextFilter
{
    private static readonly StringId Keyword = StringId.Get("BLOCK_SIZE");
    private static readonly BlockSizeFilter Instance = new();
    private static readonly FieldInfo Filters = typeof(TextManager).GetField("_globalFilters", BindingFlags.Instance | BindingFlags.NonPublic);
    private static bool _registered;

    internal static void Install(HarmonyLib.Harmony harmony)
        => harmony.Patch(HarmonyLib.AccessTools.Method(typeof(TextManager), nameof(TextManager.AddFilter)), prefix: new HarmonyLib.HarmonyMethod(typeof(BlockSizeFilter), nameof(AddFilterPrefix)));

    private static void AddFilterPrefix(StringId keyword)
    {
        if (keyword == Keyword && Filters?.GetValue(Singleton<TextManager>.Instance) is IDictionary filters && filters[Keyword] == Instance) filters.Remove(Keyword);
    }

    /// <summary>Hosts: only when the game registered no BLOCK_SIZE filter of its own.</summary>
    public static void EnsureHost()
    {
        var manager = Singleton<TextManager>.Instance;
        if (manager == null || Filters?.GetValue(manager) is not IDictionary filters || filters.Contains(Keyword)) return;
        filters[Keyword] = Instance;
    }

    public static void Sync(bool joined)
    {
        var manager = Singleton<TextManager>.Instance;
        if (manager == null || Filters?.GetValue(manager) is not IDictionary filters) return;
        if (joined && filters[Keyword] != Instance)
        {
            // Replaces SE2's own filter too: on a joiner its size table can be empty and the lookup throws.
            filters[Keyword] = Instance;
            _registered = true;
        }
        else if (!joined && _registered)
        {
            if (filters[Keyword] == Instance && HostWorld.Current == null) manager.RemoveFilter(Keyword);
            _registered = false;
        }
    }

    public bool FormatText(StringBuilder text, ReadOnlySpan<char> formatString, ITextFilter? _)
    {
        if (!Guid.TryParse(formatString, out var guid) || !Singleton<DefinitionManager>.Instance.TryGetDefinition(guid, out CubeBlockDefinition block)) return false;
        text.Append(block.DisplaySize.ToString(CultureInfo.InvariantCulture)).Append(' ').Append('m');
        return true;
    }
}
