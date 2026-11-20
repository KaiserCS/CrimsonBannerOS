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

        var noPassword = !UsesSystemPassword && !PasswordUtil.HasPassword;
        PasswordBox.IsVisible = !noPassword;
        HintLabel.IsVisible = noPassword;
        if (UsesSystemPassword) PasswordBox.PlaceholderText = "Your Linux password";

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

    /// <summary>The lock screen asks for the user's Linux password (PAM), unless Account says to use Crimson Banner's
    /// own lock password instead or PAM isn't there.</summary>
    public static bool UsesSystemPassword => SettingsStore.Current.LockWithSystemPassword && SystemAuth.Available;

    private bool _checking;

    private async void TryLogin()
    {
        if (_checking) return;
        bool ok;
        if (UsesSystemPassword)
        {
            _checking = true;
            PasswordBox.IsEnabled = false;
            ErrorLabel.IsVisible = false;
            var (outcome, why) = await SystemAuth.CheckAsync(PasswordBox.Text ?? "");
            _checking = false;
            PasswordBox.IsEnabled = true;
            if (outcome == SystemAuth.Outcome.Unavailable)
            {
                // PAM itself is broken here: Crimson Banner's own lock password (if any) still works
                Console.Error.WriteLine("lock screen: PAM: " + why);
                ok = PasswordUtil.Verify(PasswordBox.Text);
            }
            else ok = outcome == SystemAuth.Outcome.Accepted;
        }
        else ok = PasswordUtil.Verify(PasswordBox.Text);
        if (ok)
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
