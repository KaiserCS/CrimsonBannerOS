// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Ctrl+Alt+Delete: Lock, Log off, Restart, Shut down or the Task Manager.</summary>
public static class PowerMenu
{
    private static Window? _open;

    /// <summary>What the menu offers, in order: (label, action for <see cref="ShellActions.Perform"/> or "taskmgr").</summary>
    public static (string Label, string Action)[] Choices(bool session) => new[]
    {
        ("Lock", "lock"),
        (session ? "Log off" : "Log off (lock screen)", "logoff"),
        (session ? "Restart" : "Restart Crimson Banner OS", "restart"),
        (session ? "Shut down" : "Close Crimson Banner OS", "shutdown"),
        ("Task Manager", "taskmgr"),
    };

    public static void Show()
    {
        if (_open != null) { _open.Activate(); return; }
        var list = new StackPanel { Spacing = 6, Width = 260 };
        var win = new Window
        {
            Title = "Crimson Banner OS",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "What would you like to do?", FontSize = 16, FontWeight = FontWeight.SemiBold },
                    list,
                },
            },
        };
        Button Item(string label, Action act)
        {
            var b = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 8) };
            b.Click += (_, _) => { win.Close(); act(); };
            return b;
        }
        foreach (var (label, action) in Choices(ShellActions.IsSession))
            list.Children.Add(Item(label, action == "taskmgr" ? () => AppRegistry.Launch("taskmgr") : () => ShellActions.Perform(action)));
        var cancel = Item("Cancel", () => { });
        cancel.Margin = new Thickness(0, 8, 0, 0);
        list.Children.Add(cancel);

        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.Opened += (_, _) => (list.Children[0] as Button)?.Focus();
        win.Closed += (_, _) => _open = null;
        _open = win;
        if (WindowManager.Desktop is { } d) win.Show(d); else win.Show();
    }
}
