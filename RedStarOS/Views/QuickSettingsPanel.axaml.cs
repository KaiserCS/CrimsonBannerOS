using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Quick settings, opened from the system tray: network, theme, do-not-disturb, volume, battery, power.</summary>
public partial class QuickSettingsPanel : UserControl, IShellPanel
{
    public event Action? CloseRequested;

    private readonly DispatcherTimer _volumeTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _pendingVolume;
    private bool _loadingVolume;
    private bool _muted;

    public QuickSettingsPanel()
    {
        InitializeComponent();

        UserLabel.Text = Environment.UserName;
        HostLabel.Text = Environment.MachineName;
        var avatar = ShellUi.LoadAvatar();
        if (avatar != null) AvatarImage.Source = avatar;

        RefreshNetwork();
        RefreshBattery();
        RefreshToggles();

        VolumeSlider.ValueChanged += (_, _) =>
        {
            VolumeLabel.Text = (int)VolumeSlider.Value + "%";
            if (_loadingVolume) return;
            _pendingVolume = (int)VolumeSlider.Value;
            _volumeTimer.Stop();
            _volumeTimer.Start();                                    // apply 200 ms after the last movement
        };
        _volumeTimer.Tick += async (_, _) =>
        {
            _volumeTimer.Stop();
            await SystemInfo.SetVolume(_pendingVolume);
        };
        DetachedFromVisualTree += (_, _) => _volumeTimer.Stop();

        _ = LoadVolume();
    }

    private async Task LoadVolume()
    {
        var volume = await SystemInfo.GetVolume();
        var muted = await SystemInfo.GetMuted();
        if (volume == null)
        {
            VolumeSlider.IsEnabled = false;
            MuteButton.IsEnabled = false;
            VolumeLabel.Text = "n/a";
            ToolTip.SetTip(VolumeSlider, "Volume control needs pactl or amixer");
            return;
        }
        _loadingVolume = true;
        VolumeSlider.Value = Math.Min(100, volume.Value);
        _loadingVolume = false;
        _muted = muted == true;
        UpdateMuteButton();
    }

    private void UpdateMuteButton() => MuteButton.Content = _muted ? "🔇" : "🔊";

    private void RefreshNetwork()
    {
        var kind = SystemInfo.ConnectionKind();
        NetGlyph.Content = Icons.Create(kind switch { "wifi" => "wifi", "wired" => "wired", _ => "offline" }, 18);
        NetTitle.Text = kind switch { "wifi" => "Wi-Fi", "wired" => "Wired", _ => "Offline" };
        NetSub.Text = kind == "offline" ? "Not connected" : SystemInfo.NetworkSummary();
    }

    private void RefreshBattery()
    {
        var b = SystemInfo.BatteryInfo();
        BatteryCard.IsVisible = b != null;
        if (b != null) BatteryLabel.Text = (b.Charging ? "⚡ " : "🔋 ") + b.Percent + "%" + (b.Charging ? "  ·  Charging" : "");
    }

    private void RefreshToggles()
    {
        ShellUi.SetClass(ThemeTile, "on", ThemeService.IsDark);
        ThemeSub.Text = ThemeService.IsDark ? "On" : "Off";
        ShellUi.SetClass(DndTile, "on", Notifier.DoNotDisturb);
        DndSub.Text = Notifier.DoNotDisturb ? "On" : "Off";
    }

    // ------------------------------------------------------------ handlers

    private void OnSettings(object? s, RoutedEventArgs e) { CloseRequested?.Invoke(); ShellActions.OpenSettings(); }
    private void OnNetwork(object? s, RoutedEventArgs e) { CloseRequested?.Invoke(); WindowManager.Open(new NetworkWindow()); }
    private void OnTheme(object? s, RoutedEventArgs e) { ShellActions.ToggleTheme(); RefreshToggles(); }
    private void OnDnd(object? s, RoutedEventArgs e) { Notifier.SetDoNotDisturb(!Notifier.DoNotDisturb); RefreshToggles(); }
    private void OnNextWallpaper(object? s, RoutedEventArgs e) => WallpaperService.Next();

    private async void OnMute(object? s, RoutedEventArgs e)
    {
        _muted = !_muted;
        UpdateMuteButton();
        await SystemInfo.SetMuted(_muted);
    }

    private void OnLogOff(object? s, RoutedEventArgs e) { CloseRequested?.Invoke(); ShellActions.LogOff(); }
    private void OnRestart(object? s, RoutedEventArgs e) { CloseRequested?.Invoke(); ShellActions.Restart(); }
    private void OnShutdown(object? s, RoutedEventArgs e) { CloseRequested?.Invoke(); ShellActions.Shutdown(); }
}
