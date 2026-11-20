// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Gems' rules: an 8×8 board of six kinds, swaps that must make a line of three, chains that fall and refill,
/// and a fresh board when no move is left. Public for the tests.</summary>
public sealed class GemsGame
{
    public const int Size = 8, Kinds = 6;
    public readonly int[,] Board = new int[Size, Size];      // [x, y], y = 0 at the top
    public int Score { get; private set; }
    public int Moves { get; private set; }
    private readonly Random _rng;

    public GemsGame(int? seed = null)
    {
        _rng = seed is { } s ? new Random(s) : new Random();
        Fill();
    }

    /// <summary>A board with no lines already made and at least one move.</summary>
    public void Fill()
    {
        do
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int k;
                    do k = _rng.Next(Kinds);
                    while ((x >= 2 && Board[x - 1, y] == k && Board[x - 2, y] == k) || (y >= 2 && Board[x, y - 1] == k && Board[x, y - 2] == k));
                    Board[x, y] = k;
                }
        } while (FindMove() == null);
    }

    /// <summary>Cells in lines of three or more.</summary>
    public HashSet<(int X, int Y)> Matches()
    {
        var hit = new HashSet<(int, int)>();
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; )
            {
                int k = Board[x, y], n = 1;
                while (x + n < Size && Board[x + n, y] == k && k >= 0) n++;
                if (n >= 3) for (int i = 0; i < n; i++) hit.Add((x + i, y));
                x += n;
            }
        for (int x = 0; x < Size; x++)
            for (int y = 0; y < Size; )
            {
                int k = Board[x, y], n = 1;
                while (y + n < Size && Board[x, y + n] == k && k >= 0) n++;
                if (n >= 3) for (int i = 0; i < n; i++) hit.Add((x, y + i));
                y += n;
            }
        return hit;
    }

    private void Swap(int ax, int ay, int bx, int by) => (Board[ax, ay], Board[bx, by]) = (Board[bx, by], Board[ax, ay]);

    public static bool Adjacent(int ax, int ay, int bx, int by) => Math.Abs(ax - bx) + Math.Abs(ay - by) == 1;

    /// <summary>A swap that would make a line, if any (for hints and to know the game can go on).</summary>
    public (int AX, int AY, int BX, int BY)? FindMove()
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
                {
                    int bx = x + dx, by = y + dy;
                    if (bx >= Size || by >= Size) continue;
                    Swap(x, y, bx, by);
                    bool ok = Matches().Count > 0;
                    Swap(x, y, bx, by);
                    if (ok) return (x, y, bx, by);
                }
        return null;
    }

    /// <summary>Swaps two neighbours if that makes a line; false (and nothing changed) otherwise.</summary>
    public bool TrySwap(int ax, int ay, int bx, int by)
    {
        if (!Adjacent(ax, ay, bx, by)) return false;
        Swap(ax, ay, bx, by);
        if (Matches().Count == 0) { Swap(ax, ay, bx, by); return false; }
        Moves++;
        return true;
    }

    /// <summary>One round of a chain: clears the lines (scoring more for longer lines and later links of the chain),
    /// lets the rest fall and fills from the top. Returns the cleared cells and, per column, how far each gem fell.
    /// Empty when nothing was left to clear.</summary>
    public (HashSet<(int X, int Y)> Cleared, int[,] Fell) Settle(int chain)
    {
        var hit = Matches();
        var fell = new int[Size, Size];
        if (hit.Count == 0) return (hit, fell);
        Score += hit.Count * 10 * chain + Math.Max(0, hit.Count - 3) * 20;
        foreach (var (x, y) in hit) Board[x, y] = -1;
        for (int x = 0; x < Size; x++)
        {
            int write = Size - 1;
            for (int y = Size - 1; y >= 0; y--)
                if (Board[x, y] >= 0)
                {
                    if (write != y) { Board[x, write] = Board[x, y]; fell[x, write] = write - y; }
                    write--;
                }
            for (int y = write; y >= 0; y--) { Board[x, y] = _rng.Next(Kinds); fell[x, y] = write + 1; }
        }
        return (hit, fell);
    }
}

/// <summary>Gems: swap two neighbouring gems to line up three or more of a kind. Lines vanish, the gems above fall,
/// and new ones drop in; chains score more. Click two gems or drag one onto its neighbour. H shows a move.</summary>
public sealed class GemsWindow : Window
{
    private GemsGame _game = new();
    private readonly GemsView _view;
    private readonly TextBlock _score = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _best = new() { Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };

