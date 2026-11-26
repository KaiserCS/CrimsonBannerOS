// SPDX-License-Identifier: GPL-3.0-or-later
namespace CrimsonPlanner.Model;

/// <summary>Turnover against its target: the shadow price's signal.</summary>
public enum Signal { Rapid, Normal, Low, NoStock }

public sealed class ProductResult
{
    public required Product Product { get; init; }
    public double Produced { get; init; }
    public double Depleted { get; init; }
    public double Stock { get; init; }
    /// <summary>Depletion over the average stock held in the period (times the stock turned over).</summary>
    public double Turnover { get; init; }
    public double TargetTurnover { get; init; }
    public Signal Signal { get; init; }
    /// <summary>Depletion, smoothed with the periods before it: the demand expected next period.</summary>
    public double ExpectedDemand { get; init; }
    public double TargetStock { get; init; }
    /// <summary>What people need of it next period: expected demand, plus or minus the stock correction.</summary>
    public double FinalDemand { get; init; }
    /// <summary>What has to be made to meet final demand and every other sector's use of it (Leontief).</summary>
    public double GrossOutput { get; init; }
    /// <summary>The part of the gross output other sectors use up.</summary>
    public double IntermediateUse { get; init; }
    /// <summary>Hours of labour one unit takes, directly and through everything it's made from.</summary>
    public double LabourValue { get; init; }
    public double DirectLabour { get; init; }
    public double? Capacity { get; init; }
    /// <summary>The quota proposed: the gross output, scaled down if capacity or labour can't meet it all.</summary>
    public double Proposed { get; init; }
    public double? LastEnacted { get; init; }
    /// <summary>Change of the proposal from the last quota enacted (null: none enacted before).</summary>
    public double? Change { get; init; }
    public bool Major { get; init; }
}

public sealed class Flag
{
    public required string Council { get; init; }
    public required string Product { get; init; }
    public required string Why { get; init; }
    public double Value { get; init; }
}

public sealed class CouncilQuota
{
    public required string Region { get; init; }
    public required string Council { get; init; }
    public required string Product { get; init; }
    public double Quota { get; init; }
}

public sealed class PlanResult
{
    public int Period { get; init; }
    public bool Productive { get; init; }
    public double SpectralRadius { get; init; }
    public string? Problem { get; init; }
    public List<ProductResult> Products { get; init; } = new();
    public double LabourRequired { get; init; }
    public double? LabourAvailable { get; init; }
    /// <summary>Labour the proposals take (under LabourRequired when capacity or labour can't meet the whole need).</summary>
    public double LabourProposed { get; init; }
    /// <summary>What held the proposals under the need: products at their capacity, or labour. Empty: nothing.</summary>
    public List<string> Limits { get; init; } = new();
    public bool Fits => Limits.Count == 0;
    public List<Flag> Flags { get; init; } = new();
    public List<string> CouncilsReporting { get; init; } = new();
    public List<string> CouncilsMissing { get; init; } = new();
    public bool LedgerIntact { get; init; } = true;
}

