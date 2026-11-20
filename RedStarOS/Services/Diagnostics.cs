using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace RedStarOS.Services;

/// <summary>
/// Errors and system facts in one place: unexpected errors are written to crash.log (and the shell keeps running
/// instead of taking the whole session down); Settings → Diagnostics shows them and exports everything as a zip
/// to attach to a bug report.
/// </summary>
public static class Diagnostics
{
    public static string CrashLog => AppPaths.ConfigFile("crash.log");
    private static int _recent;

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("background task", e.Exception); e.SetObserved(); };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log("ui", e.Exception);
            e.Handled = true;          // the desktop is the whole session: keep it alive
            if (_recent++ < 3)
            {
                SystemSounds.Play("error");
                Notifier.Show("Something went wrong", "An error was caught and saved. Details: Settings → Diagnostics.", "settings",
                    new NotificationAction("Details", () => Views.SettingsWindow.OpenPage("Diagnostics")));
            }
        };
    }

    public static void Log(string where, Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var f = CrashLog;
            if (File.Exists(f) && new FileInfo(f).Length > 1024 * 1024) File.Move(f, f + ".old", true);
            File.AppendAllText(f, $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {UpdateChecker.Current.ToString(3)}\n{ex}\n\n");
        }
        catch { }
    }

    private static string Read(string path) { try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return ""; } }

    public static string Tail(string path, int lines)
    {
        var all = Read(path).Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    /// <summary>Facts that help explain a problem (no personal files, no passwords).</summary>
    public static List<(string Key, string Value)> SystemFacts()
    {
        string os = "Linux";
        var rel = Read("/etc/os-release").Split('\n').FirstOrDefault(l => l.StartsWith("PRETTY_NAME="));
        if (rel != null) os = rel[12..].Trim('"');
        string Tool(string t) => ProcessUtil.Which(t) != null ? "yes" : "no";
        return new()
        {
            ("Red Star OS", UpdateChecker.Current.ToString(3) + (Environment.GetEnvironmentVariable("APPIMAGE") != null ? " (AppImage)" : " (from source)")),
            ("System", os),
            ("Kernel", Read("/proc/sys/kernel/osrelease").Trim()),
            ("Architecture", RuntimeInformation.OSArchitecture.ToString()),
            (".NET", RuntimeInformation.FrameworkDescription),
            ("Session", (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?") + (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") != null ? " (Wayland)" : "") + (Environment.GetEnvironmentVariable("DISPLAY") is { } d ? " DISPLAY=" + d : "")),
            ("Desktop", Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?"),
            ("Global shortcuts", X11Desktop.Available ? (CompIpc.Connected ? "on (from redstar-comp)" : "on") : "off"),
            ("Compositor", InputIsolation.InRedStarComp ? "redstar-comp" + (CompIpc.Connected ? ", connected" : ", not connected") : X11Props.CompositorRunning() ? "an X11 compositor" : "none"),
            ("Screenshots", InputIsolation.InRedStarComp ? $"grim {Tool("grim")}, screen sharing portal (xdg-desktop-portal-wlr) {(PortalSetup.WlrInstalled ? "yes" : "no")}"
                : $"scrot {Tool("scrot")}, maim {Tool("maim")}, import {Tool("import")}"),
            ("Web browser", RedStarOnline.ProgramPath() is { } online ? "Red Star Online (WebKitGTK), " + online : "Red Star Online not installed"),
            ("Graphics", GraphicsFact()),
            ("Red Star windows", Graphics.ShellRenderer + (Graphics.ShellReason.Length > 0 ? " — " + Graphics.ShellReason : "")),
            ("Browser fallbacks", OnlineFallbacks()),
            ("Video (libvlc)", File.Exists("/usr/lib/x86_64-linux-gnu/libvlc.so.5") || File.Exists("/usr/lib/libvlc.so.5") || File.Exists("/usr/lib64/libvlc.so.5") ? "yes" : "not found"),
            ("Tools", $"nmcli {Tool("nmcli")}, pactl {Tool("pactl")}, bluetoothctl {Tool("bluetoothctl")}, xrandr {Tool("xrandr")}, cups {Tool("lpstat")}, setsid {Tool("setsid")}, python3 {Tool("python3")}, xdg-open {Tool("xdg-open")}"),
            ("Memory", Read("/proc/meminfo").Split('\n').FirstOrDefault(l => l.StartsWith("MemTotal"))?.Replace("MemTotal:", "").Trim() ?? "?"),
        };
    }

    /// <summary>Red Star Online turns these off by itself after a page crash (~/.cache/redstaros/online).</summary>
    private static string OnlineFallbacks()
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "redstaros", "online");
        var off = new List<string>();
        if (File.Exists(Path.Combine(dir, "hwdecode-off"))) off.Add("hardware video decoding off");
        if (File.Exists(Path.Combine(dir, "gpu-off"))) off.Add("GPU drawing off");
        return off.Count == 0 ? "none" : string.Join(", ", off);
    }

    private static string GraphicsFact()
    {
        var node = Graphics.HasRenderNode() ? "GPU render node present" : "no GPU render node (/dev/dri)";
        var gpu = Environment.GetEnvironmentVariable("REDSTAR_GPU") is { } g ? (g == "1" ? "GPU access (DRI3)" : "no GPU access")
                  : Graphics.DisplayHasGpu() ? "GPU access (DRI3)" : "no GPU access";
        string where = InputIsolation.InRedStarComp ? "redstar-comp (Xwayland)" : InputIsolation.Mode switch
        {
            "xwayland" => "Red Star's own display (Xwayland)",
            "xephyr" or "xephyr-on-wayland" => "Red Star's own display (Xephyr)",
            _ => Environment.GetEnvironmentVariable("REDSTAR_SESSION") == "1" ? "Red Star session" : "your desktop"
        };
        bool comp = X11Props.CompositorRunning(fresh: true);
        return $"{where}: {gpu}, {node}, compositor {(comp ? "running" : "not running")} · setting: {Graphics.Mode}";
    }

    /// <summary>Writes a zip with system facts and the logs. Returns its path.</summary>
    public static string Export(string folder)
    {
        Directory.CreateDirectory(folder);
        var zipPath = Path.Combine(folder, $"redstar-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var info = new StringBuilder();
        foreach (var (k, v) in SystemFacts()) info.Append(k).Append(": ").Append(v).Append('\n');
        void Add(string name, string content)
        {
            var e = zip.CreateEntry(name);
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }
        Add("system.txt", info.ToString());
        foreach (var f in new[] { "crash.log", "crash.log.old", "browser.log", "browser-error.log" })
        {
            var p = AppPaths.ConfigFile(f);
            if (File.Exists(p)) Add(f, Read(p));
        }
        return zipPath;
    }

    public static void ClearLogs()
    {
        foreach (var f in new[] { "crash.log", "crash.log.old", "browser.log", "browser.log.old", "browser-error.log" })
            try { File.Delete(AppPaths.ConfigFile(f)); } catch { }
    }
}
