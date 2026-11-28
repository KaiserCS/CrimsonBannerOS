// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CrimsonPlanner.Model;

namespace CrimsonPlanner.Views;

/// <summary>
/// Planner's simple view: the same plan, in plain words, for anyone. One page walks through a period in the order
/// it happens (hand in the reports, check what stands out, decide each product, send out the shares), and a second
/// says how it works without the arithmetic. Every decision here is the same decision the advanced pages record.
///   simple   This period
///   howto    How it works
/// </summary>
public sealed partial class PlannerWindow
{
    private bool _simple = true;

    private static string ViewFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "crimson-planner", "view");

    /// <summary>The view last used on this computer (simple, unless the advanced one was chosen).</summary>
    private static bool LoadSimple()
    {
        try { return !File.Exists(ViewFile) || File.ReadAllText(ViewFile).Trim() != "advanced"; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private void SetView(bool simple)
    {
        _simple = simple;
        if (_autosave)
            try { Directory.CreateDirectory(Path.GetDirectoryName(ViewFile)!); File.WriteAllText(ViewFile, simple ? "simple\n" : "advanced\n"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        BuildNav();
        ShowPage(simple ? "simple" : "overview");
    }

    private static readonly (string Id, string Label)[] SimplePages = { ("simple", "This period"), ("howto", "How it works") };
    private static readonly (string Id, string Label)[] AdvancedPages =
    {
        ("overview", "Overview"), ("economy", "Economy"), ("councils", "Councils"), ("reports", "Reports"),
        ("plan", "Plan"), ("quotas", "Quotas"), ("ledger", "Ledger"),
    };

    private void BuildNav()
    {
        _nav.Children.Clear();
        foreach (var (id, label) in _simple ? SimplePages : AdvancedPages)
        {
            var b = new Button { Classes = { "nav" }, Content = new TextBlock { Text = label, FontSize = 14 }, Tag = id };
            b.Click += (_, _) => ShowPage(id);
            _nav.Children.Add(b);
        }
        _viewSwitch.Children.Clear();
        var simple = Pill("Simple", accent: _simple);
        var advanced = Pill("Advanced", accent: !_simple);
        simple.Click += (_, _) => { if (!_simple) SetView(true); };
        advanced.Click += (_, _) => { if (_simple) SetView(false); };
        _viewSwitch.Children.Add(simple);
        _viewSwitch.Children.Add(advanced);
    }

    private readonly StackPanel _viewSwitch = new() { Orientation = Orientation.Horizontal, Spacing = 6 };

    // ── This period ──
    private Control SimplePage()
    {
        var col = Page($"PERIOD {_period} · IN PLAIN WORDS", _plan.Name,
            "Planner suggests how much of each product to make next, from what the councils report. It only suggests: the councils decide. Go through the four steps in order.");
        col.Children.Add(PeriodPicker());
        var r = Result();

        // 1 · the reports
        int reporting = r.CouncilsReporting.Count, all = _plan.Councils.Count();
        var s1 = Step("1", "Hand in the councils' reports",
            all == 0 ? "There are no councils in this plan yet. Add them in the advanced view, under Councils."
            : reporting == all ? $"All {all} councils have reported for period {_period}."
            : $"{reporting} of {all} councils have reported for period {_period}. Still to come: {string.Join(", ", r.CouncilsMissing)}.");
        s1.Children.Add(Muted("A report is a spreadsheet file (CSV) saying, for each product, how much was made, how much people took, and how much is left. You can also drop report files on this window."));
        var actions = new WrapPanel();
        var choose = Pill("Choose report files", accent: true);
        choose.Click += async (_, _) => await ChooseReports();
        var form = Pill("Write a blank form");
        form.Click += (_, _) => { WriteForm(); Refresh(); };
        foreach (var b in new[] { choose, form }) { b.Margin = new Thickness(0, 4, 8, 0); actions.Children.Add(b); }
        s1.Children.Add(actions);
        col.Children.Add(Panel(s1));

        // 2 · what stands out
        var s2 = Step("2", "Check anything that looks wrong",
            r.Flags.Count == 0 && r.LedgerIntact
                ? "Nothing stands out. Every report is in line with that council's earlier ones and its neighbours', and none has been changed since it was handed in."
                : "Some figures should be counted again before the products they affect are decided.");
        if (!r.LedgerIntact)
            s2.Children.Add(Plain("A report has been changed since it was handed in, so it no longer matches its record. Ask the council to send it again.", strong: true));
        foreach (var f in r.Flags)
            s2.Children.Add(Plain($"{f.Council}. {f.Why}. Ask {f.Council} to count it again.", strong: true));
        col.Children.Add(Panel(s2));

        // 3 · decide
        if (!r.Productive || r.Products.Count == 0)
        {
            col.Children.Add(Panel(Step("3", "Decide each product", r.Problem ?? "There's nothing to decide until the councils' reports are in.")));
            return col;
        }
        int undecided = r.Products.Count(p => Decisions.For(_plan, _period, p.Product.Id) == null);
        var s3 = Step("3", "Decide each product",
            undecided == 0 ? "Every product is decided for this period." : $"{undecided} of {r.Products.Count} products still to decide. Agree to Planner's figure, change it, or reject it.");
        var who = new WrapPanel();
        var by = new TextBox { Text = _decidedBy, PlaceholderText = "e.g. Northern Regional Council", Width = 300 };
        by.LostFocus += (_, _) => _decidedBy = by.Text?.Trim() ?? "";
        var whoBox = Labelled("Which council is deciding", by);
        whoBox.Margin = new Thickness(0, 0, 16, 8);
        who.Children.Add(whoBox);
        if (r.Products.Any(p => p.Major && Decisions.For(_plan, _period, p.Product.Id) == null))
        {
            var vf = Num(_votesFor, v => _votesFor = (int)Math.Round(v ?? 0), 70, save: false);
            var va = Num(_votesAgainst, v => _votesAgainst = (int)Math.Round(v ?? 0), 70, save: false);
            var f = Labelled("Votes for", vf); f.Margin = new Thickness(0, 0, 16, 8);
            var a = Labelled("Against", va); a.Margin = new Thickness(0, 0, 16, 8);
            who.Children.Add(f); who.Children.Add(a);
        }
        s3.Children.Add(who);
        if (r.Products.Any(p => !p.Major && Decisions.For(_plan, _period, p.Product.Id) == null))
        {
            var easy = Pill("Agree to every small change");
            easy.Click += (_, _) =>
            {
                int n = 0;
                foreach (var p in r.Products.Where(q => !q.Major && Decisions.For(_plan, _period, q.Product.Id) == null))
                    if (Decisions.Decide(_plan, _period, p, DecisionStatus.Ratified, null, _decidedBy, _votesFor, _votesAgainst) == null) n++;
                if (n > 0) Changed();
                Say(string.IsNullOrWhiteSpace(_decidedBy) ? "Say which council is deciding first." : $"Agreed to {n} product{(n == 1 ? "" : "s")}.", string.IsNullOrWhiteSpace(_decidedBy));
                Refresh();
            };
            easy.HorizontalAlignment = HorizontalAlignment.Left;
            s3.Children.Add(easy);
        }
        foreach (var p in r.Products) s3.Children.Add(ProductCard(r, p));
        col.Children.Add(Panel(s3));

        // 4 · send out
        var shares = Engine.Disaggregate(_plan, _period);
        var s4 = Step("4", "Send each council its share",
            shares.Count == 0
                ? "Once a product is decided, its quota is shared among the councils by how much each can make, and each council gets a file of its own."
                : $"{_plan.Products.Count - undecided} of {_plan.Products.Count} products are decided. Each council's share is in proportion to how much it can make.");
        if (shares.Count > 0)
        {
            var t = Table("Council," + string.Join(",", _plan.Products.Select(p => $"{p.Name} ({p.Unit})")));
            int row = 1;
            foreach (var council in shares.GroupBy(q => q.Council))
            {
                var cells = new List<Control> { Cell(council.Key, bold: true) };
                foreach (var p in _plan.Products)
                    cells.Add(Cell(council.FirstOrDefault(x => x.Product == p.Id) is { } q ? Engine.Fmt(q.Quota) : "not decided", right: true));
                Cells(t, row++, cells.ToArray());
            }
            s4.Children.Add(new ScrollViewer { Content = t, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            var export = Pill("Write each council's file", accent: true);
            export.HorizontalAlignment = HorizontalAlignment.Left;
            export.Click += (_, _) => { ExportQuotas(); Refresh(); };
            s4.Children.Add(export);
        }
        col.Children.Add(Panel(s4));
        return col;
    }

    /// <summary>One product, said plainly: how its stock stands, what Planner suggests, and the decision.</summary>
    private Control ProductCard(PlanResult r, ProductResult p)
    {
        var u = p.Product.Unit;
        var box = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 6) };
        rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable("ShellRuleBrush"));
        box.Children.Add(rule);
        var head = new WrapPanel();
        head.Children.Add(new TextBlock { Text = p.Product.Name, FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(Badge(SimpleSignal(p.Signal), accent: p.Signal is Signal.Rapid or Signal.NoStock));
        box.Children.Add(head);
        box.Children.Add(Plain(p.Signal switch
        {
            Signal.Rapid => "People are taking it faster than the stock can keep up with.",
            Signal.NoStock => "There's none left in stock.",
            Signal.Low => "More is in stock than people are taking, so less needs making.",
            _ => "What's in stock and what people take are in balance.",
        }));
        var change = p.Change is double c
            ? (Math.Abs(c) < 0.0005 ? "the same as last time" : c.ToString("+0.#%;-0.#%", CultureInfo.InvariantCulture) + " on last time")
            : "the first quota for it";
        box.Children.Add(Plain($"Planner suggests making {Engine.Fmt(p.Proposed)} {u} ({change}).", strong: true));
        if (Limited(r, p) && p.GrossOutput - p.Proposed > p.GrossOutput * 1e-6)
            box.Children.Add(Plain($"That's {Engine.Fmt(p.GrossOutput - p.Proposed)} {u} less than is needed: {string.Join(" and ", r.Limits)} can't stretch further."));
        if (p.Major)
            box.Children.Add(Plain($"This is a big change, so it needs at least {_plan.Settings.Quorum} councils' votes in favour, and more for than against."));

        var d = Decisions.For(_plan, _period, p.Product.Id);
        if (d != null)
        {
            var done = new WrapPanel();
            var said = d.Status switch
            {
                DecisionStatus.Ratified => "Agreed",
                DecisionStatus.Amended => "Changed",
                _ => "Rejected",
            };
            done.Children.Add(new TextBlock
            {
                Text = $"{said}: " + (d.Value is double v ? $"{Engine.Fmt(v)} {u}" : "no quota") + $", by {d.DecidedBy}.",
                FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), TextWrapping = TextWrapping.Wrap,
            });
            var again = new Button { Content = "Decide again", Classes = { "flat" } };
            again.Click += (_, _) => { _plan.Decisions.Remove(d); Changed(rebuild: true); };
            done.Children.Add(again);
            box.Children.Add(done);
            return box;
        }

        void Decide(DecisionStatus st, double? value)
        {
            var err = Decisions.Decide(_plan, _period, p, st, value, _decidedBy, _votesFor, _votesAgainst);
            if (err != null) Say(p.Product.Name + ": " + err, true); else Changed();
            Refresh();
        }
        var buttons = new WrapPanel();
        var agree = Pill("Agree", accent: true);
        agree.Click += (_, _) => Decide(DecisionStatus.Ratified, null);
        var amount = new TextBox { PlaceholderText = "another figure", Width = 130 };
        var amend = Pill("Change");
        amend.Click += (_, _) =>
        {
            if (double.TryParse(amount.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Decide(DecisionStatus.Amended, v);
            else { Say(p.Product.Name + ": write the figure you want as a number, then press Change.", true); Refresh(); }
        };
        var reject = Pill("Reject");
        reject.Click += (_, _) => Decide(DecisionStatus.Rejected, null);
        foreach (var c2 in new Control[] { agree, amount, amend, reject }) { c2.Margin = new Thickness(0, 2, 8, 0); buttons.Children.Add(c2); }
        box.Children.Add(buttons);
        box.Children.Add(Muted(p.LastEnacted is double last
            ? $"Rejecting keeps last period's quota, {Engine.Fmt(last)} {u}."
            : "Rejecting leaves it without a quota this period."));
        return box;
    }

    private static string SimpleSignal(Signal s) => s switch
    {
        Signal.Rapid => "RUNNING SHORT",
        Signal.NoStock => "NONE LEFT",
        Signal.Low => "TOO MUCH IN STOCK",
        _ => "IN BALANCE",
    };

    // ── How it works ──
    private Control HowToPage()
    {
        var col = Page("HOW PLANNING WORKS", "From reports to quotas",
            "The whole process, without the arithmetic. Everything Planner works out can be traced back to what the councils reported, and nothing leaves this computer.");
        void Part(string n, string title, params string[] lines)
        {
            var s = Step(n, title, lines[0]);
            foreach (var l in lines.Skip(1)) s.Children.Add(Plain(l));
            col.Children.Add(Panel(s));
        }
        Part("1", "Councils count",
            "At the end of each period, every local council counts three things for each product: how much its workplaces made, how much people took from the shops and stores, and how much is left.",
            "It hands these figures in as a report. Each report is sealed as it comes in, so a figure changed afterwards shows up.");
        Part("2", "Planner works out what's needed",
            "If people took a lot and little is left, more is needed next time; if stock is piling up, less is. Planner smooths this over the last few periods, so one odd period doesn't swing the plan.",
            "Making one thing uses up others: a tractor needs steel, bread needs flour. So Planner adds what every workplace needs from the others, not only what people take home.",
            "Then it checks what fits: how much each council can make, and how many hours of work there are. If something can't be met in full, it says by how much.");
        Part("3", "Councils decide",
            "Planner's figures are suggestions. A council agrees to each one, changes it, or rejects it, in which case last period's quota stands.",
            $"A big change (more than {_plan.Settings.MajorShift:0%} on last time, or a product's first quota) needs at least {_plan.Settings.Quorum} councils' votes in favour.");
        Part("4", "Shares go out",
            "Each decided quota is shared among the local councils by how much each can make. Every council gets a file with its share, to pass on to its workplaces.",
            "Then the next period begins, and the councils count again.");

        var more = new StackPanel { Spacing = 8 };
        more.Children.Add(Plain("Every figure, the formulas behind them and the sealed record of reports are in the advanced view.", strong: true));
        var go = Pill("Open the advanced view");
        go.HorizontalAlignment = HorizontalAlignment.Left;
        go.Click += (_, _) => SetView(false);
        more.Children.Add(go);
        col.Children.Add(Panel(more));
        return col;
    }

    // ── parts ──

    /// <summary>A step: its number and title, and what stands now.</summary>
    private static StackPanel Step(string n, string title, string now)
    {
        var s = new StackPanel { Spacing = 8 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        // the step's number on the accent, as the accent pills are
        var dot = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = n, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        dot.Bind(Border.BackgroundProperty, dot.GetResourceObservable("ShellAccentBrush"));
        head.Children.Add(dot);
        head.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        s.Children.Add(head);
        s.Children.Add(Plain(now));
        return s;
    }

    private static TextBlock Plain(string text, bool strong = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left,
        FontSize = 14, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal,
    };

    private async Task ChooseReports()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the councils' reports",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Council reports (CSV)") { Patterns = new[] { "*.csv" } } },
        });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path) ImportFile(path);
        if (files.Count > 0) Refresh();
    }

    private void ExportQuotas()
    {
        var folder = Path.Combine(PlansFolder, "Quotas", PlanCsv.Safe(_plan.Name), $"period-{_period}");
        try
        {
            var files = PlanCsv.ExportQuotas(_plan, _period, folder);
            Say($"Wrote {files.Count} files to {folder}: one for everything, and one for each council in its region's folder.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say("The quotas couldn't be written: " + e.Message, true); }
    }
}
