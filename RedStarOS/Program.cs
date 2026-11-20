using Avalonia;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

// Red Star OS is a Linux desktop: Linux-only APIs (file modes, ptys, X11) are fine everywhere in it.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace RedStarOS;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // On top of another desktop, Red Star runs inside its own display so every key and click stays in it.
        if (RedStarOS.Services.InputIsolation.TryRunIsolated(args) is { } exitCode)
        {
            Environment.ExitCode = exitCode;
            return;
        }
        ApplyEarlyAccessibility();
        RedStarOS.Services.WindowManagerHost.EnsureForSession();   // the Red Star session: title bars even without openbox
        bool wayland = RedStarOS.Services.WaylandShell.ShouldUse();   // inside redstar-comp, when turned on
        try
        {
            BuildAvaloniaApp(wayland).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (wayland)
        {
            Environment.ExitCode = RedStarOS.Services.WaylandShell.RestartThroughXwayland(args, ex);
        }
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(false);

    /// <summary>X11 (through Xwayland inside redstar-comp), or Avalonia's native Wayland backend (experimental).</summary>
    public static AppBuilder BuildAvaloniaApp(bool wayland)
    {
        var b = AppBuilder.Configure<App>().UsePlatformDetect();
        if (wayland)
        {
            b = b.UseWayland().With(new WaylandPlatformOptions { AppId = "redstaros" });
            RedStarOS.Services.Graphics.ShellRenderer = "native Wayland (EGL)";
            RedStarOS.Services.Graphics.ShellReason = "experimental: Settings → Desktop & Taskbar";
        }
        else b = b.With(X11Options());
        return b.LogToTrace();
    }

    /// <summary>
    /// GPU or software drawing for Red Star's windows (Services/Graphics decides: GPU when the display offers it and
    /// a compositor runs; software inside Xephyr or without a compositor, where GPU-drawn popups flicker).
    /// </summary>
    private static X11PlatformOptions X11Options()
    {
        var o = new X11PlatformOptions();
        bool gpu;
        try { gpu = RedStarOS.Services.Graphics.UseGpuForShell(); } catch { gpu = true; }
        if (!gpu) o.RenderingMode = new[] { X11RenderingMode.Software };
        RedStarOS.Services.Graphics.ShellRenderer = gpu ? "GPU (OpenGL)" : "software";
        return o;
    }

    /// <summary>
    /// Settings that must be in place before the first window exists: the size of everything (Avalonia's global
    /// scale factor) and the mouse-pointer size. Values the user already set in
    /// the environment win.
    /// </summary>
    private static void ApplyEarlyAccessibility()
    {
        try
        {
            var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(cfg)) cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var file = Path.Combine(cfg, "redstaros", "settings.json");
            if (!File.Exists(file)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;

            if (root.TryGetProperty("UiScale", out var sc) && sc.TryGetDouble(out var scale) && scale > 1.01 && scale <= 3
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR")))
            {
                var v = scale.ToString("0.##", CultureInfo.InvariantCulture);
                Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", v);
                Environment.SetEnvironmentVariable("REDSTAR_UI_SCALE", v);          // Red Star Online and other helpers read this
            }
            if (root.TryGetProperty("LargeCursor", out var lc) && lc.ValueKind == JsonValueKind.True)
                Environment.SetEnvironmentVariable("XCURSOR_SIZE", "48");
        }
        catch { }
    }
}
