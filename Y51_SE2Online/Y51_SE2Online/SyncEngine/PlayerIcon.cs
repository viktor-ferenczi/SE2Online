using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Keen.VRage.Core.Render;
using Keen.VRage.Library.Filesystem;
using Keen.VRage.Library.Utils;

namespace Y51_SE2Online.SyncEngine;

/// <summary>The Engineer badge used for player name tags, shipped beside the plugin DLL.</summary>
internal static class PlayerIcon
{
    // HUD icons are DDS (R8G8B8A8_UNORM_SRGB, full mips) named .png, like the game's UI/HUD/GPS icons.
    public const string RelativePath = "Textures/GUI/Icons/SE2Char/Engineer.png";
    private const string AppDataPath = "Y51_SE2Online/Content/" + RelativePath;
    private static ResourceHandle<TextureAsset>? _icon;
    private static bool _tried;

    public static ResourceHandle<TextureAsset> Get(ResourceHandle<TextureAsset> fallback)
    {
        if (!_tried)
        {
            _tried = true;
            try { _icon = Load(); }
            catch (Exception ex) { GameLog.Instance.Warn($"nametags: player icon unavailable, using the GPS icon ({ex.GetType().Name}: {ex.Message})"); }
        }
        return _icon ?? fallback;
    }

    private static ResourceHandle<TextureAsset>? Load()
    {
        string source = PluginPaths.ContentDir is { } content ? Path.Combine(content, RelativePath) : null;
        if (source == null || !File.Exists(source))
        {
            GameLog.Instance.Warn("nametags: " + source + " is missing; player tags use the GPS icon");
            return null;
        }
        // The game's resource system only registers files under its own roots, so mirror the icon into the profile.
        var appData = Singleton<FileSystem>.Instance.GetAppDataFiles();
        byte[] bytes = File.ReadAllBytes(source);
        if (!appData.FileExists(AppDataPath) || !Same(appData, bytes))
        {
            appData.CreateDirectory(Path.GetDirectoryName(AppDataPath).Replace('\\', '/'));
            using var stream = appData.Open(AppDataPath, FileMode.Create, FileAccess.Write);
            stream.Write(bytes);
        }
        string target = appData.GetAbsolutePath(AppDataPath);
        var create = typeof(FileHandle).GetMethod("CreateNormalizedFileHandle", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMethodException(nameof(FileHandle), "CreateNormalizedFileHandle");
        var file = (FileHandle)create.Invoke(null, new object[] { RootPath.AppData, AppDataPath });
        var icon = (ResourceHandle<TextureAsset>)ResourceHandle.GetOrRegister(file);
        GameLog.Instance.Info("nametags: player tags use " + target + " (" + icon + ")");
        return icon;
    }

    private static bool Same(RootFileSystem files, byte[] bytes)
    {
        using var stream = files.OpenRead(AppDataPath);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.GetBuffer().AsSpan(0, (int)memory.Length).SequenceEqual(bytes);
    }
}