/// <summary>
/// The Economic Planning Algorithm, as transparent arithmetic: every figure can be traced back to the reports.
///   1. Logging: the councils' reports for the period, added up per product.
///   2. Sorting: turnover against its target (the shadow price), demand smoothed with the periods before, and the
///      stock correction, give each product's final demand.
///   3. Execution: Leontief's input-output model gives the gross output that meets final demand and every sector's
///      inputs; labour and capacity are checked; the proposal is what fits; councils' reports that stand out are
///      flagged for a recount.
/// </summary>
public static class Engine
{
    public static PlanResult Run(Plan plan, int? period = null)
    {
        plan.Normalise();
        int t = period ?? plan.LatestPeriod;
        int n = plan.Products.Count;
        var s = plan.Settings;
        var ledgerOk = Ledger.Verify(plan).Ok;
        if (n == 0)
            return new PlanResult { Period = t, Problem = "There are no products yet (Economy).", LedgerIntact = ledgerOk };

        // ── 1. logging: the period's figures per product, and every earlier period's depletion for smoothing ──
        var periods = plan.Reports.Select(r => r.Period).Where(p => p <= t).Distinct().OrderBy(p => p).ToList();
        double[] Sum(int p, Func<ReportLine, double> f)
        {
            var a = new double[n];
            foreach (var r in plan.Reports.Where(r => r.Period == p))
                for (int i = 0; i < n; i++)
                    if (r.Line(plan.Products[i].Id) is { } l) a[i] += f(l);
            return a;
        }
        var produced = Sum(t, l => l.Produced);
        var depleted = Sum(t, l => l.Depleted);
        var stock = Sum(t, l => l.Stock);
        int prevPeriod = periods.Where(p => p < t).DefaultIfEmpty(int.MinValue).Max();
        var prevStock = prevPeriod == int.MinValue ? null : Sum(prevPeriod, l => l.Stock);

        // ── 2. sorting: expected demand (smoothed), turnover and the stock correction → final demand ──
        var expected = new double[n];
        bool first = true;
        foreach (var p in periods)
        {
            var d = Sum(p, l => l.Depleted);
            for (int i = 0; i < n; i++) expected[i] = first ? d[i] : s.Smoothing * d[i] + (1 - s.Smoothing) * expected[i];
            first = false;
        }
        var final = new double[n];
        var turnover = new double[n];
        var target = new double[n];
        var targetStock = new double[n];
        var signal = new Signal[n];
        for (int i = 0; i < n; i++)
        {
            var cover = Math.Max(plan.Products[i].StockCover, 1e-9);
            target[i] = 1 / cover;
            // stock at the start: the last period's end, or worked back from this one's figures
            double start = prevStock?[i] ?? Math.Max(0, stock[i] - produced[i] + depleted[i]);
            double avg = (start + stock[i]) / 2;
            turnover[i] = avg > 0 ? depleted[i] / avg : depleted[i] > 0 ? double.PositiveInfinity : 0;
            signal[i] = avg <= 0 && depleted[i] > 0 ? Signal.NoStock
                : turnover[i] > target[i] * (1 + s.TurnoverBand) ? Signal.Rapid
                : turnover[i] < target[i] * (1 - s.TurnoverBand) ? Signal.Low
                : Signal.Normal;
            targetStock[i] = cover * expected[i];
            final[i] = Math.Max(0, expected[i] + (targetStock[i] - stock[i]) / Math.Max(s.CorrectionPeriods, 1));
        }

        // ── 3. execution: Leontief's x = (I − A)⁻¹ d, labour values v = l (I − A)⁻¹ ──
        var a = Matrix(plan);
        double rho = SpectralRadius(a);
        var labour = plan.Products.Select(p => p.Labour).ToArray();
        var ia = IdentityMinus(a);
        double[]? x = rho < 1 ? Solve(ia, final) : null;
        double[]? v = rho < 1 ? Solve(Transpose(ia), labour) : null;
        if (x == null || v == null)
        {
            return new PlanResult
            {
                Period = t, Productive = false, SpectralRadius = rho, LedgerIntact = ledgerOk,
                Problem = $"This economy can't produce a surplus: its sectors use up as much as they make, or more (the input table's largest eigenvalue is {rho:0.###}, and has to be under 1). Lower some inputs in Economy.",
            };
        }
        var intermediate = Multiply(a, x);
        double labourRequired = Dot(labour, x);

        // capacity per product (the councils' sum; none given: no limit) and the share of the plan that fits
        var capacity = new double?[n];
        for (int i = 0; i < n; i++)
        {
            var id = plan.Products[i].Id;
            var caps = plan.Councils.Where(c => c.Capacity.ContainsKey(id)).Select(c => c.Capacity[id]).ToList();
            capacity[i] = caps.Count > 0 ? caps.Sum() : null;
        }
        var (fit, limits) = Fit(plan, ia, final, capacity, labour);

        var results = new List<ProductResult>();
        for (int i = 0; i < n; i++)
        {
            var p = plan.Products[i];
            double proposed = fit[i];
            var last = LastEnacted(plan, p.Id, t);
            double? change = last is double lastQ && lastQ > 0 ? (proposed - lastQ) / lastQ : null;
            bool major = last == null || change == null || Math.Abs(change.Value) > s.MajorShift;
            results.Add(new ProductResult
            {
                Product = p, Produced = produced[i], Depleted = depleted[i], Stock = stock[i],
                Turnover = turnover[i], TargetTurnover = target[i], Signal = signal[i],
                ExpectedDemand = expected[i], TargetStock = targetStock[i], FinalDemand = final[i],
                GrossOutput = x[i], IntermediateUse = intermediate[i], LabourValue = v[i], DirectLabour = labour[i] * x[i],
                Capacity = capacity[i], Proposed = proposed, LastEnacted = last, Change = change, Major = major,
            });
        }

        var reporting = plan.Reports.Where(r => r.Period == t).Select(r => r.Council).Distinct().ToList();
        return new PlanResult
        {
            Period = t, Productive = true, SpectralRadius = rho, Products = results,
            LabourRequired = labourRequired, LabourAvailable = s.LabourAvailable, LabourProposed = Dot(labour, fit), Limits = limits,
            Flags = Anomalies(plan, t), LedgerIntact = ledgerOk,
            CouncilsReporting = reporting,
            CouncilsMissing = plan.Councils.Select(c => c.Name).Where(c => !reporting.Contains(c)).ToList(),
        };
    }