    public GemsWindow()
    {
        Title = "Gems";
        Width = 620; Height = 720; MinWidth = 420; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _view = new GemsView(() => _game, Refresh);
        Button B(string text, Action act) { var b = new Button { Content = text }; b.Click += (_, _) => act(); return b; }
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12, 10),
            Children = { B("New game (F2)", NewGame), B("Hint (H)", () => _view.ShowHint()), _score, _best }
        };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_view);
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2) NewGame();
            else if (e.Key == Key.H) _view.ShowHint();
            else return;
            e.Handled = true;
        };
        Opened += (_, _) => Refresh();
    }

    private void NewGame() { _game = new GemsGame(); _view.Reset(); Refresh(); }

    private void Refresh()
    {
        var s = SettingsStore.Current;
        if (_game.Score > s.GemsBest) { s.GemsBest = _game.Score; SettingsStore.Save(); }
        _score.Text = $"{_game.Score:N0} points";
        _best.Text = $"{_game.Moves} moves · best {s.GemsBest:N0}";
        _view.InvalidateVisual();
    }
}

internal sealed class GemsView : Control
{
    private readonly Func<GemsGame> _game;
    private readonly Action _changed;
    private (int X, int Y)? _selected, _pressed;
    private (int, int, int, int)? _hint;
    private double[,] _drop = new double[GemsGame.Size, GemsGame.Size];        // how many cells above its place each gem still is
    private HashSet<(int X, int Y)> _vanish = new();
    private double _vanishT;
    private int _chain;
    private bool _busy;
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private DateTime _last;
    private double _cell, _ox, _oy;

    private static readonly Color[] Palette =
    {
        Color.Parse("#E53E3E"), Color.Parse("#3182CE"), Color.Parse("#38A169"), Color.Parse("#D69E2E"), Color.Parse("#805AD5"), Color.Parse("#DD6B20")
    };

    public GemsView(Func<GemsGame> game, Action changed)
    {
        _game = game; _changed = changed; ClipToBounds = true;
        _anim.Tick += (_, _) => Animate();
    }

    public void Reset() { _selected = null; _hint = null; _busy = false; _anim.Stop(); _drop = new double[GemsGame.Size, GemsGame.Size]; _vanish.Clear(); InvalidateVisual(); }
    public void ShowHint() { if (!_busy) { _hint = _game().FindMove(); InvalidateVisual(); } }

    private void Begin()
    {
        _busy = true; _chain = 0;
        Next();
        _last = DateTime.UtcNow;
        _anim.Start();
    }

    // the next link of the chain: what vanishes now, then the fall
    private void Next()
    {
        var g = _game();
        _vanish = g.Matches();
        _vanishT = _vanish.Count > 0 ? 1 : 0;
        if (_vanish.Count == 0)
        {
            _busy = false; _anim.Stop();
            if (g.FindMove() == null) { g.Fill(); Notifier.Show("Gems", "No moves were left, so here's a fresh board.", "gems"); }
            _changed();
        }
    }

