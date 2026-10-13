using System;
using System.IO;

namespace Y51_SE2Online;

/// <summary>
/// Where the plugin keeps its files. Under Pulsar the DLL sits in a cache folder that is wiped whenever the
/// plugin is rebuilt or updated, so settings go to Pulsar's per-plugin data folder instead.
/// </summary>
internal static class PluginPaths
{
	private static readonly string DllDir = string.IsNullOrEmpty(typeof(PluginPaths).Assembly.Location) ? null : Path.GetDirectoryName(typeof(PluginPaths).Assembly.Location);

	private static string _dataDir;

	public static bool InMemory => DllDir == null;

	/// <summary>Installed from Pulsar's plugin hub: Pulsar compiles it into Modern/GitHub/&lt;plugin&gt;/Bin and updates it itself.</summary>
	public static bool PulsarHub => MpPlugin.PulsarDataDir != null && DllDir != null
		&& string.Equals(Directory.GetParent(DllDir)?.Parent?.Name, "GitHub", StringComparison.OrdinalIgnoreCase);

	public static string DataDir => _dataDir ??= MpPlugin.PulsarDataDir ?? DllDir ?? Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Y51_SE2Online")).FullName;

	/// <summary>The shipped Content folder: Pulsar's copy of the registration's AssetFolder, or beside the DLL.</summary>
	/// <remarks>A Pulsar Local install resolves the AssetFolder path against the DLL folder, where it does not exist.</remarks>
	public static string ContentDir => Directory.Exists(MpPlugin.PulsarAssetDir) ? MpPlugin.PulsarAssetDir : DllDir == null ? null : Path.Combine(DllDir, "Content");

	public static string Data(string fileName)
	{
		string path = Path.Combine(DataDir, fileName);
		// 0.13.4 Pulsar Local installs kept their files beside the DLL.
		string old = DllDir == null ? null : Path.Combine(DllDir, fileName);
		if (old != null && old != path && !File.Exists(path) && File.Exists(old))
		{
			try { File.Copy(old, path); }
			catch (IOException) { }
		}
		return path;
	}
}