    /// <summary>
    /// The most of the need that capacity and labour allow. A product over its capacity has its own final demand cut
    /// by the excess, divided by how much its demand raises its own output, as often as needed; if what's left is other sectors'
    /// use of it, the whole plan is scaled to fit. Then, if labour falls short, the whole plan is scaled to it.
    /// Returns the gross outputs that fit, and what limited them.
    /// </summary>
    public static (double[] Output, List<string> Limits) Fit(Plan plan, double[,] ia, double[] final, double?[] capacity, double[] labour)
    {
        int n = final.Length;
        var d = (double[])final.Clone();
        var limits = new List<string>();
        var x = Solve(ia, d)!;
        // how much one more unit of each product's own demand raises its own output (the Leontief inverse's
        // diagonal, at least 1): its demand is cut by the excess over this, so its output lands on its capacity
        var self = new double[n];
        for (int i = 0; i < n; i++)
        {
            var e = new double[n]; e[i] = 1;
            self[i] = Math.Max(1, Solve(ia, e)![i]);
        }
        for (int round = 0; round < 100; round++)
        {
            bool over = false;
            for (int i = 0; i < n; i++)
            {
                if (capacity[i] is not double cap || x[i] <= cap * (1 + 1e-9)) continue;
                over = true;
                var name = plan.Products[i].Name + " capacity";
                if (!limits.Contains(name)) limits.Add(name);
                if (d[i] > 1e-9) d[i] = Math.Max(0, d[i] - (x[i] - cap) / self[i]);
                else
                {
                    // its own demand is gone: what's left is the other sectors' use of it, so everything shrinks
                    double k = x[i] > 0 ? cap / x[i] : 0;
                    for (int j = 0; j < n; j++) d[j] *= k;
                }
            }
            x = Solve(ia, d)!;
            if (!over) break;
        }
        if (plan.Settings.LabourAvailable is double avail && Dot(labour, x) > avail * (1 + 1e-9))
        {
            double k = avail / Dot(labour, x);
            for (int j = 0; j < n; j++) x[j] *= k;
            limits.Add("labour");
        }
        for (int j = 0; j < n; j++) x[j] = Math.Max(0, x[j]);
        return (x, limits);
    }

    /// <summary>The quota last enacted for a product before a period (ratified or amended), if any.</summary>
    public static double? LastEnacted(Plan plan, string productId, int beforePeriod) =>
        plan.Decisions.Where(d => d.Product == productId && d.Period < beforePeriod && d.Value != null
                                  && d.Status is DecisionStatus.Ratified or DecisionStatus.Amended)
            .OrderByDescending(d => d.Period).ThenByDescending(d => d.When).Select(d => d.Value).FirstOrDefault();

    // ───────── fraud and volatility: reports that stand out ─────────

