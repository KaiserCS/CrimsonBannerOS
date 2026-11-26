// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace CrimsonPlanner.Model;

/// <summary>
/// The plain files councils send and receive, readable in any spreadsheet:
///   reports in:  council,period,product,produced,depleted,stock     (one file may hold many councils, as a region's)
///   quotas out:  region,council,product,unit,quota                  (one file per council, and one for everything)
/// A product is named by its id or its name.
/// </summary>
public static class PlanCsv
{
    public static readonly string ReportHeader = "council,period,product,produced,depleted,stock";

    public sealed record ImportResult(List<Report> Reports, List<string> Problems);

    /// <summary>Reads councils' reports. Unknown councils or products, and bad lines, are listed, not guessed at.</summary>
    public static ImportResult ReadReports(Plan plan, string text, string source)
    {
        var problems = new List<string>();
        var reports = new Dictionary<(string, int), Report>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int start = 0;
        while (start < lines.Length && (lines[start].Trim().Length == 0 || lines[start].TrimStart().StartsWith('#'))) start++;
        if (start >= lines.Length) return new ImportResult(new(), new() { "The file is empty." });
        var head = Split(lines[start]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => head.IndexOf(name);
        int cC = Col("council"), cP = Col("period"), cPr = Col("product"), cMade = Col("produced"), cDep = Col("depleted"), cSt = Col("stock");
        if (cC < 0 || cP < 0 || cPr < 0 || cDep < 0 || cSt < 0)
            return new ImportResult(new(), new() { $"The first line has to name the columns: {ReportHeader}" });
        for (int i = start + 1; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith('#')) continue;
            var f = Split(raw);
            string Get(int c) => c >= 0 && c < f.Count ? f[c].Trim() : "";
            var council = Get(cC);
            var match = plan.Councils.FirstOrDefault(c => string.Equals(c.Name, council, StringComparison.OrdinalIgnoreCase));
            if (match == null) { problems.Add($"Line {i + 1}: no council called \"{council}\" (add it in Councils first)."); continue; }
            if (!int.TryParse(Get(cP), NumberStyles.Integer, CultureInfo.InvariantCulture, out var period) || period < 1)
            { problems.Add($"Line {i + 1}: the period has to be a whole number from 1 (got \"{Get(cP)}\")."); continue; }
            var prodName = Get(cPr);
            var product = plan.Products.FirstOrDefault(p => p.Id == prodName)
                          ?? plan.Products.FirstOrDefault(p => string.Equals(p.Name, prodName, StringComparison.OrdinalIgnoreCase));
            if (product == null) { problems.Add($"Line {i + 1}: no product called \"{prodName}\" (Economy lists them)."); continue; }
            if (!Num(Get(cDep), out var dep) || !Num(Get(cSt), out var st) || !Num(cMade < 0 ? "0" : Get(cMade), out var made))
            { problems.Add($"Line {i + 1}: produced, depleted and stock have to be numbers, none below 0."); continue; }
            var key = (match.Name, period);
            if (!reports.TryGetValue(key, out var r))
                reports[key] = r = new Report { Council = match.Name, Period = period, Imported = DateTime.UtcNow, Source = source };
            r.Lines.RemoveAll(l => l.Product == product.Id);
            r.Lines.Add(new ReportLine { Product = product.Id, Produced = made, Depleted = dep, Stock = st });
        }
        return new ImportResult(reports.Values.ToList(), problems);
    }

    public static string WriteReports(Plan plan, IEnumerable<Report> reports)
    {
        var sb = new StringBuilder(ReportHeader).Append('\n');
        foreach (var r in reports.OrderBy(r => r.Period).ThenBy(r => r.Council))
            foreach (var l in r.Lines)
                sb.Append(Cell(r.Council)).Append(',').Append(r.Period).Append(',').Append(Cell(l.Product)).Append(',')
                  .Append(N(l.Produced)).Append(',').Append(N(l.Depleted)).Append(',').Append(N(l.Stock)).Append('\n');
        return sb.ToString();
    }

    /// <summary>The quotas for a period: everything in one file, and one file per council, in region folders.</summary>
    public static List<string> ExportQuotas(Plan plan, int period, string folder)
    {
        var quotas = Engine.Disaggregate(plan, period);
        var written = new List<string>();
        Directory.CreateDirectory(folder);
        string Line(CouncilQuota q)
        {
            var p = plan.Products.First(p => p.Id == q.Product);
            return $"{Cell(q.Region)},{Cell(q.Council)},{Cell(p.Name)},{Cell(p.Unit)},{N(Math.Round(q.Quota, 3))}\n";
        }
        const string header = "region,council,product,unit,quota\n";
        var all = Path.Combine(folder, $"quotas-period-{period}.csv");
        File.WriteAllText(all, header + string.Concat(quotas.Select(Line)));
        written.Add(all);
        foreach (var g in quotas.GroupBy(q => (q.Region, q.Council)))
        {
            var dir = Path.Combine(folder, Safe(g.Key.Region));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Safe(g.Key.Council) + ".csv");
            File.WriteAllText(file, header + string.Concat(g.Select(Line)));
            written.Add(file);
        }
        return written;
    }

    public static string Safe(string name)
    {
        var s = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '-').ToArray()).Trim();
        return s.Length == 0 ? "unnamed" : s;
    }

    private static bool Num(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && !double.IsInfinity(v);

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Cell(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>One CSV line into its fields (quotes, and doubled quotes inside them).</summary>
    public static List<string> Split(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
