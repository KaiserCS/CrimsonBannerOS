using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RedStarOS.Services;

/// <summary>
/// GPU acceleration: decides, per part of Red Star, whether to draw with the GPU or the CPU.
///  • A display "has a GPU" when its X server offers DRI3 (direct GPU access for apps — Xorg and Xwayland do;
///    Xephyr doesn't) and the kernel has a GPU render node (/dev/dri/renderD*).
///  • Red Star's own windows use the GPU there when a compositor runs too (without one, GPU-drawn popups flicker).
///  • Red Star Online renders pages and decodes video (VA-API) on the GPU whenever the display has one; if the
///    GPU process keeps crashing the engine falls back to the CPU by itself.
/// Settings → Diagnostics: Automatic (default) / Always use the GPU / Software only.
/// REDSTAR_RENDER=gpu|software overrides it for Red Star's windows.
/// </summary>
public static class Graphics
{
    /// <summary>"auto", "gpu" or "software" — read straight from settings.json, so it works before anything loads.</summary>
    public static string Mode
    {
        get
        {
            try
            {
                var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(cfg)) cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                var f = Path.Combine(cfg, "redstaros", "settings.json");
                if (File.Exists(f))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    if (doc.RootElement.TryGetProperty("Graphics", out var g) && g.GetString() is "gpu" or "software")
                        return g.GetString()!;
                }
            }
            catch { }
            return "auto";
        }
    }

    /// <summary>How Red Star's own windows are drawn in this run ("GPU (OpenGL)" / "software"), for Diagnostics.</summary>
    public static string ShellRenderer { get; set; } = "GPU (OpenGL)";
    public static string ShellReason { get; set; } = "";

    private static bool OwnDisplay =>
        Environment.GetEnvironmentVariable("REDSTAR_NESTED") == "1" || Environment.GetEnvironmentVariable("REDSTAR_SESSION") == "1";

    /// <summary>
    /// Kernel drivers whose render node can't be relied on to draw: virtual machines' display adapters. VMware's
    /// vmwgfx (VMware, VirtualBox's VMSVGA, QEMU's -vga vmware) offers DRI3 and a render node even without 3D, and
    /// with it Mesa, picom's glx backend and wlroots fail and take the session down; "safe graphics" (nomodeset)
    /// only worked because it leaves no render node at all. On these the desktop draws in software.
    /// </summary>
    public static readonly string[] SoftwareOnlyDrivers =
        { "vmwgfx", "qxl", "bochs", "bochs-drm", "cirrus", "cirrus-qemu", "simpledrm", "hyperv_drm", "vboxvideo", "udl", "ast", "mgag200" };

    /// <summary>The kernel driver behind a render node (renderD128 → "amdgpu"), or null.</summary>
    public static string? DriverOf(string node, string sysDrm = "/sys/class/drm")
    {
        try
        {
            var link = Path.Combine(sysDrm, Path.GetFileName(node), "device", "driver");
            var target = new FileInfo(link).LinkTarget;
            return target == null ? null : Path.GetFileName(target.TrimEnd('/'));
        }
        catch { return null; }
    }

    /// <summary>True when there's a render node whose driver can draw (not a virtual machine's display adapter).</summary>
    public static bool HasRenderNode() => HasRenderNode("/dev/dri", "/sys/class/drm");

    public static bool HasRenderNode(string devDri, string sysDrm)
    {
        try
        {
            if (!Directory.Exists(devDri)) return false;
            return Directory.GetFiles(devDri, "renderD*").Any(n => DriverOf(n, sysDrm) is not { } d || !SoftwareOnlyDrivers.Contains(d));
        }
        catch { return false; }
    }

    /// <summary>A render node exists but only on a driver that can't draw (see SoftwareOnlyDrivers).</summary>
    public static bool OnlySoftwareOnlyNodes()
    {
        try { return Directory.Exists("/dev/dri") && Directory.GetFiles("/dev/dri", "renderD*").Length > 0 && !HasRenderNode(); }
        catch { return false; }
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr d);
    [DllImport("libX11.so.6")] private static extern bool XQueryExtension(IntPtr d, string name, out int opcode, out int ev, out int err);

    /// <summary>True when the X display (default: this process's DISPLAY) lets apps use the GPU directly.</summary>
    public static bool DisplayHasGpu(string? display = null)
    {
        if (!HasRenderNode()) return false;
        try
        {
            var d = XOpenDisplay(display);
            if (d == IntPtr.Zero) return false;
            try { return XQueryExtension(d, "DRI3", out _, out _, out _); }
            finally { XCloseDisplay(d); }
        }
        catch { return false; }
    }

    /// <summary>
    /// For the display Red Star starts (or is) in: "1" when GPU acceleration should be offered to what runs inside.
    /// Passed down as REDSTAR_GPU.
    /// </summary>
    public static string GpuFlagFor(string? display)
    {
        var mode = Mode;
        if (mode == "software") return "0";
        if (mode == "gpu") return "1";
        return DisplayHasGpu(display) ? "1" : "0";
    }

    private static bool EnvGpu => Environment.GetEnvironmentVariable("REDSTAR_GPU") is { } v ? v == "1" : DisplayHasGpu();

    /// <summary>Red Star's own windows: GPU or software rendering (decided once, before Avalonia starts).</summary>
    public static bool UseGpuForShell()
    {
        var env = Environment.GetEnvironmentVariable("REDSTAR_RENDER");
        if (env == "gpu") { ShellReason = "REDSTAR_RENDER=gpu"; return true; }
        if (env == "software") { ShellReason = "REDSTAR_RENDER=software"; return false; }
        var mode = Mode;
        if (mode == "gpu") { ShellReason = "set to always use the GPU"; return true; }
        if (mode == "software") { ShellReason = "set to software only"; return false; }
        if (!OwnDisplay) { ShellReason = "on your desktop"; return true; }                 // the host desktop's compositor handles popups
        if (!EnvGpu) { ShellReason = "this display has no GPU access (Xephyr)"; return false; }
        if (!X11Props.CompositorRunning(fresh: true)) { ShellReason = "no compositor (install picom to use the GPU)"; return false; }
        ShellReason = "GPU and compositor available";
        return true;
    }

    /// <summary>Red Star Online: render pages and decode video on the GPU?</summary>
    public static bool UseGpuForWeb()
    {
        var mode = Mode;
        if (mode == "software") return false;
        if (mode == "gpu") return true;
        return EnvGpu;
    }
}
