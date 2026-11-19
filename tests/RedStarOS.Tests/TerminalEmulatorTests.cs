using System.Text;
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>The xterm emulator: the same checks that were run against vim, less, top and nano.</summary>
public class TerminalEmulatorTests
{
    private static TermEmulator Feed(string s, int cols = 80, int rows = 24)
    {
        var e = new TermEmulator(cols, rows);
        e.Feed(s);
        return e;
    }

    private static string Row(TermEmulator e, int r) => e.ScreenLine(r).GetText();

    [Fact]
    public void Prints_text_and_moves_to_new_lines()
    {
        var e = Feed("hello\r\nworld");
        Assert.Equal("hello", Row(e, 0));
        Assert.Equal("world", Row(e, 1));
        Assert.Equal(5, e.CursorX);
    }

    [Fact]
    public void Tabs_stop_every_eight_columns() => Assert.Equal("a       b", Row(Feed("a\tb"), 0));

    [Fact]
    public void Long_lines_wrap_and_are_marked_as_wrapped()
    {
        var e = Feed(new string('x', 100), cols: 80);
        Assert.Equal(new string('x', 80), Row(e, 0));
        Assert.Equal(new string('x', 20), Row(e, 1));
        Assert.True(e.ScreenLine(0).Wrapped);
    }

    [Fact]
    public void Colours_16_256_and_true_colour()
    {
        var e = Feed("\u001b[1;31mR\u001b[0m\u001b[38;5;208mO\u001b[38;2;10;20;30mT\u001b[48:2::1:2:3mU");
        var c = e.ScreenLine(0).Cells;
        Assert.Equal(TermAttr.Palette(1), c[0].Attr.Fg);
        Assert.True(c[0].Attr.Flags.HasFlag(CellFlags.Bold));
        Assert.Equal(TermAttr.Palette(208), c[1].Attr.Fg);
        Assert.Equal(TermAttr.Rgb(10, 20, 30), c[2].Attr.Fg);
        Assert.Equal(TermAttr.Rgb(1, 2, 3), c[3].Attr.Bg);
    }

    [Fact]
    public void Wide_characters_take_two_cells_and_combining_marks_join()
    {
        var e = Feed("中é");
        var c = e.ScreenLine(0).Cells;
        Assert.Equal(2, c[0].Width);
        Assert.Equal(0, c[1].Width);
        Assert.Equal("é", c[2].Text);
        Assert.Equal(3, e.CursorX);
    }

    [Fact]
    public void Alternate_screen_keeps_the_main_screen()
    {
        var e = Feed("prompt$ ");
        e.Feed("\u001b[?1049h\u001b[2J\u001b[Hfull screen app");
        Assert.True(e.IsAltScreen);
        Assert.Equal("full screen app", Row(e, 0));
        e.Feed("\u001b[?1049l");
        Assert.False(e.IsAltScreen);
        Assert.Equal("prompt$", Row(e, 0));
    }

    [Fact]
    public void Scroll_region_insert_and_delete_lines()
    {
        var e = Feed("1\r\n2\r\n3\r\n4\r\n5", cols: 10, rows: 5);
        e.Feed("\u001b[2;4r\u001b[2;1H\u001b[L");      // insert a line at row 2 inside rows 2-4
        Assert.Equal(new[] { "1", "", "2", "3", "5" }, Enumerable.Range(0, 5).Select(r => Row(e, r)));
        e.Feed("\u001b[2;1H\u001b[M");                 // and delete it again
        Assert.Equal(new[] { "1", "2", "3", "", "5" }, Enumerable.Range(0, 5).Select(r => Row(e, r)));
    }

    [Fact]
    public void Erase_in_line_and_display()
    {
        var e = Feed("abcdef\r\nghijkl");
        e.Feed("\u001b[1;3H\u001b[K");
        Assert.Equal("ab", Row(e, 0));
        e.Feed("\u001b[2;1H\u001b[2K");
        Assert.Equal("", Row(e, 1));
    }

