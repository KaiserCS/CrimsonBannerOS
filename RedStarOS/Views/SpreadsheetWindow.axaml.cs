using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Red Star Sheets: a 100,000 x 702 virtual grid with formulas, formatting, sorting, filtering, charts, CSV and .xlsx.</summary>
public partial class SpreadsheetWindow : Window
{
    private readonly SheetModel _m;
    private string? _path;
    private bool _modified, _forceClose, _syncing, _editing, _csvWarned;
    private int _er, _ec;

    private static readonly string[] NumNames = { "General", "Number", "Thousands", "Currency", "Percent", "Date", "Text" };
    private static readonly string[] FilterOps = { "contains", "equals", "not equal", ">", "<", "blank", "not blank" };

    public SpreadsheetWindow()
    {
        InitializeComponent();
        _m = Sheet.Model;

        _m.Changed += () => { _modified = true; UpdateTitle(); };

        Sheet.EditRequested += BeginEdit;
        Sheet.SelectionChanged += OnSelectionChanged;
        Sheet.MetricsChanged += OnMetrics;
        Sheet.ShortcutRequested += OnShortcut;

        VScroll.PropertyChanged += OnScrollBar;
        HScroll.PropertyChanged += OnScrollBar;

        Editor.AddHandler(KeyDownEvent, OnEditorKey, RoutingStrategies.Tunnel);
        Editor.LostFocus += (_, _) => { if (_editing) EndEdit(true, 0, 0, false); };
        Editor.TextChanged += (_, _) =>
        {
            if (!_editing) return;
            FormulaBox.Text = Editor.Text;
            var rc = Sheet.CellRect(_er, _ec);
            double want = Math.Max(rc.Width, Math.Min(Host.Bounds.Width - rc.X - 4, (Editor.Text?.Length ?? 0) * 8.5 + 24));
            Editor.Width = Math.Max(70, want);
        };
        FormulaBox.KeyDown += OnFormulaKey;

        NumBox.ItemsSource = NumNames;
        NumBox.SelectedIndex = 0;
        NumBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || NumBox.SelectedItem is not string s) return;
            ApplyNum(s);
            Sheet.Focus();
        };

        FilterOp.ItemsSource = FilterOps;
        FilterOp.SelectedIndex = 0;

        // the suite's colour buttons, tab row and zoom (OfficeChrome)
        static uint Argb(string? hex) => hex == null ? 0u : 0xFF000000u | Convert.ToUInt32(hex.TrimStart('#'), 16);
        OfficeChrome.Fit(TextColorBtn, "text-color", "Text colour", "#C8102E", h => ApplyFormat(f => f.Color = Argb(h)), "Automatic", this);
        OfficeChrome.Fit(FillColorBtn, "fill", "Fill colour", "#FFF200", h => ApplyFormat(f => f.Fill = Argb(h)), "No colour", this);
        OfficeChrome.SingleTab(this, TabsPanel, "sheet", " - Sheets", Close);
        Zoom.Minimum = 50; Zoom.Maximum = 200;
        Zoom.Changed += p => ZoomHost.LayoutTransform = new ScaleTransform(p / 100.0, p / 100.0);

        KeyDown += OnWindowKey;
        Closing += OnClosing;
        Opened += (_, _) => { Sheet.Focus(); OnMetrics(); OnSelectionChanged(); };

        _modified = false;
        UpdateTitle();
        StatusText.Text = "Type to enter data. Formulas start with =, for example =SUM(A1:A10).";
    }

    // ------------------------------------------------------------ title, status, scrolling

    private void UpdateTitle()
    {
        var name = _path == null ? "Untitled" : Path.GetFileName(_path);
        Title = (_modified ? "• " : "") + name + " - Sheets";
    }

    private void OnScrollBar(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncing || e.Property != RangeBase.ValueProperty) return;
        Sheet.SetScroll(HScroll.Value, VScroll.Value);
    }

    private void OnMetrics()
    {
        _syncing = true;
        VScroll.Minimum = 0;
        VScroll.Maximum = Sheet.MaxScrollY;
        VScroll.ViewportSize = Math.Max(1, Sheet.ViewportH);
        VScroll.SmallChange = SheetModel.DefaultRowHeight;
        VScroll.LargeChange = Math.Max(1, Sheet.ViewportH);
        VScroll.Value = Sheet.ScrollY;
        HScroll.Minimum = 0;
        HScroll.Maximum = Sheet.MaxScrollX;
        HScroll.ViewportSize = Math.Max(1, Sheet.ViewportW);
        HScroll.SmallChange = 60;
        HScroll.LargeChange = Math.Max(1, Sheet.ViewportW);
        HScroll.Value = Sheet.ScrollX;
        _syncing = false;
        if (_editing) PositionEditor();
    }

    private static string Address(int r, int c) => FormulaEvaluator.CellName(r, c);

    private void OnSelectionChanged()
    {
        int r1 = Sheet.SelR1, r2 = Sheet.SelR2, c1 = Sheet.SelC1, c2 = Sheet.SelC2;
        if (r1 == r2 && c1 == c2) NameLabel.Text = Address(r1, c1);
        else if (r1 == 0 && r2 >= SheetModel.MaxRows - 1) NameLabel.Text = FormulaEvaluator.ColumnName(c1) + ":" + FormulaEvaluator.ColumnName(c2);
        else if (c1 == 0 && c2 >= SheetModel.MaxCols - 1) NameLabel.Text = (r1 + 1) + ":" + (r2 + 1);
        else NameLabel.Text = Address(r1, c1) + ":" + Address(r2, c2);

        if (!_editing) FormulaBox.Text = _m.GetRaw(Sheet.AncR, Sheet.AncC);

        // sum / average / count of the numbers in the selection
        var s = Sheet.SelectionForEdit();
        double sum = 0; int count = 0;
        int rows = Math.Min(s.r2, Math.Max(0, _m.UsedRows - 1)), cols = Math.Min(s.c2, Math.Max(0, _m.UsedCols - 1));
        if ((s.r1 != s.r2 || s.c1 != s.c2) && (long)(rows - s.r1 + 1) * (cols - s.c1 + 1) <= 200000)
        {
            for (int r = s.r1; r <= rows; r++)
                for (int c = s.c1; c <= cols; c++)
                    if (_m.Eval.Eval(r, c) is double d) { sum += d; count++; }
        }
        SummaryText.Text = count > 0
            ? "Sum " + FormulaEvaluator.Format(Math.Round(sum, 8)) + "   Average " + FormulaEvaluator.Format(Math.Round(sum / count, 8)) + "   Count " + count
            : "";

        _syncing = true;
        var fmt = _m.GetFmt(Sheet.AncR, Sheet.AncC);
        BoldBtn.Classes.Set("on", fmt?.Bold == true); ItalicBtn.Classes.Set("on", fmt?.Italic == true); UnderBtn.Classes.Set("on", fmt?.Underline == true);
        AlLBtn.Classes.Set("on", fmt?.Align == 1); AlCBtn.Classes.Set("on", fmt?.Align == 2); AlRBtn.Classes.Set("on", fmt?.Align == 3);
        var kind = (fmt?.Num ?? NumFmt.General).ToString();
        NumBox.SelectedItem = NumNames.Contains(kind) ? kind : "General";
        _syncing = false;
    }

    private void MarkModified()
    {
        _modified = true;
        UpdateTitle();
    }

    // ------------------------------------------------------------ cell editing

    private void BeginEdit(int r, int c, string? initial)
    {
        if (_editing) EndEdit(true, 0, 0, false);
        _editing = true;
        _er = r; _ec = c;
        Sheet.EnsureVisible(r, c);
        Editor.Text = initial ?? _m.GetRaw(r, c);
        PositionEditor();
        Editor.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            Editor.Focus();
            Editor.CaretIndex = (Editor.Text ?? "").Length;
        }, DispatcherPriority.Input);
    }

    private void PositionEditor()
    {
        var rc = Sheet.CellRect(_er, _ec);
        Editor.Margin = new Thickness(rc.X, rc.Y, 0, 0);
        Editor.Width = Math.Max(70, rc.Width);
        Editor.Height = Math.Max(22, rc.Height);
    }

    private void EndEdit(bool commit, int dr, int dc, bool refocus = true)
    {
        if (!_editing) return;
        _editing = false;
        var text = Editor.Text ?? "";
        Editor.IsVisible = false;
        if (commit && text != _m.GetRaw(_er, _ec)) _m.Enter(_er, _ec, text);
        if (refocus) Sheet.Focus();
        if (dr != 0 || dc != 0) Sheet.Select(_er + dr, _ec + dc, false);
        else OnSelectionChanged();
    }

    private void EndEditIfAny() { if (_editing) EndEdit(true, 0, 0, false); }

    private void OnEditorKey(object? sender, KeyEventArgs e)
    {
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        switch (e.Key)
        {
            case Key.Enter: e.Handled = true; EndEdit(true, shift ? -1 : 1, 0); break;
            case Key.Tab: e.Handled = true; EndEdit(true, 0, shift ? -1 : 1); break;
            case Key.Escape: e.Handled = true; EndEdit(false, 0, 0); break;
            case Key.Up: e.Handled = true; EndEdit(true, -1, 0); break;
            case Key.Down: e.Handled = true; EndEdit(true, 1, 0); break;
        }
    }

    private void OnFormulaKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_editing) { EndEdit(true, 1, 0); return; }
            var text = FormulaBox.Text ?? "";
            if (text != _m.GetRaw(Sheet.AncR, Sheet.AncC)) _m.Enter(Sheet.AncR, Sheet.AncC, text);
            Sheet.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FormulaBox.Text = _m.GetRaw(Sheet.AncR, Sheet.AncC);
            Sheet.Focus();
        }
    }

    // ------------------------------------------------------------ formatting

    private void ForEachSelected(Action<int, int, CellFormat> change)
    {
        EndEditIfAny();
        var s = Sheet.SelectionForEdit();
        _m.BeginEdit();
        for (int r = s.r1; r <= s.r2; r++)
            for (int c = s.c1; c <= s.c2; c++)
            {
                int rr = r, cc = c;
                _m.UpdateFmt(rr, cc, f => change(rr, cc, f));
            }
        _m.EndEdit();
        Sheet.Focus();
    }

    private void ApplyFormat(Action<CellFormat> change) => ForEachSelected((_, _, f) => change(f));

    private void Toggle(Func<CellFormat, bool> read, Action<CellFormat, bool> write)
    {
        bool on = !(_m.GetFmt(Sheet.AncR, Sheet.AncC) is CellFormat f && read(f));
        ApplyFormat(x => write(x, on));
    }

    private void OnShortcut(char key)
    {
        switch (key)
        {
            case 'B': Toggle(f => f.Bold, (f, v) => f.Bold = v); break;
            case 'I': Toggle(f => f.Italic, (f, v) => f.Italic = v); break;
            case 'U': Toggle(f => f.Underline, (f, v) => f.Underline = v); break;
        }
    }

    private void ApplyNum(string name)
    {
        if (!Enum.TryParse<NumFmt>(name, out var kind)) return;
        ApplyFormat(f => { f.Num = kind; f.Dec = -1; });
    }

    private int EffectiveDecimals(int r, int c)
    {
        var f = _m.GetFmt(r, c);
        if (f != null && f.Dec >= 0) return f.Dec;
        switch (f?.Num ?? NumFmt.General)
        {
            case NumFmt.Number:
            case NumFmt.Thousands:
            case NumFmt.Currency: return 2;
            case NumFmt.Percent: return 0;
            default:
                {
                    var text = _m.Display(r, c, out bool isNum, out _, out _);
                    if (!isNum) return 0;
                    int i = text.IndexOf('.');
                    return i < 0 || text.Contains('E') ? 0 : text.Length - i - 1;
                }
        }
    }

    private void ChangeDecimals(int delta)
    {
        int next = Math.Clamp(EffectiveDecimals(Sheet.AncR, Sheet.AncC) + delta, 0, 10);
        ApplyFormat(f => f.Dec = next);
    }

    // ------------------------------------------------------------ data tools

    private bool LooksLikeHeader(int r1, int c1, int c2, int r2)
    {
        bool firstText = false;
        for (int c = c1; c <= c2; c++)
        {
            var v = _m.Eval.Eval(r1, c);
            if (v is double) return false;
            if (v is string s && s.Length > 0) firstText = true;
        }
        if (!firstText) return false;
        for (int r = r1 + 1; r <= Math.Min(r2, r1 + 25); r++)
            for (int c = c1; c <= c2; c++)
                if (_m.Eval.Eval(r, c) is double) return true;
        return false;
    }

    private void SortSelected(bool ascending)
    {
        EndEditIfAny();
        int used = _m.UsedRows, usedC = _m.UsedCols;
        if (used < 2) return;
        int keyCol = Sheet.AncC;
        int r1 = Sheet.SelR1, r2 = Math.Min(Sheet.SelR2, used - 1), c1 = Sheet.SelC1, c2 = Math.Min(Sheet.SelC2, Math.Max(0, usedC - 1));
        if (c1 == c2) { c1 = 0; c2 = Math.Max(0, usedC - 1); }
        if (keyCol < c1 || keyCol > c2) keyCol = c1;
        if (r2 - r1 < 1) { r1 = 0; r2 = used - 1; }
        bool header = r1 == 0 && LooksLikeHeader(r1, c1, c2, r2);
        if (header) r1++;
        if (r2 <= r1) return;
        _m.SortRows(r1, r2, c1, c2, keyCol, ascending);
        StatusText.Text = "Sorted by column " + FormulaEvaluator.ColumnName(keyCol) + (ascending ? " A to Z" : " Z to A") + (header ? " (heading row kept)" : "") + ". Formulas keep their original references.";
        Sheet.Focus();
    }

    private void ShowFilter()
    {
        int cols = Math.Max(1, _m.UsedCols);
        var items = new List<string>();
        for (int c = 0; c < cols; c++)
        {
            var head = _m.Display(0, c, out _, out _, out _);
            items.Add(FormulaEvaluator.ColumnName(c) + (head.Length > 0 ? "  —  " + (head.Length > 18 ? head.Substring(0, 18) + "…" : head) : ""));
        }
        FilterCol.ItemsSource = items;
        FilterCol.SelectedIndex = Math.Min(Sheet.AncC, cols - 1);
        FilterPanel.IsVisible = true;
        FilterVal.Focus();
    }

    private void ApplyFilterNow()
    {
        int col = Math.Max(0, FilterCol.SelectedIndex);
        var op = FilterOp.SelectedItem as string ?? "contains";
        int last = Math.Max(1, _m.UsedRows - 1);
        _m.ApplyFilter(col, op, FilterVal.Text ?? "", 1, last);
        int hidden = _m.HiddenRows.Count;
        StatusText.Text = hidden == 0 ? "Every row matches." : hidden + " row(s) hidden by the filter.";
    }

    private void Freeze(string how)
    {
        EndEditIfAny();
        switch (how)
        {
            case "row": _m.FrozenRows = 1; break;
            case "col": _m.FrozenCols = 1; break;
            case "sel": _m.FrozenRows = Sheet.AncR; _m.FrozenCols = Sheet.AncC; break;
            case "toggle": _m.FrozenRows = _m.FrozenRows > 0 || _m.FrozenCols > 0 ? 0 : 1; if (_m.FrozenRows == 0) _m.FrozenCols = 0; break;
            default: _m.FrozenRows = 0; _m.FrozenCols = 0; break;
        }
        _m.RaiseLayoutChanged();
        Sheet.SetScroll(0, 0);
        MarkModified();
        Sheet.Focus();
    }

    private (int, int, int, int) Region(int r, int c)
    {
        if (_m.IsEmpty(r, c)) return (0, 0, Math.Max(0, _m.UsedRows - 1), Math.Max(0, _m.UsedCols - 1));
        int r1 = r, r2 = r, c1 = c, c2 = c;
        bool Any(int ra, int rb, int ca, int cb)
        {
            for (int i = ra; i <= rb; i++)
                for (int j = ca; j <= cb; j++)
                    if (!_m.IsEmpty(i, j)) return true;
            return false;
        }
        bool grow = true;
        while (grow)
        {
            grow = false;
            int ca = Math.Max(0, c1 - 1), cb = Math.Min(SheetModel.MaxCols - 1, c2 + 1);
            if (r1 > 0 && Any(r1 - 1, r1 - 1, ca, cb)) { r1--; grow = true; }
            if (r2 < SheetModel.MaxRows - 1 && Any(r2 + 1, r2 + 1, ca, cb)) { r2++; grow = true; }
            int ra = Math.Max(0, r1 - 1), rb = Math.Min(SheetModel.MaxRows - 1, r2 + 1);
            if (c1 > 0 && Any(ra, rb, c1 - 1, c1 - 1)) { c1--; grow = true; }
            if (c2 < SheetModel.MaxCols - 1 && Any(ra, rb, c2 + 1, c2 + 1)) { c2++; grow = true; }
        }
        return (r1, c1, r2, c2);
    }

    private async Task MakeChart()
    {
        EndEditIfAny();
        var s = Sheet.SelectionForEdit();
        int r1 = s.r1, c1 = s.c1, r2 = Math.Min(s.r2, Math.Max(0, _m.UsedRows - 1)), c2 = Math.Min(s.c2, Math.Max(0, _m.UsedCols - 1));
        if (r1 == r2 && c1 == c2) (r1, c1, r2, c2) = Region(r1, c1);
        var data = ChartData.From(_m, r1, c1, r2, c2);
        if (data == null)
        {
            await Dialogs.ShowMessage(this, "Chart", "Select a block of cells that contains numbers (with headings in the first row and labels in the first column if you like), then try again.");
            return;
        }
        var win = new SheetChartWindow(data, "");
        WindowManager.Open(win);
    }

    // ------------------------------------------------------------ files

    private static FilePickerFileType XlsxType => new("Excel workbook") { Patterns = new[] { "*.xlsx" } };
    private static FilePickerFileType CsvType => new("CSV (comma separated values)") { Patterns = new[] { "*.csv", "*.tsv", "*.txt" } };

    private async Task<bool> ConfirmDiscard()
    {
        EndEditIfAny();
        if (!_modified) return true;
        int r = await Dialogs.Ask(this, "Sheets", "Save changes to this spreadsheet before continuing?", "Save", "Don't save", "Cancel");
        if (r == 0) return await Save(false);
        return r == 1;
    }

    private async Task NewSheet()
    {
        if (!await ConfirmDiscard()) return;
        _m.Clear();
        _path = null;
        _modified = false;
        Sheet.SelectRange(0, 0, 0, 0);
        Sheet.SetScroll(0, 0);
        UpdateTitle();
        OnSelectionChanged();
    }

    private async Task OpenDialog()
    {
        if (!await ConfirmDiscard()) return;
        var files = await FileDialogs.Open(this, new FilePickerOpenOptions
        {
            Title = "Open spreadsheet",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Spreadsheets") { Patterns = new[] { "*.xlsx", "*.csv", "*.tsv" } }, XlsxType, CsvType, FilePickerFileTypes.All }
        });
        var local = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (local != null) OpenPath(local);
    }

    /// <summary>Opens a .xlsx, .csv or .tsv file.</summary>
    public void OpenPath(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".xlsx") XlsxIO.Load(_m, path);
            else if (ext == ".xls" || ext == ".ods") throw new InvalidDataException("Only .xlsx and .csv files can be opened. Save the file as .xlsx in your other program first.");
            else _m.LoadCsv(File.ReadAllText(path));
            _path = path;
            _modified = false;
            Sheet.SelectRange(0, 0, 0, 0);
            Sheet.SetScroll(0, 0);
            UpdateTitle();
            OnSelectionChanged();
            RecentDocs.Add(path);
            StatusText.Text = "Opened " + Path.GetFileName(path) + " (" + _m.UsedRows + " rows, " + _m.UsedCols + " columns).";
        }
        catch (Exception ex)
        {
            _ = Dialogs.ShowMessage(this, "Open", "Could not open " + Path.GetFileName(path) + ": " + ex.Message);
        }
    }

    /// <summary>Kept for the file opener: loads a CSV (or any supported file) by path.</summary>
    public void LoadCsv(string path) => OpenPath(path);

    private bool CsvLosesData()
    {
        foreach (var (_, _, cell) in _m.AllCells())
            if ((cell.Raw.Length > 0 && cell.Raw[0] == '=') || (cell.Fmt != null && !cell.Fmt.IsDefault)) return true;
        return false;
    }

    private async Task<bool> Save(bool saveAs)
    {
        EndEditIfAny();
        string? target = saveAs ? null : _path;
        if (target != null && Path.GetExtension(target).ToLowerInvariant() is not (".xlsx" or ".csv" or ".tsv")) target = null;
        if (target == null)
        {
            var file = await FileDialogs.Save(this, new FilePickerSaveOptions
            {
                Title = "Save spreadsheet",
                SuggestedFileName = _path != null ? Path.GetFileNameWithoutExtension(_path) + ".xlsx" : "Spreadsheet.xlsx",
                DefaultExtension = "xlsx",
                FileTypeChoices = new[] { XlsxType, CsvType }
            });
            target = file?.TryGetLocalPath();
            if (target == null) return false;
            var e = Path.GetExtension(target).ToLowerInvariant();
            if (e != ".xlsx" && e != ".csv" && e != ".tsv") target += ".xlsx";
        }

        try
        {
            var ext = Path.GetExtension(target).ToLowerInvariant();
            if (ext == ".xlsx") XlsxIO.Save(_m, target);
            else
            {
                if (!_csvWarned && CsvLosesData())
                {
                    int r = await Dialogs.Ask(this, "Save as CSV", "CSV files only hold the calculated values. Formulas and formatting will not be saved in this file.", "Save as CSV", "Cancel");
                    if (r != 0) return false;
                    _csvWarned = true;
                }
                var text = _m.ToCsv();
                if (ext == ".tsv") text = string.Join("\n", SheetModel.ParseDelimited(text, ',').Select(row => string.Join("\t", row))) + "\n";
                await File.WriteAllTextAsync(target, text, new UTF8Encoding(false));
            }
            _path = target;
            _modified = false;
            UpdateTitle();
            RecentDocs.Add(target);
            StatusText.Text = "Saved " + Path.GetFileName(target);
            return true;
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Save", "Could not save: " + ex.Message);
            return false;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_modified || _forceClose) return;
        e.Cancel = true;
        if (await ConfirmDiscard()) { _forceClose = true; Close(); }
    }

    // ------------------------------------------------------------ menu / toolbar

    private void OnWindowKey(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        switch (e.Key)
        {
            case Key.S: e.Handled = true; _ = Save(shift); break;
            case Key.O: e.Handled = true; _ = OpenDialog(); break;
            case Key.N: e.Handled = true; _ = NewSheet(); break;
        }
    }

    private async void OnMenu(object? sender, RoutedEventArgs e)
    {
        var tag = (sender as Control)?.Tag as string ?? "";
        try
        {
            if (tag.StartsWith("num:")) { ApplyNum(tag.Substring(4)); return; }
            if (tag.StartsWith("align:")) { byte a = byte.Parse(tag.Substring(6)); ApplyFormat(f => f.Align = a); return; }
            if (tag.StartsWith("freeze:")) { Freeze(tag.Substring(7)); return; }
            if (tag.StartsWith("border:")) { ApplyBorders(tag.Substring(7)); return; }

            switch (tag)
            {
                case "new": await NewSheet(); break;
                case "open": await OpenDialog(); break;
                case "save": await Save(false); break;
                case "saveas": await Save(true); break;
                case "close": Close(); break;

                case "undo": EndEditIfAny(); _m.Undo(); Sheet.Focus(); break;
                case "redo": EndEditIfAny(); _m.Redo(); Sheet.Focus(); break;
                case "cut": Sheet.Copy(true); break;
                case "copy": Sheet.Copy(false); break;
                case "paste": Sheet.Paste(); break;
                case "clear":
                    {
                        EndEditIfAny();
                        var s = Sheet.SelectionForEdit();
                        _m.ClearContents(s.r1, s.c1, s.r2, s.c2);
                        Sheet.Focus();
                        break;
                    }
                case "filldown":
                    if (Sheet.SelR2 > Sheet.SelR1) _m.Fill(Sheet.SelR1, Sheet.SelC1, Sheet.SelR1, Sheet.SelC2, Sheet.SelR1, Sheet.SelC1, Sheet.SelR2, Sheet.SelC2);
                    break;
                case "fillright":
                    if (Sheet.SelC2 > Sheet.SelC1) _m.Fill(Sheet.SelR1, Sheet.SelC1, Sheet.SelR2, Sheet.SelC1, Sheet.SelR1, Sheet.SelC1, Sheet.SelR2, Sheet.SelC2);
                    break;
                case "selectall": Sheet.SelectRange(0, 0, SheetModel.MaxRows - 1, SheetModel.MaxCols - 1); Sheet.Focus(); break;

                case "bold": OnShortcut('B'); break;
                case "italic": OnShortcut('I'); break;
                case "underline": OnShortcut('U'); break;
                case "decplus": ChangeDecimals(1); break;
                case "decminus": ChangeDecimals(-1); break;
                case "clearfmt":
                    {
                        EndEditIfAny();
                        var s = Sheet.SelectionForEdit();
                        _m.BeginEdit();
                        for (int r = s.r1; r <= s.r2; r++)
                            for (int c = s.c1; c <= s.c2; c++)
                                if (_m.GetCell(r, c) is Cell cell && cell.Fmt != null) _m.SetCell(r, c, new Cell { Raw = cell.Raw, Fmt = null });
                        _m.EndEdit();
                        Sheet.Focus();
                        break;
                    }
                case "autofit":
                    {
                        var s = Sheet.SelectionForEdit();
                        for (int c = s.c1; c <= Math.Min(s.c2, s.c1 + 60); c++) Sheet.AutoFitColumn(c);
                        MarkModified();
                        break;
                    }
                case "resetsize":
                    _m.ColWidths.Clear(); _m.RowHeights.Clear();
                    _m.RaiseLayoutChanged();
                    MarkModified();
                    break;

                case "sortasc": SortSelected(true); break;
                case "sortdesc": SortSelected(false); break;
                case "filter": ShowFilter(); break;
                case "filterapply": ApplyFilterNow(); break;
                case "filterclose": FilterPanel.IsVisible = false; Sheet.Focus(); break;
                case "clearfilter": _m.ClearFilter(); StatusText.Text = "All rows are visible."; break;

                case "chart": await MakeChart(); break;
                case "functions": await Dialogs.ShowMessage(this, "Functions", FunctionHelp); break;
                case "keys": await Dialogs.ShowMessage(this, "Keyboard shortcuts", KeyHelp); break;
            }
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Sheets", ex.Message);
        }
    }

    private void ApplyBorders(string how)
    {
        var s = Sheet.SelectionForEdit();
        switch (how)
        {
            case "outline":
                ForEachSelected((r, c, f) =>
                {
                    byte b = 0;
                    if (c == s.c1) b |= 1;
                    if (r == s.r1) b |= 2;
                    if (c == s.c2) b |= 4;
                    if (r == s.r2) b |= 8;
                    f.Borders = b;
                });
                break;
            case "toggle":
                {
                    bool has = (_m.GetFmt(Sheet.AncR, Sheet.AncC)?.Borders ?? 0) != 0;
                    byte v = has ? (byte)0 : (byte)15;
                    ApplyFormat(f => f.Borders = v);
                    break;
                }
            default:
                {
                    byte v = byte.Parse(how);
                    ApplyFormat(f => f.Borders = v);
                    break;
                }
        }
    }

    private const string FunctionHelp =
        "Math: SUM, AVERAGE, MIN, MAX, COUNT, COUNTA, MEDIAN, PRODUCT, STDEV, LARGE, SMALL, SUMPRODUCT, ABS, ROUND, ROUNDUP, ROUNDDOWN, INT, MOD, POWER, SQRT, EXP, LN, LOG, PI, RAND, RANDBETWEEN\n\n" +
        "Conditional: IF, IFERROR, AND, OR, NOT, CHOOSE, COUNTIF, COUNTIFS, SUMIF, SUMIFS, AVERAGEIF\n\n" +
        "Lookup: VLOOKUP, HLOOKUP, INDEX, MATCH, ROW, COLUMN\n\n" +
        "Text: LEN, UPPER, LOWER, PROPER, TRIM, LEFT, RIGHT, MID, FIND, SEARCH, SUBSTITUTE, CONCAT, REPT, TEXT, VALUE\n\n" +
        "Dates: TODAY, NOW, DATE, YEAR, MONTH, DAY, WEEKDAY\n\n" +
        "Operators: + - * / ^ % & = <> < > <= >=\n" +
        "Cell references: A1, $A$1, ranges like A1:C10.";

    private const string KeyHelp =
        "Arrows: move.  Ctrl+Arrow: jump to the edge of the data.  Shift+Arrow: extend the selection.\n" +
        "Type to start editing, F2 to edit the current cell, Enter / Tab to confirm, Esc to cancel.\n" +
        "Ctrl+C / X / V: copy, cut, paste (also with other programs).\n" +
        "Ctrl+Z / Y: undo, redo.   Ctrl+B / I / U: bold, italic, underline.\n" +
        "Ctrl+D / R: fill down / right.  Drag the small square in a selection corner to fill a series.\n" +
        "Delete: clear the selected cells.   Ctrl+Home / Ctrl+End: first / last used cell.\n" +
        "Drag a column or row heading border to resize it; double-click a column border to fit its contents.";
}
