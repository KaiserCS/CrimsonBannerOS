using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>One terminal in a tab: the TerminalView with its own scrollbar, and a thin accent frame when it is the
/// active pane of a split tab.</summary>
public sealed class TerminalPane : Border
{
    public TerminalView View { get; }
    public string Title = "Terminal";
    public bool Bell;
    public bool KeepOpenOnExit;              // a command (not a shell): leave the output visible when it ends
    private readonly ScrollBar _bar;
    private bool _sync;

    public TerminalPane(TerminalProfile profile, double zoom)
    {
        View = new TerminalView();
        View.ApplyProfile(profile, zoom);
        _bar = new ScrollBar { Orientation = Orientation.Vertical, AllowAutoHide = true, Minimum = 0 };
        Grid.SetColumn(_bar, 1);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { View, _bar } };
        Child = grid;
        BorderThickness = new Thickness(1);
        BorderBrush = Brushes.Transparent;
        ApplyBackground();

        _bar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_sync) View.ScrollTo((int)Math.Round(_bar.Value));
        };
        View.ViewChanged += SyncScrollbar;
    }

    /// <summary>The pane's background matches the terminal so the scrollbar strip and padding blend in.</summary>
    public void ApplyBackground()
    {
        uint bg = View.BackgroundRgb;
        Background = new SolidColorBrush(Color.FromRgb((byte)(bg >> 16), (byte)(bg >> 8), (byte)bg));
    }

    public void SetActive(bool active, bool showFrame)
    {
        BorderBrush = active && showFrame
            ? (Application.Current?.FindResource("ShellAccentBrush") as IBrush ?? Brushes.SteelBlue)
            : Brushes.Transparent;
    }

    public void SyncScrollbar()
    {
        _sync = true;
        _bar.Maximum = View.MaxViewTop;
        _bar.ViewportSize = View.Emu.Rows;
        _bar.Value = View.ViewTop;
        _bar.IsVisible = View.MaxViewTop > 0;
        _sync = false;
    }
}
