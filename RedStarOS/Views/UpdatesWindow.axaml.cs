using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Checks a GitHub releases feed for a newer version and can update the AppImage.</summary>
public partial class UpdatesWindow : Window
{
    private string? _pageUrl;

    private static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public UpdatesWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"Installed version: {CurrentVersion.ToString(3)}";
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
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RedStarOS-Updater");
            var json = await http.GetStringAsync(uri);
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            _pageUrl = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() : null;

            var clean = tag.TrimStart('v', 'V');
            if (Version.TryParse(clean, out var latest))
            {
                var cur = CurrentVersion;
                var current3 = new Version(cur.Major, cur.Minor, Math.Max(0, cur.Build));
                var latest3 = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
                if (latest3 > current3)
                {
                    ResultLabel.Text = $"Version {clean} is available.";
                    PageButton.IsVisible = _pageUrl != null;
                    UpdateButton.IsVisible = Environment.GetEnvironmentVariable("APPIMAGE") != null;
                }
                else ResultLabel.Text = "You are up to date.";
            }
            else ResultLabel.Text = $"Latest release is '{tag}' (could not compare it with {CurrentVersion.ToString(3)}).";
        }
        catch (Exception ex)
        {
            ResultLabel.Text = "Could not check for updates: " + ex.Message;
        }
        finally
        {
            Progress.IsVisible = false;
            CheckButton.IsEnabled = true;
        }
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
            ResultLabel.Text = "AppImageUpdate was started. Restart Red Star OS when it finishes.";
        }
        else
        {
            ResultLabel.Text = "AppImageUpdate is not installed. Use the download page instead.";
            if (_pageUrl != null) PageButton.IsVisible = true;
        }
    }
}
