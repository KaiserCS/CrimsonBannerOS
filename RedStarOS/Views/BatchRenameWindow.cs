using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Rename several items at once, with a live preview (Files → select several → F2).</summary>
public sealed class BatchRenameWindow : Window
{
    private readonly IReadOnlyList<string> _paths;
    private readonly BatchRename.Options _o = new();
    private readonly StackPanel _preview = new() { Spacing = 2 };
    private readonly TextBlock _summary = new() { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new() { Content = "Rename", Classes = { "primary" }, MinWidth = 100, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
    private List<BatchRename.Item> _items = new();

    /// <summary>What was renamed (from, to), once the window closes; empty when cancelled.</summary>
    public List<(string From, string To)> Done { get; private set; } = new();
    public List<string> Errors { get; } = new();

    public BatchRenameWindow(IReadOnlyList<string> paths)
    {
        _paths = paths;
        Title = $"Rename {paths.Count} items";
        Width = 640; Height = 560; MinWidth = 520; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var mode = new ComboBox { ItemsSource = new[] { "Find and replace", "Add text", "Number them", "Change case" }, SelectedIndex = 0, MinWidth = 200 };
        var panels = new Panel();

        // find & replace
        var find = new TextBox { PlaceholderText = "Find" };
        var replaceBox = new TextBox { PlaceholderText = "Replace with" };
        var regex = new CheckBox { Content = "Regular expression" };
        var matchCase = new CheckBox { Content = "Match case" };
        var replacePanel = new StackPanel { Spacing = 8, Children = { Two(find, replaceBox), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { regex, matchCase } } } };

        // add text
        var text = new TextBox { PlaceholderText = "Text to add" };
        var where = new ComboBox { ItemsSource = new[] { "at the end of the name", "at the start of the name" }, SelectedIndex = 0 };
        var addPanel = new StackPanel { Spacing = 8, IsVisible = false, Children = { Two(text, where) } };

        // numbering
        var pattern = new TextBox { Text = _o.Pattern, PlaceholderText = "Name with ### for the number" };
        var start = new NumericUpDown { Value = 1, Minimum = 0, Maximum = 1_000_000, Increment = 1, FormatString = "0", MinWidth = 120 };
        var step = new NumericUpDown { Value = 1, Minimum = 1, Maximum = 1000, Increment = 1, FormatString = "0", MinWidth = 120 };
        var numberPanel = new StackPanel
        {
            Spacing = 8, IsVisible = false,
            Children =
            {
                pattern,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Label("Start at"), start, Label("Step"), step } },
                new TextBlock { Text = "### becomes 001, 002, … (as many digits as #). * stands for the old name, e.g. “* ##”.", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap }
            }
        };

        // case
        var caseBox = new ComboBox { ItemsSource = new[] { "lowercase", "UPPERCASE", "Title Case", "Sentence case" }, SelectedIndex = 0, MinWidth = 200 };
        var casePanel = new StackPanel { IsVisible = false, Children = { caseBox } };

        panels.Children.AddRange(new Control[] { replacePanel, addPanel, numberPanel, casePanel });
        var ext = new CheckBox { Content = "Change the extensions too" };

