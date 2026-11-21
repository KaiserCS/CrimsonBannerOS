using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Checks a GitHub releases feed for a newer version and can update the AppImage.</summary>
public partial class UpdatesWindow : Window
{
    private string? _pageUrl;

    public UpdatesWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"Installed version: {AppVersion.Current.Display} ({AppVersion.KindName(AppVersion.Kind)})";
        UrlBox.Text = SettingsStore.Current.UpdateUrl ?? UpdateChecker.FeedUrl();
    }

    private async void OnCheck(object? s, RoutedEventArgs e)
    {
        var url = (UrlBox.Text ?? "").Trim();
        SettingsStore.Current.UpdateUrl = url.Length == 0 ? null : url;
        SettingsStore.Save();

        PageButton.IsVisible = false;
        UpdateButton.IsVisible = false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            ResultLabel.Text = "Enter the releases API URL of the project first.";
            return;
        }

        Progress.IsVisible = true;
        CheckButton.IsEnabled = false;
        ResultLabel.Text = "Checking...";
        var r = await UpdateChecker.CheckAsync(uri.ToString());
        Progress.IsVisible = false;
        CheckButton.IsEnabled = true;
        _pageUrl = r.PageUrl;
        if (r.Error != null && r.Latest.Length == 0) { ResultLabel.Text = "Could not check for updates: " + r.Error; return; }
        if (r.Error != null) { ResultLabel.Text = r.Error; PageButton.IsVisible = _pageUrl != null; return; }
        SettingsStore.Current.LastUpdateCheck = DateTime.Now;
        SettingsStore.Save();
        if (r.Newer)
        {
            ResultLabel.Text = $"Version {r.Latest} is available.";
            PageButton.IsVisible = _pageUrl != null;
            UpdateButton.IsVisible = Environment.GetEnvironmentVariable("APPIMAGE") != null;
        }
        else ResultLabel.Text = UpToDateText(r.Latest);
    }

    /// <summary>"You are up to date.", or for a development build of the latest release, that it's already past it.</summary>
    public static string UpToDateText(string latest)
    {
        var cur = AppVersion.Current;
        return !cur.IsRelease && AppVersion.Parse(latest)?.Release == cur.Release
            ? $"You are up to date: this development build already has everything in {latest}."
            : "You are up to date.";
    }

    private void OnOpenPage(object? s, RoutedEventArgs e)
    {
        if (_pageUrl != null) SystemUtil.OpenExternal(_pageUrl);
    }

    private void OnUpdate(object? s, RoutedEventArgs e)
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var tool = ProcessUtil.Which("appimageupdatetool") ?? ProcessUtil.Which("AppImageUpdate");
        if (appImage != null && tool != null)
        {
            ProcessUtil.StartDetached(tool, new[] { appImage });
            ResultLabel.Text = "AppImageUpdate was started. Restart Crimson Banner OS when it finishes.";
        }
        else
        {
            ResultLabel.Text = "AppImageUpdate is not installed. Use the download page instead.";
            if (_pageUrl != null) PageButton.IsVisible = true;
        }
    }
}
