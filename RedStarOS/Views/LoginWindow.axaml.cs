using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Lock / log-off screen.</summary>
public partial class LoginWindow : Window
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TranslateTransform _shake = new();

    public LoginWindow()
    {
        InitializeComponent();
        WaylandShell.Frameless(this, "Crimson Banner OS - Login");
        Card.RenderTransform = _shake;

        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();

        // Enter signs in, also when no password box is shown.
        KeyDown += (_, e) => { if (e.Key == Key.Enter) { TryLogin(); e.Handled = true; } };

        UserLabel.Text = Environment.UserName;
        var avatar = ShellUi.LoadAvatar();
        if (avatar != null) AvatarImage.Source = avatar;

        var noPassword = !PasswordUtil.HasPassword;
        PasswordBox.IsVisible = !noPassword;
        HintLabel.IsVisible = noPassword;

        Opened += (_, _) =>
        {
            if (noPassword) Focus(); else PasswordBox.Focus();
        };
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockLabel.Text = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        DateLabel.Text = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
    }

    private void OnLogin(object? sender, RoutedEventArgs e) => TryLogin();

    private void TryLogin()
    {
        if (PasswordUtil.Verify(PasswordBox.Text))
        {
            SystemSounds.Play("login");
            Close();
            return;
        }
        ErrorLabel.IsVisible = true;
        PasswordBox.Text = "";
        PasswordBox.Focus();
        Shake();
    }

    /// <summary>A quick left-right wobble of the card tells the user the password was wrong.</summary>
    private void Shake()
    {
        int step = 0;
        var offsets = new[] { -14, 14, -10, 10, -5, 5, 0 };
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        t.Tick += (_, _) =>
        {
            _shake.X = offsets[step++];
            if (step >= offsets.Length) t.Stop();
        };
        t.Start();
    }

    private void OnRestart(object? s, RoutedEventArgs e) { Close(); ShellActions.Restart(); }
    private void OnShutdown(object? s, RoutedEventArgs e) { Close(); ShellActions.Shutdown(); }

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();
        base.OnClosed(e);
    }
}
