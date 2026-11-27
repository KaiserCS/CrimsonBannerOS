// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using Avalonia.Threading;
using RedStarOS.Services;
using RedStarOS.Views;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>Virtual desktops (the shell's side: the window managers have their own tests) and night light.</summary>
public class VirtualDesktopTests
{
    // ─────────── closing a desktop ───────────
    [Fact]
    public void Closing_a_desktop_moves_its_windows_left_and_the_later_ones_down()
    {
        var desks = new Dictionary<long, int> { [1] = 0, [2] = 1, [3] = 2, [4] = -1, [5] = 1 };
        var plan = VirtualDesktops.ClosePlan(desks, 1);
        Assert.Equal(new Dictionary<long, int> { [2] = 0, [5] = 0, [3] = 1 }, plan);
        // the first desktop's windows join the next one, which becomes the first
        Assert.Equal(new Dictionary<long, int> { [2] = 0, [5] = 0, [3] = 1 }, VirtualDesktops.ClosePlan(desks, 0));
        Assert.Equal(new Dictionary<long, int> { [3] = 1 }, VirtualDesktops.ClosePlan(desks, 2));
    }

    [Theory]
    [InlineData(0, 1, 0)]       // a desktop after the one in use closes: nothing changes
    [InlineData(2, 1, 1)]       // the one in use closes: the one on its left
    [InlineData(0, 0, 0)]       // the first, in use, closes: the next one, now first
    [InlineData(3, 1, 2)]       // one before the one in use closes: same desktop, one number lower
    public void Closing_a_desktop_keeps_a_sensible_one_shown(int current, int closed, int after) =>
        Assert.Equal(after, VirtualDesktops.CurrentAfterClose(current, closed));

    // ─────────── what the compositor says ───────────
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Redstar_comp_lines_set_the_desktops_and_where_windows_are()
    {
        VirtualDesktops.Reset();
        int switched = -1;
        void OnSwitched(int d) => switched = d;
        VirtualDesktops.Switched += OnSwitched;
        try
        {
            CompIpc.Handle("desktops\t3\t1");
            CompIpc.Handle("on-desktop\t4294967300\t2");
            CompIpc.Handle("on-desktop\t81788934\t-1");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, VirtualDesktops.Count);
            Assert.Equal(1, VirtualDesktops.Current);
            Assert.Equal(1, switched);
            Assert.Equal(2, VirtualDesktops.DeskOf(4294967300));
            Assert.Equal(-1, VirtualDesktops.DeskOf(81788934));
            Assert.Equal(1, VirtualDesktops.DeskOf(12345));        // not placed yet: on the one shown
            CompIpc.Handle("desktops\t2\t1");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, VirtualDesktops.Count);
        }
        finally { VirtualDesktops.Switched -= OnSwitched; VirtualDesktops.Reset(); CompIpc.Reset(); }
    }

    [Fact]
    public void The_window_managers_every_desktop_value_reads_as_minus_one()
    {
        Assert.Equal(-1, X11Desktop.DeskNumber(0xFFFFFFFFL));
        Assert.Equal(2, X11Desktop.DeskNumber(2));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Desktops_are_named_desktop_n_until_renamed()
    {
        var names = SettingsStore.Current.DesktopNames;
        var saved = new List<string>(names);
        try
        {
            names.Clear();
            Assert.Equal("Desktop 2", VirtualDesktops.Name(1));
            VirtualDesktops.Rename(1, "  Work  ");
            Assert.Equal("Work", VirtualDesktops.Name(1));
            Assert.Equal("Desktop 1", VirtualDesktops.Name(0));
            VirtualDesktops.Rename(1, "");
            Assert.Equal("Desktop 2", VirtualDesktops.Name(1));
        }
        finally { names.Clear(); names.AddRange(saved); }
    }

    // ─────────── night light ───────────
    [Fact]
    public void Daylight_is_no_tint_and_warmer_takes_blue_then_green_away()
    {
        var day = NightLight.Multipliers(6500);
        Assert.Equal(1, day.R, 3); Assert.Equal(1, day.G, 3); Assert.Equal(1, day.B, 3);
        var warm = NightLight.Multipliers(3400);
        Assert.Equal(1, warm.R, 3);
        Assert.True(warm.G < 1 && warm.B < warm.G, $"{warm}");
        var warmer = NightLight.Multipliers(2000);
        Assert.True(warmer.G < warm.G && warmer.B < warm.B, $"{warmer}");
    }

    [Fact]
    public void A_gamma_ramp_runs_from_black_to_the_multiplier()
    {
        Assert.Equal(0, NightLight.RampValue(0, 256, 0.5));
        Assert.Equal(65535, NightLight.RampValue(255, 256, 1));
        Assert.InRange(NightLight.RampValue(255, 256, 0.5), 32767, 32768);
    }

    [Fact]
    public void The_redstar_comp_line_is_written_with_dots_whatever_the_language()
    {
        var was = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // a decimal comma, as in German (the tests run without culture data, so made by hand)
            var comma = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            System.Globalization.CultureInfo.CurrentCulture = comma;
            Assert.Equal("nightlight\t1\t0.75\t0.5", CompIpc.NightLightLine(1, 0.75, 0.5));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = was; }
    }

    [Theory]
    [InlineData("+404251-0740023", 40.714, -74.006)]       // America/New_York
    [InlineData("+5222+00454", 52.367, 4.9)]               // Europe/Amsterdam
    [InlineData("-3352+15113", -33.867, 151.217)]          // Australia/Sydney
    public void Time_zone_places_are_read_from_zone_tab(string iso, double lat, double lon)
    {
        var p = NightLight.ParseCoordinates(iso);
        Assert.NotNull(p);
        Assert.Equal(lat, p!.Value.Lat, 2);
        Assert.Equal(lon, p.Value.Lon, 2);
    }

    [Fact]
    public void A_time_zone_is_found_in_the_table()
    {
        const string tab = "# comment\nUS\t+404251-0740023\tAmerica/New_York\tEastern (most areas)\nNL\t+5222+00454\tEurope/Amsterdam\n";
        Assert.Equal(52.367, NightLight.PlaceOf("Europe/Amsterdam", tab)!.Value.Lat, 2);
        Assert.Null(NightLight.PlaceOf("Mars/Olympus", tab));
    }

    [Fact]
    public void Sunrise_and_sunset_match_the_almanac()
    {
        // New York, midsummer: about 05:25 and 20:31 EDT
        var ny = NightLight.SunTimes(new DateOnly(2026, 6, 21), 40.7128, -74.006);
        AssertNear(new DateTime(2026, 6, 21, 9, 25, 0, DateTimeKind.Utc), ny.Rise!.Value);
        AssertNear(new DateTime(2026, 6, 22, 0, 31, 0, DateTimeKind.Utc), ny.Set!.Value);
        // Amsterdam, late November: about 08:20 and 16:33 CET
        var ams = NightLight.SunTimes(new DateOnly(2026, 11, 26), 52.37, 4.9);
        AssertNear(new DateTime(2026, 11, 26, 7, 20, 0, DateTimeKind.Utc), ams.Rise!.Value);
        AssertNear(new DateTime(2026, 11, 26, 15, 35, 0, DateTimeKind.Utc), ams.Set!.Value);
        // Tromsø: polar night in December, midnight sun in June
        var winter = NightLight.SunTimes(new DateOnly(2026, 12, 21), 69.65, 18.96);
        Assert.True(winter.Rise == null && winter.DarkAllDay);
        var summer = NightLight.SunTimes(new DateOnly(2026, 6, 21), 69.65, 18.96);
        Assert.True(summer.Rise == null && !summer.DarkAllDay);
    }

    private static void AssertNear(DateTime expected, DateTime actual) =>
        Assert.True(Math.Abs((expected - actual).TotalMinutes) < 6, $"expected about {expected:u}, got {actual:u}");

    [Theory]
    [InlineData("21:00", "07:00", 23, true)]
    [InlineData("21:00", "07:00", 6, true)]
    [InlineData("21:00", "07:00", 12, false)]
    [InlineData("21:00", "07:00", 7, false)]
    [InlineData("08:00", "17:00", 12, true)]
    [InlineData("08:00", "17:00", 20, false)]
    public void Set_hours_work_across_midnight(string from, string to, int hour, bool on) =>
        Assert.Equal(on, NightLight.Scheduled(new DateTime(2026, 11, 26, hour, 0, 0), "hours", from, to, null));

    [Fact]
    public void Sunset_to_sunrise_follows_the_sun()
    {
        var ny = (40.7128, -74.006);
        Assert.True(NightLight.Scheduled(new DateTime(2026, 6, 22, 2, 0, 0, DateTimeKind.Utc), "sunset", "", "", ny));    // 22:00 EDT
        Assert.False(NightLight.Scheduled(new DateTime(2026, 6, 21, 16, 0, 0, DateTimeKind.Utc), "sunset", "", "", ny));  // noon EDT
        Assert.Null(NightLight.Scheduled(DateTime.Now, "off", "", "", ny));
        Assert.Null(NightLight.Scheduled(DateTime.Now, "sunset", "", "", null));    // place unknown: by hand only
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void The_night_light_tile_says_until_when()
    {
        var s = SettingsStore.Current;
        var (on, sched, from, to, h24) = (s.NightLight, s.NightLightSchedule, s.NightLightFrom, s.NightLightTo, s.Clock24h);
        try
        {
            s.NightLightSchedule = "hours"; s.NightLightFrom = "21:00"; s.NightLightTo = "07:00"; s.Clock24h = true;
            s.NightLight = true;
            Assert.Equal("On until 07:00", QuickSettingsPanel.NightLightStatus(DateTime.Now));
            s.NightLight = false;
            Assert.Equal("Off until 21:00", QuickSettingsPanel.NightLightStatus(DateTime.Now));
            s.NightLightSchedule = "off";
            Assert.Equal("Off", QuickSettingsPanel.NightLightStatus(DateTime.Now));
        }
        finally { (s.NightLight, s.NightLightSchedule, s.NightLightFrom, s.NightLightTo, s.Clock24h) = (on, sched, from, to, h24); }
    }
}
