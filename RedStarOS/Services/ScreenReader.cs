// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// The screen reader: Orca (the GNOME project's, GPL), which reads programs through AT-SPI, the accessibility bus.
/// Crimson Banner's own windows are readable since Avalonia 12, whose Linux AT-SPI2 backend exposes their controls;
/// GTK programs such as Crimson Online have their own. Super+Alt+S switches it on and off, like on GNOME.
/// </summary>
public static class ScreenReader
{
    public static bool Installed => ProcessUtil.Which("orca") != null;

    public static async Task<bool> RunningAsync()
    {
        if (ProcessUtil.Which("pgrep") is not { } pg) return false;
        var (code, _) = await ProcessUtil.RunAsync(pg, new[] { "-x", "orca" }, 3000);
        return code == 0;
    }

    public static void Start()
    {
        if (ProcessUtil.Which("orca") is not { } orca)
        {
            Notifier.Show("Screen reader", "Install Orca to have the screen read aloud (Arch/CachyOS: pacman -S orca · Debian/Ubuntu: apt install orca).", "settings");
            return;
        }
        ProcessUtil.StartDetached(orca, new[] { "--replace" }, AppPaths.Home);
    }

    public static async Task StopAsync()
    {
        if (ProcessUtil.Which("orca") is { } orca) await ProcessUtil.RunAsync(orca, new[] { "--quit" }, 5000);
        if (await RunningAsync() && ProcessUtil.Which("pkill") is { } pk) await ProcessUtil.RunAsync(pk, new[] { "-x", "orca" }, 3000);
    }

    /// <summary>Super+Alt+S, and the switch in Settings → Accessibility.</summary>
    public static async void Toggle()
    {
        bool on = !await RunningAsync();
        SettingsStore.Current.ScreenReaderOn = on;
        SettingsStore.Save();
        if (on) { Start(); SystemSounds.Play("notify"); }
        else await StopAsync();
    }
}

/// <summary>
/// The on-screen keyboard: Onboard (GPL-3) under X11. Inside redstar-comp, the usual Wayland on-screen keyboards
/// (Squeekboard, wvkbd) need the layer-shell protocol, which redstar-comp doesn't offer yet.
/// </summary>
public static class OnScreenKeyboard
{
    public static string? Program => InputIsolation.InRedStarComp ? null : ProcessUtil.Which("onboard");

    public static string WhyNot => InputIsolation.InRedStarComp
        ? "An on-screen keyboard inside redstar-comp needs the layer-shell protocol, which redstar-comp doesn't have yet."
        : "Install Onboard for an on-screen keyboard (Arch/CachyOS: AUR onboard · Debian/Ubuntu: apt install onboard).";

    public static void Toggle()
    {
        if (Program is not { } exe) { Notifier.Show("On-screen keyboard", WhyNot, "settings"); return; }
        // Onboard shows itself again when started while running, and has its own Hide button
        ProcessUtil.StartDetached(exe, Array.Empty<string>(), AppPaths.Home);
    }
}
