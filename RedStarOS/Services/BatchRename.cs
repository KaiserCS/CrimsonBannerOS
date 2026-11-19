using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RedStarOS.Services;

/// <summary>
/// Renaming many files at once (Files → select several → F2): find and replace (optionally a regular expression),
/// add text at the start or end, number them ("Holiday ###" → Holiday 001, 002 …) or change the case.
/// The extension is kept unless asked otherwise. <see cref="Apply"/> renames in two steps, so swapping names
/// (a → b, b → a) works.
/// </summary>
public static class BatchRename
{
    public enum Mode { Replace, Add, Number, Case }
    public enum CaseKind { Lower, Upper, Title, Sentence }

    public sealed class Options
    {
        public Mode Mode = Mode.Replace;
        public string Find = "", ReplaceWith = "";
        public bool Regex, MatchCase;
        public string Text = "";
        public bool AtStart;
        public string Pattern = "File ###";      // # = digit of the number, padded with zeros
        public int Start = 1, Step = 1;
        public CaseKind Case = CaseKind.Lower;
        public bool IncludeExtension;            // rename the extension too
    }

    public sealed record Item(string OldPath, string NewName, string? Problem)
    {
        public string OldName => Path.GetFileName(OldPath);
        public bool Changes => Problem == null && NewName != OldName;
    }

    /// <summary>The new names (and what's wrong with any of them) for <paramref name="paths"/>, in that order.</summary>
    public static List<Item> Preview(IReadOnlyList<string> paths, Options o)
    {
        var result = new List<Item>();
        Regex? rx = null;
        string? rxError = null;
        if (o.Mode == Mode.Replace && o.Regex && o.Find.Length > 0)
        {
            try { rx = new Regex(o.Find, o.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException ex) { rxError = "Not a valid pattern: " + ex.Message; }
        }
        int n = o.Start;
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.TrimEnd('/'));
            bool dir = Directory.Exists(path);
            var (stem, ext) = dir || o.IncludeExtension ? (name, "") : Split(name);
            string newStem;
            string? problem = rxError;
            try
            {
                newStem = o.Mode switch
                {
                    Mode.Replace when o.Find.Length == 0 => stem,
                    Mode.Replace when rx != null => rx.Replace(stem, o.ReplaceWith),
                    Mode.Replace when rxError != null => stem,
                    Mode.Replace => Replace(stem, o.Find, o.ReplaceWith, o.MatchCase),
                    Mode.Add => o.AtStart ? o.Text + stem : stem + o.Text,
                    Mode.Number => Number(o.Pattern, n, stem),
                    Mode.Case => ChangeCase(stem, o.Case),
                    _ => stem
                };
            }
            catch (RegexMatchTimeoutException) { newStem = stem; problem = "The pattern took too long."; }
            n += o.Step;
            var newName = (newStem + ext).Trim();
            if (problem == null)
            {
                if (newName.Length == 0) problem = "The name would be empty.";
                else if (newName.Contains('/')) problem = "A name can't contain “/”.";
                else if (newName is "." or "..") problem = "That name isn't allowed.";
            }
            result.Add(new Item(path, newName, problem));
        }

