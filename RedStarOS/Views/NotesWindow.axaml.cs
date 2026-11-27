using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Notes: search, pinning, autosave to ~/.config/redstaros/notes, delete with undo.
/// Every note is a plain .txt file, so other programs can edit them too.
/// </summary>
public partial class NotesWindow : Window
{
    public sealed class NoteItem
    {
        public string FilePath { get; init; } = "";
        public string Text { get; set; } = "";
        public DateTime Modified { get; set; } = DateTime.Now;
        public bool Pinned { get; set; }

        public string Title
        {
            get
            {
                var first = Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                return string.IsNullOrEmpty(first) ? "New note" : (first.Length > 60 ? first[..60] : first);
            }
        }

        public string Heading => Title;

        public string Preview
        {
            get
            {
                var line = Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Skip(1).FirstOrDefault();
                return string.IsNullOrEmpty(line) ? "No additional text" : (line.Length > 80 ? line[..80] : line);
            }
        }

        public string DateText => Modified.Date == DateTime.Today ? Modified.ToString("HH:mm") : Modified.ToString("d MMM");
    }

    private readonly List<NoteItem> _all = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private NoteItem? _current;
    private NoteItem? _pendingDelete;
    private bool _loading, _dirty, _refreshing;

    private static string NotesDir
    {
        get
        {
            var d = Path.Combine(AppPaths.ConfigDir, "notes");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string PinsFile => Path.Combine(NotesDir, "pins.json");

    public NotesWindow()
    {
        InitializeComponent();

        var pins = LoadPins();
        foreach (var f in new DirectoryInfo(NotesDir).GetFiles("*.txt"))
        {
            try
            {
                _all.Add(new NoteItem
                {
                    FilePath = f.FullName,
                    Text = File.ReadAllText(f.FullName),
                    Modified = f.LastWriteTime,
                    Pinned = pins.Contains(f.Name)
                });
            }
            catch { /* unreadable file: skip */ }
        }
        if (_all.Count == 0) CreateNote();

        SearchBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) RefreshList(); };
        NoteList.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            Flush();
            _current = NoteList.SelectedItem as NoteItem;
            LoadEditor();
        };
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || _loading || _current == null) return;
            _dirty = true;
            StatusLabel.Text = "Saving…";
            UpdateWordCount();
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Flush(); RefreshList(); };
        _undoTimer.Tick += (_, _) => CommitDelete();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.N && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { OnNew(null, new RoutedEventArgs()); e.Handled = true; }
        };
        Closing += (_, _) => { Flush(); CommitDelete(); };

        RefreshList(_all.OrderByDescending(n => n.Modified).First());
        LoadEditor();
    }

    // ------------------------------------------------------------ list

    private void RefreshList(NoteItem? select = null)
    {
        _refreshing = true;
        var q = (SearchBox.Text ?? "").Trim();
        var list = _all
            .Where(n => q.Length == 0 || n.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Modified)
            .ToList();
        if (select != null) _current = select;
        NoteList.ItemsSource = list;
        NoteList.SelectedItem = _current != null && list.Contains(_current) ? _current : null;
        _refreshing = false;

        ListEmpty.IsVisible = list.Count == 0;
        ListEmptyTitle.Text = q.Length > 0 ? "No matching notes" : "No notes yet";
        ListEmptyText.Text = q.Length > 0 ? "Try other words, or clear the search." : "Press New note (Ctrl+N) and start typing; notes save as you go.";
        CountLabel.Text = q.Length > 0 ? $"{list.Count} MATCHING" : $"{_all.Count} NOTE{(_all.Count == 1 ? "" : "S")}";
        PinButton.Content = Icons.Label("pin", _current?.Pinned == true ? "Unpin" : "Pin", 15);
    }

    private void LoadEditor()
    {
        _loading = true;
        Editor.Text = _current?.Text ?? "";
        _loading = false;
        Editor.IsEnabled = _current != null;
        PinButton.Content = Icons.Label("pin", _current?.Pinned == true ? "Unpin" : "Pin", 15);
        StatusLabel.Text = "";
        UpdateWordCount();
    }

    private void UpdateWordCount()
    {
        var text = Editor.Text ?? "";
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        WordLabel.Text = $"{words} word{(words == 1 ? "" : "s")}  ·  {text.Length} characters";
    }

    // ------------------------------------------------------------ saving

    private void Flush()
    {
        if (!_dirty || _current == null) return;
        try
        {
            var text = Editor.Text ?? "";
            File.WriteAllText(_current.FilePath, text);
            _current.Text = text;
            _current.Modified = DateTime.Now;
            _dirty = false;
            StatusLabel.Text = "Saved";
        }
        catch (Exception ex) { Notifier.Show("Notes", "Could not save note: " + ex.Message, "notes"); }
    }

    private static HashSet<string> LoadPins()
    {
        try { return File.Exists(PinsFile) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(PinsFile)) ?? new() : new(); }
        catch { return new(); }
    }

    private void SavePins()
    {
        try { File.WriteAllText(PinsFile, JsonSerializer.Serialize(_all.Where(n => n.Pinned).Select(n => Path.GetFileName(n.FilePath)).ToList())); }
        catch { /* not critical */ }
    }

    // ------------------------------------------------------------ actions

    private NoteItem CreateNote()
    {
        var path = Path.Combine(NotesDir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "");
        var note = new NoteItem { FilePath = path };
        _all.Add(note);
        return note;
    }

    private void OnNew(object? s, RoutedEventArgs e)
    {
        Flush();
        SearchBox.Text = "";
        var note = CreateNote();
        RefreshList(note);
        LoadEditor();
        Editor.Focus();
    }

    private void OnPin(object? s, RoutedEventArgs e)
    {
        if (_current == null) return;
        _current.Pinned = !_current.Pinned;
        SavePins();
        RefreshList();
    }

    private void OnDelete(object? s, RoutedEventArgs e)
    {
        if (_current == null) return;
        Flush();
        CommitDelete();                                   // an earlier pending delete becomes final

        _pendingDelete = _current;
        _all.Remove(_current);
        _current = null;
        if (_all.Count == 0) CreateNote();
        RefreshList(_all.OrderByDescending(n => n.Modified).First());
        LoadEditor();

        UndoBar.IsVisible = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void OnUndo(object? s, RoutedEventArgs e)
    {
        if (_pendingDelete == null) return;
        _undoTimer.Stop();
        var back = _pendingDelete;
        _pendingDelete = null;
        _all.Add(back);
        UndoBar.IsVisible = false;
        RefreshList(back);
        LoadEditor();
    }

    /// <summary>The note file is only removed once the undo bar has gone away.</summary>
    private void CommitDelete()
    {
        _undoTimer.Stop();
        UndoBar.IsVisible = false;
        if (_pendingDelete == null) return;
        try { File.Delete(_pendingDelete.FilePath); } catch { /* already gone */ }
        _pendingDelete = null;
        SavePins();
    }
}
