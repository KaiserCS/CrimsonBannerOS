using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Files, part two: archives (browse, extract, compress), Open with, drag and drop in and out with rubber-band
/// selection, network servers and cloud drives, Recent and Starred, the details panel (F9) and batch rename.
/// </summary>
public partial class FileManagerWindow
{
    /// <summary>Drag / clipboard data between Red Star windows: the paths, one per line.</summary>
    private static readonly DataFormat<string> InternalFormat = DataFormat.CreateStringApplicationFormat("redstar-paths");
    /// <summary>The standard list of file URLs every Linux program understands.</summary>
    private static readonly DataFormat<byte[]> UriListFormat = DataFormat.CreateBytesPlatformFormat("text/uri-list");
    /// <summary>GNOME / Nautilus / Thunar / Dolphin: "copy" or "cut", then the file URLs.</summary>
    private static readonly DataFormat<byte[]> GnomeFilesFormat = DataFormat.CreateBytesPlatformFormat("x-special/gnome-copied-files");

    private readonly CancellationTokenSource _bgCts = new();
    private readonly DispatcherTimer _detailsSoon = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private CancellationTokenSource? _detailsCts;
    private (SortKey Key, bool Asc)? _sortBeforeRecent;
    private bool _systemClipboardMayHaveFiles;
    private List<RemoteMounts.CloudRemote> _cloud = new();

