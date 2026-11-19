using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Tabbed plain-text editor with find/replace and a status bar (was Form14).</summary>
public partial class WriterWindow : Window
{
    private sealed class Doc
    {
        public TabItem Tab = null!;
        public TextBox Box = null!;
        public TextBlock TitleText = null!;
        public string? FilePath;
        public bool Dirty;
        public bool Loading;
        public string Name => FilePath == null ? "Untitled" : System.IO.Path.GetFileName(FilePath);
    }

    private readonly List<Doc> _docs = new();
    private bool _forceClose;

    public WriterWindow()
    {
        InitializeComponent();
        NewTab();
        BuildRecent();
        Tabs.SelectionChanged += (_, _) => { UpdateStatus(); UpdateWindowTitle(); };
        Closing += OnWindowClosing;
        KeyDown += OnKeys;
        FindBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FindNext(); e.Handled = true; } };
    }

    private Doc? Current() => (Tabs.SelectedItem as TabItem)?.Tag as Doc;

    // ------------------------------------------------------------ tabs

    private Doc NewTab(string text = "")
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Top,
            Text = text
        };
        ApplyStyle(box);

        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Content = "×",
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(title);
        header.Children.Add(close);

        var doc = new Doc { Box = box, TitleText = title };
        var tab = new TabItem { Header = header, Content = box, Tag = doc };
        doc.Tab = tab;

        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !doc.Loading)
            {
                if (!doc.Dirty) { doc.Dirty = true; RefreshTitle(doc); }
                UpdateStatus();
            }
            else if (e.Property == TextBox.CaretIndexProperty)
            {
                UpdateStatus();
            }
        };
        close.Click += async (_, _) => await CloseDoc(doc);

        _docs.Add(doc);
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        RefreshTitle(doc);
        return doc;
    }

    private void RefreshTitle(Doc d)
    {
        d.TitleText.Text = d.Name + (d.Dirty ? " •" : "");
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        var cur = Current();
        Title = "Red Star Studio Writer" + (cur == null ? "" : " - " + cur.Name + (cur.Dirty ? "*" : ""));
    }

    private void UpdateStatus()
    {
        var d = Current();
        if (d == null) { StatusLabel.Text = ""; return; }
        var text = d.Box.Text ?? "";
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        int caret = Math.Clamp(d.Box.CaretIndex, 0, text.Length);
        int line = 1;
        for (int i = 0; i < caret; i++) if (text[i] == '\n') line++;
        int lineStart = caret == 0 ? -1 : text.LastIndexOf('\n', caret - 1);
        int col = caret - lineStart;
        StatusLabel.Text = $"Words: {words}    Characters: {text.Length}    Line {line}, Col {col}";
    }

    private void ApplyStyle(TextBox box)
    {
        var s = SettingsStore.Current;
        box.FontSize = s.WriterFontSize > 0 ? s.WriterFontSize : 16;
        if (!string.IsNullOrEmpty(s.WriterFontFamily)) box.FontFamily = new FontFamily(s.WriterFontFamily);
        if (!string.IsNullOrEmpty(s.WriterColor) && Color.TryParse(s.WriterColor, out var c))
            box.Foreground = new SolidColorBrush(c);
    }

    // ------------------------------------------------------------ files

    public void OpenPath(string path)
    {
        var existing = _docs.FirstOrDefault(d => d.FilePath == path);
        if (existing != null) { Tabs.SelectedItem = existing.Tab; return; }

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex)
        {
            _ = Dialogs.ShowMessage(this, "Open", "Could not open that file: " + ex.Message);
            return;
        }

        var cur = Current();
        var d = cur != null && cur.FilePath == null && !cur.Dirty && string.IsNullOrEmpty(cur.Box.Text) ? cur : NewTab();
        d.Loading = true;
        d.Box.Text = text;
        d.Loading = false;
        d.FilePath = path;
        d.Dirty = false;
        RefreshTitle(d);
        AddRecent(path);
        UpdateStatus();
    }

    private async Task<bool> SaveDoc(Doc d, bool forceAs = false)
    {
        var path = d.FilePath;
        if (path == null || forceAs)
        {
            var file = await FileDialogs.Save(this, new FilePickerSaveOptions
            {
                Title = "Save Your Document",
                SuggestedFileName = d.FilePath == null ? "document.txt" : System.IO.Path.GetFileName(d.FilePath),
                DefaultExtension = "txt",
                FileTypeChoices = new[] { FilePickerFileTypes.TextPlain }
            });
            if (file == null) return false;
            path = file.TryGetLocalPath();
            if (path == null)
            {
                try
                {
                    await using var st = await file.OpenWriteAsync();
                    st.SetLength(0);
                    await using var w = new StreamWriter(st);
                    await w.WriteAsync(d.Box.Text ?? "");
                    d.Dirty = false;
                    RefreshTitle(d);
                    return true;
                }
                catch (Exception ex)
                {
                    await Dialogs.ShowMessage(this, "Save", "Could not save the file: " + ex.Message);
                    return false;
                }
            }
        }

        try
        {
            await File.WriteAllTextAsync(path, d.Box.Text ?? "");
            d.FilePath = path;
            d.Dirty = false;
            RefreshTitle(d);
            AddRecent(path);
            return true;
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Save", "Could not save the file: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> ConfirmClose(Doc d)
    {
        if (!d.Dirty) return true;
        var r = await Dialogs.Ask(this, "Unsaved changes", $"Save changes to {d.Name}?", "Save", "Discard", "Cancel");
        if (r == 0) return await SaveDoc(d);
        return r == 1;
    }

    private async Task CloseDoc(Doc d)
    {
        if (!await ConfirmClose(d)) return;
        _docs.Remove(d);
        Tabs.Items.Remove(d.Tab);
        if (_docs.Count == 0) NewTab();
        UpdateStatus();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose || !_docs.Any(d => d.Dirty)) return;
        e.Cancel = true;
        _ = CloseAllAsync();
    }

    private async Task CloseAllAsync()
    {
        foreach (var d in _docs.ToList())
            if (!await ConfirmClose(d)) return;
        _forceClose = true;
        Close();
    }

    private void AddRecent(string path)
    {
        var list = SettingsStore.Current.RecentFiles;
        list.Remove(path);
        list.Insert(0, path);
        if (list.Count > 10) list.RemoveRange(10, list.Count - 10);
        SettingsStore.Save();
        BuildRecent();
    }

    private void BuildRecent()
    {
        RecentMenu.Items.Clear();
        var recent = SettingsStore.Current.RecentFiles.Where(File.Exists).ToList();
        if (recent.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }
        foreach (var path in recent)
        {
            var p = path;
            var mi = new MenuItem { Header = Path.GetFileName(p).Replace("_", "__") };
            ToolTip.SetTip(mi, p);
            mi.Click += (_, _) => OpenPath(p);
            RecentMenu.Items.Add(mi);
        }
    }

    // ------------------------------------------------------------ menu handlers

    private void OnNew(object? s, RoutedEventArgs e) => NewTab();
    private void OnExit(object? s, RoutedEventArgs e) => Close();
    private void OnCloseTab(object? s, RoutedEventArgs e) { if (Current() is { } d) _ = CloseDoc(d); }

    private async void OnOpen(object? s, RoutedEventArgs e)
    {
        var files = await FileDialogs.Open(this, new FilePickerOpenOptions
        {
            Title = "Open A Document",
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.TextPlain, FilePickerFileTypes.All }
        });
        if (files.Count == 0) return;
        var local = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(local)) { OpenPath(local); return; }
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var d = NewTab();
            d.Loading = true;
            d.Box.Text = await reader.ReadToEndAsync();
            d.Loading = false;
            RefreshTitle(d);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Open", "Could not open that file: " + ex.Message);
        }
    }

    private async void OnSave(object? s, RoutedEventArgs e) { if (Current() is { } d) await SaveDoc(d); }
    private async void OnSaveAs(object? s, RoutedEventArgs e) { if (Current() is { } d) await SaveDoc(d, true); }

    private async void OnPrint(object? s, RoutedEventArgs e)
    {
        var text = Current()?.Box.Text ?? "";
        string message = "";
        await Task.Run(() => SystemUtil.PrintText(text, out message));
        await Dialogs.ShowMessage(this, "Print", message);
    }

    private void OnUndo(object? s, RoutedEventArgs e) { if (Current() is { } d) { d.Box.Focus(); d.Box.Undo(); } }
    private void OnRedo(object? s, RoutedEventArgs e) { if (Current() is { } d) { d.Box.Focus(); d.Box.Redo(); } }
    private void OnCut(object? s, RoutedEventArgs e) { if (Current() is { } d) { d.Box.Focus(); d.Box.Cut(); } }
    private void OnCopy(object? s, RoutedEventArgs e) { if (Current() is { } d) { d.Box.Focus(); d.Box.Copy(); } }
    private void OnPaste(object? s, RoutedEventArgs e) { if (Current() is { } d) { d.Box.Focus(); d.Box.Paste(); } }
    private void OnSelectAll(object? s, RoutedEventArgs e) { if (Current() is { } d) { d.Box.Focus(); d.Box.SelectAll(); } }

    // ------------------------------------------------------------ find & replace

    private void OnFind(object? s, RoutedEventArgs e) => ShowFind();

    private void ShowFind()
    {
        FindBar.IsVisible = true;
        var sel = Current()?.Box.SelectedText;
        if (!string.IsNullOrEmpty(sel) && !sel.Contains('\n')) FindBox.Text = sel;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void OnCloseFind(object? s, RoutedEventArgs e)
    {
        FindBar.IsVisible = false;
        Current()?.Box.Focus();
    }

    private void OnFindNext(object? s, RoutedEventArgs e) => FindNext();

    private bool FindNext()
    {
        var d = Current();
        var find = FindBox.Text ?? "";
        if (d == null || find.Length == 0) return false;

        var text = d.Box.Text ?? "";
        int start = Math.Min(Math.Max(d.Box.SelectionStart, d.Box.SelectionEnd), text.Length);
        int idx = text.IndexOf(find, start, StringComparison.OrdinalIgnoreCase);
        bool wrapped = false;
        if (idx < 0)
        {
            idx = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            wrapped = true;
        }
        if (idx < 0) { FindStatus.Text = "Not found"; return false; }

        d.Box.Focus();
        d.Box.SelectionStart = idx;
        d.Box.SelectionEnd = idx + find.Length;
        FindStatus.Text = wrapped ? "Wrapped to start" : "";
        return true;
    }

    private void OnReplace(object? s, RoutedEventArgs e)
    {
        var d = Current();
        var find = FindBox.Text ?? "";
        if (d == null || find.Length == 0) return;
        var sel = d.Box.SelectedText ?? "";
        if (string.Equals(sel, find, StringComparison.OrdinalIgnoreCase))
        {
            var repl = ReplaceBox.Text ?? "";
            int at = Math.Min(d.Box.SelectionStart, d.Box.SelectionEnd);
            var text = d.Box.Text ?? "";
            d.Box.Text = text.Remove(at, sel.Length).Insert(at, repl);
            d.Box.CaretIndex = at + repl.Length;
        }
        FindNext();
    }

    private void OnReplaceAll(object? s, RoutedEventArgs e)
    {
        var d = Current();
        var find = FindBox.Text ?? "";
        if (d == null || find.Length == 0) return;
        var text = d.Box.Text ?? "";
        int count = Regex.Matches(text, Regex.Escape(find), RegexOptions.IgnoreCase).Count;
        if (count == 0) { FindStatus.Text = "Not found"; return; }
        d.Box.Text = Regex.Replace(text, Regex.Escape(find), (ReplaceBox.Text ?? "").Replace("$", "$$"), RegexOptions.IgnoreCase);
        FindStatus.Text = $"Replaced {count}";
    }

    // ------------------------------------------------------------ text settings

    private async void OnFontColor(object? s, RoutedEventArgs e)
    {
        var st = SettingsStore.Current;
        var current = Color.TryParse(st.WriterColor ?? "", out var c) ? c : Colors.Black;
        var dlg = new TextStyleDialog(true, st.WriterFontFamily ?? "", st.WriterFontSize, current);
        await dlg.ShowDialog(this);
        if (!dlg.Accepted) return;
        st.WriterColor = dlg.SelectedColor.ToString();
        SettingsStore.Save();
        foreach (var d in _docs) ApplyStyle(d.Box);
    }

    private async void OnFontSize(object? s, RoutedEventArgs e)
    {
        var st = SettingsStore.Current;
        var dlg = new TextStyleDialog(false, st.WriterFontFamily ?? "", st.WriterFontSize, Colors.Black);
        await dlg.ShowDialog(this);
        if (!dlg.Accepted) return;
        if (!string.IsNullOrEmpty(dlg.SelectedFamily)) st.WriterFontFamily = dlg.SelectedFamily;
        st.WriterFontSize = dlg.SelectedSize;
        SettingsStore.Save();
        foreach (var d in _docs) ApplyStyle(d.Box);
    }

    // ------------------------------------------------------------ shortcuts

    private void OnKeys(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (e.Key == Key.F3) { FindNext(); e.Handled = true; return; }
        if (e.Key == Key.Escape && FindBar.IsVisible) { OnCloseFind(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (!ctrl) return;

        switch (e.Key)
        {
            case Key.N: NewTab(); e.Handled = true; break;
            case Key.O: OnOpen(null, new RoutedEventArgs()); e.Handled = true; break;
            case Key.S:
                if (Current() is { } d) _ = SaveDoc(d, shift);
                e.Handled = true;
                break;
            case Key.W:
                if (Current() is { } w) _ = CloseDoc(w);
                e.Handled = true;
                break;
            case Key.F: ShowFind(); e.Handled = true; break;
        }
    }
}