    private void Animate()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        var g = _game();
        if (_vanishT > 0)
        {
            _vanishT -= dt * 5;
            if (_vanishT <= 0)
            {
                var (_, fell) = g.Settle(++_chain);
                for (int x = 0; x < GemsGame.Size; x++) for (int y = 0; y < GemsGame.Size; y++) _drop[x, y] = fell[x, y];
                _vanish.Clear();
                _changed();
            }
        }
        else
        {
            bool moving = false;
            for (int x = 0; x < GemsGame.Size; x++)
                for (int y = 0; y < GemsGame.Size; y++)
                    if (_drop[x, y] > 0) { _drop[x, y] = Math.Max(0, _drop[x, y] - dt * 14); moving |= _drop[x, y] > 0; }
            if (!moving) Next();
        }
        InvalidateVisual();
    }

    private (int X, int Y)? CellAt(Point p)
    {
        int x = (int)Math.Floor((p.X - _ox) / _cell), y = (int)Math.Floor((p.Y - _oy) / _cell);
        return x >= 0 && y >= 0 && x < GemsGame.Size && y < GemsGame.Size ? (x, y) : null;
    }

    private void Try((int X, int Y) a, (int X, int Y) b)
    {
        _hint = null;
        if (_game().TrySwap(a.X, a.Y, b.X, b.Y)) { _selected = null; Begin(); }
        else { _selected = GemsGame.Adjacent(a.X, a.Y, b.X, b.Y) ? null : b; SystemSounds.Play("error"); }
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_busy || CellAt(e.GetPosition(this)) is not { } c) return;
        _pressed = c;
        if (_selected is { } s && s != c) { Try(s, c); _pressed = null; return; }
        _selected = _selected == c ? null : c;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_busy || _pressed is not { } p || CellAt(e.GetPosition(this)) is not { } c || c == p) return;
        if (GemsGame.Adjacent(p.X, p.Y, c.X, c.Y)) { _pressed = null; Try(p, c); }       // dragged onto a neighbour
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _pressed = null; }

    public override void Render(DrawingContext ctx)
    {
        var g = _game();
        _cell = Math.Floor(Math.Min(Bounds.Width - 24, Bounds.Height - 24) / GemsGame.Size);
        double side = _cell * GemsGame.Size;
        _ox = Math.Floor((Bounds.Width - side) / 2); _oy = Math.Floor((Bounds.Height - side) / 2);
        bool dark = ThemeService.IsDark;
        ctx.DrawRectangle(new SolidColorBrush(dark ? Color.Parse("#1A1A22") : Color.Parse("#ECECF2")), null, new Rect(_ox, _oy, side, side), 10, 10);
        for (int x = 0; x < GemsGame.Size; x++)
            for (int y = 0; y < GemsGame.Size; y++)
                if ((x + y) % 2 == 0)
                    ctx.DrawRectangle(new SolidColorBrush(dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0)), null,
                        new Rect(_ox + x * _cell, _oy + y * _cell, _cell, _cell));
        using (ctx.PushClip(new Rect(_ox, _oy, side, side)))
            for (int x = 0; x < GemsGame.Size; x++)
                for (int y = 0; y < GemsGame.Size; y++)
                {
                    int k = g.Board[x, y];
                    if (k < 0) continue;
                    double scale = _vanish.Contains((x, y)) ? Math.Max(0, _vanishT) : 1;
                    var c = new Point(_ox + (x + 0.5) * _cell, _oy + (y + 0.5 - _drop[x, y]) * _cell);
                    DrawGem(ctx, k, c, _cell * 0.38 * scale);
                }
        var pen = new Pen(new SolidColorBrush(dark ? Colors.White : Color.Parse("#1A202C")), 3);
        if (_selected is { } s) ctx.DrawRectangle(null, pen, new Rect(_ox + s.X * _cell + 3, _oy + s.Y * _cell + 3, _cell - 6, _cell - 6), 8, 8);
        if (_hint is { } h)
        {
            var hp = new Pen(new SolidColorBrush(Color.Parse("#D69E2E")), 3);
            ctx.DrawRectangle(null, hp, new Rect(_ox + h.Item1 * _cell + 3, _oy + h.Item2 * _cell + 3, _cell - 6, _cell - 6), 8, 8);
            ctx.DrawRectangle(null, hp, new Rect(_ox + h.Item3 * _cell + 3, _oy + h.Item4 * _cell + 3, _cell - 6, _cell - 6), 8, 8);
        }
    }

    /// <summary>Each kind has its own shape as well as its own colour, so it reads without telling colours apart.</summary>
    private static void DrawGem(DrawingContext ctx, int kind, Point c, double r)
    {
        if (r <= 0.5) return;
        var col = Palette[kind];
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.2, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.8, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Lighten(col, 0.35), 0), new GradientStop(col, 0.55), new GradientStop(Darken(col, 0.3), 1) }
        };
        var edge = new Pen(new SolidColorBrush(Darken(col, 0.45)), Math.Max(1, r * 0.08));
        Geometry shape = kind switch
        {
            0 => Polygon(c, r, 4, 0),                       // diamond
            1 => new EllipseGeometry(new Rect(c.X - r, c.Y - r, 2 * r, 2 * r)),   // round
            2 => Polygon(c, r * 1.1, 4, Math.PI / 4),        // square
            3 => Polygon(c, r * 1.1, 3, -Math.PI / 2),       // triangle
            4 => Polygon(c, r, 6, 0),                       // hexagon
            _ => Star(c, r * 1.1, r * 0.5, 5),              // star
        };
        ctx.DrawGeometry(fill, edge, shape);
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), null, new Point(c.X - r * 0.3, c.Y - r * 0.35), r * 0.22, r * 0.14);
    }

    private static Geometry Polygon(Point c, double r, int n, double start)
    {
        var g = new StreamGeometry();
        using var gc = g.Open();
        for (int i = 0; i < n; i++)
        {
            double a = start + i * 2 * Math.PI / n - (n == 4 && start == 0 ? Math.PI / 2 : 0);
            var p = new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
            if (i == 0) gc.BeginFigure(p, true); else gc.LineTo(p);
        }
        gc.EndFigure(true);
        return g;
    }

    private static Geometry Star(Point c, double outer, double inner, int n)
    {
        var g = new StreamGeometry();
        using var gc = g.Open();
        for (int i = 0; i < n * 2; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI / n, r = i % 2 == 0 ? outer : inner;
            var p = new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
            if (i == 0) gc.BeginFigure(p, true); else gc.LineTo(p);
        }
        gc.EndFigure(true);
        return g;
    }

    private static Color Lighten(Color c, double t) => Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
    private static Color Darken(Color c, double t) => Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));
}