    /// <summary>
    /// Reports far from their own council's history, or far from their peers that period, measured in robust
    /// deviations (the median and the median absolute deviation, so one bad figure doesn't hide another).
    /// </summary>
    public static List<Flag> Anomalies(Plan plan, int period)
    {
        var flags = new List<Flag>();
        var k = plan.Settings.AnomalyThreshold;
        var now = plan.Reports.Where(r => r.Period == period).ToList();
        foreach (var p in plan.Products)
        {
            // against its own history (three earlier periods at least)
            foreach (var r in now)
            {
                if (r.Line(p.Id) is not { } line) continue;
                var history = plan.Reports.Where(h => h.Council == r.Council && h.Period < period)
                    .Select(h => h.Line(p.Id)?.Depleted).Where(d => d != null).Select(d => d!.Value).ToList();
                if (history.Count < 3) continue;
                var z = RobustZ(line.Depleted, history, RelativeFloor);
                if (Math.Abs(z) > k)
                    flags.Add(new Flag { Council = r.Council, Product = p.Id, Value = z,
                        Why = $"{p.Name}: {Fmt(line.Depleted)} {p.Unit} used up, far {(z > 0 ? "above" : "below")} its own last {history.Count} periods (median {Fmt(Median(history))})" });
            }
            // against its peers this period, per unit of capacity where it's known (four councils at least)
            var rates = new List<(string Council, double Rate)>();
            foreach (var r in now)
            {
                if (r.Line(p.Id) is not { } line) continue;
                var c = plan.Councils.FirstOrDefault(c => c.Name == r.Council);
                double basis = c != null && c.Capacity.TryGetValue(p.Id, out var cap) && cap > 0 ? cap : 1;
                rates.Add((r.Council, line.Depleted / basis));
            }
            if (rates.Count >= 4)
                foreach (var (council, rate) in rates)
                {
                    var others = rates.Where(o => o.Council != council).Select(o => o.Rate).ToList();
                    var z = RobustZ(rate, others, RelativeFloor);
                    if (Math.Abs(z) > k && !flags.Any(f => f.Council == council && f.Product == p.Id))
                        flags.Add(new Flag { Council = council, Product = p.Id, Value = z,
                            Why = $"{p.Name}: far {(z > 0 ? "above" : "below")} the other councils this period, for its capacity" });
                }
        }
        // a report changed since it was imported
        var ledger = Ledger.Verify(plan);
        foreach (var bad in ledger.Altered)
            flags.Add(new Flag { Council = bad.Council, Product = "", Value = double.NaN,
                Why = $"its report for period {bad.Period} has changed since it was imported (the ledger doesn't match)" });
        return flags;
    }

