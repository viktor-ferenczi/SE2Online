using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Keen.Game2.Client.UI.MapScreen;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;
using Keen.VRage.UI.Screens;
using Keen.VRage.UI.Shared.Controls.BeveledBorder;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>A Share button in the GPS marker editor header (beside copy/paste/delete) that opens <see cref="GpsShareScreen"/> for the selected marker.</summary>
internal static class GpsShareUi
{
    internal sealed record Target(ushort ClientId, string Name) { public override string ToString() => Name; }

    private static readonly ConditionalWeakTable<GPSView, Action> Bound = new();

    // A class handler, not a constructor patch: GPSView's constructor is a one-line InitializeComponent call that the
    // precompiled caller inlines, so a Harmony patch on it never runs.
    internal static void Install() => Control.LoadedEvent.AddClassHandler<GPSView>((view, _) => Loaded(view));

    private static void Loaded(GPSView view)
    {
        try
        {
            if (Bound.TryGetValue(view, out var bind)) { bind(); return; }
            if (Header(view) is not { } header)
            {
                GameLog.Instance.Warn("gps share: the GPS marker editor header was not found; share is unavailable");
                return;
            }
            bind = Create(view, header);
            Bound.Add(view, bind);
            GameLog.Instance.Info("gps share: share button added to the GPS marker editor");
            bind();
        }
        catch (Exception ex) { GameLog.Instance.Warn("gps share: could not add the share button: " + ex.Message); }
    }

    private static StackPanel Header(GPSView view) =>
        (view.Content as Grid)?.Children.OfType<BeveledBorder>().Where(b => Grid.GetRow(b) == 2 && Grid.GetColumn(b) == 1)
            .Select(b => b.Child as Grid).Where(g => g != null)
            .SelectMany(g => g.Children.OfType<StackPanel>()).FirstOrDefault(s => Grid.GetColumn(s) == 1 && s.Orientation == Orientation.Horizontal);

    private static Action Create(GPSView view, StackPanel header)
    {
        GPSViewModel model = null;
        // The map takes clicks on anything outside the GPS panel as a map click and clears the selection, so the marker
        // is taken when the button is pressed and the player list opens as its own screen.
        var share = new Button { Content = "Share", FontSize = 18, Padding = new Thickness(12, 4), MinWidth = 0, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(1) };
        ToolTip.SetTip(share, "Send this GPS marker to another player");
        share.Click += (_, _) =>
        {
            if (model?.SelectedMarker?.GPSMarker is { } marker) GpsShareScreen.Open(marker);
        };
        header.Children.Insert(0, share);
        view.DataContextChanged += (_, _) => Bind();
        view.DetachedFromVisualTree += (_, _) => model = null;
        void Bind()
        {
            model = view.DataContext as GPSViewModel;
            share.IsVisible = AdminAccess.InSession;
        }
        return Bind;
    }

    internal static Target[] Targets()
    {
        var table = AdminAccess.Hosting ? AdminAccess.Host.PlayerTable() : AdminAccess.Client?.Players.ToList();
        ushort self = AdminAccess.Hosting ? SyncProtocol.HostClientId : AdminAccess.Client?.ClientId ?? 0;
        if (table == null) return Array.Empty<Target>();
        return table.Where(p => p.Connected && p.ClientId != self && p.ClientId != 0)
            .Select(p => new Target(p.ClientId, string.IsNullOrWhiteSpace(p.Name) ? "Player " + p.ClientId : p.Name))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static void Send(GPSMarker marker, Target target)
    {
        var icons = Singleton<DefinitionManager>.Instance.GetConfiguration<GPSMarkersConfiguration>().Icons;
        var share = new GpsShare(target.ClientId, marker.Name ?? "", marker.Position.X, marker.Position.Y, marker.Position.Z,
            marker.Color.R, marker.Color.G, marker.Color.B, icons.IndexOf(marker.Icon));
        if (AdminAccess.Hosting) AdminAccess.Host.LocalGpsShare(share);
        else AdminAccess.Client?.SendGpsShare(share);
    }
}

/// <summary>Pick an online player to send one GPS marker to.</summary>
public sealed class GpsShareScreen : ScreenView
{
    private static GPSMarker _pending;

    private readonly ListBox _players = OnlineUi.List("GpsSharePlayers");
    private readonly TextBlock _status = OnlineUi.Text("");
    private readonly TextBlock _empty = OnlineUi.Text("No other players are online.");
    private readonly Button _share;
    private readonly GPSMarker _marker;
    private SocialScreenViewModel _model;
    private string _signature = "";

    internal static void Open(GPSMarker marker)
    {
        var shared = JoinStatusUi.FindShared(MpPlugin.App);
        if (shared == null) return;
        _pending = marker;
        shared.CreateScreen<GpsShareScreen>(new SocialScreenViewModel(), true, ScreenLayer.Top);
    }

    public GpsShareScreen()
    {
        _marker = _pending;
        _pending = null;
        _players.ItemTemplate = new FuncDataTemplate<GpsShareUi.Target>((t, _) => t == null ? null : OnlineUi.ListCells("*", t.Name));
        _players.SelectionChanged += (_, _) => _share.IsEnabled = _players.SelectedItem != null;
        _players.DoubleTapped += (_, _) => Share();
        _share = OnlineUi.Button("Share", Share, "GpsShareSend", true);
        _share.IsEnabled = false;
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(OnlineUi.Cells("*", "PLAYER"));
        Grid.SetRow(_players, 1); body.Children.Add(_players);
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.IsHitTestVisible = false;
        Grid.SetRow(_empty, 1); body.Children.Add(_empty);
        _status.Text = _marker == null ? "No marker selected." : $"Send '{_marker.Name}' to:";
        Content = OnlineUi.Shell(this, "SHARE GPS MARKER", OnlineUi.Row(), _status, OnlineUi.Panel(body), OnlineUi.Row(_share));
        Refresh();
    }

    private void Share()
    {
        if (_marker == null || _players.SelectedItem is not GpsShareUi.Target target) return;
        try { GpsShareUi.Send(_marker, target); }
        catch (Exception ex) { GameLog.Instance.Warn("gps share: sending failed: " + ex.Message); _status.Text = "Sharing failed: " + ex.Message; return; }
        _model?.Close();
    }

    private void Refresh()
    {
        var targets = GpsShareUi.Targets();
        string signature = string.Join("|", targets.Select(t => t.ClientId + ":" + t.Name));
        if (signature == _signature) return;
        _signature = signature;
        var keep = (_players.SelectedItem as GpsShareUi.Target)?.ClientId;
        _players.ItemsSource = targets;
        _players.SelectedItem = targets.FirstOrDefault(t => t.ClientId == keep);
        _empty.IsVisible = targets.Length == 0;
        _share.IsEnabled = _players.SelectedItem != null;
    }

    private void QueueRefresh() => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= QueueRefresh;
        _model = DataContext as SocialScreenViewModel;
        if (_model != null) _model.Refresh += QueueRefresh;
    }
}