    private void InitExtras()
    {
        DetailsButton.Content = Icons.Create("panel", 18);
        Avalonia.Automation.AutomationProperties.SetName(DetailsButton, "Details panel");
        DetailsButton.Click += (_, _) => ToggleDetails();
        DetailsPanel.IsVisible = SettingsStore.Current.FilesDetails;
        _detailsSoon.Tick += (_, _) => { _detailsSoon.Stop(); UpdateDetails(); };

        ArchiveIcon.Content = Icons.Create("archive", 20);
        ArchiveExtract.Click += (_, _) => { if (Archives.SourceOf(_current) is { } a) _ = ExtractArchive(a, null, openResult: true); };
        ArchiveExtractTo.Click += (_, _) => { if (Archives.SourceOf(_current) is { } a) _ = ExtractArchivesTo(new List<string> { a }); };

        // drops onto the list: into the folder under the pointer, else into the open folder
        DragDrop.SetAllowDrop(FileList, true);
        FileList.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DropEffect(FolderUnder(e) ?? _current, e); e.Handled = true; });
        FileList.AddHandler(DragDrop.DropEvent, (_, e) => { e.Handled = true; _ = DropInto(FolderUnder(e) ?? _current, e); });

        // dragging items out, and rubber-band selection on empty space
        FileList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileList.AddHandler(PointerCaptureLostEvent, (_, _) => EndBand(), RoutingStrategies.Bubble, handledEventsToo: true);

        Activated += (_, _) => _ = CheckSystemClipboard();
        _ = RefreshCloud();
    }

    // ═════════════════════════════════ places: Recent, Starred ═════════════════════════════════
    private void NavigateVirtual(string place, bool record)
    {
        if (record && _current.Length > 0 && _current != place) { _back.Push(_current); _forward.Clear(); }
        _current = place;
        _deepResults = false;
        if (place == Places.Recent)
        {
            _sortBeforeRecent ??= (_sort, _ascending);
            _sort = SortKey.Date; _ascending = false;
        }
        Title = "Files — " + Places.Title(place);
        SearchBox.Text = "";
        SearchBox.PlaceholderText = "Search in " + Places.Title(place);
        BuildCrumbs();
        BuildSidebar();
        Reload(keepSelection: false);
        Watch();
        UpdateButtons();
        UpdateFreeSpace();
        BuildTabs();
        UpdateDetailsSoon();
    }

    /// <summary>Called by Navigate for real folders: leaving Recent puts the sort order back.</summary>
    private void RestoreSortAfterRecent()
    {
        if (_sortBeforeRecent is { } s) { (_sort, _ascending) = s; _sortBeforeRecent = null; }
    }

    private static string ShortDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return "";
        var home = AppPaths.Home.TrimEnd('/');
        return dir == home ? "Home" : dir.StartsWith(home + "/") ? "~" + dir[home.Length..] : dir;
    }

    private List<FileEntry> VirtualEntries()
    {
        var list = new List<FileEntry>();
        bool hidden = SettingsStore.Current.FilesShowHidden;
        if (_current == Places.Recent)
        {
            foreach (var (p, when) in Places.RecentFiles())
            {
                var name = Path.GetFileName(p);
                if (!hidden && name.StartsWith('.')) continue;
                long size = 0;
                try { size = new FileInfo(p).Length; } catch { }
                list.Add(Entry(p, name, false, size, when, "in " + ShortDir(Path.GetDirectoryName(p))));
            }
        }
        else
        {
            foreach (var p in Places.StarredItems())
            {
                bool dir = Directory.Exists(p);
                try
                {
                    var fi = new FileInfo(p);
                    list.Add(Entry(p, Path.GetFileName(p), dir, dir ? 0 : fi.Length, dir ? Directory.GetLastWriteTime(p) : fi.LastWriteTime));
                }
                catch { }
            }
        }
        return list;
    }

    private void SetStar(IEnumerable<FileEntry> items, bool on)
    {
        int n = 0;
        foreach (var x in items.ToList())
        {
            if (Places.IsStarred(x.FullPath) != on) Places.ToggleStar(x.FullPath);
            x.Starred = on;
            n++;
        }
        StatusLabel.Text = on ? $"Starred {(n == 1 ? "1 item" : n + " items")} — find {(n == 1 ? "it" : "them")} in Starred in the sidebar" : $"Removed the star from {(n == 1 ? "1 item" : n + " items")}";
        if (_current == Places.Starred) Reload(keepSelection: true);
        UpdateDetailsSoon();
    }

    private void SelectPath(string path)
    {
        if ((FileList.ItemsSource as List<FileEntry>)?.FirstOrDefault(x => x.FullPath == path) is { } hit)
        {
            FileList.SelectedItems?.Clear();
            FileList.SelectedItems?.Add(hit);
            FileList.ScrollIntoView(hit);
        }
    }

    private static bool IsReadOnlyFolder(string path) =>
        Places.IsVirtual(path) || path.TrimEnd('/') == FileOps.TrashFolder.TrimEnd('/') || Archives.SourceOf(path) != null;

    // ═════════════════════════════════ path bar parts ═════════════════════════════════
    private List<(string Label, string Path, string? Icon)> CrumbParts(string path)
    {
        var parts = new List<(string Label, string Path, string? Icon)>();
        path = path.TrimEnd('/');
        if (path.Length == 0) path = "/";
        var home = AppPaths.Home.TrimEnd('/');
        if (Places.IsVirtual(path)) parts.Add((Places.Title(path), path, path == Places.Recent ? "clock" : "star"));
        else if (path == FileOps.TrashFolder.TrimEnd('/')) parts.Add(("Trash", FileOps.TrashFolder, "trash"));
        else if (Archives.SourceOf(path) is { } archive && ArchiveRoot(path) is { } root)
        {
            // the archive's own folder, then the archive, then the folders inside it
            parts.AddRange(CrumbParts(Path.GetDirectoryName(archive) ?? "/"));
            parts.Add((Path.GetFileName(archive), root, "archive"));
            var acc = root;
            foreach (var seg in path.Length > root.Length ? path[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>())
            { acc += "/" + seg; parts.Add((seg, acc, null)); }
        }
        else if (RemoteMounts.Servers().FirstOrDefault(m => path == m.Path || path.StartsWith(m.Path + "/")) is { } server)
        {
            parts.Add((server.Label, server.Path, "server"));
            var acc = server.Path;
            foreach (var seg in path[server.Path.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries)) { acc += "/" + seg; parts.Add((seg, acc, null)); }
        }
        else if (path == home || path.StartsWith(home + "/"))
        {
            parts.Add(("Home", home, "home"));
            var acc = home;
            foreach (var seg in path[home.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries)) { acc += "/" + seg; parts.Add((seg, acc, null)); }
        }
        else
        {
            parts.Add(("File System", "/", "drive"));
            var acc = "";
            foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) { acc += "/" + seg; parts.Add((seg, acc, null)); }
        }
        return parts;
    }

    // ═════════════════════════════════ archives ═════════════════════════════════
    /// <summary>The top folder of an archive's browse copy that <paramref name="path"/> is in.</summary>
    private static string? ArchiveRoot(string path)
    {
        var cache = Archives.CacheDir.TrimEnd('/') + "/";
        if (!path.StartsWith(cache)) return null;
        var segs = path[cache.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length >= 2 ? cache + segs[0] + "/" + segs[1] : null;
    }

    /// <summary>At the top of an archive, "up" goes to the folder the archive is in.</summary>
    private string? ArchiveParent() =>
        Archives.SourceOf(_current) is { } a && ArchiveRoot(_current) == _current.TrimEnd('/') ? Path.GetDirectoryName(a) : null;

    private void UpdateArchiveBar()
    {
        var src = Archives.SourceOf(_current);
        ArchiveBar.IsVisible = src != null;
        if (src != null)
            ArchiveText.Text = $"Inside “{Path.GetFileName(src)}”. This is a read-only view — open, preview or copy files out, or extract the archive to change them.";
    }

    /// <summary>Runs a longer job with the progress strip (shown after a moment) and a Cancel button.</summary>
    private async Task<T> RunOp<T>(string text, Func<CancellationToken, Task<T>> job)
    {
        if (_busy) throw new IOException("Another job is still running — wait for it to finish.");
        _busy = true;
        var cts = _opCts = new CancellationTokenSource();
        OpText.Text = text;
        OpBar.IsIndeterminate = true;
        var show = DispatcherTimer.RunOnce(() => OpPanel.IsVisible = true, TimeSpan.FromMilliseconds(300));
        try { return await job(cts.Token); }
        finally
        {
            show.Dispose();
            OpPanel.IsVisible = false;
            OpBar.IsIndeterminate = false;
            _busy = false;
        }
    }

    private async Task BrowseArchive(string archive)
    {
        if (Archives.MissingTool(archive) is { } why) { await Dialogs.ShowMessage(this, "Open archive", why); return; }
        try
        {
            var dir = await RunOp($"Opening “{Path.GetFileName(archive)}”…", ct => Archives.BrowseCopy(archive, ct));
            RecentDocs.Add(archive);
            Navigate(dir, record: true);
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Stopped opening the archive."; }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Open archive", $"“{Path.GetFileName(archive)}” can't be opened: {ex.Message}"); }
    }

    private Task ExtractArchive(string archive, string? folder, bool openResult = false) =>
        ExtractArchives(new List<string> { archive }, folder, openResult);

    private async Task ExtractArchives(List<string> archives, string? folder, bool openResult = false)
    {
        var made = new List<string>();
        var errors = new List<string>();
        foreach (var a in archives)
        {
            try { made.Add(await RunOp($"Extracting “{Path.GetFileName(a)}”…", ct => Archives.Extract(a, folder, ct))); }
            catch (OperationCanceledException) { StatusLabel.Text = "Extracting stopped — nothing half-done was left behind."; break; }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(a)}: {ex.Message}"); }
        }
        if (made.Count > 0)
        {
            var list = made.ToList();
            FileTransfer.Record(list.Count == 1 ? $"Extract “{Path.GetFileName(list[0])}”" : $"Extract {list.Count} archives",
                () => { foreach (var m in list) if (File.Exists(m) || Directory.Exists(m)) FileOps.MoveToTrash(m); });
            if (openResult)
            {
                var first = made[0];
                Navigate(Path.GetDirectoryName(first)!, record: true);
                SelectPath(first);
            }
            else
            {
                Reload(keepSelection: false);
                foreach (var m in made) if ((FileList.ItemsSource as List<FileEntry>)?.FirstOrDefault(x => x.FullPath == m) is { } e) FileList.SelectedItems?.Add(e);
            }
            StatusLabel.Text = made.Count == 1 ? $"Extracted to “{Path.GetFileName(made[0])}”" : $"Extracted {made.Count} archives";
        }
        if (errors.Count > 0) await Dialogs.ShowMessage(this, "Extract", string.Join("\n", errors.Take(6)));
    }

    private async Task ExtractArchivesTo(List<string> archives)
    {
        var start = await StorageProvider.TryGetFolderFromPathAsync(Directory.Exists(_current) && !IsReadOnlyFolder(_current) ? _current : AppPaths.Home);
        var picked = await FileDialogs.OpenFolder(this, new FolderPickerOpenOptions { Title = "Extract to", AllowMultiple = false, SuggestedStartLocation = start });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
        await ExtractArchives(archives, folder);
    }

    /// <summary>"photos.zip", or "photos (2).zip" when that's taken.</summary>
    private static string UniqueArchivePath(string dir, string name)
    {
        var target = Path.Combine(dir, name);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        var ext = name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (int i = 2; ; i++)
        {
            var c = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(c) && !Directory.Exists(c)) return c;
        }
    }

    private async Task Compress(List<FileEntry> sel, string name)
    {
        if (sel.Count == 0 || ReadOnlyPlace) return;
        var paths = sel.Select(x => x.FullPath).ToList();
        var target = UniqueArchivePath(_current, name);
        try
        {
            await RunOp($"Compressing {(paths.Count == 1 ? "“" + sel[0].Name + "”" : paths.Count + " items")} to {Path.GetFileName(target)}…",
                async ct => { await Archives.Compress(paths, target, ct); return 0; });
            FileTransfer.Record($"Compress to “{Path.GetFileName(target)}”", () => { if (File.Exists(target)) FileOps.MoveToTrash(target); });
            Reload(keepSelection: false);
            SelectPath(target);
            StatusLabel.Text = $"Made “{Path.GetFileName(target)}” · {FileOps.FormatSize(new FileInfo(target).Length)}";
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Compressing stopped."; }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Compress", ex.Message); }
    }

    // ═════════════════════════════════ Open with ═════════════════════════════════
    private MenuItem OpenWithMenu(List<FileEntry> files)
    {
        var paths = files.Select(f => f.FullPath).ToList();
        var menu = new MenuItem { Header = "Open with" };
        var first = paths[0];
        if (FileOpener.RedStarAppFor(first) is { } rs && paths.All(p => FileOpener.RedStarAppFor(p) == rs))
        {
            menu.Items.Add(Item(rs, "star-filled", () => { foreach (var p in paths.Take(10)) { RecentDocs.Add(p); FileOpener.OpenInRedStar(p); } }));
        }
        var apps = DesktopEntries.AppsFor(first).Take(8).ToList();
        if (apps.Count > 0 && menu.Items.Count > 0) menu.Items.Add(new Separator());
        foreach (var app in apps)
        {
            var mi = new MenuItem { Header = app.Name, Icon = OpenWithDialog.AppIcon(app, 16) };
            var a = app;
            mi.Click += (_, _) => { foreach (var p in paths) RecentDocs.Add(p); DesktopEntries.LaunchWith(a, paths); };
            menu.Items.Add(mi);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Item("Other application…", "apps", async () =>
        {
            if (await OpenWithDialog.Show(this, first) is { } choice) OpenWithDialog.Run(choice, paths);
        }));
        return menu;
    }

    // ═════════════════════════════════ system clipboard (copy / paste with other apps) ═════════════════════════════════
    private async Task WriteSystemClipboard(List<string> paths, bool cut)
    {
        try
        {
            if (Clipboard is not { } cb) return;
            var uris = paths.Select(p => new Uri(p).AbsoluteUri).ToList();
            var item = new DataTransferItem();
            item.Set(DataFormat.Text, string.Join("\n", paths));
            item.Set(UriListFormat, Encoding.UTF8.GetBytes(string.Join("\r\n", uris) + "\r\n"));
            // GNOME / Nautilus / Thunar / Dolphin understand this one, including cut
            item.Set(GnomeFilesFormat, Encoding.UTF8.GetBytes((cut ? "cut" : "copy") + "\n" + string.Join("\n", uris)));
            item.Set(InternalFormat, string.Join("\n", paths));
            var data = new DataTransfer();
            data.Add(item);
            await cb.SetDataAsync(data);
        }
        catch { /* the in-app clipboard still works */ }
    }

    private async Task CheckSystemClipboard()
    {
        try
        {
            if (Clipboard is not { } cb) return;
            var formats = await cb.GetDataFormatsAsync();
            _systemClipboardMayHaveFiles = formats.Any(f => f == UriListFormat || f == GnomeFilesFormat || f == DataFormat.File);
        }
        catch { _systemClipboardMayHaveFiles = false; }
    }

    /// <summary>Files copied in another program (Nautilus, Dolphin, a browser's downloads…) become pasteable here.</summary>
    private async Task ReadSystemClipboard()
    {
        try
        {
            if (Clipboard is not { } cb) return;
            var formats = await cb.GetDataFormatsAsync();
            bool cut = false;
            string? text = null;
            if (formats.Contains(GnomeFilesFormat))
            {
                text = AsText(await cb.TryGetValueAsync(GnomeFilesFormat));
                if (text != null)
                {
                    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    cut = lines.FirstOrDefault()?.Trim() == "cut";
                    text = string.Join("\n", lines.Skip(1));
                }
            }
            if (text == null && formats.Contains(UriListFormat)) text = AsText(await cb.TryGetValueAsync(UriListFormat));
            var paths = PathsFromUris(text);
            if (paths.Count == 0 && await cb.TryGetFilesAsync() is { Length: > 0 } items)     // Avalonia's own file format
                paths = items.Select(i => i.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
            if (paths.Count == 0) return;
            FileClipboard.Paths = paths;
            FileClipboard.IsCut = cut;
        }
        catch { }
    }

    private static string? AsText(object? o) => o switch
    {
        string s => s,
        byte[] b => Encoding.UTF8.GetString(b),
        _ => null
    };

    private static List<string> PathsFromUris(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim().TrimEnd('\0');
            if (l.Length == 0 || l.StartsWith('#')) continue;
            try
            {
                if (l.StartsWith("file://")) { var p = new Uri(l).LocalPath; if (File.Exists(p) || Directory.Exists(p)) list.Add(p); }
                else if (l.StartsWith('/') && (File.Exists(l) || Directory.Exists(l))) list.Add(l);
            }
            catch { }
        }
        return list;
    }

    // ═════════════════════════════════ drag and drop ═════════════════════════════════
    /// <summary>Local paths in a drag from Files, another Red Star window or another program.</summary>
    private static List<string> PathsFrom(DragEventArgs e)
    {
        try
        {
            var dt = e.DataTransfer;
            if (dt.Contains(InternalFormat) && dt.TryGetValue(InternalFormat) is { } own)
                return own.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (dt.TryGetFiles() is { Length: > 0 } items)
            {
                var files = items.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
                if (files.Count > 0) return files;
            }
            if (dt.Contains(UriListFormat)) return PathsFromUris(AsText(dt.TryGetValue(UriListFormat)));
        }
        catch { }
        return new List<string>();
    }

    /// <summary>The folder item under the pointer during a drag (not one of the dragged items).</summary>
    private string? FolderUnder(DragEventArgs e)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true);
        if (item?.DataContext is FileEntry { IsDir: true } fe && !InTrash && !_dragging.Contains(fe.FullPath)) return fe.FullPath;
        return null;
    }

    private DragDropEffects DropEffect(string target, DragEventArgs e)
    {
        var paths = PathsFrom(e);
        if (paths.Count == 0) return DragDropEffects.None;
        if (target == Places.Starred) return DragDropEffects.Link;
        if (target.TrimEnd('/') == FileOps.TrashFolder.TrimEnd('/'))
            return paths.All(p => !p.StartsWith(FileOps.TrashFolder) && Archives.SourceOf(p) == null) ? DragDropEffects.Move : DragDropEffects.None;
        if (IsReadOnlyFolder(target) || !Directory.Exists(target)) return DragDropEffects.None;
        if (paths.All(p => Path.GetDirectoryName(p.TrimEnd('/'))?.TrimEnd('/') == target.TrimEnd('/'))) return DragDropEffects.None;   // already here
        if (paths.Any(p => Directory.Exists(p) && (target + "/").StartsWith(p.TrimEnd('/') + "/"))) return DragDropEffects.None;     // into itself
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) return DragDropEffects.Copy;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return DragDropEffects.Move;
        // like other file managers: move on the same drive, copy to another one (and always copy out of an archive)
        if (paths.Any(p => Archives.SourceOf(p) != null)) return DragDropEffects.Copy;
        var dest = MountFor(target);
        return paths.All(p => MountFor(p) == dest) ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private async Task DropInto(string folder, DragEventArgs e)
    {
        var effect = DropEffect(folder, e);
        var paths = PathsFrom(e);
        if (effect == DragDropEffects.None || paths.Count == 0) return;
        if (folder == Places.Starred)
        {
            foreach (var p in paths) if (!Places.IsStarred(p)) Places.ToggleStar(p);
            StatusLabel.Text = paths.Count == 1 ? $"Starred “{Path.GetFileName(paths[0])}”" : $"Starred {paths.Count} items";
            Reload(keepSelection: true);
            return;
        }
        if (folder.TrimEnd('/') == FileOps.TrashFolder.TrimEnd('/')) { await TrashPaths(paths); return; }
        await Transfer(paths, folder, move: effect == DragDropEffects.Move);
    }

    private async Task TrashPaths(List<string> paths)
    {
        var trashed = new List<string>();
        var errors = new List<string>();
        foreach (var p in paths)
            try { trashed.Add(FileOps.MoveToTrash(p)); }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(p)}: {ex.Message}"); }
        if (trashed.Count > 0)
            FileTransfer.Record((trashed.Count == 1 ? $"Move “{Path.GetFileName(paths[0])}”" : $"Move {trashed.Count} items") + " to the Trash",
                () => { foreach (var t in trashed) try { FileOps.RestoreFromTrash(t); } catch { } });
        Reload(keepSelection: false);
        BuildSidebar();
        if (errors.Count > 0) await Dialogs.ShowMessage(this, "Move to Trash", string.Join("\n", errors.Take(5)));
    }

    // ── dragging items out of the list ──
    private FileEntry? _dragCandidate;
    private FileEntry? _pendingSingleSelect;
    private Point _pressAt;
    private PointerPressedEventArgs? _pressEvent;          // a drag must start from the press that began it
    private HashSet<string> _dragging = new();

    // ── rubber band ──
    private bool _bandArmed, _bandActive;
    private Point _bandStartContent;           // in list content coordinates (survives scrolling)
    private HashSet<FileEntry> _bandBase = new();
    private ScrollViewer? _listScroll;

    private ScrollViewer? ListScroll => _listScroll ??= FileList.FindDescendantOfType<ScrollViewer>();

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(FileList).Properties;
        if (!props.IsLeftButtonPressed) return;
        var src = e.Source as Visual;
        if (src?.FindAncestorOfType<ScrollBar>(true) != null) return;
        _pressAt = e.GetPosition(FileList);
        _pressEvent = e;
        var item = src?.FindAncestorOfType<ListBoxItem>(true);
        if (item?.DataContext is FileEntry fe)
        {
            _dragCandidate = fe;
            // pressing on one of several selected items: keep the selection so all of them can be dragged
            if (e.KeyModifiers == KeyModifiers.None && e.ClickCount == 1 && FileList.SelectedItems?.Contains(fe) == true && (FileList.SelectedItems?.Count ?? 0) > 1)
            {
                _pendingSingleSelect = fe;
                e.Handled = true;
            }
            return;
        }
        // empty space: start a rubber band (Ctrl adds to the selection)
        if (e.ClickCount > 1) return;
        _bandArmed = true;
        _bandActive = false;
        var off = ListScroll?.Offset ?? default;
        _bandStartContent = new Point(_pressAt.X + off.X, _pressAt.Y + off.Y);
        _bandBase = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? Selected().ToHashSet() : new HashSet<FileEntry>();
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) FileList.SelectedItems?.Clear();
        e.Pointer.Capture(FileList);
        FileList.Focus();
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(FileList);
        var left = e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed;
        if (!left) { _dragCandidate = null; if (_bandArmed) EndBand(); return; }

        if (_dragCandidate is { } cand)
        {
            if (Math.Abs(pos.X - _pressAt.X) < 6 && Math.Abs(pos.Y - _pressAt.Y) < 6) return;
            _dragCandidate = null;
            _pendingSingleSelect = null;
            if (InTrash) return;
            var sel = Selected();
            var entries = sel.Contains(cand) ? sel : new List<FileEntry> { cand };
            if (_pressEvent is { } press) await StartDrag(press, entries);
            return;
        }

        if (!_bandArmed) return;
        if (!_bandActive && Math.Abs(pos.X - _pressAt.X) < 4 && Math.Abs(pos.Y - _pressAt.Y) < 4) return;
        _bandActive = true;

        // scroll when the band reaches the top or bottom edge
        if (ListScroll is { } sv)
        {
            double h = FileList.Bounds.Height;
            if (pos.Y > h - 12) sv.Offset = new Vector(sv.Offset.X, Math.Min(sv.Extent.Height - sv.Viewport.Height, sv.Offset.Y + 24));
            else if (pos.Y < 12) sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y - 24));
        }
        var offset = ListScroll?.Offset ?? default;
        var start = new Point(_bandStartContent.X - offset.X, _bandStartContent.Y - offset.Y);
        var clamped = new Point(Math.Clamp(pos.X, 0, FileList.Bounds.Width), Math.Clamp(pos.Y, 0, FileList.Bounds.Height));
        var rect = new Rect(start, clamped).Normalize();

        Band.IsVisible = true;
        var shown = rect.Intersect(new Rect(FileList.Bounds.Size));
        Canvas.SetLeft(Band, shown.X); Canvas.SetTop(Band, shown.Y);
        Band.Width = Math.Max(1, shown.Width); Band.Height = Math.Max(1, shown.Height);

        // what's inside: the items on screen that touch the band, plus what was selected before (Ctrl)
        var want = new HashSet<FileEntry>(_bandBase);
        foreach (var c in FileList.GetRealizedContainers())
        {
            if (c.DataContext is not FileEntry fe || !c.IsVisible) continue;
            var tl = c.TranslatePoint(new Point(0, 0), FileList);
            if (tl == null) continue;
            if (new Rect(tl.Value, c.Bounds.Size).Intersects(rect)) want.Add(fe);
        }
        // items scrolled out of view keep their state while the band still covers them
        var current = Selected().ToHashSet();
        if (FileList.SelectedItems is { } si)
        {
            foreach (var x in current.Where(x => !want.Contains(x) && IsOnScreen(x))) si.Remove(x);
            foreach (var x in want.Where(x => !current.Contains(x))) si.Add(x);
        }
    }

    private bool IsOnScreen(FileEntry fe) => FileList.GetRealizedContainers().Any(c => c.DataContext == fe);

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pendingSingleSelect is { } single && e.InitialPressMouseButton == MouseButton.Left)
        {
            FileList.SelectedItems?.Clear();
            FileList.SelectedItems?.Add(single);
        }
        _pendingSingleSelect = null;
        _dragCandidate = null;
        if (_bandArmed)
        {
            EndBand();
            e.Pointer.Capture(null);
        }
    }

    private void EndBand()
    {
        _bandArmed = false;
        _bandActive = false;
        Band.IsVisible = false;
    }

    private async Task StartDrag(PointerPressedEventArgs press, List<FileEntry> entries)
    {
        var paths = entries.Select(x => x.FullPath).ToList();
        var first = new DataTransferItem();
        first.Set(InternalFormat, string.Join("\n", paths));
        first.Set(DataFormat.Text, string.Join("\n", paths));
        first.Set(UriListFormat, Encoding.UTF8.GetBytes(string.Join("\r\n", paths.Select(p => new Uri(p).AbsoluteUri)) + "\r\n"));
        var data = new DataTransfer();
        data.Add(first);
        var items = new List<IStorageItem>();
        foreach (var p in paths)
        {
            try
            {
                IStorageItem? si = Directory.Exists(p) ? (IStorageItem?)await StorageProvider.TryGetFolderFromPathAsync(p) : await StorageProvider.TryGetFileFromPathAsync(p);
                if (si != null) items.Add(si);
            }
            catch { }
        }
        foreach (var si in items) data.Add(DataTransferItem.CreateFile(si));   // one item per file
        _dragging = paths.ToHashSet();
        try
        {
            var allowed = InVirtual || InArchive ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
            await DragDrop.DoDragDropAsync(press, data, allowed);
        }
        catch { }
        finally { _dragging = new HashSet<string>(); }
    }

    // ═════════════════════════════════ network, cloud, eject ═════════════════════════════════
    private void AddNetworkSections()
    {
        var servers = RemoteMounts.Servers();
        if (servers.Count > 0 || RemoteMounts.HasGio || RemoteMounts.HasSshfs)
        {
            Section("NETWORK");
            foreach (var m in servers)
            {
                var mount = m;
                var btn = Nav(m.Kind == "cloud" ? "cloud" : "server", m.Label, m.Path, tip: m.Uri ?? m.Path, eject: () => _ = DisconnectServer(mount));
                var dc = new MenuItem { Header = "Disconnect", Icon = Icons.Create("eject", 14) };
                dc.Click += (_, _) => _ = DisconnectServer(mount);
                btn.ContextMenu = new ContextMenu { Items = { dc } };
            }
            Nav("plus", "Connect to server…", "", tip: "smb://, sftp://, ftp:// or dav:// (WebDAV)", click: () => _ = ConnectToServer());
        }
        if (_cloud.Count > 0)
        {
            Section("CLOUD");
            foreach (var r in _cloud)
            {
                var remote = r;
                var btn = Nav("cloud", r.Name, r.Folder, tip: r.Mounted ? r.Folder : $"{r.Name} (rclone) — click to connect",
                    eject: r.Mounted ? (Action)(() => _ = DisconnectCloud(remote)) : null, click: () => _ = OpenCloud(remote));
                if (r.Mounted)
                {
                    var dc = new MenuItem { Header = "Disconnect", Icon = Icons.Create("eject", 14) };
                    dc.Click += (_, _) => _ = DisconnectCloud(remote);
                    btn.ContextMenu = new ContextMenu { Items = { dc } };
                }
            }
        }
        _ = RefreshCloud();
    }

    private bool _cloudRefreshing;

    private async Task RefreshCloud()
    {
        if (!RemoteMounts.HasRclone || _cloudRefreshing) return;
        _cloudRefreshing = true;
        try
        {
            var now = await RemoteMounts.CloudRemotes();
            string Sig(IEnumerable<RemoteMounts.CloudRemote> l) => string.Join("|", l.Select(x => x.Name + (x.Mounted ? "+" : "")));
            if (Sig(now) != Sig(_cloud) && !_bgCts.IsCancellationRequested) { _cloud = now; BuildSidebar(); }
        }
        catch { }
        finally { _cloudRefreshing = false; }
    }

    private async Task OpenCloud(RemoteMounts.CloudRemote r)
    {
        try
        {
            var dir = r.Mounted ? r.Folder : await RunOp($"Connecting to {r.Name}…", _ => RemoteMounts.MountCloud(r));
            // rclone takes a moment to show the files after it daemonises
            for (int i = 0; i < 20 && !IsMountPoint(dir); i++) await Task.Delay(150);
            _cloud = _cloud.Select(c => c.Name == r.Name ? c with { Mounted = true } : c).ToList();
            Navigate(dir, record: true);
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, r.Name, ex.Message + "\n\nCloud drives are set up with “rclone config” in a terminal."); }
    }

    private static bool IsMountPoint(string dir)
    {
        try { return File.ReadLines("/proc/mounts").Any(l => l.Split(' ') is { Length: > 2 } f && f[1].Replace("\\040", " ") == dir); }
        catch { return false; }
    }

    private async Task DisconnectCloud(RemoteMounts.CloudRemote r)
    {
        LeaveIfInside(r.Folder);
        try
        {
            await RemoteMounts.UnmountCloud(r);
            _cloud = _cloud.Select(c => c.Name == r.Name ? c with { Mounted = false } : c).ToList();
            BuildSidebar();
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Disconnect", ex.Message); }
    }

    private async Task DisconnectServer(RemoteMounts.Mount m)
    {
        LeaveIfInside(m.Path);
        try { await RemoteMounts.Disconnect(m); }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Disconnect", ex.Message); }
        BuildSidebar();
    }

    /// <summary>Mounts a drive that isn't open yet and goes there.</summary>
    private async Task OpenVolume(DriveVolume v)
    {
        try { Navigate(await RemovableDrives.Mount(v), true); }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Open drive", "Couldn't open “" + v.Label + "”: " + ex.Message); }
        BuildSidebar();
    }

    private async Task EjectDrive(string mount, string label)
    {
        LeaveIfInside(mount);
        try
        {
            if (RemovableDrives.AtMount(mount) is { } vol) await RemovableDrives.Eject(vol);
            else await RemoteMounts.Eject(mount);
            Notifier.Show("Safe to remove", $"“{label}” can be unplugged now.", "files");
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Eject", ex.Message); }
        BuildSidebar();
    }

    /// <summary>Before a drive goes away, step out of it (and stop watching it, so it isn't busy).</summary>
    private void LeaveIfInside(string root)
    {
        if (_current == root || _current.StartsWith(root.TrimEnd('/') + "/"))
        {
            _watcher?.Dispose(); _watcher = null;
            Navigate(AppPaths.Home, record: true);
        }
    }

    private async Task ConnectToServer()
    {
        var recent = SettingsStore.Current.RecentServers;
        var address = new TextBox { PlaceholderText = "smb://server/share  ·  sftp://host  ·  ftp://host  ·  davs://host/path", Text = recent.FirstOrDefault() ?? "" };
        var user = new TextBox { PlaceholderText = "User name" };
        var pass = new TextBox { PlaceholderText = "Password", PasswordChar = '•', RevealPassword = false };
        var anon = new CheckBox { Content = "Connect as a guest (no password)" };
        var error = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#E53E3E")), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var connect = new Button { Content = "Connect", Classes = { "primary" }, MinWidth = 100, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        anon.IsCheckedChanged += (_, _) => { user.IsEnabled = pass.IsEnabled = anon.IsChecked != true; };
        var recentRow = new WrapPanel { IsVisible = recent.Count > 0 };
        foreach (var r in recent.Take(6))
        {
            var b = new Button { Content = r, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 6, 6), FontSize = 12 };
            b.Click += (_, _) => address.Text = r;
            recentRow.Children.Add(b);
        }
        string? result = null;
        var dlg = new Window
        {
            Title = "Connect to server", Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18), Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Server address", FontWeight = FontWeight.SemiBold }, address,
                    new TextBlock { Text = "Recent", Classes = { "eyebrow" }, IsVisible = recent.Count > 0, Margin = new Thickness(0, 4, 0, 0) }, recentRow,
                    user, pass, anon, error,
                    new TextBlock { Text = RemoteMounts.HasGio ? "Uses GVfs, like the GNOME file manager. The password is passed to it directly and isn't saved."
                                                               : RemoteMounts.HasSshfs ? "GVfs isn't installed: only sftp:// works (through sshfs)."
                                                               : "Needs GVfs (gvfs, gvfs-backends / gvfs-smb, gvfs-fuse) or sshfs for sftp://.",
                                    FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { connect, cancel } }
                }
            }
        };
        cancel.Click += (_, _) => dlg.Close();
        connect.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(address.Text)) return;
            connect.IsEnabled = false;
            connect.Content = "Connecting…";
            error.IsVisible = false;
            try
            {
                result = await RemoteMounts.Connect(address.Text!, user.Text, pass.Text, anon.IsChecked == true);
                recent.Remove(address.Text!.Trim());
                recent.Insert(0, address.Text!.Trim());
                if (recent.Count > 10) recent.RemoveRange(10, recent.Count - 10);
                SettingsStore.Save();
                dlg.Close();
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
                connect.IsEnabled = true;
                connect.Content = "Connect";
            }
        };
        dlg.Opened += (_, _) => { address.Focus(); address.SelectAll(); };
        await dlg.ShowDialog(this);
        pass.Text = "";
        if (result != null) { BuildSidebar(); Navigate(result, record: true); }
    }

    // ═════════════════════════════════ details panel (F9) ═════════════════════════════════
    private void ToggleDetails()
    {
        SettingsStore.Current.FilesDetails = !SettingsStore.Current.FilesDetails;
        SettingsStore.Save();
        DetailsPanel.IsVisible = SettingsStore.Current.FilesDetails;
        UpdateDetails();
    }

    private void UpdateDetailsSoon()
    {
        if (!DetailsPanel.IsVisible) return;
        _detailsSoon.Stop();
        _detailsSoon.Start();
    }

    private void UpdateDetails()
    {
        _detailsCts?.Cancel();
        DetailsBody.Children.Clear();
        if (!DetailsPanel.IsVisible) return;
        var cts = _detailsCts = new CancellationTokenSource();
        var sel = Selected();

        TextBlock Value(string key, string value)
        {
            DetailsBody.Children.Add(new TextBlock { Text = key, FontSize = 11.5, Opacity = 0.6, Margin = new Thickness(0, 6, 0, 0) });
            var v = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            DetailsBody.Children.Add(v);
            return v;
        }
        Control Big(FileEntry e)
        {
            var holder = new Border { Height = 150, CornerRadius = new CornerRadius(10), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 8) };
            holder.Child = new ContentControl
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Content = new IconConverter(null, 96).Convert(e.IconKey, typeof(object), "96", System.Globalization.CultureInfo.InvariantCulture)
            };
            return holder;
        }
        void Buttons(params (string Label, Action Act)[] buttons)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var (label, act) in buttons)
            {
                var b = new Button { Content = label, Padding = new Thickness(10, 4), Margin = new Thickness(0, 0, 6, 6) };
                b.Click += (_, _) => act();
                row.Children.Add(b);
            }
            DetailsBody.Children.Add(row);
        }

        if (sel.Count == 1)
        {
            var e = sel[0];
            var big = (Border)Big(e);
            DetailsBody.Children.Add(big);
            DetailsBody.Children.Add(new SelectableTextBlock { Text = e.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            Value("Kind", Archives.IsArchive(e.FullPath) ? "Archive — open it to look inside" : FileOps.KindOf(e.FullPath, e.IsDir));
            var size = Value("Size", e.IsDir ? "Counting…" : $"{FileOps.FormatSize(e.Size)} ({e.Size:N0} bytes)");
            var dims = e.IconKey.StartsWith("image|") ? Value("Dimensions", "…") : null;
            if (!InTrash) Value("Location", ShortDir(Path.GetDirectoryName(e.FullPath)));
            try
            {
                Value("Modified", File.GetLastWriteTime(e.FullPath).ToString("f"));
                Value("Created", File.GetCreationTime(e.FullPath).ToString("f"));
                if (!e.IsDir) Value("Last opened", File.GetLastAccessTime(e.FullPath).ToString("f"));
            }
            catch { }
            if (!e.IsDir)
            {
                Value("Type", DesktopEntries.MimeFor(e.FullPath));
                var opens = DefaultApps.KindFor(e.FullPath) is { } k && DefaultApps.Get(k.Id) is var v && v.EndsWith(".desktop")
                    ? DesktopEntries.All().FirstOrDefault(a => a.FilePath == v)?.Name
                    : FileOpener.RedStarAppFor(e.FullPath) ?? DesktopEntries.AppsFor(e.FullPath).FirstOrDefault()?.Name;
                Value("Opens with", opens ?? "the system's default program");
            }
            if (!InTrash && !InArchive)
            {
                var star = new ToggleButton
                {
                    Content = Icons.Label(e.Starred ? "star-filled" : "star", e.Starred ? "Starred" : "Star"), IsChecked = e.Starred, Padding = new Thickness(10, 4), Margin = new Thickness(0, 12, 0, 0)
                };
                star.Click += (_, _) => SetStar(new[] { e }, star.IsChecked == true);
                DetailsBody.Children.Add(star);
            }
            if (!InTrash)
                Buttons(("Open", () => OpenEntry(e)), ("Properties", () => _ = ShowProperties(e)));

            _ = Task.Run(() =>
            {
                if (e.IsDir)
                {
                    int files = 0, dirs = 0; long bytes = 0;
                    try
                    {
                        foreach (var fi in new DirectoryInfo(e.FullPath).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                        {
                            if (cts.IsCancellationRequested) return;
                            if (fi is FileInfo f) { files++; bytes += f.Length; } else dirs++;
                            if (files + dirs > 200_000) break;
                        }
                    }
                    catch { }
                    Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) size.Text = $"{FileOps.FormatSize(bytes)} — {files:N0} files, {dirs:N0} folders"; });
                }
                else if (dims != null && !e.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var fs = File.OpenRead(e.FullPath);
                        var bmp = Bitmap.DecodeToWidth(fs, 520, BitmapInterpolationMode.HighQuality);
                        // the real size, without decoding it all
                        string real;
                        try { using var fs2 = File.OpenRead(e.FullPath); using var full = new Bitmap(fs2); real = $"{full.PixelSize.Width} × {full.PixelSize.Height}"; }
                        catch { real = "unknown"; }
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (cts.IsCancellationRequested) return;
                            big.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                            dims.Text = real;
                        });
                    }
                    catch { Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) dims.Text = "unknown"; }); }
                }
            }, cts.Token);
        }
        else if (sel.Count > 1)
        {
            DetailsBody.Children.Add(new Border
            {
                Height = 120, Child = new ContentControl
                {
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8,
                    Content = new IconConverter(null, 80).Convert("copy|#A0AEC0", typeof(object), "80", System.Globalization.CultureInfo.InvariantCulture)
                }
            });
            DetailsBody.Children.Add(new TextBlock { Text = $"{sel.Count} items selected", FontSize = 16, FontWeight = FontWeight.SemiBold });
            var kinds = sel.GroupBy(x => x.IsDir ? "Folder" : FileOps.KindOf(x.FullPath, false)).OrderByDescending(g => g.Count()).ToList();
            Value("Kinds", string.Join("\n", kinds.Take(5).Select(g => $"{g.Count()} × {g.Key}")) + (kinds.Count > 5 ? $"\n… and {kinds.Count - 5} more kinds" : ""));
            var size = Value("Total size", sel.Any(x => x.IsDir) ? "Counting…" : FileOps.FormatSize(sel.Sum(x => x.Size)));
            var places = sel.Select(x => Path.GetDirectoryName(x.FullPath)).Distinct().ToList();
            if (places.Count > 1) Value("In", $"{places.Count} folders");
            if (!InTrash)
                Buttons((InArchive ? "Copy" : "Rename all…", () => { if (InArchive) ClipSelection(false); else _ = Rename(); }),
                        ("Compress to .zip", () => _ = Compress(sel, TabTitle(_current) + ".zip")));
            if (sel.Any(x => x.IsDir))
                _ = Task.Run(() =>
                {
                    long total = 0;
                    foreach (var x in sel) { if (cts.IsCancellationRequested) return; total += x.IsDir ? FileTransfer.SizeOf(x.FullPath) : x.Size; }
                    Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) size.Text = FileOps.FormatSize(total); });
                }, cts.Token);
        }
        else
        {
            // nothing selected: the open folder / place
            var title = TabTitle(_current);
            var icon = InTrash ? "trash" : _current == Places.Recent ? "clock" : _current == Places.Starred ? "star" : InArchive ? "archive" : "folder";
            DetailsBody.Children.Add(new Border
            {
                Height = 120, Child = new ContentControl
                {
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Content = new IconConverter(null, 80).Convert(icon + "|" + (icon == "folder" ? ColorFor("folder") : "#A0AEC0"), typeof(object), "80", System.Globalization.CultureInfo.InvariantCulture)
                }
            });
            DetailsBody.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            int dirs = _all.Count(x => x.IsDir), files = _all.Count - dirs;
            Value("Contains", $"{files:N0} file{(files == 1 ? "" : "s")}, {dirs:N0} folder{(dirs == 1 ? "" : "s")}");
            if (!InVirtual)
            {
                Value("Size of the files here", FileOps.FormatSize(_all.Where(x => !x.IsDir).Sum(x => x.Size)));
                if (InArchive) Value("Archive", Archives.SourceOf(_current)!);
                else Value("Location", _current);
                if (!InArchive && FreeLabel.Text is { Length: > 0 } free) Value("Drive", free);
            }
            DetailsBody.Children.Add(new TextBlock { Text = "Select something to see its details.", Opacity = 0.55, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });
        }
    }
}
