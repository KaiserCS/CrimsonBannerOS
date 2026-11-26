// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>The rules of Blocks, apart from drawing: a 10×20 well, seven pieces, rotation with wall kicks, line
/// clears, levels and scoring. Public for the tests.</summary>
public sealed class BlocksGame
{
    public const int Cols = 10, Rows = 20;

    // the seven pieces, each as its four rotations of four cells (x, y) in a 4×4 box
    private static readonly (int X, int Y)[][][] Shapes = Build();
    public static readonly string[] Colors = { "#00B5D8", "#ECC94B", "#9F7AEA", "#48BB78", "#E53E3E", "#3182CE", "#ED8936" };

    private static (int, int)[][][] Build()
    {
        string[][] art =
        {
            new[] { "....", "####", "....", "...." },   // I
            new[] { ".##.", ".##.", "....", "...." },   // O
            new[] { ".#..", "###.", "....", "...." },   // T
            new[] { ".##.", "##..", "....", "...." },   // S
            new[] { "##..", ".##.", "....", "...." },   // Z
            new[] { "#...", "###.", "....", "...." },   // J
            new[] { "..#.", "###.", "....", "...." },   // L
        };
        var all = new (int, int)[7][][];
        for (int k = 0; k < 7; k++)
        {
            var cells = new List<(int, int)>();
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) if (art[k][y][x] == '#') cells.Add((x, y));
            int size = k == 0 ? 4 : k == 1 ? 2 : 3;     // rotate inside the piece's own box
            all[k] = new (int, int)[4][];
            all[k][0] = cells.ToArray();
            for (int r = 1; r < 4; r++)
                all[k][r] = all[k][r - 1].Select(c => (size - 1 - c.Item2, c.Item1)).ToArray();
            if (k == 1) for (int r = 1; r < 4; r++) all[k][r] = all[k][0];   // O doesn't turn
        }
        return all;
    }

    public readonly int[,] Well = new int[Cols, Rows];       // 0 empty, else piece + 1
    public int Piece { get; private set; }
    public int Rotation { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Next { get; private set; }
    public int Score { get; private set; }
    public int Lines { get; private set; }
    public int Level => 1 + Lines / 10;
    public bool Over { get; private set; }

    private readonly Random _rng;
    private readonly Queue<int> _bag = new();

    public BlocksGame(int? seed = null)
    {
        _rng = seed is { } s ? new Random(s) : new Random();
        Next = Draw();
        Spawn();
    }

    /// <summary>Seconds between steps down at this level.</summary>
    public double StepSeconds => Math.Max(0.05, 0.8 * Math.Pow(0.85, Level - 1));

    public IEnumerable<(int X, int Y)> Cells(int piece, int rotation, int x, int y) =>
        Shapes[piece][rotation].Select(c => (x + c.X, y + c.Y));

    public IEnumerable<(int X, int Y)> Current => Cells(Piece, Rotation, X, Y);

    /// <summary>Where the piece would land.</summary>
    public int GhostY
    {
        get { int y = Y; while (Fits(Piece, Rotation, X, y + 1)) y++; return y; }
    }

    private int Draw()
    {
        if (_bag.Count == 0)
            foreach (var p in Enumerable.Range(0, 7).OrderBy(_ => _rng.Next())) _bag.Enqueue(p);   // every piece once per seven
        return _bag.Dequeue();
    }

    public bool Fits(int piece, int rotation, int x, int y)
    {
        foreach (var (cx, cy) in Cells(piece, rotation, x, y))
        {
            if (cx < 0 || cx >= Cols || cy >= Rows) return false;
            if (cy >= 0 && Well[cx, cy] != 0) return false;
        }
        return true;
    }

    private void Spawn()
    {
        Piece = Next; Next = Draw(); Rotation = 0;
        X = Piece == 1 ? 4 : 3; Y = Piece == 0 ? -1 : 0;
        if (!Fits(Piece, Rotation, X, Y)) Over = true;
    }

    public bool Move(int dx)
    {
        if (Over || !Fits(Piece, Rotation, X + dx, Y)) return false;
        X += dx;
        return true;
    }

    /// <summary>Turns the piece (dir 1 clockwise, -1 back), nudging it off a wall or the stack when it doesn't fit.</summary>
    public bool Rotate(int dir)
    {
        if (Over) return false;
        int r = (Rotation + dir + 4) % 4;
        foreach (var (kx, ky) in new[] { (0, 0), (-1, 0), (1, 0), (0, -1), (-2, 0), (2, 0), (-1, -1), (1, -1) })
            if (Fits(Piece, r, X + kx, Y + ky)) { Rotation = r; X += kx; Y += ky; return true; }
        return false;
    }

    /// <summary>One step down; lands the piece when it can't go further. True while it's still falling.</summary>
    public bool Step(bool soft = false)
    {
        if (Over) return false;
        if (Fits(Piece, Rotation, X, Y + 1)) { Y++; if (soft) Score += 1; return true; }
        Lock();
        return false;
    }

    public int HardDrop()
    {
        int n = 0;
        while (!Over && Fits(Piece, Rotation, X, Y + 1)) { Y++; n++; }
        Score += 2 * n;
        Lock();
        return n;
    }

    /// <summary>Full rows cleared by the last landing (for the flash).</summary>
    public int LastCleared { get; private set; }

    private void Lock()
    {
        foreach (var (cx, cy) in Current)
        {
            if (cy < 0) { Over = true; continue; }
            Well[cx, cy] = Piece + 1;
        }
        int cleared = 0;
        for (int y = Rows - 1; y >= 0; y--)
        {
            bool full = true;
            for (int x = 0; x < Cols; x++) if (Well[x, y] == 0) { full = false; break; }
            if (!full) continue;
            cleared++;
            for (int yy = y; yy > 0; yy--) for (int x = 0; x < Cols; x++) Well[x, yy] = Well[x, yy - 1];
            for (int x = 0; x < Cols; x++) Well[x, 0] = 0;
            y++;                                   // look at the same row again: it now holds the one above
        }
        LastCleared = cleared;
        Score += cleared switch { 1 => 100, 2 => 300, 3 => 500, 4 => 800, _ => 0 } * Level;
        Lines += cleared;
        if (!Over) Spawn();
    }
}

