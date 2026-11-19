// SPDX-License-Identifier: GPL-3.0-or-later
using RedStarOS.Services;
using RedStarOS.Views;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>Volume steps, the Ctrl+Alt+Delete menu and power commands, and how programs inside redstar-comp pick
/// their display (screen sharing).</summary>
public class SessionControlsTests
{
    [Theory]
    [InlineData(50, 5, 55)]
    [InlineData(98, 5, 100)]       // stops at 100
    [InlineData(120, 5, 120)]      // turned past 100 elsewhere: up doesn't pull it back down
    [InlineData(120, -5, 115)]
    [InlineData(3, -5, 0)]         // stops at 0
    public void Volume_steps_stay_between_0_and_100(int now, int delta, int expected) =>
        Assert.Equal(expected, SystemInfo.StepVolume(now, delta));

    [Fact]
    public void Power_goes_through_systemd_logind()
    {
        string? Sys(string n) => n == "systemctl" ? "/usr/bin/systemctl" : null;
        Assert.Equal(new[] { "/usr/bin/systemctl", "poweroff" }, ShellActions.PowerCommand("shutdown", Sys));
        Assert.Equal(new[] { "/usr/bin/systemctl", "reboot" }, ShellActions.PowerCommand("restart", Sys));
        Assert.Null(ShellActions.PowerCommand("logoff", Sys));
        Assert.Equal(new[] { "/bin/loginctl", "poweroff" },
            ShellActions.PowerCommand("shutdown", n => n == "loginctl" ? "/bin/loginctl" : null));
        Assert.Null(ShellActions.PowerCommand("shutdown", _ => null));
    }

    [Fact]
    public void Power_menu_says_what_each_choice_does()
    {
        var session = PowerMenu.Choices(session: true);
        Assert.Equal(new[] { "lock", "logoff", "restart", "shutdown", "taskmgr" }, session.Select(c => c.Action));
        Assert.Equal(new[] { "Lock", "Log off", "Restart", "Shut down", "Task Manager" }, session.Select(c => c.Label));
        // as an app on another desktop, Shut down only closes Red Star OS: the menu says so
        Assert.Contains(PowerMenu.Choices(session: false), c => c.Label == "Close Red Star OS" && c.Action == "shutdown");
    }

    [Fact]
    public void Programs_run_on_Wayland_inside_redstar_comp_unless_turned_off()
    {
        var wayland = InputIsolation.AppDisplayEnv(true);
        Assert.Null(wayland["GDK_BACKEND"]);           // removed: GTK picks Wayland
        Assert.Null(wayland["QT_QPA_PLATFORM"]);
        Assert.Equal("auto", wayland["ELECTRON_OZONE_PLATFORM_HINT"]);
        Assert.Equal("wayland", wayland["XDG_SESSION_TYPE"]);

        var x11 = InputIsolation.AppDisplayEnv(false);
        Assert.Equal("x11", x11["GDK_BACKEND"]);
        Assert.Equal("xcb", x11["QT_QPA_PLATFORM"]);
        Assert.Equal("x11", x11["ELECTRON_OZONE_PLATFORM_HINT"]);
    }

    [Fact]
    public void Screen_sharing_portal_needs_no_layer_shell_picker()
    {
        Assert.Contains("[screencast]", PortalSetup.WlrConfig);
        Assert.Contains("chooser_type=none", PortalSetup.WlrConfig);
        Assert.Contains("ScreenCast=wlr", PortalSetup.Config);
    }
}
