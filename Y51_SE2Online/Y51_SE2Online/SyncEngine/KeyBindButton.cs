using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Shows a hotkey; click it and press a key (with modifiers) to rebind, Esc cancels.</summary>
internal sealed class KeyBindButton : Button
{
    private string _binding = "";

    // SE2's button styles select the exact Button type; without this the subclass gets no template and draws nothing.
    protected override System.Type StyleKeyOverride => typeof(Button);
    private bool _listening;

    public string Binding
    {
        get => _binding;
        set { _binding = value ?? ""; Content = _binding; }
    }

    public KeyBindButton()
    {
        FontSize = 24; MinHeight = 48; MinWidth = 220;
        Click += (_, _) => { _listening = true; Content = "Press a key..."; };
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        LostFocus += (_, _) => Cancel();
    }

    private void Cancel()
    {
        if (!_listening) return;
        _listening = false; Content = _binding;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_listening) return;
        e.Handled = true;
        if (e.Key == Key.Escape) { Cancel(); return; }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System) return;
        string text = Core.KeyBinding.Format(e.Key.ToString(), e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        if (text == null) { Content = "Unsupported key, try another"; return; }
        _listening = false;
        Binding = text;
    }
}
