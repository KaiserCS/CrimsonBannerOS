// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services.Software;

/// <summary>Runs flatpak (and the like) and reports its progress as it goes: every "NN%" it prints, and its last lines
/// for when it fails.</summary>
public static class Runner
{
    private static readonly Regex Percent = new(@"(\d{1,3})(?:\.\d+)?\s?%");

    public static async Task<(int Code, string Tail)> RunAsync(string file, IEnumerable<string> args, Action<int>? progress,
        CancellationToken ct, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // AppImage variables would confuse the programs it starts; plain English output, so it can be read
        foreach (var k in new[] { "APPIMAGE", "APPDIR", "ARGV0", "OWD" }) psi.Environment.Remove(k);
        psi.Environment["LC_ALL"] = "C.UTF-8";
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start " + file);
        var tail = new Queue<string>();
        void Line(string l)
        {
            l = l.Trim();
            if (l.Length == 0) return;
            var ms = Percent.Matches(l);
            if (ms.Count > 0 && int.TryParse(ms[^1].Groups[1].Value, out var pct) && pct <= 100) progress?.Invoke(pct);
            lock (tail) { tail.Enqueue(l); while (tail.Count > 8) tail.Dequeue(); }
        }
        var outTask = Pump(p.StandardOutput, Line);
        var errTask = Pump(p.StandardError, Line);
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
        {
            await p.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outTask, errTask);
        }
        ct.ThrowIfCancellationRequested();
        lock (tail) return (p.ExitCode, string.Join("\n", tail));
    }

    // progress bars rewrite their line with \r, so both \r and \n end a line
    private static async Task Pump(System.IO.StreamReader r, Action<string> line)
    {
        var buf = new char[4096];
        var sb = new StringBuilder();
        int n;
        while ((n = await r.ReadAsync(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++)
            {
                if (buf[i] == '\r' || buf[i] == '\n') { line(sb.ToString()); sb.Clear(); }
                else sb.Append(buf[i]);
            }
        if (sb.Length > 0) line(sb.ToString());
    }
}