/// <summary>Blocks: the falling-blocks puzzle. ←/→ move, ↑ or X turns, Z turns back, ↓ drops faster, Space drops,
/// P pauses, F2 starts again. Pauses when the window loses focus.</summary>
public sealed class BlocksWindow : Window
{
    private BlocksGame _game = new();
    private readonly BlocksView _view;
    private readonly NextView _next;
    private readonly TextBlock _score = new() { FontSize = 22, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _info = new() { Classes = { "caption" } };
    private readonly TextBlock _best = new() { Classes = { "caption" } };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private DateTime _lastStep = DateTime.UtcNow;
    private bool _paused, _down;

    public BlocksWindow()
    {
        Title = "Blocks";
        Width = 560; Height = 720; MinWidth = 420; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _view = new BlocksView(() => _game, () => _paused);
        _next = new NextView(() => _game) { Height = 80 };

        var newGame = new Button { Content = "New game (F2)", HorizontalAlignment = HorizontalAlignment.Stretch, Focusable = false };
        newGame.Click += (_, _) => NewGame();
        var pause = new Button { Content = "Pause (P)", HorizontalAlignment = HorizontalAlignment.Stretch, Focusable = false };
        pause.Click += (_, _) => TogglePause();
        var side = new StackPanel
        {
            Width = 150, Spacing = 8, Margin = new Thickness(12, 16, 16, 16),
            Children =
            {
                new TextBlock { Text = "SCORE", Classes = { "eyebrow" } }, _score, _info, _best,
                new TextBlock { Text = "NEXT", Classes = { "eyebrow" }, Margin = new Thickness(0, 12, 0, 0) },
                _next,
                newGame, pause,
                new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
                    Text = "← → move\n↑ or X turn · Z back\n↓ faster · Space drop\nP pause · F2 new game" },
            }
        };
        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(new Border { Margin = new Thickness(16, 16, 0, 16), Child = _view });
        Content = root;

        _timer.Tick += (_, _) => Tick();
        // the game's keys first (tunnelling), so a focused control can't take them: Space on a button would press it
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyUp += (_, e) => { if (e.Key == Key.Down) _down = false; };
        Deactivated += (_, _) => { if (!_paused && !_game.Over) TogglePause(); };
        Opened += (_, _) => { Focus(); _timer.Start(); Refresh(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void NewGame()
    {
        _game = new BlocksGame();
        _paused = false;
        _lastStep = DateTime.UtcNow;
        Refresh();
    }

    private void TogglePause()
    {
        if (_game.Over) return;
        _paused = !_paused;
        _lastStep = DateTime.UtcNow;
        Refresh();
    }

    private void Tick()
    {
        if (_paused || _game.Over) return;
        var step = _down ? Math.Min(0.05, _game.StepSeconds) : _game.StepSeconds;
        if ((DateTime.UtcNow - _lastStep).TotalSeconds < step) return;
        _lastStep = DateTime.UtcNow;
        _game.Step(_down);
        Landed();
        Refresh();
    }

    private void Landed()
    {
        if (_game.LastCleared > 0) { _view.Flash(); }
        if (_game.Over)
        {
            var s = SettingsStore.Current;
            if (_game.Score > s.BlocksBest) { s.BlocksBest = _game.Score; SettingsStore.Save(); }
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2) { NewGame(); e.Handled = true; return; }
        if (e.Key is Key.P or Key.Escape) { TogglePause(); e.Handled = true; return; }
        if (_paused || _game.Over) return;
        switch (e.Key)
        {
            case Key.Left: _game.Move(-1); break;
            case Key.Right: _game.Move(1); break;
            case Key.Up: case Key.X: _game.Rotate(1); break;
            case Key.Z: _game.Rotate(-1); break;
            case Key.Down: _down = true; break;
            case Key.Space: _game.HardDrop(); _lastStep = DateTime.UtcNow; Landed(); break;
            default: return;
        }
        e.Handled = true;
        Refresh();
    }

    private void Refresh()
    {
        _score.Text = _game.Score.ToString("N0");
        _info.Text = $"Level {_game.Level} · {_game.Lines} lines";
        _best.Text = $"Best {Math.Max(SettingsStore.Current.BlocksBest, _game.Score):N0}";
        _view.InvalidateVisual();
        _next.InvalidateVisual();
    }

    private sealed class NextView : Control
    {
        private readonly Func<BlocksGame> _game;
        public NextView(Func<BlocksGame> game) => _game = game;

        public override void Render(DrawingContext ctx)
        {
            var g = _game();
            double cell = Math.Min(Bounds.Width / 4.5, Bounds.Height / 2.5);
            var cells = g.Cells(g.Next, 0, 0, 0).ToList();
            double minX = cells.Min(c => c.X), maxX = cells.Max(c => c.X), minY = cells.Min(c => c.Y), maxY = cells.Max(c => c.Y);
            double ox = (Bounds.Width - (maxX - minX + 1) * cell) / 2 - minX * cell, oy = (Bounds.Height - (maxY - minY + 1) * cell) / 2 - minY * cell;
            var brush = new SolidColorBrush(Color.Parse(BlocksGame.Colors[g.Next]));
            foreach (var (x, y) in cells) ctx.DrawRectangle(brush, null, new Rect(ox + x * cell + 1, oy + y * cell + 1, cell - 2, cell - 2), 3, 3);
        }
    }
}

internal sealed class BlocksView : Control
{
    private readonly Func<BlocksGame> _game;
    private readonly Func<bool> _paused;
    private DateTime _flashUntil;

    public BlocksView(Func<BlocksGame> game, Func<bool> paused) { _game = game; _paused = paused; ClipToBounds = true; }

    public void Flash() => _flashUntil = DateTime.UtcNow.AddMilliseconds(180);

    public override void Render(DrawingContext ctx)
    {
        var g = _game();
        double cell = Math.Floor(Math.Min(Bounds.Width / BlocksGame.Cols, Bounds.Height / BlocksGame.Rows));
        double w = cell * BlocksGame.Cols, h = cell * BlocksGame.Rows;
        double ox = Math.Floor((Bounds.Width - w) / 2), oy = Math.Floor((Bounds.Height - h) / 2);
        bool dark = ThemeService.IsDark;
        var well = new SolidColorBrush(dark ? Color.Parse("#16161C") : Color.Parse("#EDEDF2"));
        var grid = new Pen(new SolidColorBrush(dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0)), 1);
        ctx.DrawRectangle(well, null, new Rect(ox, oy, w, h), 6, 6);
        for (int x = 1; x < BlocksGame.Cols; x++) ctx.DrawLine(grid, new Point(ox + x * cell, oy), new Point(ox + x * cell, oy + h));
        for (int y = 1; y < BlocksGame.Rows; y++) ctx.DrawLine(grid, new Point(ox, oy + y * cell), new Point(ox + w, oy + y * cell));

        void Cell(int x, int y, IBrush b, double opacity = 1)
        {
            if (y < 0) return;
            var r = new Rect(ox + x * cell + 1, oy + y * cell + 1, cell - 2, cell - 2);
            using (ctx.PushOpacity(opacity))
            {
                ctx.DrawRectangle(b, null, r, 3, 3);
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, new Rect(r.X, r.Y, r.Width, r.Height * 0.3), 3, 3);
            }
        }
        for (int x = 0; x < BlocksGame.Cols; x++)
            for (int y = 0; y < BlocksGame.Rows; y++)
                if (g.Well[x, y] is var k and > 0) Cell(x, y, new SolidColorBrush(Color.Parse(BlocksGame.Colors[k - 1])));
        if (!g.Over)
        {
            var brush = new SolidColorBrush(Color.Parse(BlocksGame.Colors[g.Piece]));
            int gy = g.GhostY;
            foreach (var (x, y) in g.Cells(g.Piece, g.Rotation, g.X, gy)) Cell(x, y, brush, 0.22);
            foreach (var (x, y) in g.Current) Cell(x, y, brush);
        }
        if (DateTime.UtcNow < _flashUntil)
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), null, new Rect(ox, oy, w, h), 6, 6);
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        }
        string? msg = g.Over ? $"Game over\n{g.Score:N0} points\nF2 to play again" : _paused() ? "Paused\nP to carry on" : null;
        if (msg != null)
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 10, 10, 14)), null, new Rect(ox, oy, w, h), 6, 6);
            var ft = new FormattedText(msg, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 22, Brushes.White) { TextAlignment = TextAlignment.Center, MaxTextWidth = w };
            ctx.DrawText(ft, new Point(ox, oy + (h - ft.Height) / 2));
        }
    }
}
