// SPDX-License-Identifier: GPL-3.0-or-later
namespace CrimsonPlanner.Model;

/// <summary>
/// A small made-up economy to try Planner with: five sectors, two regions of three councils, four periods of reports
/// (the last with one council's figures out of line, to show a recount), and the third period's quotas ratified.
/// The figures are invented; only their shape is meant to be realistic.
/// </summary>
public static class Example
{
    public static Plan SmallEconomy()
    {
        var plan = new Plan { Name = "Example: a small economy" };
        var produce = plan.AddProduct("Produce", "t", 20);
        var power = plan.AddProduct("Electricity", "MWh", 5);
        var steel = plan.AddProduct("Steel", "t", 15);
        var machines = plan.AddProduct("Machinery", "units", 400);
        var clothing = plan.AddProduct("Clothing", "thousand garments", 300);
        produce.StockCover = 0.3; power.StockCover = 0.05; steel.StockCover = 0.5; machines.StockCover = 0.6; clothing.StockCover = 0.5;

        // what one unit of the column's product uses up of the row's
        void In(Product of, Product into, double amount) => plan.Inputs[plan.IndexOf(of.Id)][plan.IndexOf(into.Id)] = amount;
        In(produce, produce, 0.05); In(produce, clothing, 0.20);
        In(power, produce, 0.10); In(power, power, 0.05); In(power, steel, 0.50); In(power, machines, 2.0); In(power, clothing, 0.30);
        In(steel, machines, 1.50); In(steel, power, 0.002);
        In(machines, produce, 0.002); In(machines, power, 0.001); In(machines, steel, 0.01); In(machines, machines, 0.02); In(machines, clothing, 0.005);
        In(clothing, clothing, 0.01);

        // councils, with what each can make in a period
        var names = new[] { ("Northern Region", new[] { "Ashford", "Brightwater", "Cedar Falls" }), ("Southern Region", new[] { "Dunmore", "Eastvale", "Fairhaven" }) };
        var baseCap = new Dictionary<string, double> { [produce.Id] = 9000, [power.Id] = 30000, [steel.Id] = 3000, [machines.Id] = 450, [clothing.Id] = 160 };
        int seed = 7;
        double Rnd() { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / (double)0x7fffffff; }
        foreach (var (region, councils) in names)
        {
            var r = new Region { Name = region };
            foreach (var c in councils)
            {
                var council = new Council { Name = c };
                foreach (var (id, cap) in baseCap) council.Capacity[id] = Math.Round(cap * (0.7 + 0.6 * Rnd()));
                r.Councils.Add(council);
            }
            plan.Regions.Add(r);
        }

        // four periods of reports: what people took, what was made, what's left in stock
        var demand = new Dictionary<string, double> { [produce.Id] = 5200, [power.Id] = 14000, [steel.Id] = 600, [machines.Id] = 120, [clothing.Id] = 95 };
        var stockShare = new Dictionary<string, double> { [produce.Id] = 0.30, [power.Id] = 0.05, [steel.Id] = 0.50, [machines.Id] = 1.10, [clothing.Id] = 0.25 };
        var stocks = new Dictionary<(string, string), double>();
        var start = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        for (int period = 1; period <= 4; period++)
            foreach (var c in plan.Councils)
            {
                var report = new Report { Council = c.Name, Period = period, Imported = start.AddDays(30 * period), Source = "example" };
                foreach (var p in plan.Products)
                {
                    double share = c.Capacity[p.Id] / plan.Councils.Sum(x => x.Capacity[p.Id]);
                    double used = demand[p.Id] * 6 * share * (1 + 0.03 * period) * (0.94 + 0.12 * Rnd());
                    if (period == 4 && c.Name == "Eastvale" && p.Id == clothing.Id) used *= 4;     // out of line: a recount
                    var key = (c.Name, p.Id);
                    double before = stocks.TryGetValue(key, out var s0) ? s0 : used * stockShare[p.Id];
                    double made = used * (0.95 + 0.1 * Rnd());
                    double after = Math.Max(0, before + made - used);
                    stocks[key] = after;
                    report.Lines.Add(new ReportLine { Product = p.Id, Produced = Math.Round(made, 1), Depleted = Math.Round(used, 1), Stock = Math.Round(after, 1) });
                }
                Ledger.Record(plan, report);
            }

        // the third period's quotas, ratified by the regional councils
        var third = Engine.Run(plan, 3);
        foreach (var r in third.Products)
            Decisions.Decide(plan, 3, r, DecisionStatus.Ratified, null, "Joint regional councils", votesFor: 5, votesAgainst: 1);
        foreach (var d in plan.Decisions) d.When = start.AddDays(100);

        // labour there is: a little more than the fourth period's plan needs
        var fourth = Engine.Run(plan, 4);
        plan.Settings.LabourAvailable = Math.Round(fourth.LabourRequired * 1.06 / 1000) * 1000;
        return plan;
    }
}
