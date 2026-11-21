// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RedStarOS.Services;

/// <summary>
/// Crimson Banner OS's version, the one place the shell reads it (README, "Versions"). Releases are YY.MM.N (26.11.0);
/// a development build is a release plus the commits after it: 26.11.0+5.g1a2b3c4, with ".local" when it was built
/// from changes that weren't committed. VERSION and tools/version.sh make it; MSBuild puts it in the assembly.
/// </summary>
public static class AppVersion
{
    /// <summary>A parsed version: the release, how many commits after it (0 for the release itself; -1 when unknown)
    /// and at which commit.</summary>
    public sealed record Info(Version Release, int CommitsAfter, string? Commit, bool LocalChanges)
    {
        public bool IsRelease => CommitsAfter == 0 && Commit == null && !LocalChanges;
        /// <summary>26.11.0 — the release, three numbers.</summary>
        public string Short => $"{Release.Major}.{Release.Minor}.{Math.Max(0, Release.Build)}";
        /// <summary>26.11.0, or 26.11.0+5.g1a2b3c4 — what tools/version.sh prints.</summary>
        public string Full => IsRelease ? Short
            : Short + "+" + (CommitsAfter > 0 ? CommitsAfter + "." : "") + (Commit != null ? "g" + Commit : "") + (LocalChanges ? ".local" : "");
        /// <summary>For people: "26.11.0" or "26.11.0 (development build 1a2b3c4, 5 changes after 26.11.0)".</summary>
        public string Display
        {
            get
            {
                if (IsRelease) return Short;
                var parts = new List<string>();
                if (Commit != null) parts.Add(Commit);
                if (CommitsAfter > 0) parts.Add($"{CommitsAfter} change{(CommitsAfter == 1 ? "" : "s")} after {Short}");
                if (LocalChanges) parts.Add("with changes not committed");
                return $"{Short} (development build{(parts.Count > 0 ? " " + string.Join(", ", parts) : "")})";
            }
        }
    }

    private static readonly Regex Pattern = new(
        @"^\s*v?(?<rel>\d+\.\d+(?:\.\d+)?)(?:\+(?:(?<n>\d+)\.)?(?:g(?<hash>[0-9a-f]{4,40}))?(?<local>\.local)?)?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Reads "26.11.0", "v26.11.0", "26.11.0+5.g1a2b3c4(.local)", "26.11.0.r5.g1a2b3c4" (pacman's form) and old
    /// three-number versions such as "1.3.0". Null when it isn't a version.</summary>
    public static Info? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        // pacman's form: 26.11.0.r5.g1a2b3c4[.local]
        var pk = Regex.Match(t, @"^(?<rel>\d+\.\d+\.\d+)\.r(?<n>\d+)\.g(?<hash>[0-9a-f]+)(?<local>\.local)?$");
        var m = pk.Success ? pk : Pattern.Match(t);
        if (!m.Success || !Version.TryParse(m.Groups["rel"].Value, out var rel)) return null;
        rel = new Version(rel.Major, rel.Minor, Math.Max(0, rel.Build));
        int n = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value) : 0;
        string? hash = m.Groups["hash"].Success ? m.Groups["hash"].Value.ToLowerInvariant() : null;
        bool local = m.Groups["local"].Success;
        // a commit with no count is a development build of unknown distance (a shallow clone built it)
        if (hash != null && n == 0 && !m.Groups["n"].Success) n = -1;
        return new Info(rel, n, hash, local);
    }

    /// <summary>
    /// Whether <paramref name="latest"/> (a release's tag) is newer than <paramref name="installed"/>. A development
    /// build already has its release and some commits after it, so only a later release is newer; a release is newer
    /// than development builds of the release before it, the same order pacman uses.
    /// </summary>
    public static bool IsNewer(Info latest, Info installed)
    {
        int c = latest.Release.CompareTo(installed.Release);
        if (c != 0) return c > 0;
        // same release: newer only if it is further along (development builds between themselves)
        if (latest.IsRelease) return false;
        return !installed.IsRelease && latest.CommitsAfter > installed.CommitsAfter && installed.CommitsAfter >= 0;
    }

    private static Info? _current;

    /// <summary>This build's version (the assembly's informational version, else its version number).</summary>
    public static Info Current => _current ??= FromAssembly(Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly);

    public static Info FromAssembly(Assembly a)
    {
        var info = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (Parse(info) is { } v) return v;
        var n = a.GetName().Version ?? new Version(0, 0, 0);
        return new Info(new Version(n.Major, n.Minor, Math.Max(0, n.Build)), 0, null, false);
    }

    /// <summary>How this copy was installed, which decides where its updates come from.</summary>
    public enum InstallKind { Package, AppImage, Source }

    public const string PackageDir = "/usr/lib/crimson-banner";

    public static InstallKind Kind => KindOf(Environment.GetEnvironmentVariable("APPIMAGE"), AppContext.BaseDirectory);

    public static InstallKind KindOf(string? appImage, string baseDir)
    {
        if (!string.IsNullOrEmpty(appImage)) return InstallKind.AppImage;
        var dir = baseDir.TrimEnd('/');
        return dir == PackageDir || dir.StartsWith(PackageDir + "/", StringComparison.Ordinal) ? InstallKind.Package : InstallKind.Source;
    }

    public static string KindName(InstallKind k) => k switch
    {
        InstallKind.Package => "pacman package",
        InstallKind.AppImage => "AppImage",
        _ => "built from source",
    };

    /// <summary>The versions of the programs that go with the shell (their --version), for Diagnostics.</summary>
    public static IEnumerable<(string Name, string Version)> Components()
    {
        yield return ("Desktop shell", Current.Full);
        foreach (var (name, path) in new (string, string?)[]
                 {
                     ("Compositor (redstar-comp)", InputIsolation.RedStarCompPath()),
                     ("Window manager (redstar-wm)", WindowManagerHost.BuiltinPath()),
                     ("Crimson Online", RedStarOnline.ProgramPath()),
                 })
            yield return (name, path == null ? "not installed" : ProgramVersion(path) ?? "unknown");
    }

    private static readonly Dictionary<string, string?> Programs = new();

    /// <summary>The version a program prints for --version ("redstar-comp 26.11.0" → "26.11.0"), asked once per
    /// session.</summary>
    public static string? ProgramVersion(string path)
    {
        lock (Programs)
        {
            if (Programs.TryGetValue(path, out var known)) return known;
            string? v = null;
            try
            {
                if (File.Exists(path))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(path, "--version")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        var output = proc.StandardOutput.ReadToEndAsync();
                        if (proc.WaitForExit(3000) && proc.ExitCode == 0) v = FromVersionOutput(output.Result);
                        else try { proc.Kill(); } catch { }
                    }
                }
            }
            catch { v = null; }
            Programs[path] = v;
            return v;
        }
    }

    /// <summary>"redstar-comp 26.11.0+5.g1a2b3c4\n" → "26.11.0+5.g1a2b3c4".</summary>
    public static string? FromVersionOutput(string output)
    {
        var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (first.Length == 0) return null;
        var words = first[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words[^1] : null;
    }
}