    [Fact]
    public void Replies_to_cursor_position_and_device_attribute_queries()
    {
        var e = new TermEmulator(80, 24);
        var replies = new List<string>();
        e.Reply += replies.Add;
        e.Feed("\u001b[5;10H\u001b[6n\u001b[c\u001b[>c");
        Assert.Equal(new[] { "\u001b[5;10R", "\u001b[?62;22c", "\u001b[>0;276;0c" }, replies);
    }

    [Fact]
    public void Title_and_modes()
    {
        var e = Feed("\u001b]0;my title\u0007\u001b[?2004h\u001b[?1006;1000h\u001b[?1h");
        Assert.Equal("my title", e.Title);
        Assert.True(e.BracketedPaste);
        Assert.Equal(1000, e.MouseMode);
        Assert.Equal(1006, e.MouseEncoding);
        Assert.True(e.AppCursorKeys);
    }

    [Fact]
    public void Dec_line_drawing_characters()
    {
        var e = Feed("\u001b(0lqk\u001b(B");
        Assert.Equal("┌─┐", Row(e, 0));
    }

    [Fact]
    public void Reflow_narrow_then_wide_restores_lines_and_cursor()
    {
        var e = new TermEmulator(20, 5);
        e.Feed("$ ls\r\n" + new string('a', 35) + "\r\n$ ");
        e.Resize(10, 5);
        var all = Enumerable.Range(0, e.TotalLines).Select(i => e.GetLine(i).GetText()).Where(t => t.Length > 0).ToList();
        Assert.Equal(new[] { "$ ls", "aaaaaaaaaa", "aaaaaaaaaa", "aaaaaaaaaa", "aaaaa", "$" }, all);
        e.Resize(20, 5);
        all = Enumerable.Range(0, e.TotalLines).Select(i => e.GetLine(i).GetText()).Where(t => t.Length > 0).ToList();
        Assert.Equal(new[] { "$ ls", new string('a', 20), new string('a', 15), "$" }, all);
        Assert.Equal(2, e.CursorX);
        Assert.Equal("$", Row(e, e.CursorY));
    }

    [Fact]
    public void Taller_window_brings_scrollback_back()
    {
        var e = new TermEmulator(20, 4);
        e.Feed("l1\r\nl2\r\nl3\r\nl4\r\nl5\r\nl6\r\n$ ");
        e.Resize(20, 7);
        Assert.Equal(new[] { "l1", "l2", "l3", "l4", "l5", "l6", "$" }, Enumerable.Range(0, 7).Select(r => Row(e, r)));
        Assert.Equal(0, e.ScrollbackCount);
    }

    [Fact]
    public void Scrollback_ring_drops_the_oldest_lines()
    {
        var e = new TermEmulator(10, 3, scrollback: 100);
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++) sb.Append(i).Append("\r\n");
        e.Feed(sb.ToString());
        Assert.Equal(100, e.ScrollbackCount);
        Assert.True(e.Dropped > 0);
    }

    [Fact]
    public void Shell_runs_on_a_real_pty()
    {
        if (!OperatingSystem.IsLinux()) return;
        var output = new StringBuilder();
        var done = new ManualResetEventSlim();
        using var pty = new PtySession();
        pty.Output += (d, n) => { lock (output) output.Append(Encoding.UTF8.GetString(d, 0, n)); };
        pty.Exited += _ => done.Set();
        pty.Start(80, 24, Path.GetTempPath(), new[] { "/bin/sh", "-c", "tty; stty size; echo done" });
        Assert.True(done.Wait(10000, TestContext.Current.CancellationToken), "the command didn't finish");
        string text; lock (output) text = output.ToString();
        Assert.Contains("/dev/pts/", text);
        Assert.Contains("24 80", text);
        Assert.Contains("done", text);
    }
}
