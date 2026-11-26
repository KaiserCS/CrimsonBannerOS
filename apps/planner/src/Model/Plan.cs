// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrimsonPlanner.Model;

/// <summary>
/// A plan: the economy's structure (products, what each takes of the others, the labour each takes), the councils that
/// report and make, every report they've sent, the verification ledger over those reports, and every quota proposed
/// and decided. Saved as one JSON file: the "one larger file" every council's report is compiled into.
/// </summary>
public sealed class Plan
{
    public string Name { get; set; } = "New plan";
    public List<Product> Products { get; set; } = new();
    /// <summary>Inputs[i][j]: how much of product i making one unit of product j uses up (technical coefficients).</summary>
    public List<List<double>> Inputs { get; set; } = new();
    public List<Region> Regions { get; set; } = new();
    public List<Report> Reports { get; set; } = new();
    public List<LedgerEntry> Ledger { get; set; } = new();
    public List<QuotaDecision> Decisions { get; set; } = new();
    public PlanSettings Settings { get; set; } = new();

    [JsonIgnore] public IEnumerable<Council> Councils => Regions.SelectMany(r => r.Councils);
    [JsonIgnore] public int LatestPeriod => Reports.Count == 0 ? 0 : Reports.Max(r => r.Period);

    public int IndexOf(string productId) => Products.FindIndex(p => p.Id == productId);

    public Product AddProduct(string name, string unit, double labour)
    {
        var id = UniqueId(name);
        var p = new Product { Id = id, Name = name, Unit = unit, Labour = labour };
        Products.Add(p);
        foreach (var row in Inputs) row.Add(0);
        Inputs.Add(Enumerable.Repeat(0.0, Products.Count).ToList());
        return p;
    }

    public void RemoveProduct(int i)
    {
        var id = Products[i].Id;
        Products.RemoveAt(i);
        Inputs.RemoveAt(i);
        foreach (var row in Inputs) row.RemoveAt(i);
        foreach (var c in Councils) c.Capacity.Remove(id);
    }

    /// <summary>Keeps Inputs square and as large as the product list (after loading a file written by hand).</summary>
    public void Normalise()
    {
        int n = Products.Count;
        while (Inputs.Count < n) Inputs.Add(new List<double>());
        if (Inputs.Count > n) Inputs.RemoveRange(n, Inputs.Count - n);
        foreach (var row in Inputs)
        {
            while (row.Count < n) row.Add(0);
            if (row.Count > n) row.RemoveRange(n, row.Count - n);
        }
        foreach (var p in Products.Where(p => string.IsNullOrWhiteSpace(p.Id))) p.Id = UniqueId(p.Name);
    }

    public string UniqueId(string name)
    {
        var b = new string(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (b.Contains("--")) b = b.Replace("--", "-");
        if (b.Length == 0) b = "product";
        var id = b;
        for (int k = 2; Products.Any(p => p.Id == id); k++) id = $"{b}-{k}";
        return id;
    }

    // ───────── the file ─────────
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static Plan FromJson(string json)
    {
        var p = JsonSerializer.Deserialize<Plan>(json, Json) ?? throw new InvalidDataException("The file holds no plan.");
        p.Normalise();
        return p;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".saving";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    public static Plan Load(string path) => FromJson(File.ReadAllText(path));
}

public sealed class Product
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "units";
    /// <summary>Hours of labour making one unit takes directly (its inputs' labour is counted through them).</summary>
    public double Labour { get; set; }
    /// <summary>How many periods of demand the shops and stores should hold in stock: its turnover's target is 1 / this.</summary>
    public double StockCover { get; set; } = 0.5;
}

public sealed class Region
{
    public string Name { get; set; } = "";
    public List<Council> Councils { get; set; } = new();
}

public sealed class Council
{
    public string Name { get; set; } = "";
    /// <summary>How much of each product (by id) the council's workplaces can make in a period. Missing: not known.</summary>
    public Dictionary<string, double> Capacity { get; set; } = new();
}

/// <summary>One council's figures for one period, from its shops, stores and workplaces.</summary>
public sealed class Report
{
    public string Council { get; set; } = "";
    public int Period { get; set; }
    public List<ReportLine> Lines { get; set; } = new();
    public DateTime Imported { get; set; }
    /// <summary>Where it came from (a file name), as told.</summary>
    public string Source { get; set; } = "";

    public ReportLine? Line(string productId) => Lines.FirstOrDefault(l => l.Product == productId);
}

public sealed class ReportLine
{
    public string Product { get; set; } = "";
    /// <summary>Made in the period.</summary>
    public double Produced { get; set; }
    /// <summary>Taken out of stock by people in the period (sold or handed out): the period's final demand.</summary>
    public double Depleted { get; set; }
    /// <summary>Left in stock at the end of the period.</summary>
    public double Stock { get; set; }
}

public enum DecisionStatus { Proposed, Ratified, Amended, Rejected }

/// <summary>A council's decision on one proposed quota: the EPA advises, councils decide.</summary>
public sealed class QuotaDecision
{
    public int Period { get; set; }
    public string Product { get; set; } = "";
    public double Proposed { get; set; }
    public DecisionStatus Status { get; set; }
    /// <summary>The quota enacted: the proposal (ratified), the council's figure (amended), or none (rejected).</summary>
    public double? Value { get; set; }
    public string DecidedBy { get; set; } = "";
    public bool Major { get; set; }
    public int VotesFor { get; set; }
    public int VotesAgainst { get; set; }
    public int Quorum { get; set; }
    public string Note { get; set; } = "";
    public DateTime When { get; set; }
}

public sealed class PlanSettings
{
    /// <summary>How much a new period's figures count against what came before (exponential smoothing, 0–1).</summary>
    public double Smoothing { get; set; } = 0.5;
    /// <summary>Turnover within this fraction of its target counts as normal.</summary>
    public double TurnoverBand { get; set; } = 0.25;
    /// <summary>Over how many periods a stock shortfall or surplus is made up.</summary>
    public double CorrectionPeriods { get; set; } = 2;
    /// <summary>A quota changing by more than this fraction from the last one enacted is a major shift: it needs a quorum.</summary>
    public double MajorShift { get; set; } = 0.10;
    /// <summary>Councils' votes a major shift needs.</summary>
    public int Quorum { get; set; } = 3;
    /// <summary>A report this many robust deviations from its council's history, or from its peers, is flagged for a recount.</summary>
    public double AnomalyThreshold { get; set; } = 3.5;
    /// <summary>Hours of labour there are in a period, if known.</summary>
    public double? LabourAvailable { get; set; }
}
