using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Red Star Writer: native rich-text word processor (.docx, .html, .md, .txt, PDF).</summary>
public partial class RichWriterWindow : Window
{
    public static RichWriterWindow? Instance { get; private set; }

    private sealed class Rec { public string? Path { get; set; } public string Json { get; set; } = ""; public string Title { get; set; } = ""; }

    private static readonly double[] Sizes = { 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72 };
    private static readonly double[] Spacings = { 1.0, 1.15, 1.5, 2.0, 2.5 };
    private static readonly string[] Palette =
    {
        "#000000", "#434343", "#666666", "#999999", "#CCCCCC", "#FFFFFF",
        "#D93025", "#E8710A", "#F9AB00", "#188038", "#1A73E8", "#9334E6",
        "#F6AEA9", "#FDD6A8", "#FFF0A6", "#A8DAB5", "#AECBFA", "#D7AEFB"
    };
    private static string RecoveryDir => Path.Combine(AppPaths.ConfigDir, "writer-recovery");
    private static bool _recoveryChecked;

    private readonly List<DocTab> _tabs = new();
    private DocTab _cur = null!;
    private MenuItem? _recentMenu;
    private bool _syncing, _forceClose, _letter;
    private bool _pageView = true;
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(4) };

    public RichWriterWindow()
    {
        InitializeComponent();
        Instance = this;

        foreach (var s in Sizes) SizeBox.Items.Add(s.ToString("0.#"));
        foreach (var s in Spacings) SpacingBox.Items.Add(s.ToString("0.0#") + "×");
        FontBox.Items.Add("Default font");
        try
        {
            foreach (var n in FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n))
                FontBox.Items.Add(n);
        }
        catch { }
        FontBox.SelectedIndex = 0; SizeBox.SelectedIndex = Array.IndexOf(Sizes, 11.0); SpacingBox.SelectedIndex = 0;
        StyleBox.SelectedIndex = 0; PaperBox.SelectedIndex = 0;

        ColorBtn.Flyout = BuildSwatches(false);
        HiBtn.Flyout = BuildSwatches(true);

        StyleBox.SelectionChanged += (_, _) => { if (_syncing || StyleBox.SelectedIndex < 0) return; Doc.SetParaStyle((PStyle)StyleBox.SelectedIndex); Doc.Focus(); };
        FontBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || FontBox.SelectedIndex < 0) return;
            Doc.SetFont(FontBox.SelectedIndex == 0 ? null : FontBox.SelectedItem as string); Doc.Focus();
        };
        SizeBox.SelectionChanged += (_, _) => { if (_syncing || SizeBox.SelectedIndex < 0) return; Doc.SetSize(Sizes[SizeBox.SelectedIndex]); Doc.Focus(); };
        SpacingBox.SelectionChanged += (_, _) => { if (_syncing || SpacingBox.SelectedIndex < 0) return; Doc.SetSpacing(Spacings[SpacingBox.SelectedIndex]); Doc.Focus(); };
        PaperBox.SelectionChanged += (_, _) => { if (_syncing) return; _letter = PaperBox.SelectedIndex == 1; Doc.SetPageMode(_pageView, _letter); };
        PageNumChk.IsCheckedChanged += (_, _) => { Doc.PageNumbers = PageNumChk.IsChecked == true; Doc.InvalidateVisual(); };
        ZoomSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && !_syncing) Doc.Zoom = ZoomSlider.Value / 100.0; };
        Doc.ZoomChanged += () => { _syncing = true; ZoomSlider.Value = Doc.Zoom * 100; ZoomText.Text = (int)Math.Round(Doc.Zoom * 100) + "%"; _syncing = false; };
        Doc.Changed += OnDocChanged;
        Doc.SelectionChanged += SyncRibbon;
        Doc.OpenLinkRequested += OpenLink;
        ZoomText.Text = "100%";

        FindBox.TextChanged += (_, _) => RunFind(false);
        CaseChk.IsCheckedChanged += (_, _) => RunFind(false);
        FindBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { StepFind(!e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; } else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; } };
        ReplaceBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { DoReplace(); e.Handled = true; } else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; } };
        if (FileBtn.Flyout is MenuFlyout fm) { _recentMenu = fm.Items.OfType<MenuItem>().First(m => (m.Header as string) == "Open recent"); fm.Opening += (_, _) => FillRecent(); }

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnWindowKey, RoutingStrategies.Tunnel);
        Closing += OnClosing;
        Closed += (_, _) => { _autosave.Stop(); if (ReferenceEquals(Instance, this)) Instance = null; };

        _autosave.Tick += (_, _) => Autosave();
        _autosave.Start();

        NewTab();
        Opened += async (_, _) => await CheckRecovery();
    }

    // ───────────────────────── tabs ─────────────────────────
    private DocTab NewTab(RichDoc? doc = null, string? path = null)
    {
        var t = new DocTab();
        if (doc != null) t.Doc = doc;
        t.Path = path;
        _tabs.Add(t);
        SwitchTo(t);
        return t;
    }

    private void SwitchTo(DocTab t)
    {
        if (_cur != null && _tabs.Contains(_cur)) { Doc.SaveViewState(); _cur.ScrollY = Scroller.Offset.Y; }
        _cur = t;
        Doc.SetTab(t);
        Doc.PageNumbers = PageNumChk.IsChecked == true;
        Dispatcher.UIThread.Post(() => Scroller.Offset = new Vector(Scroller.Offset.X, t.ScrollY), DispatcherPriority.Background);
        RebuildTabs();
        UpdateTitle(); UpdateCounts(); SyncRibbon();
        Doc.Focus();
    }

    private void RebuildTabs()
    {
        ShellTabs.Build(TabsPanel, _tabs.Select(t =>
        {
            var tab = t;
            return new TabSpec
            {
                Title = tab.Title,
                IconName = "document",
                Prefix = tab.Dirty ? "● " : null,
                Active = ReferenceEquals(tab, _cur),
                Tooltip = tab.Path ?? "Not saved yet",
                Select = () => SwitchTo(tab),
                Close = () => _ = CloseTab(tab),
            };
        }));
    }

    private async Task<bool> CloseTab(DocTab t)
    {
        if (t.Dirty)
        {
            if (!ReferenceEquals(t, _cur)) SwitchTo(t);
            var a = await Dialogs.Ask(this, "Writer", $"Save changes to “{t.Title}”?", "Save", "Don't save", "Cancel");
            if (a == 0) { if (!await Save(t, false)) return false; }
            else if (a != 1) return false;
        }
        int i = _tabs.IndexOf(t);
        _tabs.Remove(t);
        DeleteRecovery(t);
        if (_tabs.Count == 0) NewTab();
        else if (ReferenceEquals(t, _cur)) SwitchTo(_tabs[Math.Min(i, _tabs.Count - 1)]);
        else RebuildTabs();
        return true;
    }

    private void UpdateTitle() => Title = $"{(_cur.Dirty ? "● " : "")}{_cur.Title} — Writer";

    private void OnDocChanged()
    {
        UpdateTitle(); UpdateCounts();
        RebuildTabsLabelOnly();
    }

    private bool _lastDirty;
    private void RebuildTabsLabelOnly()
    {
        if (_lastDirty == _cur.Dirty) return;
        _lastDirty = _cur.Dirty;
        RebuildTabs();
    }

    private void UpdateCounts()
    {
        var (w, c) = _cur.Doc.Counts();
        CountText.Text = $"{w:N0} words · {c:N0} characters";
    }

    // ───────────────────────── ribbon sync ─────────────────────────
    private void SyncRibbon()
    {
        if (_cur == null) return;
        _syncing = true;
        try
        {
            var f = Doc.CurrentFormat();
            BoldBtn.Classes.Set("primary", f.Bold); ItalicBtn.Classes.Set("primary", f.Italic);
            UnderBtn.Classes.Set("primary", f.Underline); StrikeBtn.Classes.Set("primary", f.Strike);
            SupBtn.Classes.Set("primary", f.Sup); SubBtn.Classes.Set("primary", f.Sub);
            var p = Doc.CaretPara;
            StyleBox.SelectedIndex = (int)p.Style;
            AlLBtn.Classes.Set("primary", p.Align == Align.Left); AlCBtn.Classes.Set("primary", p.Align == Align.Center);
            AlRBtn.Classes.Set("primary", p.Align == Align.Right); AlJBtn.Classes.Set("primary", p.Align == Align.Justify);
            BulletBtn.Classes.Set("primary", p.List == ListKind.Bullet); NumBtn.Classes.Set("primary", p.List == ListKind.Number);
            var fi = f.Font == null ? 0 : FontBox.Items.IndexOf(f.Font);
            FontBox.SelectedIndex = fi < 0 ? 0 : fi;
            var si = Array.IndexOf(Sizes, f.Size);
            SizeBox.SelectedIndex = si;
            var sp = Array.FindIndex(Spacings, s => Math.Abs(s - p.Spacing) < 0.01);
            SpacingBox.SelectedIndex = sp;
            TableOpsBtn.IsEnabled = Doc.CaretInTable;
        }
        finally { _syncing = false; }
    }

    private Flyout BuildSwatches(bool highlight)
    {
        var fly = new Flyout();
        var wrap = new WrapPanel { Width = 214 };
        foreach (var hex in Palette)
        {
            var h = hex;
            var b = new Button
            {
                Width = 28, Height = 28, Margin = new Thickness(2), Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse(h)),
                BorderBrush = new SolidColorBrush(Color.Parse("#888888")), BorderThickness = new Thickness(1)
            };
            b.Click += (_, _) => { Pick(highlight, h); fly.Hide(); };
            wrap.Children.Add(b);
        }
        var none = new Button { Content = highlight ? "No highlight" : "Automatic" };
        none.Click += (_, _) => { Pick(highlight, null); fly.Hide(); };
        var custom = new Button { Content = "Custom…" };
        custom.Click += async (_, _) =>
        {
            fly.Hide();
            var v = await Dialogs.Prompt(this, highlight ? "Highlight colour" : "Text colour", "Colour (hex, e.g. #1A73E8)", "#");
            var c = DocTextIO.ParseColor(v);
            if (c != null) Pick(highlight, c);
        };
        fly.Content = new StackPanel { Spacing = 6, Children = { wrap, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { none, custom } } } };
        return fly;
    }

    private void Pick(bool highlight, string? hex)
    {
        if (highlight) Doc.SetHighlight(hex); else Doc.SetColor(hex);
        Doc.Focus();
    }

    // ───────────────────────── commands ─────────────────────────
    private void OnCmd(object? sender, RoutedEventArgs e)
    {
        var tag = (sender as Control)?.Tag as string;
        if (tag == null) return;
        Exec(tag);
    }

    private void Exec(string tag)
    {
        switch (tag)
        {
            case "newtab": NewTab(); break;
            case "open": _ = OpenDialog(); break;
            case "save": _ = Save(_cur, false); break;
            case "saveas": _ = Save(_cur, true); break;
            case "pdf": _ = ExportPdf(); break;
            case "print": _ = Print(); break;
            case "closetab": _ = CloseTab(_cur); break;
            case "undo": Doc.Undo(); break;
            case "redo": Doc.Redo(); break;
            case "bold": Doc.ToggleBold(); break;
            case "italic": Doc.ToggleItalic(); break;
            case "underline": Doc.ToggleUnderline(); break;
            case "strike": Doc.ToggleStrike(); break;
            case "sup": Doc.ToggleSup(); break;
            case "sub": Doc.ToggleSub(); break;
            case "clearfmt": Doc.ClearFormatting(); break;
            case "sizeup": StepSize(1); break;
            case "sizedown": StepSize(-1); break;
            case "alleft": Doc.SetAlign(Align.Left); break;
            case "alcenter": Doc.SetAlign(Align.Center); break;
            case "alright": Doc.SetAlign(Align.Right); break;
            case "aljust": Doc.SetAlign(Align.Justify); break;
            case "bullets": Doc.ToggleList(ListKind.Bullet); break;
            case "numbers": Doc.ToggleList(ListKind.Number); break;
            case "indent": Doc.Indent(1); break;
            case "outdent": Doc.Indent(-1); break;
            case "link": _ = InsertLink(); break;
            case "picture": _ = InsertPicture(); break;
            case "table": _ = InsertTable(); break;
            case "rowadd": Doc.TableAddRow(true); break;
            case "coladd": Doc.TableAddCol(); break;
            case "rowdel": Doc.TableDeleteRow(); break;
            case "coldel": Doc.TableDeleteCol(); break;
            case "rule": Doc.InsertRule(); break;
            case "pagebreak": Doc.InsertPageBreak(); break;
            case "find": ShowFind(false); break;
            case "findnext": StepFind(true); break;
            case "findprev": StepFind(false); break;
            case "replace": DoReplace(); break;
            case "replaceall": DoReplaceAll(); break;
            case "findclose": CloseFind(); break;
            case "pageview":
                _pageView = !_pageView;
                PageBtn.Content = _pageView ? "Page view" : "Web view";
                PaperBox.IsEnabled = _pageView;
                Doc.SetPageMode(_pageView, _letter);
                break;
            case "zoomin": Doc.Zoom += 0.1; break;
            case "zoomout": Doc.Zoom -= 0.1; break;
        }
        if (tag is not ("find" or "findnext" or "findprev" or "replace" or "replaceall" or "open" or "save" or "saveas" or "pdf" or "print" or "link" or "picture" or "table"))
            Doc.Focus();
    }

    private void StepSize(int d)
    {
        var cur = Doc.CurrentFormat().Size;
        int i = Array.FindIndex(Sizes, s => s >= cur - 0.01);
        if (i < 0) i = Sizes.Length - 1;
        if (Math.Abs(Sizes[i] - cur) > 0.01 && d < 0) i++;   // between sizes: step down from the next higher
        i = Math.Clamp(i + d, 0, Sizes.Length - 1);
        Doc.SetSize(Sizes[i]);
    }

    private void OnWindowKey(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!ctrl) { if (e.Key == Key.Escape && FindBar.IsVisible && !FindBox.IsFocused && !ReplaceBox.IsFocused) { CloseFind(); e.Handled = true; } return; }
        switch (e.Key)
        {
            case Key.N: NewTab(); break;
            case Key.O: _ = OpenDialog(); break;
            case Key.S: _ = Save(_cur, shift); break;
            case Key.W: _ = CloseTab(_cur); break;
            case Key.P: _ = Print(); break;
            case Key.F: ShowFind(false); break;
            case Key.H: ShowFind(true); break;
            case Key.K: _ = InsertLink(); break;
            case Key.OemPlus: case Key.Add: Doc.Zoom += 0.1; break;
            case Key.OemMinus: case Key.Subtract: Doc.Zoom -= 0.1; break;
            case Key.Tab:
            {
                int i = _tabs.IndexOf(_cur);
                SwitchTo(_tabs[(i + (shift ? _tabs.Count - 1 : 1)) % _tabs.Count]);
                break;
            }
            default: return;
        }
        e.Handled = true;
    }

    // ───────────────────────── files ─────────────────────────
    private static readonly string[] Opens = { ".docx", ".html", ".htm", ".md", ".markdown", ".txt" };

    public void OpenPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var existing = _tabs.FirstOrDefault(t => t.Path == full);
            if (existing != null) { SwitchTo(existing); return; }
            RichDoc doc; bool letter = _letter, pn = Doc.PageNumbers;
            var ext = Path.GetExtension(full).ToLowerInvariant();
            if (!File.Exists(full))
            {
                _ = Dialogs.ShowMessage(this, "Writer", "That file no longer exists:\n" + full);
                return;
            }
            switch (ext)
            {
                case ".docx":
                    var (d, info) = DocxIO.Load(full);
                    doc = d; letter = info.Letter; pn = info.PageNumbers;
                    break;
                case ".html": case ".htm": doc = DocTextIO.FromHtml(File.ReadAllText(full), Path.GetDirectoryName(full)); break;
                case ".md": case ".markdown": doc = DocTextIO.FromMarkdown(File.ReadAllText(full)); break;
                default: doc = DocTextIO.FromText(File.ReadAllText(full)); break;
            }
            // reuse an empty, untouched first tab
            if (_tabs.Count == 1 && !_cur.Dirty && _cur.Path == null && _cur.Doc.PlainText().Length == 0)
            {
                _cur.Doc = doc; _cur.Path = full;
                SwitchTo(_cur);
            }
            else NewTab(doc, full);
            if (ext == ".docx") { _syncing = true; PaperBox.SelectedIndex = letter ? 1 : 0; PageNumChk.IsChecked = pn; _syncing = false; _letter = letter; Doc.PageNumbers = pn; Doc.SetPageMode(_pageView, _letter); }
            RecentDocs.Add(full);
            _cur.Dirty = false; _lastDirty = false;
            RebuildTabs(); UpdateTitle();
        }
        catch (Exception ex)
        {
            _ = Dialogs.ShowMessage(this, "Writer", "Couldn't open the file:\n" + ex.Message);
        }
    }

    /// <summary>Opens an article (HTML) from the browser as a new unsaved document.</summary>
    public void OpenHtmlDocument(string title, string html)
    {
        try
        {
            // the article usually carries its own headline; only add the page title when it doesn't
            bool hasHeading = System.Text.RegularExpressions.Regex.IsMatch(html, @"<h1[\s>]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var doc = DocTextIO.FromHtml((hasHeading || string.IsNullOrWhiteSpace(title) ? "" : "<h1>" + System.Net.WebUtility.HtmlEncode(title) + "</h1>") + html);
            if (_tabs.Count == 1 && !_cur.Dirty && _cur.Path == null && _cur.Doc.PlainText().Length == 0)
            {
                _cur.Doc = doc; SwitchTo(_cur);
            }
            else NewTab(doc);
            _cur.Dirty = true;
            RebuildTabs(); UpdateTitle();
        }
        catch (Exception ex)
        {
            _ = Dialogs.ShowMessage(this, "Writer", "Couldn't import the page:\n" + ex.Message);
        }
    }

    public void Raise()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
    }

    private async Task OpenDialog()
    {
        var r = await FileDialogs.Open(this, new FilePickerOpenOptions
        {
            Title = "Open document",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Documents") { Patterns = Opens.Select(x => "*" + x).ToList() },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        foreach (var f in r) { var p = f.TryGetLocalPath(); if (p != null) OpenPath(p); }
    }

    private void WriteFile(DocTab t, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".docx": DocxIO.Save(t.Doc, path, _letter, Doc.PageNumbers); break;
            case ".html": case ".htm": File.WriteAllText(path, DocTextIO.ToHtml(t.Doc, Path.GetFileNameWithoutExtension(path))); break;
            case ".md": case ".markdown": File.WriteAllText(path, DocTextIO.ToMarkdown(t.Doc)); break;
            default: File.WriteAllText(path, t.Doc.PlainText()); break;
        }
    }

    private async Task<bool> Save(DocTab t, bool saveAs)
    {
        var path = t.Path;
        if (path == null || saveAs)
        {
            var r = await FileDialogs.Save(this, new FilePickerSaveOptions
            {
                Title = "Save document",
                SuggestedFileName = path != null ? Path.GetFileNameWithoutExtension(path) : (t.Title == "Untitled" ? "Document" : Path.GetFileNameWithoutExtension(t.Title)),
                DefaultExtension = "docx",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Word document (.docx)") { Patterns = new[] { "*.docx" } },
                    new FilePickerFileType("Web page (.html)") { Patterns = new[] { "*.html" } },
                    new FilePickerFileType("Markdown (.md)") { Patterns = new[] { "*.md" } },
                    new FilePickerFileType("Plain text (.txt)") { Patterns = new[] { "*.txt" } }
                }
            });
            path = r?.TryGetLocalPath();
            if (path == null) return false;
            if (Path.GetExtension(path) == "") path += ".docx";
        }
        try
        {
            Doc.SaveViewState();
            WriteFile(t, path);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Writer", "Couldn't save the file:\n" + ex.Message);
            return false;
        }
        t.Path = path; t.Dirty = false; _lastDirty = false;
        RecentDocs.Add(path);
        DeleteRecovery(t);
        RebuildTabs(); UpdateTitle();
        return true;
    }

    private async Task<string?> PickPdf()
    {
        var r = await FileDialogs.Save(this, new FilePickerSaveOptions
        {
            Title = "Export as PDF",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_cur.Title),
            DefaultExtension = "pdf",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } }
        });
        var p = r?.TryGetLocalPath();
        if (p != null && Path.GetExtension(p) == "") p += ".pdf";
        return p;
    }

    private async Task ExportPdf()
    {
        var p = await PickPdf();
        if (p == null) return;
        try { DocPdf.Save(_cur.Doc, p, _letter, Doc.PageNumbers, Path.GetFileNameWithoutExtension(_cur.Title)); Notifier.Show("Writer", "Saved " + Path.GetFileName(p), "writer"); }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Writer", "Couldn't create the PDF:\n" + ex.Message); }
    }

    private async Task Print()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "redstar-print-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
        try { DocPdf.Save(_cur.Doc, tmp, _letter, Doc.PageNumbers, _cur.Title); }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Writer", "Couldn't prepare the document:\n" + ex.Message); return; }
        var a = await Dialogs.Ask(this, "Print", "Send to the default printer, or open the PDF in your viewer (where you can pick a printer and options)?", "Default printer", "Open PDF", "Cancel");
        try
        {
            if (a == 0) Process.Start(new ProcessStartInfo("lp", "\"" + tmp + "\"") { UseShellExecute = false });
            else if (a == 1) Process.Start(new ProcessStartInfo("xdg-open", "\"" + tmp + "\"") { UseShellExecute = false });
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Print", "Couldn't start printing (is CUPS 'lp' installed?):\n" + ex.Message); }
    }

    private void FillRecent()
    {
        _recentMenu!.Items.Clear();
        var list = RecentDocs.List(10).Where(File.Exists).ToList();
        if (list.Count == 0) { _recentMenu!.Items.Add(new MenuItem { Header = "(none yet)", IsEnabled = false }); return; }
        foreach (var f in list)
        {
            var path = f;
            var mi = new MenuItem { Header = Path.GetFileName(path) };
            ToolTip.SetTip(mi, path);
            mi.Click += (_, _) => OpenPath(path);
            _recentMenu!.Items.Add(mi);
        }
    }

    // ───────────────────────── inserts ─────────────────────────
    private async Task InsertLink()
    {
        var cur = Doc.LinkAtCaret();
        var url = await Dialogs.Prompt(this, "Link", cur != null ? "Address (clear it to remove the link)" : "Address", cur ?? "https://");
        if (url == null) { Doc.Focus(); return; }
        url = url.Trim();
        if (url.Length == 0 || url == "https://") { if (cur != null) Doc.RemoveLink(); }
        else
        {
            if (!url.Contains("://") && !url.StartsWith("mailto:") && !url.StartsWith("#")) url = (url.Contains('@') && !url.Contains('/') ? "mailto:" : "https://") + url;
            Doc.InsertLink(url);
        }
        Doc.Focus();
    }

    private async Task InsertPicture()
    {
        var r = await FileDialogs.Open(this, new FilePickerOpenOptions
        {
            Title = "Insert picture",
            FileTypeFilter = new[] { new FilePickerFileType("Pictures") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp" } } }
        });
        var p = r.FirstOrDefault()?.TryGetLocalPath();
        if (p != null) InsertImageFile(p);
        Doc.Focus();
    }

    private void InsertImageFile(string p)
    {
        try
        {
            var bytes = File.ReadAllBytes(p);
            // Avalonia's Bitmap handles png/jpg/bmp/gif/webp; other formats are re-encoded through Skia as PNG so .docx stays valid
            bool png = bytes.Length > 4 && bytes[0] == 0x89, jpg = bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            if (!png && !jpg)
            {
                using var bmp = SkiaSharp.SKBitmap.Decode(bytes);
                if (bmp == null) return;
                using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                bytes = data.ToArray();
            }
            Doc.InsertImage(bytes);
        }
        catch (Exception ex) { _ = Dialogs.ShowMessage(this, "Writer", "Couldn't insert the picture:\n" + ex.Message); }
    }

    private async Task InsertTable()
    {
        var s = await Dialogs.Prompt(this, "Insert table", "Rows × columns", "3x3");
        if (s == null) { Doc.Focus(); return; }
        var parts = s.ToLowerInvariant().Replace('×', 'x').Split(new[] { 'x', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var r) && int.TryParse(parts[1], out var c) && r > 0 && c > 0)
            Doc.InsertTable(r, c);
        else await Dialogs.ShowMessage(this, "Insert table", "Type it like 3x4 (3 rows, 4 columns).");
        Doc.Focus();
    }

    private void OpenLink(string url)
    {
        if (url.StartsWith("#")) return;
        try { Process.Start(new ProcessStartInfo("xdg-open", "\"" + url + "\"") { UseShellExecute = false }); } catch { }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
        if (files == null) return;
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (Opens.Contains(ext)) OpenPath(f);
            else if (FileOps.ImageExtensions.Contains(ext)) InsertImageFile(f);
        }
    }

    // ───────────────────────── find / replace ─────────────────────────
    private void ShowFind(bool replace)
    {
        FindBar.IsVisible = true;
        ReplaceBox.IsVisible = true;
        var sel = Doc.HasSelection ? Doc.SelectedText() : "";
        if (sel.Length > 0 && !sel.Contains('\n')) FindBox.Text = sel;
        (replace ? ReplaceBox : FindBox).Focus();
        if (!replace) FindBox.SelectAll();
        RunFind(false);
    }

    private void CloseFind()
    {
        FindBar.IsVisible = false;
        Doc.ClearFind();
        Doc.Focus();
    }

    private void RunFind(bool step)
    {
        var q = FindBox.Text ?? "";
        int n = Doc.FindAll(q, CaseChk.IsChecked == true);
        FindInfo.Text = q.Length == 0 ? "" : n == 0 ? "No matches" : n + (n == 1 ? " match" : " matches");
        if (step && n > 0) Doc.FindStep(true);
    }

    private void StepFind(bool fwd)
    {
        if (Doc.MatchCount == 0) RunFind(false);
        if (Doc.MatchCount == 0) return;
        Doc.FindStep(fwd);
        FindInfo.Text = $"{Doc.MatchIndex + 1} of {Doc.MatchCount}";
    }

    private void DoReplace()
    {
        var q = FindBox.Text ?? "";
        if (q.Length == 0) return;
        if (Doc.MatchCount == 0) Doc.FindAll(q, CaseChk.IsChecked == true);
        Doc.ReplaceCurrent(q, ReplaceBox.Text ?? "", CaseChk.IsChecked == true);
        RunFind(false);
        if (Doc.MatchCount > 0) FindInfo.Text = $"{Doc.MatchCount} left";
    }

    private void DoReplaceAll()
    {
        var q = FindBox.Text ?? "";
        if (q.Length == 0) return;
        int n = Doc.ReplaceAll(q, ReplaceBox.Text ?? "", CaseChk.IsChecked == true);
        Doc.FindAll(q, CaseChk.IsChecked == true);
        FindInfo.Text = n == 0 ? "No matches" : $"Replaced {n}";
    }

    // ───────────────────────── recovery ─────────────────────────
    private void Autosave()
    {
        try
        {
            foreach (var t in _tabs)
            {
                if (!t.Dirty) continue;
                Directory.CreateDirectory(RecoveryDir);
                var rec = new Rec { Path = t.Path, Json = t.Doc.ToJson(), Title = t.Title };
                File.WriteAllText(Path.Combine(RecoveryDir, t.Id + ".json"), JsonSerializer.Serialize(rec));
            }
        }
        catch { }
    }

    private void DeleteRecovery(DocTab t)
    {
        try { var f = Path.Combine(RecoveryDir, t.Id + ".json"); if (File.Exists(f)) File.Delete(f); } catch { }
    }

    private async Task CheckRecovery()
    {
        if (_recoveryChecked) return;
        _recoveryChecked = true;
        try
        {
            if (!Directory.Exists(RecoveryDir)) return;
            var files = Directory.GetFiles(RecoveryDir, "*.json");
            if (files.Length == 0) return;
            var a = await Dialogs.Ask(this, "Writer", $"Writer closed unexpectedly. Restore {files.Length} unsaved document{(files.Length == 1 ? "" : "s")}?", "Restore", "Discard");
            foreach (var f in files)
            {
                try
                {
                    if (a == 0)
                    {
                        var rec = JsonSerializer.Deserialize<Rec>(File.ReadAllText(f));
                        var doc = rec == null ? null : RichDoc.FromJson(rec.Json);
                        if (doc != null)
                        {
                            bool reuse = _tabs.Count == 1 && !_cur.Dirty && _cur.Path == null && _cur.Doc.PlainText().Length == 0;
                            var t = reuse ? _cur : NewTab();
                            t.Doc = doc; t.Path = rec!.Path; t.Dirty = true;
                            SwitchTo(t);
                        }
                    }
                    File.Delete(f);
                }
                catch { }
            }
            RebuildTabs(); UpdateTitle();
        }
        catch { }
    }

    // ───────────────────────── closing ─────────────────────────
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose) return;
        if (!_tabs.Any(t => t.Dirty)) { foreach (var t in _tabs) DeleteRecovery(t); return; }
        e.Cancel = true;
        foreach (var t in _tabs.ToList())
        {
            if (!t.Dirty) continue;
            if (!ReferenceEquals(t, _cur)) SwitchTo(t);
            var a = await Dialogs.Ask(this, "Writer", $"Save changes to “{t.Title}”?", "Save", "Don't save", "Cancel");
            if (a == 0) { if (!await Save(t, false)) return; }
            else if (a != 1) return;
        }
        foreach (var t in _tabs) DeleteRecovery(t);
        _forceClose = true;
        Close();
    }
}
