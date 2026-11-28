// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CrimsonPlanner.Model;

namespace CrimsonPlanner.Views;

/// <summary>
/// Planner's window, in the shell's deck look: a sidebar of pages and the page beside it. The plan is saved to its
/// file after every change (Documents/Plans), as the shell's Notes are.
///   Overview   the plan, the three steps (logging, sorting, execution) and how each stands
///   Economy    products, what each takes of the others, the labour each takes, and the plan's settings
///   Councils   regions and their local councils, with what each can make
///   Reports    councils' reports taken in (CSV), each recorded in the ledger
///   Plan       the period's proposals, and the councils' decisions on them
///   Quotas     the enacted quotas, down to every council, and their files
///   Ledger     the verification ledger, checked
/// The simple view (SimpleView.cs) puts the same plan in plain words for anyone, and is what Planner opens with
/// until someone chooses the advanced one.
/// </summary>
public sealed partial class PlannerWindow : Window
{
    private Plan _plan;
    private string? _path;
    private string _page = "overview";
    private int _period;
    private PlanResult? _result;
    private readonly ContentControl _content = new();
    private readonly StackPanel _nav = new() { Spacing = 2 };
    private readonly TextBlock _fileLine = new() { Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private string _message = "";
    private bool _messageBad;
    private string _decidedBy = "";
    private int _votesFor, _votesAgainst;

    public static string PlansFolder
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            return Path.Combine(docs, "Plans");
        }
    }

    public PlannerWindow() : this(null) { }

    public PlannerWindow(string? file, bool save = true)
    {
        Title = "Planner";
        Width = 1240; Height = 820; MinWidth = 900; MinHeight = 600;
        _autosave = save;
        _plan = new Plan();
        if (!save) { }      // the tests' window: they give it its plan
        else if (file != null && File.Exists(file)) Open(file, quiet: true);
        else if (LatestPlan() is { } recent) Open(recent, quiet: true);
        else { _plan = Example.SmallEconomy(); _path = PathFor(_plan); Save(); }
        _period = _plan.LatestPeriod;
        _simple = save && LoadSimple();

        var side = new DockPanel { Width = 216, Margin = new Thickness(14, 16, 6, 14) };
        var brand = new StackPanel { Spacing = 2, Margin = new Thickness(10, 2, 0, 18) };
        brand.Children.Add(Kicker("CRIMSON BANNER OS", accent: true));
        brand.Children.Add(new TextBlock { Text = "Planner", FontSize = 22, FontWeight = FontWeight.Bold });
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);
        var foot = new StackPanel { Spacing = 6, Margin = new Thickness(10, 10, 4, 0) };
        foot.Children.Add(Kicker("VIEW"));
        foot.Children.Add(_viewSwitch);
        foot.Children.Add(Kicker("ADVISES · COUNCILS DECIDE"));
        foot.Children.Add(_fileLine);
        DockPanel.SetDock(foot, Dock.Bottom);
        side.Children.Add(foot);
        side.Children.Add(_nav);
        BuildNav();

        // pages fit the window's width (their panels wrap); only wide tables scroll sideways, each in its own panel
        var scroll = new ScrollViewer { Content = _content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        root.Children.Add(side);
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        ShowPage(_simple ? "simple" : "overview");
    }

    private readonly bool _autosave;

    // ───────── pages ─────────

    public void ShowPage(string page)
    {
        _page = page;
        foreach (var b in _nav.Children.OfType<Button>())
            if ((string?)b.Tag == page) b.Classes.Add("on"); else b.Classes.Remove("on");
        _fileLine.Text = _path == null ? "Not saved" : "Saved in Plans as " + Path.GetFileName(_path);
        _content.Content = page switch
        {
            "economy" => EconomyPage(),
            "councils" => CouncilsPage(),
            "reports" => ReportsPage(),
            "plan" => PlanPage(),
            "quotas" => QuotasPage(),
            "ledger" => LedgerPage(),
            "simple" => SimplePage(),
            "howto" => HowToPage(),
            _ => OverviewPage(),
        };
    }

    private void Refresh() => ShowPage(_page);

    private PlanResult Result()
    {
        if (_result == null || _result.Period != _period) _result = Engine.Run(_plan, _period);
        return _result;
    }

    /// <summary>After any change: the figures are worked out again, and the plan saved.</summary>
    private void Changed(bool rebuild = false)
    {
        _result = null;
        Save();
        if (rebuild) Refresh();
    }

    private void Say(string text, bool bad = false) { _message = text; _messageBad = bad; }

    private Control? Message()
    {
        if (_message.Length == 0) return null;
        var t = new TextBlock { Text = _message, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        if (_messageBad) t.Foreground = Brushes.OrangeRed;
        _message = "";
        return Panel(t);
    }

    private StackPanel Page(string kicker, string title, string about)
    {
        var col = new StackPanel { Spacing = 16, Margin = new Thickness(22, 18, 28, 32), MaxWidth = 1400, HorizontalAlignment = HorizontalAlignment.Left };
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(Kicker(kicker, accent: true));
        head.Children.Add(new TextBlock { Text = title, Classes = { "display" } });
        head.Children.Add(Muted(about));
        col.Children.Add(head);
        if (Message() is { } m) col.Children.Add(m);
        return col;
    }

    // ── Overview ──
    private Control OverviewPage()
    {
        var col = Page("ECONOMIC PLANNING ALGORITHM · ADVISORY", _plan.Name,
            "Planner proposes quotas from what the councils report: it advises, and councils decide. Every figure can be traced back to the reports it came from, and nothing leaves this computer.");
        var r = Result();
        var lcd = Lcd(("PERIOD", _period.ToString("00")), ("PRODUCTS", _plan.Products.Count.ToString("00")),
                      ("COUNCILS", _plan.Councils.Count().ToString("00")), ("RECOUNTS", r.Flags.Select(f => f.Council).Distinct().Count().ToString("00")));
        col.Children.Add(lcd);

        int reporting = r.CouncilsReporting.Count, all = _plan.Councils.Count();
        var ledger = Ledger.Verify(_plan);
        int rapid = r.Products.Count(p => p.Signal is Signal.Rapid or Signal.NoStock), low = r.Products.Count(p => p.Signal == Signal.Low);
        int decided = _plan.Products.Count(p => Decisions.For(_plan, _period, p.Id) is { Status: not DecisionStatus.Proposed });
        var steps = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        void Step(int c, string kicker, string title, string body, string action, string page)
        {
            var s = new StackPanel { Spacing = 6 };
            s.Children.Add(Kicker(kicker, accent: true));
            s.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
            s.Children.Add(Muted(body));
            var b = Pill(action); b.Click += (_, _) => ShowPage(page); b.Margin = new Thickness(0, 6, 0, 0);
            s.Children.Add(b);
            var p = Panel(s); Grid.SetColumn(p, c); steps.Children.Add(p);
        }
        Step(0, "1 · LOGGING", $"{reporting} of {all} councils reported",
            (r.CouncilsMissing.Count > 0 ? "Still to report: " + string.Join(", ", r.CouncilsMissing) + ". " : "") + (ledger.Ok ? "The ledger checks out." : ledger.Summary),
            "Reports", "reports");
        Step(1, "2 · SORTING", r.Productive ? $"{rapid} running short, {low} overstocked" : "The economy can't be solved",
            r.Productive ? "Turnover against its target is each product's shadow price; demand is smoothed with the periods before." : r.Problem ?? "",
            "Economy", "economy");
        Step(2, "3 · EXECUTION", $"{decided} of {_plan.Products.Count} quotas decided",
            r.Fits ? "Capacity and labour meet the whole need." : "Held back by " + string.Join(", ", r.Limits) + ".",
            "Plan", "plan");
        col.Children.Add(steps);

        // plans on this computer
        var files = new StackPanel { Spacing = 8 };
        files.Children.Add(Kicker("PLANS"));
        var name = new TextBox { Text = _plan.Name, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        name.LostFocus += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text) && name.Text != _plan.Name) { _plan.Name = name.Text.Trim(); Changed(); } };
        files.Children.Add(Labelled("This plan's name", name));
        var actions = new WrapPanel();
        var fresh = Pill("New plan"); fresh.Click += (_, _) => { NewPlan(empty: true); };
        var example = Pill("Open the example"); example.Click += (_, _) => { NewPlan(empty: false); };
        foreach (var b in new[] { fresh, example }) { b.Margin = new Thickness(0, 0, 8, 8); actions.Children.Add(b); }
        files.Children.Add(actions);
        var list = Directory.Exists(PlansFolder)
            ? Directory.GetFiles(PlansFolder, "*.plan.json").OrderByDescending(File.GetLastWriteTimeUtc).ToList() : new List<string>();
        foreach (var f in list)
        {
            var row = new DockPanel();
            var open = Pill(f == _path ? "Open now" : "Open");
            open.IsEnabled = f != _path;
            open.Click += (_, _) => { Open(f); _period = _plan.LatestPeriod; ShowPage("overview"); };
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
            row.Children.Add(new TextBlock { Text = Path.GetFileName(f) + "   ·   " + File.GetLastWriteTime(f).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center });
            files.Children.Add(row);
        }
        files.Children.Add(Muted($"Plans are kept in {PlansFolder}. Drop a plan file or a council's report (CSV) on this window to open or take it in."));
        col.Children.Add(Panel(files));
        return col;
    }

    // ── Economy ──
    private Control EconomyPage()
    {
        var col = Page("ECONOMY · THE STRUCTURE OF PRODUCTION", "What is made, and from what",
            "Each product's labour, the stock the shops should hold, and what making one unit of it uses up of every other product. These are the input–output table Planner works from.");
        int n = _plan.Products.Count;

        // products
        var prod = new StackPanel { Spacing = 8 };
        prod.Children.Add(Kicker("PRODUCTS"));
        var g = Table("Product,Unit,Labour (hours per unit),Stock to hold (periods of demand),");
        for (int i = 0; i < n; i++)
        {
            var p = _plan.Products[i];
            int row = i + 1, idx = i;
            var name = Text(p.Name, v => p.Name = v, 200);
            var unit = Text(p.Unit, v => p.Unit = v, 150);
            var labour = Num(p.Labour, v => p.Labour = v ?? 0);
            var cover = Num(p.StockCover, v => p.StockCover = Math.Max(v ?? 0.5, 0.01));
            var remove = new Button { Content = "Remove", Classes = { "flat" } };
            remove.Click += (_, _) => { _plan.RemoveProduct(idx); Changed(rebuild: true); };
            Cells(g, row, name, unit, labour, cover, remove);
        }
        prod.Children.Add(g);
        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var newName = new TextBox { PlaceholderText = "A new product", Width = 200 };
        var newUnit = new TextBox { PlaceholderText = "Its unit", Width = 150 };
        var addB = Pill("Add");
        addB.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(newName.Text)) return;
            _plan.AddProduct(newName.Text.Trim(), string.IsNullOrWhiteSpace(newUnit.Text) ? "units" : newUnit.Text.Trim(), 0);
            Changed(rebuild: true);
        };
        add.Children.Add(newName); add.Children.Add(newUnit); add.Children.Add(addB);
        prod.Children.Add(add);
        col.Children.Add(Panel(prod));

        // the input table
        if (n > 0)
        {
            var inputs = new StackPanel { Spacing = 8 };
            inputs.Children.Add(Kicker("WHAT EACH TAKES OF THE OTHERS"));
            inputs.Children.Add(Muted("Down a column: what making one unit of that product uses up of each product on the left. Machinery taking 1.5 t of steel per unit, say, is 1.5 in the steel row, machinery column."));
            var m = Table("Uses up ↓   to make one →," + string.Join(",", _plan.Products.Select(p => $"{p.Name} ({p.Unit})")));
            for (int i = 0; i < n; i++)
            {
                var cells = new List<Control> { new TextBlock { Text = $"{_plan.Products[i].Name} ({_plan.Products[i].Unit})", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold } };
                for (int j = 0; j < n; j++)
                {
                    int ii = i, jj = j;
                    cells.Add(Num(_plan.Inputs[i][j], v => _plan.Inputs[ii][jj] = v ?? 0, 96));
                }
                Cells(m, i + 1, cells.ToArray());
            }
            inputs.Children.Add(new ScrollViewer { Content = m, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            var r = Result();
            inputs.Children.Add(Muted(r.Productive
                ? $"The economy is productive: its sectors make more than they use up (the table's largest eigenvalue is {r.SpectralRadius:0.###}, under 1)."
                : r.Problem ?? ""));
            col.Children.Add(Panel(inputs));
        }

        // settings
        var s = _plan.Settings;
        var set = new StackPanel { Spacing = 10 };
        set.Children.Add(Kicker("HOW THE PLAN IS WORKED OUT"));
        var sg = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 14, RowSpacing = 8 };
        int sr = 0;
        void Setting(string label, Control box, string about)
        {
            sg.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
            var a = Muted(about); a.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(l, sr); Grid.SetRow(box, sr); Grid.SetRow(a, sr);
            Grid.SetColumn(box, 1); Grid.SetColumn(a, 2);
            sg.Children.Add(l); sg.Children.Add(box); sg.Children.Add(a);
            sr++;
        }
        Setting("Smoothing", Num(s.Smoothing, v => s.Smoothing = Math.Clamp(v ?? 0.5, 0.01, 1)), "How much a new period counts against the ones before (1: only the newest; lower: steadier).");
        Setting("Turnover band", Num(s.TurnoverBand * 100, v => s.TurnoverBand = Math.Clamp((v ?? 25) / 100, 0, 5)), "% either side of a product's target turnover still counted as normal.");
        Setting("Correction", Num(s.CorrectionPeriods, v => s.CorrectionPeriods = Math.Max(v ?? 2, 1)), "Periods over which stock that's short or over is made up.");
        Setting("Major shift", Num(s.MajorShift * 100, v => s.MajorShift = Math.Max((v ?? 10) / 100, 0)), "% change from the last quota enacted above which councils need a quorum.");
        Setting("Quorum", Num(s.Quorum, v => s.Quorum = (int)Math.Max(Math.Round(v ?? 3), 1)), "Councils' votes in favour a major shift needs.");
        Setting("Recount at", Num(s.AnomalyThreshold, v => s.AnomalyThreshold = Math.Max(v ?? 3.5, 1)), "Robust deviations from a council's history, or its peers, at which its report is flagged.");
        Setting("Labour available", Num(s.LabourAvailable, v => s.LabourAvailable = v, allowEmpty: true), "Hours of work there are in a period (empty: not known, no limit).");
        set.Children.Add(sg);
        col.Children.Add(Panel(set));
        return col;
    }

    // ── Councils ──
    private Control CouncilsPage()
    {
        var col = Page("COUNCILS · WHO REPORTS, WHO MAKES", "Regions and local councils",
            "Each local council reports what its shops and workplaces did, and receives its share of the quotas, in proportion to what its workplaces can make. Leave a capacity empty if it isn't known.");
        foreach (var region in _plan.Regions.ToList())
        {
            var box = new StackPanel { Spacing = 8 };
            var head = new DockPanel();
            var removeR = new Button { Content = "Remove region", Classes = { "flat" } };
            removeR.Click += (_, _) => { _plan.Regions.Remove(region); Changed(rebuild: true); };
            DockPanel.SetDock(removeR, Dock.Right);
            head.Children.Add(removeR);
            head.Children.Add(Text(region.Name, v => region.Name = v, 280));
            box.Children.Add(head);
            var g = Table("Council," + string.Join(",", _plan.Products.Select(p => $"{p.Name} ({p.Unit})")) + ",");
            int row = 1;
            foreach (var c in region.Councils.ToList())
            {
                var cells = new List<Control> { Text(c.Name, v => RenameCouncil(c, v), 170) };
                foreach (var p in _plan.Products)
                {
                    var id = p.Id;
                    cells.Add(Num(c.Capacity.TryGetValue(id, out var cap) ? cap : null, v => { if (v is double d) c.Capacity[id] = d; else c.Capacity.Remove(id); }, 96, allowEmpty: true));
                }
                var remove = new Button { Content = "Remove", Classes = { "flat" } };
                remove.Click += (_, _) => { region.Councils.Remove(c); Changed(rebuild: true); };
                cells.Add(remove);
                Cells(g, row++, cells.ToArray());
            }
            box.Children.Add(new ScrollViewer { Content = g, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var nn = new TextBox { PlaceholderText = "A new local council", Width = 220 };
            var ab = Pill("Add council");
            ab.Click += (_, _) =>
            {
                var name = nn.Text?.Trim() ?? "";
                if (name.Length == 0) return;
                if (_plan.Councils.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) { Say($"There's already a council called {name}.", true); Refresh(); return; }
                region.Councils.Add(new Council { Name = name });
                Changed(rebuild: true);
            };
            add.Children.Add(nn); add.Children.Add(ab);
            box.Children.Add(add);
            box.Children.Add(Kicker("CAPACITY: HOW MUCH EACH CAN MAKE IN A PERIOD"));
            col.Children.Add(Panel(box));
        }
        var addR = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var rn = new TextBox { PlaceholderText = "A new region", Width = 240 };
        var rb = Pill("Add region", accent: true);
        rb.Click += (_, _) => { if (string.IsNullOrWhiteSpace(rn.Text)) return; _plan.Regions.Add(new Region { Name = rn.Text.Trim() }); Changed(rebuild: true); };
        addR.Children.Add(rn); addR.Children.Add(rb);
        col.Children.Add(addR);
        return col;
    }

    private void RenameCouncil(Council c, string name)
    {
        if (name == c.Name || _plan.Councils.Any(x => x != c && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return;
        // reports and decisions keep their council: they're renamed with it, and the ledger records them again
        var reports = _plan.Reports.Where(r => r.Council == c.Name).ToList();
        foreach (var r in reports) { _plan.Reports.Remove(r); r.Council = name; }
        foreach (var r in reports) Ledger.Record(_plan, r);
        c.Name = name;
    }

    // ── Reports ──
    private Control ReportsPage()
    {
        var col = Page("1 · COMPUTATIONAL LOGGING", "Councils' reports",
            "Each council reports, per product, what was made, what people took out of stock (sold or handed out) and what's left. Every report is recorded in the ledger as it's taken in.");
        var imp = new StackPanel { Spacing = 8 };
        imp.Children.Add(Kicker("TAKE IN REPORTS"));
        imp.Children.Add(Muted($"A CSV file, one line per council, period and product: {PlanCsv.ReportHeader}. One file can hold many councils, as a region's does. Drop files on this window, give a path, or put them in {InboxFolder} and take them in from the list below."));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var path = new TextBox { PlaceholderText = "/path/to/report.csv", Width = 460 };
        var go = Pill("Take in", accent: true);
        go.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(path.Text)) { ImportFile(path.Text.Trim()); Refresh(); } };
        row.Children.Add(path); row.Children.Add(go);
        imp.Children.Add(row);
        if (Directory.Exists(InboxFolder))
            foreach (var f in Directory.GetFiles(InboxFolder, "*.csv").OrderBy(f => f))
            {
                var r = new DockPanel();
                var b = Pill("Take in"); b.Click += (_, _) => { ImportFile(f); Refresh(); };
                DockPanel.SetDock(b, Dock.Right);
                r.Children.Add(b);
                r.Children.Add(new TextBlock { Text = Path.GetFileName(f), VerticalAlignment = VerticalAlignment.Center });
                imp.Children.Add(r);
            }
        var blank = Pill("Write a blank report form");
        blank.Click += (_, _) => { WriteForm(); Refresh(); };
        imp.Children.Add(blank);
        col.Children.Add(Panel(imp));

        var ledger = Ledger.Verify(_plan);
        foreach (var period in _plan.Reports.Select(r => r.Period).Distinct().OrderByDescending(p => p))
        {
            var box = new StackPanel { Spacing = 6 };
            var reports = _plan.Reports.Where(r => r.Period == period).OrderBy(r => r.Council).ToList();
            box.Children.Add(Kicker($"PERIOD {period} · {reports.Count} OF {_plan.Councils.Count()} COUNCILS", accent: period == _plan.LatestPeriod));
            var g = Table("Council,Taken in,From,Ledger," + string.Join(",", _plan.Products.Select(p => p.Name + " used up")));
            int r = 1;
            foreach (var rep in reports)
            {
                bool altered = ledger.Altered.Contains((rep.Council, rep.Period)) || ledger.Unrecorded.Contains((rep.Council, rep.Period));
                var cells = new List<Control>
                {
                    Cell(rep.Council, bold: true), Cell(rep.Imported.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)),
                    Cell(rep.Source), Badge(altered ? "CHANGED" : "RECORDED", accent: altered),
                };
                foreach (var p in _plan.Products) cells.Add(Cell(rep.Line(p.Id) is { } l ? Engine.Fmt(l.Depleted) : "–", right: true));
                Cells(g, r++, cells.ToArray());
            }
            box.Children.Add(new ScrollViewer { Content = g, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            col.Children.Add(Panel(box));
        }
        return col;
    }

    public static string InboxFolder => Path.Combine(PlansFolder, "Reports");

    /// <summary>Takes in a file: a plan opens, a CSV is read as councils' reports and recorded.</summary>
    public void ImportFile(string file)
    {
        try
        {
            if (!File.Exists(file)) { Say($"There's no file {file}.", true); return; }
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { Open(file); _period = _plan.LatestPeriod; return; }
            var result = PlanCsv.ReadReports(_plan, File.ReadAllText(file), Path.GetFileName(file));
            foreach (var r in result.Reports) Ledger.Record(_plan, r);
            if (result.Reports.Count > 0) { _period = Math.Max(_period, result.Reports.Max(r => r.Period)); Changed(); }
            var took = $"Took in {result.Reports.Count} report{(result.Reports.Count == 1 ? "" : "s")} from {Path.GetFileName(file)}";
            Say(result.Problems.Count == 0 ? took + "."
                : took + $", and left out {result.Problems.Count} line{(result.Problems.Count == 1 ? "" : "s")}: " + string.Join(" ", result.Problems.Take(5)) + (result.Problems.Count > 5 ? " …" : ""),
                bad: result.Problems.Count > 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Say($"{Path.GetFileName(file)} couldn't be read: {e.Message}", true);
        }
    }

    private void WriteForm()
    {
        Directory.CreateDirectory(InboxFolder);
        int next = _plan.LatestPeriod + 1;
        var file = Path.Combine(InboxFolder, $"report-form-period-{next}.csv");
        var lines = new List<string> { PlanCsv.ReportHeader };
        foreach (var c in _plan.Councils)
            foreach (var p in _plan.Products) lines.Add($"{c.Name},{next},{p.Id},,,");
        File.WriteAllText(file, string.Join("\n", lines) + "\n");
        Say($"Wrote {file}: a line for every council and product, to be filled in and taken in.");
    }

    // ── Plan ──
    private Control PlanPage()
    {
        var col = Page("2 · SORTING  ·  3 · EXECUTION", $"Period {_period}: the proposals",
            "Demand comes from what people took out of stock, smoothed with the periods before and corrected toward the stock each product should hold. Each sector's output then has to cover that demand and every other sector's inputs. These are proposals: a council ratifies, amends or rejects each one.");
        col.Children.Add(PeriodPicker());
        var r = Result();
        if (!r.Productive || r.Products.Count == 0) { col.Children.Add(Panel(Muted(r.Problem ?? "Nothing to work out yet."))); return col; }

        string K(double h) => (h / 1000).ToString(h >= 100000 ? "0" : "0.0", CultureInfo.InvariantCulture);
        col.Children.Add(Lcd(("LABOUR NEEDED · K H", K(r.LabourRequired)), ("AVAILABLE", r.LabourAvailable is double a ? K(a) : "--"),
                             ("PROPOSED", K(r.LabourProposed)), ("RECOUNTS", r.Flags.Count.ToString("00"))));
        if (!r.Fits)
            col.Children.Add(Panel(Muted("The whole need can't be met: " + string.Join(", ", r.Limits) + " holds it back. The proposals are the most of it that fits; the difference shows as a shortfall below.")));
        if (!r.LedgerIntact)
            col.Children.Add(Panel(Muted("The ledger doesn't check out (Ledger): some reports may have been changed. Recount before deciding.")));

        // who decides
        var dec = new StackPanel { Spacing = 8 };
        dec.Children.Add(Kicker("THE COUNCIL DECIDING", accent: true));
        var by = new TextBox { Text = _decidedBy, PlaceholderText = "e.g. Northern Regional Council", Width = 300 };
        by.LostFocus += (_, _) => _decidedBy = by.Text?.Trim() ?? "";
        var vf = Num(_votesFor, v => _votesFor = (int)Math.Round(v ?? 0), 70, save: false);
        var va = Num(_votesAgainst, v => _votesAgainst = (int)Math.Round(v ?? 0), 70, save: false);
        var line = new WrapPanel();
        foreach (var c in new Control[] { Labelled("Council", by), Labelled("Votes for", vf), Labelled("Against", va) }) { c.Margin = new Thickness(0, 0, 16, 8); line.Children.Add(c); }
        dec.Children.Add(line);
        dec.Children.Add(Muted($"Ratify, amend or reject each proposal above. Major shifts need {_plan.Settings.Quorum} votes in favour, and more for than against."));
        var all = Pill("Ratify every proposal that isn't a major shift");
        all.Click += (_, _) =>
        {
            int n = 0;
            foreach (var p in r.Products.Where(q => !q.Major && Decisions.For(_plan, _period, q.Product.Id) == null))
                if (Decisions.Decide(_plan, _period, p, DecisionStatus.Ratified, null, _decidedBy, _votesFor, _votesAgainst) == null) n++;
            if (n > 0) Changed();
            Say(string.IsNullOrWhiteSpace(_decidedBy) ? "Say which council decides first." : $"Ratified {n} proposal{(n == 1 ? "" : "s")}.", string.IsNullOrWhiteSpace(_decidedBy));
            Refresh();
        };
        dec.Children.Add(all);
        col.Children.Add(Panel(dec));

        // what each product needs, and the proposal
        var box = new StackPanel { Spacing = 8 };
        box.Children.Add(Kicker("PER PRODUCT"));
        // what to decide on first, then the working that led to it
        var g = Table("Product,Signal,Need,Proposed,Change,Decision,Turnover (target),Expected demand,Final demand,Capacity used,Labour per unit (h)");
        int row = 1;
        foreach (var p in r.Products)
        {
            var u = p.Product.Unit;
            var use = p.Capacity is double cap && cap > 0 ? p.Proposed / cap : (double?)null;
            var bar = new StackPanel { Spacing = 2, Width = 110 };
            bar.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = Math.Min(use ?? 0, 1), Height = 6, MinHeight = 6, MinWidth = 0, Width = 110 });
            bar.Children.Add(new TextBlock { Text = use is double x ? x.ToString("0%", CultureInfo.InvariantCulture) : "not known", FontSize = 11, Classes = { "muted" } });
            var d = Decisions.For(_plan, _period, p.Product.Id);
            Cells(g, row++,
                Cell($"{p.Product.Name}\n{u}", bold: true),
                Badge(p.Signal switch { Signal.Rapid => "RUNNING SHORT", Signal.NoStock => "NO STOCK", Signal.Low => "OVERSTOCKED", _ => "NORMAL" }, accent: p.Signal is Signal.Rapid or Signal.NoStock),
                Cell(Engine.Fmt(p.GrossOutput), right: true),
                Cell(Engine.Fmt(p.Proposed) + (Limited(r, p) && p.GrossOutput - p.Proposed > p.GrossOutput * 1e-6 ? $"\nshort {Engine.Fmt(p.GrossOutput - p.Proposed)}" : ""), right: true, bold: true),
                Cell((p.Change is double c ? c.ToString("+0.#%;-0.#%;0%", CultureInfo.InvariantCulture) : "first") + (p.Major ? "\nMAJOR" : ""), right: true),
                DecisionCell(p, d),
                Cell($"{Engine.Fmt(p.Turnover)}  ({Engine.Fmt(p.TargetTurnover)})", right: true),
                Cell(Engine.Fmt(p.ExpectedDemand), right: true),
                Cell(Engine.Fmt(p.FinalDemand), right: true),
                bar,
                Cell(Engine.Fmt(p.LabourValue), right: true));
        }
        box.Children.Add(new ScrollViewer { Content = g, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        box.Children.Add(Muted("Need: what has to be made to meet final demand and every other sector's inputs. Labour per unit counts the labour in its inputs too. A major shift, or a first quota, needs a quorum of votes."));
        col.Children.Add(Panel(box));


        // recounts
        if (r.Flags.Count > 0 || r.CouncilsMissing.Count > 0)
        {
            var flags = new StackPanel { Spacing = 6 };
            flags.Children.Add(Kicker("RECOUNTS · FIGURES THAT STAND OUT", accent: true));
            foreach (var f in r.Flags)
                flags.Children.Add(new TextBlock { Text = $"{f.Council} — {f.Why}.", TextWrapping = TextWrapping.Wrap });
            if (r.CouncilsMissing.Count > 0)
                flags.Children.Add(Muted("Not reported this period: " + string.Join(", ", r.CouncilsMissing) + ". The plan is worked out without them."));
            flags.Children.Add(Muted("A flagged council's report should be counted again, region by region and council by council, before quotas that depend on it are decided."));
            col.Children.Add(Panel(flags));
        }
        return col;
    }

    /// <summary>Held back itself (at its capacity, or by labour), rather than moved a little by another product's trim.</summary>
    private static bool Limited(PlanResult r, ProductResult p) => r.Limits.Contains(p.Product.Name + " capacity") || r.Limits.Contains("labour");

    private Control DecisionCell(ProductResult p, QuotaDecision? d)
    {
        var s = new StackPanel { Spacing = 4, MinWidth = 210 };
        if (d != null)
        {
            s.Children.Add(Badge(d.Status.ToString().ToUpperInvariant(), accent: d.Status != DecisionStatus.Rejected));
            s.Children.Add(new TextBlock { Text = (d.Value is double v ? Engine.Fmt(v) + " " + p.Product.Unit : "no quota") + " · " + d.DecidedBy, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            var undo = new Button { Content = "Decide again", Classes = { "flat" }, FontSize = 11 };
            undo.Click += (_, _) => { _plan.Decisions.Remove(d); Changed(rebuild: true); };
            s.Children.Add(undo);
            return s;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var ok = Pill("Ratify"); var no = Pill("Reject");
        var amount = new TextBox { PlaceholderText = "amend to", Width = 90 };
        var amend = Pill("Amend");
        void Decide(DecisionStatus st, double? value)
        {
            var err = Decisions.Decide(_plan, _period, p, st, value, _decidedBy, _votesFor, _votesAgainst);
            if (err != null) Say(p.Product.Name + ": " + err, true); else Changed();
            Refresh();
        }
        ok.Click += (_, _) => Decide(DecisionStatus.Ratified, null);
        no.Click += (_, _) => Decide(DecisionStatus.Rejected, null);
        amend.Click += (_, _) =>
        {
            if (double.TryParse(amount.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Decide(DecisionStatus.Amended, v);
            else { Say(p.Product.Name + ": give the amended quota as a number.", true); Refresh(); }
        };
        buttons.Children.Add(ok); buttons.Children.Add(no);
        s.Children.Add(buttons);
        var am = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        am.Children.Add(amount); am.Children.Add(amend);
        s.Children.Add(am);
        return s;
    }

    private Control PeriodPicker()
    {
        var periods = _plan.Reports.Select(r => r.Period).Distinct().OrderByDescending(p => p).ToList();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = "Period", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
        var box = new ComboBox { ItemsSource = periods.Select(p => p == _plan.LatestPeriod ? $"{p} (latest)" : p.ToString()).ToList(), MinWidth = 140 };
        box.SelectedIndex = Math.Max(0, periods.IndexOf(_period));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0 && box.SelectedIndex < periods.Count && periods[box.SelectedIndex] != _period)
            {
                _period = periods[box.SelectedIndex];
                Dispatcher.UIThread.Post(Refresh);
            }
        };
        row.Children.Add(box);
        return row;
    }

    // ── Quotas ──
    private Control QuotasPage()
    {
        var col = Page("3 · EXECUTION · QUOTAS ENACTED", $"Period {_period}: quotas by council",
            "What the councils enacted, shared among the local councils in proportion to what each can make, region by region. Export writes one file per council, to send to its workplaces.");
        col.Children.Add(PeriodPicker());
        var decided = _plan.Products.Select(p => (p, d: Decisions.For(_plan, _period, p.Id))).ToList();
        var sum = new StackPanel { Spacing = 8 };
        sum.Children.Add(Kicker("THE PERIOD'S QUOTAS"));
        var g = Table("Product,Proposed,Decision,Quota,Decided by,Votes");
        int row = 1;
        foreach (var (p, d) in decided)
            Cells(g, row++, Cell(p.Name, bold: true),
                Cell(d == null ? "–" : Engine.Fmt(d.Proposed) + " " + p.Unit, right: true),
                d == null ? Badge("UNDECIDED") : Badge(d.Status.ToString().ToUpperInvariant(), accent: d.Status != DecisionStatus.Rejected),
                Cell(d?.Value is double v ? Engine.Fmt(v) + " " + p.Unit : "–", right: true, bold: true),
                Cell(d?.DecidedBy ?? ""),
                Cell(d == null ? "" : d.Major ? $"{d.VotesFor} for, {d.VotesAgainst} against (quorum {d.Quorum})" : "not needed"));
        sum.Children.Add(g);
        col.Children.Add(Panel(sum));

        var quotas = Engine.Disaggregate(_plan, _period);
        if (quotas.Count == 0)
        {
            col.Children.Add(Panel(Muted("No quota has been enacted for this period yet: ratify or amend the proposals in Plan.")));
            return col;
        }
        foreach (var region in quotas.GroupBy(q => q.Region))
        {
            var box = new StackPanel { Spacing = 8 };
            box.Children.Add(Kicker(region.Key.ToUpperInvariant(), accent: true));
            var t = Table("Council," + string.Join(",", _plan.Products.Select(p => $"{p.Name} ({p.Unit})")));
            int r = 1;
            foreach (var council in region.GroupBy(q => q.Council))
            {
                var cells = new List<Control> { Cell(council.Key, bold: true) };
                foreach (var p in _plan.Products)
                    cells.Add(Cell(council.FirstOrDefault(x => x.Product == p.Id) is { } cq ? Engine.Fmt(cq.Quota) : "–", right: true));
                Cells(t, r++, cells.ToArray());
            }
            box.Children.Add(new ScrollViewer { Content = t, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            col.Children.Add(Panel(box));
        }
        var export = Pill("Export the quotas", accent: true);
        export.Click += (_, _) => { ExportQuotas(); Refresh(); };
        col.Children.Add(export);
        return col;
    }

    // ── Ledger ──
    private Control LedgerPage()
    {
        var col = Page("VERIFICATION LEDGER", "Every report, sealed as it came in",
            "Each report is recorded with a checksum of its figures, and each entry is sealed with the one before it. A report changed afterwards no longer matches, and an entry changed or removed breaks the chain from there on.");
        var check = Ledger.Verify(_plan);
        var head = new StackPanel { Spacing = 8 };
        head.Children.Add(Kicker(check.Ok ? "CHECKED · INTACT" : "CHECKED · PROBLEM", accent: true));
        head.Children.Add(new TextBlock { Text = check.Summary, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        col.Children.Add(Panel(head));
        var box = new StackPanel { Spacing = 8 };
        box.Children.Add(Kicker($"{_plan.Ledger.Count} ENTRIES"));
        var g = Table("#,Council,Period,Report's checksum,Sealed with,Entry,Recorded");
        int row = 1;
        foreach (var e in Enumerable.Reverse(_plan.Ledger).Take(200))
            Cells(g, row++, Cell((e.Index + 1).ToString(CultureInfo.InvariantCulture), right: true), Cell(e.Council, bold: true),
                Cell(e.Period.ToString(CultureInfo.InvariantCulture), right: true), Mono(e.Report[..16]), Mono(e.Previous[..16]), Mono(e.Hash[..16]),
                Cell(e.When.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)));
        box.Children.Add(new ScrollViewer { Content = g, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        if (_plan.Ledger.Count > 200) box.Children.Add(Muted("The newest 200 are shown; every entry is checked."));
        col.Children.Add(Panel(box));
        return col;
    }

    // ───────── files ─────────

    private static string PathFor(Plan plan)
    {
        Directory.CreateDirectory(PlansFolder);
        var b = PlanCsv.Safe(plan.Name);
        var p = Path.Combine(PlansFolder, b + ".plan.json");
        for (int k = 2; File.Exists(p); k++) p = Path.Combine(PlansFolder, $"{b} {k}.plan.json");
        return p;
    }

    private static string? LatestPlan() =>
        Directory.Exists(PlansFolder) ? Directory.GetFiles(PlansFolder, "*.plan.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;

    private void Open(string file, bool quiet = false)
    {
        try
        {
            _plan = Plan.Load(file);
            _path = file;
            _result = null;
            if (!quiet) Say($"Opened {Path.GetFileName(file)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
        {
            if (quiet) { _plan = Example.SmallEconomy(); _path = PathFor(_plan); Save(); }
            Say($"{Path.GetFileName(file)} couldn't be opened: {e.Message}", true);
        }
    }

    private void NewPlan(bool empty)
    {
        _plan = empty ? new Plan { Name = "New plan" } : Example.SmallEconomy();
        _path = PathFor(_plan);
        _period = _plan.LatestPeriod;
        _result = null;
        Save();
        Say(empty ? "A new, empty plan: add products in Economy and councils in Councils, then take in their reports." : "Opened a copy of the example economy.");
        if (empty && _simple) { SetView(false); Say("A new, empty plan: add products in Economy and councils in Councils, then take in their reports."); }
        ShowPage(empty ? "economy" : _simple ? "simple" : "overview");
    }

    private void Save()
    {
        if (!_autosave || _path == null) return;
        try { _plan.Save(_path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say("The plan couldn't be saved: " + e.Message, true); }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
        if (files == null || files.Count == 0) return;
        foreach (var f in files) ImportFile(f);
        ShowPage(_simple ? "simple" : files.Any(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) ? "reports" : "overview");
    }

    /// <summary>For the tests: a page of the example (at a period, or the latest), without saving anything.</summary>
    public static PlannerWindow Sample(string page, int? period = null)
    {
        var w = new PlannerWindow(null, save: false);
        w._plan = Example.SmallEconomy();
        w._path = null;
        w._period = period ?? w._plan.LatestPeriod;
        w._decidedBy = "Northern Regional Council";
        w._result = null;
        w._simple = page is "simple" or "howto";
        w.BuildNav();
        w.ShowPage(page);
        return w;
    }
}
