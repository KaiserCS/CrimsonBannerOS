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
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>One tile of a Mahjongg board: where it sits (layer, and x/y in half-tile units) and its face.</summary>
public sealed class MahjonggTile
{
    public int Layer, X, Y, Face;
    public bool Removed;
}

/// <summary>Mahjongg solitaire's rules: a three-layer pyramid of 136 tiles (34 faces, four of each), dealt so it can
/// always be cleared, matching free pairs, hints, undo and shuffling. Public for the tests.</summary>
public sealed class MahjonggGame
{
    public const int Faces = 34;
    public readonly List<MahjonggTile> Tiles = new();
    private readonly Stack<(MahjonggTile, MahjonggTile)> _undo = new();
    private readonly Random _rng;

    /// <summary>The layout: layer 0 is 12×7 tiles, layer 1 8×5 on top of it, layer 2 4×3 on top of that (136).</summary>
    public static IEnumerable<(int Layer, int X, int Y)> Layout()
    {
        for (int y = 0; y < 7; y++) for (int x = 0; x < 12; x++) yield return (0, x * 2, y * 2);
        for (int y = 0; y < 5; y++) for (int x = 0; x < 8; x++) yield return (1, 4 + x * 2, 2 + y * 2);
        for (int y = 0; y < 3; y++) for (int x = 0; x < 4; x++) yield return (2, 8 + x * 2, 4 + y * 2);
    }

    public MahjonggGame(int? seed = null)
    {
        _rng = seed is { } s ? new Random(s) : new Random();
        foreach (var (l, x, y) in Layout()) Tiles.Add(new MahjonggTile { Layer = l, X = x, Y = y, Face = -1 });
        Deal();
    }

    public int Left => Tiles.Count(t => !t.Removed);
    public bool Won => Left == 0;

    private static bool Overlaps(MahjonggTile a, MahjonggTile b) => Math.Abs(a.X - b.X) < 2 && Math.Abs(a.Y - b.Y) < 2;

    /// <summary>Free: nothing on top of it, and nothing beside it on at least one side.</summary>
    public bool IsFree(MahjonggTile t, Func<MahjonggTile, bool>? present = null)
    {
        present ??= o => !o.Removed;
        bool left = false, right = false;
        foreach (var o in Tiles)
        {
            if (o == t || !present(o)) continue;
            if (o.Layer == t.Layer + 1 && Overlaps(o, t)) return false;
            if (o.Layer == t.Layer && Math.Abs(o.Y - t.Y) < 2)
            {
                if (o.X == t.X - 2) left = true;
                else if (o.X == t.X + 2) right = true;
            }
        }
        return !(left && right);
    }

