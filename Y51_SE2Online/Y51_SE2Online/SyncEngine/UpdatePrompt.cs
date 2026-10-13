using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Keen.Game2.Client.UI.Library;
using Keen.Game2.Client.UI.Library.Dialogs.OneOptionDialog;
using Keen.Game2.Client.UI.Library.Dialogs.ThreeOptionsDialog;
using Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog;
using Keen.VRage.Library.Localization;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Release check against the main site, and the native dialogs for new releases and version refusals.</summary>
internal static class UpdatePrompt
{
    public const string VersionUrl = "https://se2-online.yellow51.dev/version.php";
    private static bool _checked;
    private static volatile string _available;

    public static async Task<string> LatestAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Y51_SE2Online/" + MpPlugin.Version);
        using var json = JsonDocument.Parse(await http.GetStringAsync(VersionUrl));
        return json.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
    }

    /// <summary>Checks once per game start; the dialog waits for the main menu.</summary>
    public static void Tick(SharedUIComponent shared, bool atMainMenu)
    {
        if (!_checked)
        {
            _checked = true;
            Task.Run(async () =>
            {
                try
                {
                    string latest = await LatestAsync();
                    GameLog.Instance.Info($"update check: newest release {latest ?? "unknown"}, this game runs {MpPlugin.Version}");
                    if (VersionMismatch.IsNewer(latest, MpPlugin.Version) && latest != MpMenu.Settings.SkippedUpdate) _available = latest;
                }
                catch (Exception ex) { GameLog.Instance.Info("update check: " + ex.GetType().Name + ": " + ex.Message); }
            });
        }
        string available = _available;
        if (available == null || shared == null || !atMainMenu) return;
        _available = null;
        if (PluginPaths.PulsarHub)
        {
            shared.ShowDialog(new TwoOptionsDialogViewModel(new TwoOptionsDialogDefinition
            {
                Title = LocKey.FromString("Update available"),
                Content = LocKey.FromString($"Y51_SE2Online {available} is available. You have {MpPlugin.Version}.\n\nServers only admit players on their exact version. " + PulsarUpdateHint),
                ConfirmOption = LocKey.FromString("OK"),
                CancelOption = LocKey.FromString("Skip this version")
            }) { CancelAction = () => MpMenu.RememberSkippedUpdate(available) });
            return;
        }
        shared.ShowDialog(new ThreeOptionsDialogViewModel(new ThreeOptionsDialogDefinition
        {
            Title = LocKey.FromString("Update available"),
            Content = LocKey.FromString($"Y51_SE2Online {available} is available. You have {MpPlugin.Version}.\n\nServers only admit players on their exact version. Update now?"),
            ConfirmOption = LocKey.FromString("Update"),
            DefaultOption = LocKey.FromString("Skip this version"),
            CancelOption = LocKey.FromString("Later")
        })
        {
            ConfirmAction = OpenDownloadPage,
            DefaultAction = () => MpMenu.RememberSkippedUpdate(available)
        });
    }

    /// <summary>A server refused the join because its plugin version differs.</summary>
    public static void VersionRefused(SharedUIComponent shared, string hostVersion, string serverText)
    {
        string text = string.IsNullOrEmpty(hostVersion) ? serverText : VersionMismatch.Explain(hostVersion, MpPlugin.Version);
        if (VersionMismatch.IsNewer(hostVersion, MpPlugin.Version) && PluginPaths.PulsarHub)
            text += "\n\n" + PulsarUpdateHint;
        else if (VersionMismatch.IsNewer(hostVersion, MpPlugin.Version))
        {
            shared.ShowDialog(new TwoOptionsDialogViewModel(new TwoOptionsDialogDefinition
            {
                Title = LocKey.FromString("Different version"), Content = LocKey.FromString(text),
                ConfirmOption = LocKey.FromString("Get update"), CancelOption = LocKey.FromString("OK")
            }) { ConfirmAction = OpenDownloadPage });
            return;
        }
        shared.ShowDialog(new OneOptionDialogViewModel(new OneOptionDialogDefinition
        {
            Title = LocKey.FromString("Different version"), Content = LocKey.FromString(text), ConfirmOption = LocKey.FromString("OK")
        }));
    }

    // Hub releases go live only after the hub merges the new commit, which can lag the website.
    private const string PulsarUpdateHint = "Pulsar installs plugin updates when the game starts, so restart the game to update. If the version stays the same, the update is still waiting for approval on Pulsar's plugin hub.";

    // Downloads need a Steam sign-in on the site, so the browser does the update.
    public static void OpenDownloadPage()
    {
        try { Process.Start(new ProcessStartInfo(VersionMismatch.DownloadPage) { UseShellExecute = true }); }
        catch (Exception ex) { GameLog.Instance.Warn("update: could not open the download page: " + ex.Message); }
    }
}
