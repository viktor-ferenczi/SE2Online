using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>The grid ownership controls, shown in the terminal's tab and in the pause menu's screen.</summary>
internal sealed class GridOwnershipView
{
    private readonly ListBox _grids = OnlineUi.List("OwnershipGridList"), _players = OnlineUi.List("OwnershipPlayerList");
    private readonly TextBox _search = OnlineUi.Input("Search grids", 100);
    private readonly TextBlock _status = OnlineUi.Text(""), _details = OnlineUi.Text("Select a grid.", "SubTitle", 28), _page = OnlineUi.Text(""), _empty = OnlineUi.Text("No grids available.");
    private readonly Button _private, _faction, _everyone, _claim, _transfer, _release, _previous, _next;
    private SocialSnapshot _snapshot = new();
    private string _signature, _playersSignature;
    private bool _closed;

    internal Control Navigation, Body, Actions, EmbeddedActions;
    internal TextBlock Status => _status;
    internal bool Closed { set => _closed = value; }

    public GridOwnershipView()
    {
        _grids.ItemTemplate = new FuncDataTemplate<SocialGrid>((g, _) => g == null ? null : OnlineUi.ListCells("*,220,150", g.Name, PlayerName(g.Owner), g.Sharing.ToString()));
        _players.ItemTemplate = new FuncDataTemplate<SocialPlayer>((p, _) => p == null ? null : OnlineUi.ListCells("*", p.Name));
        _search.Width = 480; _search.TextChanged += (_, _) => RefreshPage(0);
        _grids.SelectionChanged += (_, _) => RefreshActions(); _players.SelectionChanged += (_, _) => RefreshActions();
        _private = OnlineUi.Button("Private", () => Share(GridSharing.Private), "GridSharePrivate");
        _faction = OnlineUi.Button("Faction", () => Share(GridSharing.Faction), "GridShareFaction");
        _everyone = OnlineUi.Button("Everyone", () => Share(GridSharing.Everyone), "GridShareEveryone");
        _claim = OnlineUi.Button("Claim grid", () => Send(SocialAction.ClaimGrid), "ClaimGrid");
        _transfer = OnlineUi.Button("Transfer ownership", () => Confirm("Transfer ownership", "Transfer this grid to the selected player? Its sharing resets to Private.", SocialAction.TransferGrid), "TransferGrid");
        _release = OnlineUi.Button("Release ownership", () => Confirm("Release ownership", "Release this grid? Everyone can use it and any player can claim it.", SocialAction.ReleaseGrid), "ReleaseGrid");
        _previous = OnlineUi.Button("Previous", () => RefreshPage(_snapshot.Offset - 100), "PreviousOwnershipPage");
        _next = OnlineUi.Button("Next", () => RefreshPage(_snapshot.Offset + 100), "NextOwnershipPage");
        foreach (var button in new[] { _private, _faction, _everyone, _claim, _transfer, _release }) { button.FontSize = 20; button.Padding = new Thickness(12, 8); }
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        left.Children.Add(OnlineUi.Cells("*,220,150", "GRID", "OWNER", "SHARING")); Grid.SetRow(_grids, 1); left.Children.Add(_grids);
        Grid.SetRow(_empty, 1); _empty.IsHitTestVisible = false; left.Children.Add(_empty);
        var pages = OnlineUi.Row(_previous, _page, _next); Grid.SetRow(pages, 2); pages.Margin = new Thickness(0, 12, 0, 0); left.Children.Add(pages);
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto") };
        right.Children.Add(_details);
        var share = OnlineUi.Row(_private, _faction, _everyone); share.Margin = new Thickness(0, 16, 0, 14); Grid.SetRow(share, 1); right.Children.Add(share);
        var help = OnlineUi.Text("Private: only you. Faction: your faction members. Everyone: all players. Only the owner changes sharing or transfers ownership.", "SubtleNotice", 21); Grid.SetRow(help, 2); right.Children.Add(help);
        var label = OnlineUi.Text("TRANSFER TO PLAYER", "SubTitle"); label.Margin = new Thickness(0, 20, 0, 10); Grid.SetRow(label, 3); right.Children.Add(label);
        Grid.SetRow(_players, 4); right.Children.Add(_players);
        var transfer = OnlineUi.Row(_transfer, _claim); transfer.Margin = new Thickness(0, 14, 0, 10); Grid.SetRow(transfer, 5); right.Children.Add(transfer);
        Grid.SetRow(_release, 6); right.Children.Add(_release);
        Navigation = OnlineUi.Row(_search, OnlineUi.Button("Refresh", () => RefreshPage(_snapshot.Offset), "RefreshOwnership"));
        Body = OnlineUi.Columns(OnlineUi.Panel(left), OnlineUi.Panel(right), "*,24,640");
        Actions = OnlineUi.Row(OnlineUi.Button("Factions", SocialUi.OpenFactions, "OwnershipFactions"));
        EmbeddedActions = OnlineUi.Row();
        RefreshControls(); RefreshPage(0);
    }
    internal void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closed) RefreshControls(); });
    private void RefreshPage(int offset) => SocialUi.Send(new(SocialAction.Refresh, Name: _search.Text ?? "", Offset: Math.Max(0, offset)));
    private string PlayerName(long id) => id == 0 ? "Unowned" : PlayerIdentities.IsNpc(id) ? "NPC / Scenario" : _snapshot.Players.FirstOrDefault(p => p.Identity == id)?.Name ?? "Player " + id;
    private void Share(GridSharing sharing) => Send(SocialAction.ShareGrid, sharing);
    private SocialRequest Request(SocialAction action, GridSharing sharing = GridSharing.Private) => new(action, Player: (_players.SelectedItem as SocialPlayer)?.Identity ?? 0, Grid: (_grids.SelectedItem as SocialGrid)?.NetId ?? 0, Sharing: sharing);
    private void Send(SocialAction action, GridSharing sharing = GridSharing.Private) => SocialUi.Send(Request(action, sharing));
    private void Confirm(string title, string text, SocialAction action)
    {
        var request = Request(action); SocialUi.Confirm(title, text, () => SocialUi.Send(request));
    }
    private void RefreshControls()
    {
        _snapshot = SocialUi.Snapshot;
        _status.Text = _snapshot.Actor == 0 ? "Start or join a multiplayer world to manage grid ownership." : _snapshot.Message;
        string signature = _snapshot.Revision + ":" + string.Join("|", _snapshot.Grids.Select(g => g.NetId + ":" + g.Name + ":" + g.Owner + ":" + g.Sharing));
        if (_signature != signature)
        {
            uint selected = (_grids.SelectedItem as SocialGrid)?.NetId ?? 0;
            _signature = signature; _grids.ItemsSource = _snapshot.Grids; _grids.SelectedItem = _snapshot.Grids.FirstOrDefault(g => g.NetId == selected);
        }
        string players = _snapshot.Revision.ToString();
        if (_playersSignature != players)
        {
            long selected = (_players.SelectedItem as SocialPlayer)?.Identity ?? 0;
            _playersSignature = players; var rows = _snapshot.Players.Where(p => p.Identity != _snapshot.Actor).OrderBy(p => p.Name).ToArray();
            _players.ItemsSource = rows; _players.SelectedItem = rows.FirstOrDefault(p => p.Identity == selected);
        }
        _page.Text = _snapshot.TotalGrids == 0 ? "0 grids" : (_snapshot.Offset + 1) + " - " + (_snapshot.Offset + _snapshot.Grids.Length) + " / " + _snapshot.TotalGrids;
        _previous.IsEnabled = _snapshot.Actor != 0 && _snapshot.Offset > 0; _next.IsEnabled = _snapshot.Actor != 0 && _snapshot.Offset + _snapshot.Grids.Length < _snapshot.TotalGrids;
        _empty.IsVisible = _snapshot.Grids.Length == 0; RefreshActions();
    }
    private void RefreshActions()
    {
        if (_claim == null) return;
        var grid = _grids.SelectedItem as SocialGrid;
        bool owner = grid != null && _snapshot.Actor != 0 && grid.Owner == _snapshot.Actor;
        _private.IsEnabled = _everyone.IsEnabled = owner;
        _faction.IsEnabled = owner && _snapshot.Factions.Any(f => f.Members.Contains(_snapshot.Actor));
        _claim.IsEnabled = grid != null && _snapshot.Actor != 0 && grid.Owner == 0;
        _transfer.IsEnabled = owner && _players.SelectedItem is SocialPlayer;
        _release.IsEnabled = owner;
        _details.Text = grid == null ? "Select a grid to view ownership and sharing." : grid.Name + "\nOwner: " + PlayerName(grid.Owner) + "\nSharing: " + grid.Sharing;
    }
}

public sealed class GridOwnershipScreen : ScreenView
{
    private readonly GridOwnershipView _view = new();
    private SocialScreenViewModel _model;

    public GridOwnershipScreen()
    {
        Content = OnlineUi.Shell(this, "GRID OWNERSHIP", _view.Navigation, _view.Status, _view.Body, _view.Actions);
        OnDisposing += _ => { _view.Closed = true; if (_model != null) _model.Refresh -= _view.QueueRefresh; };
    }
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= _view.QueueRefresh;
        _model = DataContext as SocialScreenViewModel; if (_model != null) _model.Refresh += _view.QueueRefresh;
    }
}