    /// <summary>Gives every tile a face by taking the full board apart two free tiles at a time and giving each pair
    /// the same face: played back, that order clears the board, so every deal can be won.</summary>
    private void Deal()
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            var standing = new HashSet<MahjonggTile>(Tiles);
            var faces = Enumerable.Range(0, Faces).SelectMany(f => new[] { f, f }).OrderBy(_ => _rng.Next()).ToList();  // pairs of pairs
            int next = 0;
            bool ok = true;
            while (standing.Count > 0)
            {
                var free = standing.Where(t => IsFree(t, standing.Contains)).ToList();
                if (free.Count < 2) { ok = false; break; }
                var a = free[_rng.Next(free.Count)];
                free.Remove(a);
                var b = free[_rng.Next(free.Count)];
                a.Face = b.Face = faces[next++];
                standing.Remove(a); standing.Remove(b);
            }
            if (ok) return;
        }
        throw new InvalidOperationException("couldn't deal a Mahjongg board");
    }

    public static bool Match(int a, int b) => a == b;

    public bool TryRemove(MahjonggTile a, MahjonggTile b)
    {
        if (a == b || a.Removed || b.Removed || !Match(a.Face, b.Face) || !IsFree(a) || !IsFree(b)) return false;
        a.Removed = b.Removed = true;
        _undo.Push((a, b));
        return true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var (a, b) = _undo.Pop();
        a.Removed = b.Removed = false;
        return true;
    }

    /// <summary>A free pair that can be taken, if there is one.</summary>
    public (MahjonggTile, MahjonggTile)? Hint()
    {
        var free = Tiles.Where(t => !t.Removed && IsFree(t)).ToList();
        foreach (var g in free.GroupBy(t => t.Face)) { var two = g.Take(2).ToList(); if (two.Count == 2) return (two[0], two[1]); }
        return null;
    }

    /// <summary>Deals the remaining tiles' faces again so that they can be cleared (when no pair is free).</summary>
    public void Shuffle()
    {
        var rest = Tiles.Where(t => !t.Removed).ToList();
        var faces = rest.Select(t => t.Face).ToList();
        for (int attempt = 0; attempt < 200; attempt++)
        {
            var pool = faces.GroupBy(f => f).SelectMany(g => Enumerable.Repeat(g.Key, g.Count() / 2)).OrderBy(_ => _rng.Next()).ToList();
            var standing = new HashSet<MahjonggTile>(rest);
            bool ok = true; int next = 0;
            while (standing.Count > 0)
            {
                var free = standing.Where(t => IsFree(t, standing.Contains)).ToList();
                if (free.Count < 2 || next >= pool.Count) { ok = false; break; }
                var a = free[_rng.Next(free.Count)]; free.Remove(a);
                var b = free[_rng.Next(free.Count)];
                a.Face = b.Face = pool[next++];
                standing.Remove(a); standing.Remove(b);
            }
            if (ok) { _undo.Clear(); return; }
        }
    }
}

/// <summary>Mahjongg solitaire: take away the tiles in matching pairs. A tile can be taken when nothing lies on it and
/// one of its long sides is open. H shows a pair, Ctrl+Z takes back, F2 deals a new board.</summary>
public sealed class MahjonggWindow : Window
{
    private MahjonggGame _game = new();
    private bool _counted;
    private readonly MahjonggView _view;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };

    public MahjonggWindow()
    {
        Title = "Mahjongg";
        Width = 1000; Height = 720; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _view = new MahjonggView(() => _game, Changed);

        Button B(string text, Action act) { var b = new Button { Content = text }; b.Click += (_, _) => act(); return b; }
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 10),
            Children = { B("New board (F2)", NewGame), B("Hint (H)", () => _view.ShowHint()), B("Undo (Ctrl+Z)", () => { _game.Undo(); Changed(); }),
                         B("Shuffle", () => { _game.Shuffle(); Changed(); }), _status }
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
            else if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { _game.Undo(); Changed(); }
            else return;
            e.Handled = true;
        };
        Opened += (_, _) => Changed();
    }

    private void NewGame() { _game = new MahjonggGame(); _counted = false; _view.Reset(); Changed(); }

    private void Changed()
    {
        if (_game.Won)
        {
            if (!_counted) { _counted = true; SettingsStore.Current.MahjonggWins++; SettingsStore.Save(); }
            _status.Text = $"Cleared! Boards won: {SettingsStore.Current.MahjonggWins}. F2 for a new one.";
        }
        else _status.Text = $"{_game.Left} tiles left" + (_game.Hint() == null ? " · no pairs are free: Shuffle or Undo" : "");
        _view.InvalidateVisual();
    }
}

internal sealed class MahjonggView : Control
{
    private readonly Func<MahjonggGame> _game;
    private readonly Action _changed;
    private MahjonggTile? _selected;
    private (MahjonggTile, MahjonggTile)? _hint;
    private readonly List<(MahjonggTile Tile, Rect Rect)> _hit = new();

    public MahjonggView(Func<MahjonggGame> game, Action changed) { _game = game; _changed = changed; ClipToBounds = true; }

    public void Reset() { _selected = null; _hint = null; }
    public void ShowHint() { _hint = _game().Hint(); InvalidateVisual(); }

