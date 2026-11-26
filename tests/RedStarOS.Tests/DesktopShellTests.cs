// SPDX-License-Identifier: GPL-3.0-or-later
using RedStarOS.Services;
using RedStarOS.Views;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>The Windows-like parts of the desktop: icon places, Show desktop, Snap Assist's half, jump lists.</summary>
public class DesktopShellTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "desk-test-" + Guid.NewGuid().ToString("N"));
    public DesktopShellTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Icons_fill_down_the_columns_and_keep_where_they_were_put()
    {
        var l = new DesktopLayout();
        var at = l.Place(new[] { "a", "b", "c", "d", "e" }, rows: 2, cols: 4);
        Assert.Equal(new[] { (0, 0), (0, 1), (1, 0), (1, 1), (2, 0) }, new[] { at["a"], at["b"], at["c"], at["d"], at["e"] });

        // c dragged to the far corner: it stays there, and the others close up around it
        l.Cells["c"] = new[] { 3, 1 };
        at = l.Place(new[] { "a", "b", "c", "d", "e" }, 2, 4);
        Assert.Equal((3, 1), at["c"]);
        Assert.Equal(new[] { (0, 0), (0, 1), (1, 0), (1, 1) }, new[] { at["a"], at["b"], at["d"], at["e"] });

        // a place off a smaller screen, or taken twice, is given up
        l.Cells["d"] = new[] { 9, 9 };
        l.Cells["e"] = new[] { 3, 1 };
        at = l.Place(new[] { "a", "b", "c", "d", "e" }, 2, 4);
        Assert.Equal(5, at.Values.Distinct().Count());
        Assert.Equal((3, 1), at["c"]);

        // auto arrange ignores kept places
        l.AutoArrange = true;
        Assert.Equal((1, 0), l.Place(new[] { "a", "b", "c" }, 2, 4)["c"]);
    }

    [Fact]
    public void Dragged_icons_land_on_the_nearest_free_place()
    {
        var at = new Dictionary<string, (int Col, int Row)> { ["a"] = (0, 0), ["b"] = (0, 1), ["c"] = (1, 0) };
        var moved = DesktopLayout.Move(at, new[] { "a" }, dCol: 1, dRow: 0, rows: 3, cols: 3);
        Assert.Equal((0, 1), moved["b"]);
        Assert.Equal((1, 0), moved["c"]);
        Assert.NotEqual((1, 0), moved["a"]);                      // c is there: the nearest free place instead
        Assert.Equal(1, Math.Abs(moved["a"].Col - 1) + Math.Abs(moved["a"].Row - 0));
        // both moved together, past the edge: kept on the grid
        moved = DesktopLayout.Move(at, new[] { "a", "b" }, dCol: 9, dRow: 0, rows: 3, cols: 3);
        Assert.Equal((2, 0), moved["a"]);
        Assert.Equal((2, 1), moved["b"]);
    }

    [Fact]
    public void Sorting_puts_folders_first()
    {
        var items = new[] { ("b.txt", false, 10L, 3), ("Zeta", true, 0L, 1), ("a.png", false, 99L, 2), ("Alpha", true, 0L, 4) };
        string[] Names(DesktopSort s) => DesktopLayout.Ordered(items, s, i => i.Item1, i => i.Item2, i => i.Item3, i => new DateTime(2026, 1, i.Item4)).Select(i => i.Item1).ToArray();
        Assert.Equal(new[] { "Alpha", "Zeta", "a.png", "b.txt" }, Names(DesktopSort.Name));
        Assert.Equal(new[] { "Alpha", "Zeta", "a.png", "b.txt" }, Names(DesktopSort.Size));
        Assert.Equal(new[] { "Alpha", "Zeta", "a.png", "b.txt" }, Names(DesktopSort.Type));
        Assert.Equal(new[] { "Alpha", "Zeta", "b.txt", "a.png" }, Names(DesktopSort.Date));      // newest first
    }

    [Fact]
    public void The_layout_is_kept_between_sessions()
    {
        var file = Path.Combine(_dir, "desktop.json");
        var l = new DesktopLayout { Size = DesktopIconSize.Large, Sort = DesktopSort.Date, ShowIcons = false };
        l.Keep(new Dictionary<string, (int, int)> { ["x"] = (2, 3) });
        l.Save(file);
        var back = DesktopLayout.Load(file);
        Assert.Equal((DesktopIconSize.Large, DesktopSort.Date, false), (back.Size, back.Sort, back.ShowIcons));
        Assert.Equal(new[] { 2, 3 }, back.Cells["x"]);
        Assert.Equal(DesktopIconSize.Medium, DesktopLayout.Load(Path.Combine(_dir, "none.json")).Size);
    }

    [Fact]
    public void Show_desktop_hides_the_windows_then_brings_the_same_ones_back()
    {
        var wins = new List<X11Desktop.XWin> { new(1, "One", "a", 0, false), new(2, "Two", "b", 0, true), new(3, "Three", "c", 0, false), new(9, "Desktop", "d", 0, false) };
        var (hide, restore) = ShowDesktop.Plan(wins, Array.Empty<long>(), desktop: 9);
        Assert.Equal(new long[] { 1, 3 }, hide);                     // not the one already minimised, nor the desktop
        Assert.Empty(restore);
        var after = wins.Select(w => w with { Minimized = w.Id != 9 || w.Minimized }).ToList();
        (hide, restore) = ShowDesktop.Plan(after, new long[] { 1, 3 }, 9);
        Assert.Equal(new long[] { 1, 3 }, restore);
        // a window opened meanwhile: the press hides it (and everything) instead
        after.Add(new(4, "New", "e", 0, false));
        (hide, restore) = ShowDesktop.Plan(after, new long[] { 1, 3 }, 9);
        Assert.Equal(new long[] { 4 }, hide);
        Assert.Empty(restore);
    }

    [Theory]
    [InlineData("left", "right")]
    [InlineData("bottom-left", "right")]
    [InlineData("right", "left")]
    [InlineData("top-right", "left")]
    [InlineData("maximize", null)]
    public void Snap_assist_offers_the_half_left_over(string zone, string? fill) => Assert.Equal(fill, SnapAssist.Remaining(zone));

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_taskbar_preview_shows_the_whole_window_not_a_corner_of_it()
    {
        // a big window, red to its edges: its picture must be red to its edges too
        var w = new Avalonia.Controls.Window { Width = 1200, Height = 800, Background = Avalonia.Media.Brushes.Red };
        w.Show();
        try
        {
            var bmp = TaskPreview.Thumbnail(w);
            Assert.NotNull(bmp);
            Assert.True(bmp!.PixelSize.Width <= TaskPreview.ThumbWidth * 2 && bmp.PixelSize.Height <= TaskPreview.ThumbHeight * 2, $"{bmp.PixelSize}");
            int pw = bmp.PixelSize.Width, ph = bmp.PixelSize.Height;
            var px = new byte[pw * ph * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { bmp.CopyPixels(new Avalonia.PixelRect(0, 0, pw, ph), handle.AddrOfPinnedObject(), px.Length, pw * 4); }
            finally { handle.Free(); }
            foreach (var (x, y) in new[] { (2, 2), (pw - 3, 2), (2, ph - 3), (pw - 3, ph - 3), (pw / 2, ph / 2) })
            {
                int i = (y * pw + x) * 4;                     // BGRA
                Assert.True(px[i + 2] > 200 && px[i + 1] < 60 && px[i] < 60 && px[i + 3] > 200, $"({x},{y}) is {px[i + 2]},{px[i + 1]},{px[i]},{px[i + 3]}");
            }
        }
        finally { w.Close(); }
    }

    [Fact]
    public void Jump_lists_are_the_files_each_app_opens()
    {
        var recent = new[] { "/d/report.docx", "/d/photo.png", "/d/budget.xlsx", "/d/notes.txt", "/d/report.docx", "/d/song.mp3", "/d/gone.docx" };
        bool Exists(string p) => p != "/d/gone.docx";
        Assert.Equal(new[] { "/d/report.docx", "/d/notes.txt" }, JumpList.Recent("writer", recent, exists: Exists));
        Assert.Equal(new[] { "/d/photo.png" }, JumpList.Recent("imageviewer", recent, exists: Exists));
        Assert.Equal(new[] { "/d/report.docx", "/d/budget.xlsx", "/d/notes.txt" }, JumpList.Recent("studio", recent, exists: Exists));
        Assert.Empty(JumpList.Recent("calculator", recent, exists: Exists));
        Assert.Single(JumpList.Recent("writer", recent, max: 1, exists: Exists));
    }
}
