using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keen.VRage.Core.Worlds;
using Keen.VRage.Library.Serialization;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Dedicated;

public sealed class DedicatedConfig
{
    public string SaveId { get; set; } = "";
    public string StartContent { get; set; } = "";
    public string WorldName { get; set; } = "Y51 Dedicated World";
    public bool? EnableAutosave { get; set; } = true;
    public bool? PlayMission { get; set; }
    public int SaveIntervalSeconds { get; set; } = 300;
    public bool SaveOnExit { get; set; } = true;
    public JsonElement? OverrideModList { get; set; }
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public OnlineSettings Network { get; set; } = new OnlineSettings
    {
        FriendsOnly = false, Advertise = false, SessionName = "Y51 Dedicated Server", AllowDirectIp = true
    };
    public Dictionary<string, Dictionary<string, JsonElement>> SessionSettings { get; set; } = new();
    public Dictionary<string, Dictionary<string, JsonElement>> DefinitionSettings { get; set; } = new();
    public string SettingsDumpFile { get; set; } = "native-settings.json";
    public string DefinitionDumpFile { get; set; } = "native-definitions.json";
    public string ResumeFile { get; set; } = "dedicated-state.json";
    public string CommandFile { get; set; } = "dedicated-command.txt";
    [JsonIgnore] public string ConfigPath { get; private set; }
    [JsonIgnore] public string Directory => Path.GetDirectoryName(ConfigPath);
    [JsonIgnore] public string DumpPath => Path.GetFullPath(Path.Combine(Directory, SettingsDumpFile));
    [JsonIgnore] public string DefinitionDumpPath => Path.GetFullPath(Path.Combine(Directory, DefinitionDumpFile));
    [JsonIgnore] public string ResumePath => Path.GetFullPath(Path.Combine(Directory, ResumeFile));
    [JsonIgnore] public string CommandPath => Path.GetFullPath(Path.Combine(Directory, CommandFile));

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true, IncludeFields = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static DedicatedConfig Read(string path)
    {
        path = Path.GetFullPath(path);
        var config = JsonSerializer.Deserialize<DedicatedConfig>(File.ReadAllText(path), Json)
            ?? throw new ArgumentException("Dedicated configuration is empty.");
        config.ConfigPath = path;
        if (!string.IsNullOrWhiteSpace(config.SaveId) && !string.IsNullOrWhiteSpace(config.StartContent))
            throw new ArgumentException("Choose SaveId or StartContent, not both.");
        if (string.IsNullOrWhiteSpace(config.WorldName) || config.WorldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("WorldName must be a valid native save name.");
        if (config.SaveIntervalSeconds < 0 || (config.SaveIntervalSeconds > 0 && config.SaveIntervalSeconds < 30))
            throw new ArgumentException("SaveIntervalSeconds must be 0 or at least 30.");
        if (config.Network == null) throw new ArgumentException("Network must be configured.");
        if (config.Network.FriendsOnly) throw new ArgumentException("Dedicated Steam servers have no Steam friends. Set Network.FriendsOnly to false.");
        if (!config.Network.AllowDirectIp) throw new ArgumentException("Dedicated servers require Network.AllowDirectIp=true for Steam Game Server IP networking.");
        if (config.Network.Advertise && string.IsNullOrWhiteSpace(config.Network.PublicAddress)) throw new ArgumentException("Set Network.PublicAddress to the public IP or hostname and port before advertising a dedicated server.");
        string error = config.Network.Validate();
        if (error != null) throw new ArgumentException(error);
        if (config.SessionSettings == null) throw new ArgumentException("SessionSettings cannot be null.");
        if (config.DefinitionSettings == null) throw new ArgumentException("DefinitionSettings cannot be null.");
        if (string.IsNullOrWhiteSpace(config.SettingsDumpFile)) throw new ArgumentException("SettingsDumpFile cannot be empty.");
        if (string.IsNullOrWhiteSpace(config.DefinitionDumpFile)) throw new ArgumentException("DefinitionDumpFile cannot be empty.");
        if (string.IsNullOrWhiteSpace(config.ResumeFile) || string.IsNullOrWhiteSpace(config.CommandFile))
            throw new ArgumentException("ResumeFile and CommandFile cannot be empty.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            config.ConfigPath, config.DumpPath, config.DefinitionDumpPath, config.ResumePath, config.CommandPath,
            Path.Combine(config.Directory, "dedicated-status.json"),
            Path.Combine(config.Directory, "dedicated-console.jsonl")
        };
        if (paths.Count != 7) throw new ArgumentException("Configuration, schema, resume, command, status and console log files must have distinct paths.");
        if (config.OverrideModList.HasValue)
            SerializationHelper.DeserializeFromJson<ModList>(config.OverrideModList.Value.GetRawText());
        NativeSessionSettings.Validate(config.SessionSettings);
        NativeDefinitionSettings.Validate(config.DefinitionSettings);
        return config;
    }
}