    // what's on each face: suits (dots, bamboo, characters 1–9), winds, dragons
    private static (string Big, string Small, Color Ink) FaceOf(int f)
    {
        if (f < 27)
        {
            int suit = f / 9, n = f % 9 + 1;
            return suit switch
            {
                0 => (n.ToString(CultureInfo.InvariantCulture), "●", Color.Parse("#2B6CB0")),
                1 => (n.ToString(CultureInfo.InvariantCulture), "‖", Color.Parse("#2F855A")),
                _ => (n.ToString(CultureInfo.InvariantCulture), "✶", Color.Parse("#C8102E")),
            };
        }
        return (f - 27) switch
        {
            0 => ("E", "wind", Color.Parse("#2D3748")),
            1 => ("S", "wind", Color.Parse("#2D3748")),
            2 => ("W", "wind", Color.Parse("#2D3748")),
            3 => ("N", "wind", Color.Parse("#2D3748")),
            4 => ("◆", "red", Color.Parse("#C8102E")),
            5 => ("◆", "green", Color.Parse("#2F855A")),
            _ => ("◇", "white", Color.Parse("#4A5568")),
        };
    }

    public override void Render(DrawingContext ctx)
    {
        var g = _game();
        _hit.Clear();
        // board size in half-tile units: 24 × 14, plus room for the layers' lift
        double unitW = (Bounds.Width - 40) / 25.0, unitH = (Bounds.Height - 40) / (14.0 * 1.0 + 1);
        double u = Math.Max(8, Math.Min(unitW, unitH / 1.3));
        double tw = 2 * u, th = 2 * u * 1.3, lift = u * 0.35;
        double ox = (Bounds.Width - 24 * u) / 2, oy = (Bounds.Height - 14 * u * 1.3) / 2;
        var face = new SolidColorBrush(Color.Parse("#FBF7EC"));
        var side = new SolidColorBrush(Color.Parse("#C9B98F"));
        var edge = new Pen(new SolidColorBrush(Color.Parse("#8A7A55")), 1);
        var selPen = new Pen(new SolidColorBrush(Color.Parse("#C8102E")), 3);
        var hintPen = new Pen(new SolidColorBrush(Color.Parse("#D69E2E")), 3);
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

        foreach (var t in g.Tiles.Where(t => !t.Removed).OrderBy(t => t.Layer).ThenBy(t => t.Y).ThenBy(t => t.X))   // later tiles cover the sides of earlier ones
        {
            double x = ox + t.X * u - t.Layer * lift, y = oy + t.Y * u * 1.3 - t.Layer * lift;
            var r = new Rect(x + 1, y + 1, tw - 4, th - 4);
            ctx.DrawRectangle(side, null, r.Translate(new Vector(lift * 0.8, lift * 0.8)), 4, 4);
            bool free = g.IsFree(t);
            ctx.DrawRectangle(face, edge, r, 4, 4);
            var (big, small, ink) = FaceOf(t.Face);
            var fg = new SolidColorBrush(ink);
            var ft = new FormattedText(big, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, th * 0.38, fg);
            ctx.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + r.Height * 0.12));
            var fs = new FormattedText(small, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, th * 0.17, fg);
            ctx.DrawText(fs, new Point(r.X + (r.Width - fs.Width) / 2, r.Y + r.Height * 0.62));
            if (!free) ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(46, 0, 0, 0)), null, r, 4, 4);
            if (t == _selected) ctx.DrawRectangle(null, selPen, r, 4, 4);
            else if (_hint is { } h && (h.Item1 == t || h.Item2 == t)) ctx.DrawRectangle(null, hintPen, r, 4, 4);
            _hit.Add((t, r));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var g = _game();
        // the topmost tile under the pointer: drawn last
        var hit = _hit.LastOrDefault(h => h.Rect.Contains(p) && !h.Tile.Removed).Tile;
        _hint = null;
        if (hit == null || !g.IsFree(hit)) { _selected = null; InvalidateVisual(); return; }
        if (_selected == null || _selected == hit) { _selected = _selected == hit ? null : hit; InvalidateVisual(); return; }
        if (g.TryRemove(_selected, hit)) { _selected = null; _changed(); }
        else { _selected = hit; InvalidateVisual(); }
    }
}
