// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>The Crimson Banner icon theme: which programs get a picture on the tile, and what that picture is.</summary>
public class IconThemeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "icons-" + Guid.NewGuid().ToString("N"));

    public IconThemeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Own(string name)
    {
        var p = Path.Combine(_dir, "own", name + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[] { 1 });
        return p;
    }

    [Fact]
    public void Programs_the_theme_draws_or_without_an_icon_get_no_picture()
    {
        var own = new Dictionary<string, string> { ["firefox"] = Own("firefox"), ["org.gnome.Nautilus"] = Own("nautilus") };
        var jobs = AdaptiveIcons.Plan(new[] { "firefox", "org.gnome.Nautilus", "no-icon-anywhere", "/opt/app/icon.png", null, "", "firefox" },
            new HashSet<string> { "org.gnome.Nautilus" }, i => own.TryGetValue(i, out var p) ? p : null, Path.Combine(_dir, "theme"));
        var job = Assert.Single(jobs);                    // firefox once; Nautilus is drawn; the rest have nothing to set
        Assert.Equal("firefox", job.Icon);
    }

    [Fact]
    public void A_picture_is_made_again_only_when_the_programs_icon_is_newer()
    {
        var src = Own("steam");
        var made = Path.Combine(_dir, "theme", "128x128", "apps", "steam.png");
        Directory.CreateDirectory(Path.GetDirectoryName(made)!);
        File.WriteAllBytes(made, new byte[] { 1 });
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(made, DateTime.UtcNow);
        Assert.Empty(AdaptiveIcons.Plan(new[] { "steam" }, new HashSet<string>(), _ => src, Path.Combine(_dir, "theme")));
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddDays(1));          // the program was updated
        Assert.Single(AdaptiveIcons.Plan(new[] { "steam" }, new HashSet<string>(), _ => src, Path.Combine(_dir, "theme")));
    }

    [AvaloniaFact]
    public void A_programs_icon_sits_on_the_graphite_tile()
    {
        // a plain white square for the program's own icon
        var own = new WriteableBitmap(new PixelSize(96, 96), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = own.Lock())
        {
            var white = new int[96 * 96];
            Array.Fill(white, -1);
            Marshal.Copy(white, 0, fb.Address, white.Length);
        }
        using var pic = AdaptiveIcons.Draw(own, 64);
        Assert.Equal(new PixelSize(64, 64), pic.PixelSize);
        var path = Path.Combine(_dir, "out.png");
        pic.Save(path);
        using var back = new Bitmap(path);
        var px = Pixels(back);
        uint At(int x, int y) => px[y * 64 + x];
        Assert.Equal(0u, At(0, 0) >> 24);                              // the tile's rounded corner is see-through
        Assert.Equal(0xFFFFFFFFu, At(32, 32));                         // the program's icon in the middle
        uint edge = At(32, 5);                                         // the tile between the icon and its edge
        Assert.Equal(0xFFu, edge >> 24);
        Assert.True((edge & 0xFF) < 0x40, $"the tile is graphite, not {edge:X8}");
    }

    private static uint[] Pixels(Bitmap b)
    {
        var w = b.PixelSize.Width; var h = b.PixelSize.Height;
        var raw = Marshal.AllocHGlobal(w * h * 4);
        try
        {
            b.CopyPixels(new PixelRect(0, 0, w, h), raw, w * h * 4, w * 4);
            var ints = new int[w * h];
            Marshal.Copy(raw, ints, 0, ints.Length);
            return Array.ConvertAll(ints, i => unchecked((uint)i));
        }
        finally { Marshal.FreeHGlobal(raw); }
    }

    [Fact]
    public void The_chosen_icon_theme_wins_over_ours()
    {
        var s = new Settings { IconTheme = "Papirus" };
        Assert.Equal("Papirus", LookAndFeel.EffectiveIconTheme(s));
    }
}
