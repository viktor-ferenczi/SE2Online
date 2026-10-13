using System;
using System.Linq;
using Steamworks;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Steam;

/// <summary>
/// Rich presence while playing on someone else's world. SE2 ships no Steam display tokens, so the friends list's
/// "View game info" status line is the only free text; the group keys put friends on the same server together.
/// </summary>
internal static class JoinPresence
{
    private static string _shown;
    private static long _nextMs;

    public static void Tick(bool playing)
    {
        long now = MpClock.NowMs();
        if (now < _nextMs || !MpPlugin.SteamAvailable) return;
        _nextMs = now + 5000;
        var options = MpPlugin.Controller?.Current;
        if (!playing || options?.Join != true)
        {
            if (_shown != null) { foreach (string key in new[] { "status", "connect", "steam_player_group", "steam_player_group_size" }) SteamFriends.SetRichPresence(key, ""); _shown = null; }
            return;
        }
        var state = AdminAccess.State;
        int players = Math.Max(1, state.Players.Count(p => p.Connected));
        string server = AdminAccess.ServerName;
        string status = HostPresence.Clean($"SE2 Online: playing on {server} ({players}/{(state.MaxPlayers > 0 ? state.MaxPlayers : options.MaxPlayers)})", 120);
        string connect = options.JoinIp != null ? ConnectString.BuildAddress(options.JoinIp.ToString()) : options.JoinSteamId != 0 ? ConnectString.Build(options.JoinSteamId, options.VirtualPort) : "";
        string shown = status + "|" + connect;
        if (shown == _shown) return;
        _shown = shown;
        SteamFriends.SetRichPresence("status", status);
        SteamFriends.SetRichPresence("connect", connect);
        SteamFriends.SetRichPresence("steam_player_group", options.JoinIp?.ToString() ?? options.JoinSteamId.ToString());
        SteamFriends.SetRichPresence("steam_player_group_size", players.ToString());
    }
}