        // two items ending up with the same name, or a name that's taken by a file that isn't being renamed
        var renaming = new HashSet<string>(paths.Select(p => Path.GetFullPath(p.TrimEnd('/'))), StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < result.Count; i++)
        {
            var it = result[i];
            if (it.Problem != null) continue;
            var target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(it.OldPath.TrimEnd('/')))!, it.NewName);
            if (seen.TryGetValue(target, out var other))
            {
                // blame the one that changes (a name that stays the same isn't the problem)
                if (it.NewName == it.OldName && result[other].Problem == null)
                    result[other] = result[other] with { Problem = "Same new name as “" + it.OldName + "”." };
                else
                    result[i] = it with { Problem = "Same new name as “" + result[other].OldName + "”." };
                continue;
            }
            seen[target] = i;
            if (it.NewName != it.OldName && (File.Exists(target) || Directory.Exists(target)) && !renaming.Contains(target)
                && !string.Equals(target, Path.GetFullPath(it.OldPath.TrimEnd('/')), StringComparison.OrdinalIgnoreCase))
                result[i] = it with { Problem = "Something called that already exists." };
        }
        return result;
    }

    /// <summary>Renames everything that changes (two passes via temporary names); returns (from, to) pairs done.</summary>
    public static List<(string From, string To)> Apply(IReadOnlyList<Item> items, List<string>? errors = null)
    {
        var todo = items.Where(i => i.Changes).ToList();
        var temp = new List<(Item Item, string Temp)>();
        foreach (var it in todo)
        {
            var dir = Path.GetDirectoryName(it.OldPath.TrimEnd('/'))!;
            var t = Path.Combine(dir, ".rsrename-" + Guid.NewGuid().ToString("N")[..8]);
            try { Move(it.OldPath, t); temp.Add((it, t)); }
            catch (Exception ex) { errors?.Add($"{it.OldName}: {ex.Message}"); }
        }
        var done = new List<(string, string)>();
        foreach (var (it, t) in temp)
        {
            var dest = Path.Combine(Path.GetDirectoryName(it.OldPath.TrimEnd('/'))!, it.NewName);
            try
            {
                if (File.Exists(dest) || Directory.Exists(dest)) throw new IOException("Something called that already exists.");
                Move(t, dest);
                done.Add((it.OldPath, dest));
            }
            catch (Exception ex)
            {
                errors?.Add($"{it.OldName}: {ex.Message}");
                try { Move(t, it.OldPath); } catch { }
            }
        }
        return done;
    }

    /// <summary>Puts names back after <see cref="Apply"/> (also two passes).</summary>
    public static void Revert(IReadOnlyList<(string From, string To)> done)
    {
        var temp = new List<(string Temp, string Original)>();
        foreach (var (from, to) in done)
        {
            if (!File.Exists(to) && !Directory.Exists(to)) continue;
            var t = Path.Combine(Path.GetDirectoryName(to)!, ".rsrename-" + Guid.NewGuid().ToString("N")[..8]);
            try { Move(to, t); temp.Add((t, from)); } catch { }
        }
        foreach (var (t, original) in temp)
        {
            try
            {
                if (File.Exists(original) || Directory.Exists(original)) Move(t, FileOps.UniquePath(Path.GetDirectoryName(original)!, Path.GetFileName(original)));
                else Move(t, original);
            }
            catch { }
        }
    }

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
    }

    public static (string Stem, string Ext) Split(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var multi in new[] { ".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst" })
            if (lower.EndsWith(multi) && name.Length > multi.Length) return (name[..^multi.Length], name[^multi.Length..]);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? (name[..dot], name[dot..]) : (name, "");
    }

    private static string Replace(string s, string find, string with, bool matchCase) =>
        s.Replace(find, with, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>"Trip ###" + 7 → "Trip 007"; no # adds " 7" at the end; "*" stands for the old name.</summary>
    public static string Number(string pattern, int n, string oldStem)
    {
        var p = pattern.Replace("*", oldStem);
        var m = System.Text.RegularExpressions.Regex.Match(p, "#+");
        if (!m.Success) return p + " " + n;
        return p[..m.Index] + n.ToString().PadLeft(m.Length, '0') + p[(m.Index + m.Length)..];
    }

    public static string ChangeCase(string s, CaseKind c)
    {
        switch (c)
        {
            case CaseKind.Lower: return s.ToLowerInvariant();
            case CaseKind.Upper: return s.ToUpperInvariant();
            case CaseKind.Sentence: return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
            default:
                var sb = new StringBuilder(s.Length);
                bool start = true;
                foreach (var ch in s)
                {
                    sb.Append(start ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
                    start = ch is ' ' or '-' or '_' or '.' or '(' or '[';
                }
                return sb.ToString();
        }
    }
}
