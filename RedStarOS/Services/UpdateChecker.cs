using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace RedStarOS.Services;

/// <summary>
/// Looks for a new Crimson Banner OS release once a day (Settings → Software Updates can turn it off) and shows a
/// notification with "Update" / "Later" buttons. The releases feed comes from Settings, or from the update
/// information the AppImage was built with (GH_REPO=user/repo ./build-appimage.sh). Releases and development builds
/// are compared by <see cref="AppVersion"/>. A copy installed from our pacman repository isn't checked here: it
/// updates with the system's packages.
/// </summary>
public static class UpdateChecker
{
    public sealed record Result(bool Newer, string Latest, string? PageUrl, string? Error);

    /// <summary>Whether this copy updates itself from a releases feed (not when pacman looks after it).</summary>
    public static bool SelfUpdating => AppVersion.Kind != AppVersion.InstallKind.Package;

    /// <summary>The releases API URL: the one set in Settings, else the AppImage's embedded GitHub repository.</summary>
    public static string? FeedUrl()
    {
        if (!string.IsNullOrWhiteSpace(SettingsStore.Current.UpdateUrl)) return SettingsStore.Current.UpdateUrl;
        try
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (appImage == null || !File.Exists(appImage)) return null;
            using var fs = File.OpenRead(appImage);
            var buf = new byte[Math.Min(fs.Length, 1 << 20)];
            int n = fs.Read(buf, 0, buf.Length);
            var text = Encoding.ASCII.GetString(buf, 0, n);
            var m = Regex.Match(text, @"gh-releases-zsync\|([\w.\-]+)\|([\w.\-]+)\|");
            return m.Success ? $"https://api.github.com/repos/{m.Groups[1].Value}/{m.Groups[2].Value}/releases/latest" : null;
        }
        catch { return null; }
    }

    public static async Task<Result> CheckAsync(string? url = null)
    {
        url ??= FeedUrl();
        if (url == null) return new Result(false, "", null, "No releases feed is set.");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RedStarOS-Updater");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var page = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            return Compare(tag, page, AppVersion.Current);
        }
        catch (Exception ex) { return new Result(false, "", null, ex.Message); }
    }

    /// <summary>A release's tag against the installed version.</summary>
    public static Result Compare(string tag, string? page, AppVersion.Info installed)
    {
        var latest = AppVersion.Parse(tag);
        if (latest == null) return new Result(false, tag, page, $"The latest release, '{tag}', isn't a version this can compare.");
        return new Result(AppVersion.IsNewer(latest, installed), latest.Short, page, null);
    }

    /// <summary>Started with the desktop: first check after two minutes, then every six hours (at most daily).</summary>
    public static void StartBackground()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromHours(6);
            var s = SettingsStore.Current;
            if (!SelfUpdating || !s.AutoUpdateCheck || (s.LastUpdateCheck is { } last && (DateTime.Now - last).TotalHours < 24)) return;
            var r = await CheckAsync();
            if (r.Error != null) return;
            s.LastUpdateCheck = DateTime.Now;
            SettingsStore.Save();
            if (!r.Newer || r.Latest == s.SkippedVersion) return;
            Notifier.Show("Update available", $"Crimson Banner OS {r.Latest} is ready to install (you have {AppVersion.Current.Short}).", "settings",
                new NotificationAction("Update…", () => Views.SettingsWindow.OpenPage("Software Updates")),
                new NotificationAction("Skip this version", () => { SettingsStore.Current.SkippedVersion = r.Latest; SettingsStore.Save(); }));
        };
        timer.Start();
    }
}
