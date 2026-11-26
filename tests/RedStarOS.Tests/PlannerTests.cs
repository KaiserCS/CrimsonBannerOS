// SPDX-License-Identifier: GPL-3.0-or-later
using CrimsonPlanner.Model;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>Planner's engine (apps/planner): the arithmetic against examples worked by hand, the example economy's
/// flags and limits, the ledger, the files councils send and the councils' decisions.</summary>
public class PlannerTests
{
    /// <summary>Two sectors: A = [[0.2, 0.3], [0.4, 0.1]], labour [1, 2], final demand [100, 200].</summary>
    private static Plan Two(double[,]? a = null)
    {
        var p = new Plan();
        p.AddProduct("Grain", "t", 1);
        p.AddProduct("Tools", "units", 2);
        a ??= new[,] { { 0.2, 0.3 }, { 0.4, 0.1 } };
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) p.Inputs[i][j] = a[i, j];
        p.Regions.Add(new Region { Name = "R", Councils = { new Council { Name = "C" } } });
        return p;
    }

    private static void Report(Plan p, int period, string council, params (string Product, double Made, double Used, double Stock)[] lines)
    {
        var r = new Report { Council = council, Period = period, Imported = new DateTime(2026, 1, period, 0, 0, 0, DateTimeKind.Utc) };
        foreach (var l in lines) r.Lines.Add(new ReportLine { Product = l.Product, Produced = l.Made, Depleted = l.Used, Stock = l.Stock });
        Ledger.Record(p, r);
    }

    [Fact]
    public void Leontief_output_and_labour_values_match_the_hand_worked_example()
    {
        var p = Two();
        var ia = Engine.IdentityMinus(Engine.Matrix(p));
        var x = Engine.Solve(ia, new[] { 100.0, 200.0 })!;
        Assert.Equal(250, x[0], 6);
        Assert.Equal(1000.0 / 3, x[1], 6);
        var v = Engine.Solve(Engine.Transpose(ia), new[] { 1.0, 2.0 })!;
        Assert.Equal(1.7 / 0.6, v[0], 6);
        Assert.Equal(1.9 / 0.6, v[1], 6);
        // the labour in final demand, counted through the labour values, is the labour of the gross output
        Assert.Equal(v[0] * 100 + v[1] * 200, 1 * x[0] + 2 * x[1], 6);
        Assert.Equal(0.5, Engine.SpectralRadius(Engine.Matrix(p)), 4);
    }

    [Fact]
    public void An_economy_that_uses_up_more_than_it_makes_is_said_so()
    {
        var p = Two(new[,] { { 0.6, 0.5 }, { 0.5, 0.6 } });
        Report(p, 1, "C", ("grain", 10, 10, 5), ("tools", 10, 10, 5));
        var r = Engine.Run(p);
        Assert.False(r.Productive);
        Assert.Equal(1.1, r.SpectralRadius, 3);
        Assert.Contains("can't produce a surplus", r.Problem);
    }

    [Fact]
    public void Final_demand_is_smoothed_demand_corrected_toward_the_stock_to_hold()
    {
        var p = Two(new double[2, 2]);
        p.Products[0].StockCover = 0.5;
        p.Settings.Smoothing = 0.5;
        p.Settings.CorrectionPeriods = 2;
        Report(p, 1, "C", ("grain", 100, 100, 50), ("tools", 0, 0, 0));
        Report(p, 2, "C", ("grain", 200, 200, 50), ("tools", 0, 0, 0));
        var g = Engine.Run(p).Products[0];
        Assert.Equal(150, g.ExpectedDemand, 6);               // 0.5·200 + 0.5·100
        Assert.Equal(75, g.TargetStock, 6);                   // half a period of it
        Assert.Equal(150 + (75 - 50) / 2.0, g.FinalDemand, 6);
        Assert.Equal(4, g.Turnover, 6);                       // 200 used from an average of 50 held
        Assert.Equal(Signal.Rapid, g.Signal);                 // against a target of 2
        Assert.Equal(g.FinalDemand, g.GrossOutput, 6);        // no inputs: the need is the demand
    }

    [Fact]
    public void Turnover_marks_overstocked_and_normal_products()
    {
        var p = Two(new double[2, 2]);
        Report(p, 1, "C", ("grain", 10, 10, 100), ("tools", 10, 10, 5));
        Report(p, 2, "C", ("grain", 10, 10, 100), ("tools", 10, 10, 5));
        var r = Engine.Run(p);
        Assert.Equal(Signal.Low, r.Products[0].Signal);       // 0.1 against a target of 2
        Assert.Equal(Signal.Normal, r.Products[1].Signal);    // 2 against 2
    }

    [Fact]
    public void The_example_flags_only_the_report_that_was_made_out_of_line()
    {
        var plan = Example.SmallEconomy();
        var r = Engine.Run(plan, 4);
        Assert.True(r.Productive);
        Assert.True(r.LedgerIntact);
        var f = Assert.Single(r.Flags.Select(x => (x.Council, x.Product)).Distinct());
        Assert.Equal(("Eastvale", "clothing"), f);
        Assert.Empty(Engine.Anomalies(plan, 3));
    }

    [Fact]
    public void Capacity_trims_only_the_product_that_runs_out_and_a_big_change_is_a_major_shift()
    {
        var plan = Example.SmallEconomy();
        var r = Engine.Run(plan, 4);
        var clothing = r.Products.Single(p => p.Product.Id == "clothing");
        Assert.Equal(clothing.Capacity!.Value, clothing.Proposed, 3);
        Assert.True(clothing.GrossOutput > clothing.Proposed);
        Assert.Contains("Clothing capacity", r.Limits);
        Assert.True(clothing.Major);
        var steel = r.Products.Single(p => p.Product.Id == "steel");
        // steel isn't at its capacity: it only moves by the little less clothing's machinery needs
        Assert.True(Math.Abs(steel.GrossOutput - steel.Proposed) < steel.GrossOutput * 0.001);
        Assert.False(steel.Major);
        Assert.True(r.LabourAvailable >= r.LabourProposed);
    }

    [Fact]
    public void Labour_short_scales_the_whole_plan_to_it()
    {
        var p = Two();
        Report(p, 1, "C", ("grain", 100, 100, 50), ("tools", 100, 100, 50));
        var full = Engine.Run(p);
        p.Settings.LabourAvailable = full.LabourRequired / 2;
        var half = Engine.Run(p);
        Assert.Contains("labour", half.Limits);
        Assert.Equal(full.LabourRequired / 2, half.LabourProposed, 6);
        Assert.Equal(full.Products[0].GrossOutput / 2, half.Products[0].Proposed, 6);
    }

    [Fact]
    public void The_ledger_catches_a_changed_report_and_a_changed_entry()
    {
        var plan = Example.SmallEconomy();
        Assert.True(Ledger.Verify(plan).Ok);
        plan.Reports.First(r => r.Council == "Dunmore" && r.Period == 2).Lines[0].Depleted += 1;
        var c = Ledger.Verify(plan);
        Assert.False(c.Ok);
        Assert.Contains(("Dunmore", 2), c.Altered);
        Assert.Contains(Engine.Anomalies(plan, 4), f => f.Council == "Dunmore" && f.Why.Contains("changed since it was imported"));

        var other = Example.SmallEconomy();
        other.Ledger[5].Council = "Somewhere else";
        Assert.Equal(5, Ledger.Verify(other).BrokenAt);
    }

    [Fact]
    public void A_saved_plan_opens_the_same_and_its_ledger_still_checks_out()
    {
        var plan = Example.SmallEconomy();
        var again = Plan.FromJson(plan.ToJson());
        Assert.Equal(plan.ToJson(), again.ToJson());
        Assert.True(Ledger.Verify(again).Ok);
        Assert.Equal(Engine.Run(plan, 4).LabourRequired, Engine.Run(again, 4).LabourRequired, 6);
    }

    [Fact]
    public void Reports_are_read_from_CSV_and_unknown_councils_and_products_are_listed_not_guessed()
    {
        var p = Two();
        var csv = "# a region's reports\n" + PlanCsv.ReportHeader + "\n" +
                  "C,1,grain,10,8,4\nC,1,Tools,5,4,\"2\"\nNowhere,1,grain,1,1,1\nC,1,bread,1,1,1\nC,x,grain,1,1,1\nC,1,grain,1,-1,1\n";
        var r = PlanCsv.ReadReports(p, csv, "region.csv");
        var rep = Assert.Single(r.Reports);
        Assert.Equal(2, rep.Lines.Count);
        Assert.Equal(4, rep.Line("tools")!.Depleted);
        Assert.Equal(4, r.Problems.Count);
        Assert.Contains(r.Problems, x => x.Contains("Nowhere"));
        Assert.Contains(r.Problems, x => x.Contains("bread"));
        // taken in twice: the newer replaces the older, and both are in the ledger
        Ledger.Record(p, rep);
        Ledger.Record(p, PlanCsv.ReadReports(p, csv, "again.csv").Reports[0]);
        Assert.Single(p.Reports);
        Assert.Equal(2, p.Ledger.Count);
        Assert.True(Ledger.Verify(p).Ok);
        Assert.Equal(PlanCsv.WriteReports(p, p.Reports), PlanCsv.WriteReports(p, PlanCsv.ReadReports(p, PlanCsv.WriteReports(p, p.Reports), "x").Reports));
    }

    [Fact]
    public void A_major_shift_needs_a_quorum_and_a_rejection_keeps_the_last_quota()
    {
        var plan = Example.SmallEconomy();
        var r = Engine.Run(plan, 4);
        var clothing = r.Products.Single(p => p.Product.Id == "clothing");
        Assert.Contains("quorum", Decisions.Decide(plan, 4, clothing, DecisionStatus.Ratified, null, "Council", 1, 0));
        Assert.Null(Decisions.Decide(plan, 4, clothing, DecisionStatus.Ratified, null, "Council", 3, 1));
        Assert.Equal(clothing.Proposed, Decisions.For(plan, 4, "clothing")!.Value!.Value, 6);

        var steel = r.Products.Single(p => p.Product.Id == "steel");
        Assert.Contains("which council", Decisions.Decide(plan, 4, steel, DecisionStatus.Ratified, null, " "));
        Assert.Null(Decisions.Decide(plan, 4, steel, DecisionStatus.Rejected, null, "Council"));
        Assert.Equal(steel.LastEnacted!.Value, Decisions.For(plan, 4, "steel")!.Value!.Value, 6);
        // an amendment far from the last quota is a major shift too
        Assert.Contains("major shift", Decisions.Decide(plan, 4, steel, DecisionStatus.Amended, steel.LastEnacted * 2, "Council"));
    }

    [Fact]
    public void Quotas_are_shared_among_councils_by_capacity_and_add_up()
    {
        var plan = Example.SmallEconomy();
        var q = Engine.Disaggregate(plan, 3);
        foreach (var p in plan.Products)
        {
            var enacted = Decisions.For(plan, 3, p.Id)!.Value!.Value;
            Assert.Equal(enacted, q.Where(x => x.Product == p.Id).Sum(x => x.Quota), 6);
            var a = plan.Councils.First();
            var share = a.Capacity[p.Id] / plan.Councils.Sum(c => c.Capacity[p.Id]);
            Assert.Equal(enacted * share, q.Single(x => x.Product == p.Id && x.Council == a.Name).Quota, 6);
        }
        var dir = Path.Combine(Path.GetTempPath(), "planner-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = PlanCsv.ExportQuotas(plan, 3, dir);
            Assert.Equal(1 + plan.Councils.Count(), files.Count);
            Assert.True(File.Exists(Path.Combine(dir, "Northern Region", "Ashford.csv")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