        void Read()
        {
            _o.Mode = (BatchRename.Mode)Math.Max(0, mode.SelectedIndex);
            _o.Find = find.Text ?? ""; _o.ReplaceWith = replaceBox.Text ?? "";
            _o.Regex = regex.IsChecked == true; _o.MatchCase = matchCase.IsChecked == true;
            _o.Text = text.Text ?? ""; _o.AtStart = where.SelectedIndex == 1;
            _o.Pattern = pattern.Text ?? ""; _o.Start = (int)(start.Value ?? 1); _o.Step = (int)(step.Value ?? 1);
            _o.Case = (BatchRename.CaseKind)Math.Max(0, caseBox.SelectedIndex);
            _o.IncludeExtension = ext.IsChecked == true;
            replacePanel.IsVisible = _o.Mode == BatchRename.Mode.Replace;
            addPanel.IsVisible = _o.Mode == BatchRename.Mode.Add;
            numberPanel.IsVisible = _o.Mode == BatchRename.Mode.Number;
            casePanel.IsVisible = _o.Mode == BatchRename.Mode.Case;
            Refresh();
        }
        foreach (var tb in new[] { find, replaceBox, text, pattern }) tb.TextChanged += (_, _) => Read();
        foreach (var cb in new[] { regex, matchCase, ext }) cb.IsCheckedChanged += (_, _) => Read();
        foreach (var co in new[] { mode, where, caseBox }) co.SelectionChanged += (_, _) => Read();
        start.ValueChanged += (_, _) => Read();
        step.ValueChanged += (_, _) => Read();

        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close();
        _apply.Click += (_, _) =>
        {
            Done = BatchRename.Apply(_items, Errors);
            Close();
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"), Margin = new Thickness(18) };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 12), Children = { Label("Rename by"), mode } };
        Grid.SetRow(panels, 1);
        ext.Margin = new Thickness(0, 10, 0, 10);
        Grid.SetRow(ext, 2);
        var previewFrame = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8),
            Child = new ScrollViewer { Content = _preview }
        };
        previewFrame.BorderBrush = Application.Current?.FindResource("ShellBorderBrush") as IBrush;
        previewFrame.Background = Application.Current?.FindResource("ShellCardBrush") as IBrush;
        Grid.SetRow(previewFrame, 3);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(_summary);
        Grid.SetColumn(_apply, 1); Grid.SetColumn(cancel, 2);
        _apply.Margin = new Thickness(8, 0); bottom.Children.Add(_apply); bottom.Children.Add(cancel);
        Grid.SetRow(bottom, 4);
        root.Children.AddRange(new Control[] { top, panels, ext, previewFrame, bottom });
        Content = root;
        Opened += (_, _) => find.Focus();
        Read();
    }

    private static TextBlock Label(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center };

    private static Grid Two(Control a, Control b)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        Grid.SetColumn(b, 2);
        g.Children.Add(a); g.Children.Add(b);
        return g;
    }

    private void Refresh()
    {
        _items = BatchRename.Preview(_paths, _o);
        _preview.Children.Clear();
        var warn = new SolidColorBrush(Color.Parse("#E53E3E"));
        foreach (var it in _items.Take(500))
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*"), Margin = new Thickness(0, 2) };
            var oldT = new TextBlock { Text = it.OldName, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = it.Changes ? 0.7 : 0.45 };
            var arrow = new TextBlock { Text = "→", Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center };
            var newT = new TextBlock
            {
                Text = it.Problem != null ? $"{it.NewName}  — {it.Problem}" : it.NewName,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = it.Changes ? FontWeight.SemiBold : FontWeight.Normal,
                Opacity = it.Changes || it.Problem != null ? 1 : 0.45,
            };
            if (it.Problem != null) newT.Foreground = warn;
            ToolTip.SetTip(newT, newT.Text);
            Grid.SetColumn(arrow, 1); Grid.SetColumn(newT, 2);
            g.Children.Add(oldT); g.Children.Add(arrow); g.Children.Add(newT);
            _preview.Children.Add(g);
        }
        int changes = _items.Count(i => i.Changes), problems = _items.Count(i => i.Problem != null);
        _summary.Text = problems > 0 ? $"{changes} will be renamed · {problems} can't be (shown in red) and stay as they are"
                      : changes == 0 ? "Nothing changes yet." : $"{changes} of {_items.Count} will be renamed.";
        _apply.IsEnabled = changes > 0;
        _apply.Content = changes > 0 ? $"Rename {changes}" : "Rename";
    }

    /// <summary>Shows the window for <paramref name="paths"/>; records an undo step for what was renamed.</summary>
    public static async Task<List<(string From, string To)>> Run(Window owner, IReadOnlyList<string> paths)
    {
        var w = new BatchRenameWindow(paths);
        await w.ShowDialog(owner);
        if (w.Done.Count > 0)
        {
            var done = w.Done.ToList();
            foreach (var (f, t) in done) Places.Moved(f, t);
            FileTransfer.Record($"Rename {done.Count} item{(done.Count == 1 ? "" : "s")}", () =>
            {
                BatchRename.Revert(done);
                foreach (var (f, t) in done) Places.Moved(t, f);
            });
        }
        if (w.Errors.Count > 0) await Dialogs.ShowMessage(owner, "Rename", string.Join("\n", w.Errors.Take(6)));
        return w.Done;
    }
}
