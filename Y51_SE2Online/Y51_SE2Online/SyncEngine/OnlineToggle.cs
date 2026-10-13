using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NativeSwitch = Keen.Game2.Client.UI.Library.Controls.ContentControls.Switch;

namespace Y51_SE2Online.SyncEngine;

internal sealed class OnlineToggle : Grid
{
    private readonly NativeSwitch _switch = new() { MinHeight = 50, HorizontalAlignment = HorizontalAlignment.Stretch };
    public event EventHandler IsCheckedChanged;
    public bool IsChecked { get => _switch.IsChecked; set => _switch.IsChecked = value; }
    public OnlineToggle(string label)
    {
        ColumnDefinitions = new ColumnDefinitions("*,24,480");
        MinHeight = 54;
        Children.Add(OnlineUi.Text(label, "SubTitle"));
        _switch.Classes.Add("SubTitle");
        Grid.SetColumn(_switch, 2); Children.Add(_switch);
        _switch.PropertyChanged += (_, e) => { if (e.Property == NativeSwitch.IsCheckedProperty) IsCheckedChanged?.Invoke(this, EventArgs.Empty); };
    }
}
