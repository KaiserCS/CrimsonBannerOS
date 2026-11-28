// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

public class LookAndFeelTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "rs-ini-" + Guid.NewGuid().ToString("N"), "settings.ini");

    [Fact]
    public void Ini_keys_are_set_and_everything_else_is_kept()
    {
        var path = Temp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# mine\n[Settings]\ngtk-theme-name=Old\ngtk-enable-animations=0\n\n[Other]\nx=1\n");
        LookAndFeel.MergeIni(path, "Settings", new[] { new KeyValuePair<string, string>("gtk-theme-name", "Adwaita"),
                                                        new KeyValuePair<string, string>("gtk-cursor-theme-size", "48") });
        var lines = File.ReadAllLines(path);
        Assert.Contains("# mine", lines);
        Assert.Contains("gtk-theme-name=Adwaita", lines);
        Assert.Contains("gtk-enable-animations=0", lines);
        Assert.Contains("x=1", lines);
        Assert.DoesNotContain("gtk-theme-name=Old", lines);
        // the new key stays in [Settings], not under [Other]
        Assert.True(Array.IndexOf(lines, "gtk-cursor-theme-size=48") < Array.IndexOf(lines, "[Other]"));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void A_missing_file_gets_its_section()
    {
        var path = Temp();
        LookAndFeel.MergeIni(path, "Appearance", new[] { new KeyValuePair<string, string>("style", "Fusion") });
        Assert.Equal(new[] { "[Appearance]", "style=Fusion" }, File.ReadAllLines(path));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Gtk_fonts_are_in_points()
    {
        var s = new Settings { UiFont = "Noto Sans", UiFontSize = 14 };
        Assert.Equal("Noto Sans 10.5", LookAndFeel.GtkFont(s));
        s.UiFontSize = 16;
        Assert.Equal("Noto Sans 12", LookAndFeel.GtkFont(s));
    }

    [Fact]
    public void Dark_and_the_chosen_themes_reach_gtk()
    {
        var s = new Settings { GtkTheme = "Arc", IconTheme = "Papirus", CursorTheme = "Bibata" };
        var keys = LookAndFeel.GtkKeys(s, dark: true).ToDictionary(k => k.Key, k => k.Value);
        Assert.Equal("1", keys["gtk-application-prefer-dark-theme"]);
        Assert.Equal("Arc", keys["gtk-theme-name"]);
        Assert.Equal("Papirus", keys["gtk-icon-theme-name"]);
        Assert.Equal("Bibata", keys["gtk-cursor-theme-name"]);
        Assert.False(keys.ContainsKey("gtk-font-name"));      // no font chosen: GTK keeps its own
    }

    [Fact]
    public void A_persons_own_copy_of_the_gtk_theme_follows_their_accent()
    {
        const string ours = "[X-GNOME-Metatheme]\nGtkTheme=CrimsonBanner\n# accent: #0078D4\n";
        // crimson: the system's copy is crimson, so no copy of their own; one of ours there is removed
        Assert.Equal((false, true, false), LookAndFeel.UserGtkThemePlan("#C8102E", ours));
        // the same accent as the copy: nothing to do
        Assert.Equal((true, true, true), LookAndFeel.UserGtkThemePlan("#0078d4", ours));
        // another accent: made again
        Assert.Equal((true, true, false), LookAndFeel.UserGtkThemePlan("#107C10", ours));
        // a folder of the person's own by that name: never touched
        Assert.Equal((true, false, false), LookAndFeel.UserGtkThemePlan("#107C10", "[Desktop Entry]\nName=Mine\n"));
        Assert.Equal((true, false, false), LookAndFeel.UserGtkThemePlan("#107C10", null));
    }
}
