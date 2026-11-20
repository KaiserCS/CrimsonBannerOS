using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace RedStarOS.Services;

/// <summary>
/// Looks for a new Red Star OS release once a day (Settings → Software Updates can turn it off) and shows a
/// notification with "Update" / "Later" buttons. The releases feed comes from Settings, or from the update
/// information the AppImage was built with (GH_REPO=user/repo ./build-appimage.sh).
/// </summary>
public static class UpdateChecker
{
    public sealed record Result(bool Newer, string Latest, string? PageUrl, string? Error);

    public static Version Current => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

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
            var tag = (doc.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
            var page = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            if (!Version.TryParse(tag, out var latest)) return new Result(false, tag, page, null);
            var cur = Current;
            bool newer = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build)) > new Version(cur.Major, cur.Minor, Math.Max(0, cur.Build));
            return new Result(newer, tag, page, null);
        }
        catch (Exception ex) { return new Result(false, "", null, ex.Message); }
    }

    /// <summary>Started with the desktop: first check after two minutes, then every six hours (at most daily).</summary>
    public static void StartBackground()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromHours(6);
            var s = SettingsStore.Current;
            if (!s.AutoUpdateCheck || (s.LastUpdateCheck is { } last && (DateTime.Now - last).TotalHours < 24)) return;
            var r = await CheckAsync();
            if (r.Error != null) return;
            s.LastUpdateCheck = DateTime.Now;
            SettingsStore.Save();
            if (!r.Newer || r.Latest == s.SkippedVersion) return;
            Notifier.Show("Update available", $"Crimson Banner OS {r.Latest} is ready to install (you have {Current.ToString(3)}).", "settings",
                new NotificationAction("Update…", () => Views.SettingsWindow.OpenPage("Software Updates")),
                new NotificationAction("Skip this version", () => { SettingsStore.Current.SkippedVersion = r.Latest; SettingsStore.Save(); }));
        };
        timer.Start();
    }
}
