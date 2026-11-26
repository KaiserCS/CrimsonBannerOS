// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RedStarOS.Services;

public enum DesktopIconSize { Small, Medium, Large }
public enum DesktopSort { Name, Size, Type, Date }

/// <summary>
/// Where the desktop's icons sit, as on Windows: on a grid filled down the left-hand columns first, each icon kept
/// where it was dragged (by name), new ones in the first free place. Auto arrange instead lays them out in the sort
/// order with no gaps. Kept in ~/.config/redstaros/desktop.json.
/// </summary>
public sealed class DesktopLayout
{
    public DesktopIconSize Size { get; set; } = DesktopIconSize.Medium;
    public DesktopSort Sort { get; set; } = DesktopSort.Name;
    public bool AutoArrange { get; set; }
    public bool ShowIcons { get; set; } = true;
    /// <summary>Name → column and row.</summary>
    public Dictionary<string, int[]> Cells { get; set; } = new();

    public static string FilePath => AppPaths.ConfigFile("desktop.json");

    public static DesktopLayout Load(string? file = null)
    {
        try
        {
            var f = file ?? FilePath;
            if (File.Exists(f)) return JsonSerializer.Deserialize<DesktopLayout>(File.ReadAllText(f)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save(string? file = null)
    {
        try
        {
            var f = file ?? FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A cell's size and its icon's, in points.</summary>
    public static (double W, double H, double Icon) CellSize(DesktopIconSize s) => s switch
    {
        DesktopIconSize.Small => (78, 74, 32),
        DesktopIconSize.Large => (116, 138, 80),
        _ => (92, 102, 48),
    };

    /// <summary>The items in the chosen order: folders first, then by name, size, type or date (newest first).</summary>
    public static IEnumerable<T> Ordered<T>(IEnumerable<T> items, DesktopSort sort, Func<T, string> name, Func<T, bool> isDir,
                                            Func<T, long> size, Func<T, DateTime> modified)
    {
        var dirsFirst = items.OrderBy(i => isDir(i) ? 0 : 1);
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        return sort switch
        {
            DesktopSort.Size => dirsFirst.ThenByDescending(size).ThenBy(name, cmp),
            DesktopSort.Type => dirsFirst.ThenBy(i => Path.GetExtension(name(i)), cmp).ThenBy(name, cmp),
            DesktopSort.Date => dirsFirst.ThenByDescending(modified).ThenBy(name, cmp),
            _ => dirsFirst.ThenBy(name, cmp),
        };
    }

    /// <summary>
    /// Places the names (already in order) on a grid of <paramref name="rows"/> rows: each where it was kept if that
    /// place is free and on the grid, the rest in the first free places, down each column from the left.
    /// </summary>
    public Dictionary<string, (int Col, int Row)> Place(IReadOnlyList<string> names, int rows, int cols)
    {
        rows = Math.Max(1, rows);
        cols = Math.Max(1, cols);
        var at = new Dictionary<string, (int, int)>();
        var taken = new HashSet<(int, int)>();
        if (!AutoArrange)
            foreach (var n in names)
                if (Cells.TryGetValue(n, out var c) && c.Length == 2 && c[0] >= 0 && c[1] >= 0 && c[0] < cols && c[1] < rows && taken.Add((c[0], c[1])))
                    at[n] = (c[0], c[1]);
        int next = 0;
        foreach (var n in names)
        {
            if (at.ContainsKey(n)) continue;
            while (taken.Contains((next / rows, next % rows))) next++;
            var cell = (next / rows, next % rows);
            taken.Add(cell);
            at[n] = cell;
        }
        return at;
    }

    /// <summary>Moves icons by a number of cells, each to the free place nearest where it lands; returns the new places.</summary>
    public static Dictionary<string, (int Col, int Row)> Move(Dictionary<string, (int Col, int Row)> at, IReadOnlyCollection<string> moving,
                                                              int dCol, int dRow, int rows, int cols)
    {
        var result = new Dictionary<string, (int, int)>(at);
        var taken = new HashSet<(int, int)>(at.Where(kv => !moving.Contains(kv.Key)).Select(kv => kv.Value));
        foreach (var n in moving.OrderBy(n => at[n].Col).ThenBy(n => at[n].Row))
        {
            var want = (Math.Clamp(at[n].Col + dCol, 0, cols - 1), Math.Clamp(at[n].Row + dRow, 0, rows - 1));
            var best = want;
            if (taken.Contains(want))
            {
                double bestD = double.MaxValue;
                for (int c = 0; c < cols; c++)
                    for (int r = 0; r < rows; r++)
                    {
                        if (taken.Contains((c, r))) continue;
                        double d = Math.Pow(c - want.Item1, 2) + Math.Pow(r - want.Item2, 2);
                        if (d < bestD) { bestD = d; best = (c, r); }
                    }
            }
            taken.Add(best);
            result[n] = best;
        }
        return result;
    }

    /// <summary>Remembers where everything is now (and forgets icons that are gone).</summary>
    public void Keep(Dictionary<string, (int Col, int Row)> at) =>
        Cells = at.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.Col, kv.Value.Row });
}
