// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>"Authentication required": the administrator password prompt for polkit (PolkitAgent).</summary>
public sealed class PolkitDialog : Window
{
    private readonly TaskCompletionSource<bool> _done = new();
    private readonly TextBox _password = new() { PasswordChar = '●', PlaceholderText = "Password", MinWidth = 300 };
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.Parse("#E53E3E")), TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly ComboBox _user = new() { MinWidth = 300 };
    private readonly Button _ok = new() { Content = "Authenticate", Classes = { "primary" }, IsDefault = true };
    private readonly Func<string, string, Task<(bool Ok, string? Error)>> _try;

    public PolkitDialog() : this(new PolkitAgent.Request("org.example.test", "Authentication is needed to test this.", "",
        new System.Collections.Generic.Dictionary<string, string>(), "", new[] { Environment.UserName }), (_, _) => Task.FromResult((false, (string?)"test"))) { }

    public PolkitDialog(PolkitAgent.Request req, Func<string, string, Task<(bool Ok, string? Error)>> tryPassword)
    {
        _try = tryPassword;
        Title = "Authentication required";
        Width = 460; SizeToContent = SizeToContent.Height; CanResize = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _user.ItemsSource = req.Users;
        _user.SelectedIndex = 0;
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Finish(false);
        _ok.Click += (_, _) => _ = Attempt();
        var details = new Expander
        {
            Header = "Details",
            Content = new TextBlock
            {
                Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
                Text = "Action: " + req.ActionId + string.Concat(req.Details.Where(d => d.Key is "program" or "command_line" or "polkit.caller-pid")
                    .Select(d => "\n" + d.Key + ": " + d.Value)),
            },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(22), Spacing = 12,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children =
                {
                    new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(Color.Parse("#C8102E")),
                        Child = new TextBlock { Text = "🔒", FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                    new TextBlock { Text = "Authentication required", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                } },
                new TextBlock { Text = req.Message.Length > 0 ? req.Message : "A program wants to do something that needs an administrator.", TextWrapping = TextWrapping.Wrap },
                req.Users.Count > 1 ? _user : new TextBlock { Text = "As " + req.Users.FirstOrDefault(), Classes = { "caption" } },
                _password, _error, details,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _ok } },
            },
        };
        Opened += (_, _) => { Activate(); _password.Focus(); };
        Closed += (_, _) => _done.TrySetResult(false);
    }

    public Task<bool> Result => _done.Task;

    private async Task Attempt()
    {
        if (_user.SelectedItem is not string user) return;
        _ok.IsEnabled = _password.IsEnabled = false;
        _error.IsVisible = false;
        var (ok, error) = await _try(user, _password.Text ?? "");
        if (ok) { Finish(true); return; }
        _ok.IsEnabled = _password.IsEnabled = true;
        _password.Text = "";
        _error.Text = error ?? "That password didn't work.";
        _error.IsVisible = true;
        _password.Focus();
    }

    private void Finish(bool ok) { _done.TrySetResult(ok); Close(); }

    /// <summary>For PolkitAgent: shows the dialog on the UI thread until it's answered or polkit cancels.</summary>
    public static Task<bool> Ask(PolkitAgent.Request req, Func<string, string, Task<(bool Ok, string? Error)>> tryPassword, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            var d = new PolkitDialog(req, tryPassword);
            using var reg = ct.Register(() => Dispatcher.UIThread.Post(() => d.Close()));
            d.Show();
            SystemSounds.Play("notify");
            tcs.TrySetResult(await d.Result);
        });
        return tcs.Task;
    }
}
