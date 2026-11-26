// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CrimsonPlanner.Model;

/// <summary>One report taken in, chained to the one before it.</summary>
public sealed class LedgerEntry
{
    public int Index { get; set; }
    public string Council { get; set; } = "";
    public int Period { get; set; }
    /// <summary>SHA-256 of the report's figures, as imported.</summary>
    public string Report { get; set; } = "";
    /// <summary>The entry before's Hash (the first: 64 zeros).</summary>
    public string Previous { get; set; } = "";
    /// <summary>SHA-256 of this entry: its place, council, period, report and the entry before.</summary>
    public string Hash { get; set; } = "";
    public DateTime When { get; set; }
}

/// <summary>
/// The verification ledger: every report is recorded as it's imported, each entry sealed with the one before it, as a
/// distributed ledger is. A report changed afterwards no longer matches its entry, and an entry changed or removed
/// breaks the chain from there on, so tampering shows without anyone checking by hand.
/// </summary>
public static class Ledger
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>A report's figures in one fixed form (sorted, invariant numbers), so the same figures hash the same.</summary>
    public static string Canonical(Report r)
    {
        var sb = new StringBuilder();
        sb.Append(r.Council).Append('\n').Append(r.Period.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var l in r.Lines.OrderBy(l => l.Product, StringComparer.Ordinal))
            sb.Append(l.Product).Append(',')
              .Append(l.Produced.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(l.Depleted.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(l.Stock.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    public static string EntryHash(LedgerEntry e) =>
        Sha256($"{e.Index}|{e.Council}|{e.Period.ToString(CultureInfo.InvariantCulture)}|{e.Report}|{e.Previous}");

    /// <summary>Records a report: it replaces any earlier report of the same council and period, and is sealed.</summary>
    public static LedgerEntry Record(Plan plan, Report report)
    {
        plan.Reports.RemoveAll(r => r.Council == report.Council && r.Period == report.Period);
        plan.Reports.Add(report);
        var e = new LedgerEntry
        {
            Index = plan.Ledger.Count,
            Council = report.Council,
            Period = report.Period,
            Report = Sha256(Canonical(report)),
            Previous = plan.Ledger.Count == 0 ? Genesis : plan.Ledger[^1].Hash,
            When = report.Imported,
        };
        e.Hash = EntryHash(e);
        plan.Ledger.Add(e);
        return e;
    }

    public sealed record Check(bool Ok, int BrokenAt, List<(string Council, int Period)> Altered, List<(string Council, int Period)> Unrecorded)
    {
        public string Summary =>
            Ok ? "Every report matches the ledger, and the chain is whole."
            : BrokenAt >= 0 ? $"The chain is broken at entry {BrokenAt + 1}: an entry was changed or removed."
            : Altered.Count > 0 ? $"{Altered.Count} report{(Altered.Count == 1 ? " has" : "s have")} changed since being imported: {string.Join(", ", Altered.Select(a => $"{a.Council} (period {a.Period})"))}."
            : $"{Unrecorded.Count} report{(Unrecorded.Count == 1 ? " isn't" : "s aren't")} in the ledger: {string.Join(", ", Unrecorded.Select(a => $"{a.Council} (period {a.Period})"))}.";
    }

    /// <summary>Checks the chain, and every report against the latest entry recorded for it.</summary>
    public static Check Verify(Plan plan)
    {
        int broken = -1;
        var prev = Genesis;
        for (int i = 0; i < plan.Ledger.Count; i++)
        {
            var e = plan.Ledger[i];
            if (e.Index != i || e.Previous != prev || e.Hash != EntryHash(e)) { broken = i; break; }
            prev = e.Hash;
        }
        var altered = new List<(string, int)>();
        var unrecorded = new List<(string, int)>();
        foreach (var r in plan.Reports)
        {
            var e = plan.Ledger.LastOrDefault(x => x.Council == r.Council && x.Period == r.Period);
            if (e == null) unrecorded.Add((r.Council, r.Period));
            else if (e.Report != Sha256(Canonical(r))) altered.Add((r.Council, r.Period));
        }
        return new Check(broken < 0 && altered.Count == 0 && unrecorded.Count == 0, broken, altered, unrecorded);
    }
}
