using System;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.Interaction;
using Keen.Game2.Client.UI.InGame;
using Keen.Game2.Client.UI.Shared.SystemNotification;
using Keen.VRage.DCS.Components;
using Keen.VRage.Game.Client.Render;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Render.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Trying to use a block the host would refuse flashes it red and shows a "Permission Denied" toast.</summary>
internal static class PermissionFeedback
{
    private const long HighlightMs = 1500;
    private static readonly HighlightProperties Red = new(new ColorSRGB(255, 32, 32, 255), 0.3f);
    private static IDisposable _highlight;
    private static Entity _highlighted;
    private static long _clearAt;
    private static IObservableDisposable _toast;

    internal static void Install(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(PermissionFeedback), nameof(InteractPrefix));
        harmony.Patch(AccessTools.Method(typeof(UseAction), "InteractPrimary"), prefix);
        harmony.Patch(AccessTools.Method(typeof(UseAction), "InteractSecondary"), prefix);
    }

    /// <summary>The local player's identity in a hosted or joined world, else 0.</summary>
    internal static long LocalActor => AdminAccess.Hosting ? PlayerIdentities.HotfixIdentity : AdminAccess.Joined ? AdminAccess.Client.IdentityId : 0;

    // Feedback only: the host still decides, so a stale prediction never blocks a use that would succeed.
    private static void InteractPrefix(UseAction __instance, bool value)
    {
        try
        {
            long actor = LocalActor;
            if (!value || actor == 0 || __instance.Entity == null || WorldSocial.Access(__instance.Entity, actor) != false) return;
            Deny(__instance.Entity);
        }
        catch (Exception ex) { GameLog.Instance.WarnOnce("permission-feedback", "permissions: denied feedback failed: " + ex.Message); }
    }

    internal static void Deny(Entity entity)
    {
        long now = MpClock.NowMs();
        if (_highlighted != entity || _highlight == null)
        {
            _highlight?.Dispose();
            _highlight = entity.SingleOrDefault<IHighlightable>()?.AddHighlight(Red, null);
            _highlighted = entity;
        }
        _clearAt = now + HighlightMs;
        if (_toast != null) return;
        var ui = MpPlugin.App?.ClientSession?.SessionComponents.TryGet<SessionInGameUISessionComponent>();
        _toast = ui?.DisplayNotification(new NotificationViewModel("Permission Denied", "You don't have access to this block!", TimeSpan.FromSeconds(3)));
        if (_toast != null) _toast.OnDisposed += _ => _toast = null;
    }

    internal static void Tick(long now)
    {
        if (_highlight == null || now < _clearAt) return;
        _highlight.Dispose();
        _highlight = null;
        _highlighted = null;
    }
}