    public static double Median(IEnumerable<double> values)
    {
        var a = values.OrderBy(x => x).ToArray();
        if (a.Length == 0) return 0;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    /// <summary>
    /// The least spread taken for a set of figures, as a share of their median: a few periods of steady figures have
    /// almost none, and ordinary growth or noise would otherwise look like fraud. At 10%, with the threshold at 3.5,
    /// a figure about half again above or below the median is flagged.
    /// </summary>
    public const double RelativeFloor = 0.10;

    /// <summary>How far a value is from others, in robust standard deviations: 0.6745 · (x − median) / MAD, the MAD
    /// no less than floor · |median|.</summary>
    public static double RobustZ(double value, IReadOnlyList<double> others, double floor = 0)
    {
        var med = Median(others);
        var mad = Math.Max(Median(others.Select(o => Math.Abs(o - med))), floor * Math.Abs(med));
        if (mad < 1e-12) return Math.Abs(value - med) < 1e-9 ? 0 : Math.Sign(value - med) * double.MaxValue;
        return 0.6745 * (value - med) / mad;
    }

    // ───────── computational execution: quotas down to regions and councils ─────────

    /// <summary>
    /// Each product's enacted quota split among the councils, in proportion to their capacity for it (or, where no
    /// capacity is known, to what they made this period; or else evenly).
    /// </summary>
    public static List<CouncilQuota> Disaggregate(Plan plan, int period)
    {
        var list = new List<CouncilQuota>();
        var councils = plan.Regions.SelectMany(r => r.Councils.Select(c => (Region: r.Name, Council: c))).ToList();
        if (councils.Count == 0) return list;
        foreach (var p in plan.Products)
        {
            var d = plan.Decisions.Where(x => x.Period == period && x.Product == p.Id).OrderByDescending(x => x.When).FirstOrDefault();
            if (d?.Value is not double quota) continue;
            var weights = councils.Select(c => c.Council.Capacity.TryGetValue(p.Id, out var cap) ? cap : 0).ToArray();
            if (weights.Sum() <= 0)
                weights = councils.Select(c => plan.Reports.FirstOrDefault(r => r.Council == c.Council.Name && r.Period == period)?.Line(p.Id)?.Produced ?? 0).ToArray();
            if (weights.Sum() <= 0) weights = councils.Select(_ => 1.0).ToArray();
            double total = weights.Sum();
            for (int k = 0; k < councils.Count; k++)
                if (weights[k] > 0)
                    list.Add(new CouncilQuota { Region = councils[k].Region, Council = councils[k].Council.Name, Product = p.Id, Quota = quota * weights[k] / total });
        }
        return list;
    }

    // ───────── linear algebra ─────────

    public static double[,] Matrix(Plan plan)
    {
        int n = plan.Products.Count;
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                a[i, j] = Math.Max(0, plan.Inputs[i][j]);
        return a;
    }

    public static double[,] IdentityMinus(double[,] a)
    {
        int n = a.GetLength(0);
        var m = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = (i == j ? 1 : 0) - a[i, j];
        return m;
    }

    public static double[,] Transpose(double[,] a)
    {
        int n = a.GetLength(0), m = a.GetLength(1);
        var t = new double[m, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
                t[j, i] = a[i, j];
        return t;
    }

    public static double[] Multiply(double[,] a, double[] x)
    {
        int n = a.GetLength(0);
        var y = new double[n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < x.Length; j++)
                y[i] += a[i, j] * x[j];
        return y;
    }

    public static double Dot(double[] a, double[] b) => a.Zip(b, (p, q) => p * q).Sum();

    /// <summary>Solves m·x = b by Gaussian elimination with partial pivoting; null when m is singular.</summary>
    public static double[]? Solve(double[,] m, double[] b)
    {
        int n = b.Length;
        var a = (double[,])m.Clone();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (Math.Abs(a[piv, c]) < 1e-12) return null;
            if (piv != c)
            {
                for (int k = 0; k < n; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]);
                (x[c], x[piv]) = (x[piv], x[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = a[r, c] / a[c, c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) a[r, k] -= f * a[c, k];
                x[r] -= f * x[c];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double sum = x[r];
            for (int k = r + 1; k < n; k++) sum -= a[r, k] * x[k];
            x[r] = sum / a[r, r];
        }
        return x;
    }

    /// <summary>
    /// The largest eigenvalue of a table of inputs, which are never negative (Perron–Frobenius), found by repeated
    /// multiplication. Under 1, the economy is productive: (I − A)⁻¹ exists and has no negative entries.
    /// </summary>
    public static double SpectralRadius(double[,] a)
    {
        int n = a.GetLength(0);
        if (n == 0) return 0;
        // a little added to every entry keeps the iteration from cycling on tables with zeros (it changes the
        // eigenvalue by at most n·ε, well under what matters here)
        const double eps = 1e-9;
        var x = Enumerable.Repeat(1.0 / n, n).ToArray();
        double lambda = 0;
        for (int it = 0; it < 2000; it++)
        {
            var y = new double[n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    y[i] += (a[i, j] + eps) * x[j];
            double norm = y.Sum();
            if (norm <= 0) return 0;
            for (int i = 0; i < n; i++) y[i] /= norm;
            double change = 0;
            for (int i = 0; i < n; i++) change += Math.Abs(y[i] - x[i]);
            x = y;
            if (Math.Abs(norm - lambda) < 1e-12 && change < 1e-12) { lambda = norm; break; }
            lambda = norm;
        }
        return Math.Max(0, lambda - n * eps);
    }

    public static string Fmt(double v) =>
        double.IsInfinity(v) ? "∞" : double.IsNaN(v) ? "–"
        : Math.Abs(v) >= 1000 ? v.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)
        : Math.Abs(v) >= 10 ? v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
        : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
