using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Open / Save / folder dialogs for every Red Star app, built like Files: the same sidebar (Home, Recent, Starred, the
/// standard folders, your pinned folders, drives and connected servers), the same icons and path bar, and they open
/// in the folder that was last open in Files or in another dialog — so "where am I" is the same everywhere.
/// The calls mirror Avalonia's StorageProvider pickers (same option objects), and the results have the members the
/// apps use (<see cref="ChosenItem.TryGetLocalPath"/>, <see cref="ChosenItem.Name"/>, Open…Async).
/// </summary>
public static class FileDialogs
{
    /// <summary>A file or folder picked in a dialog.</summary>
    public sealed class ChosenItem
    {
        public ChosenItem(string path) => LocalPath = path;
        public string LocalPath { get; }
        public string Name => System.IO.Path.GetFileName(LocalPath.TrimEnd('/'));
        public string? TryGetLocalPath() => LocalPath;
        public Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(File.OpenRead(LocalPath));
        public Task<Stream> OpenWriteAsync() => Task.FromResult<Stream>(new FileStream(LocalPath, FileMode.Create, FileAccess.Write, FileShare.None));
    }

    /// <summary>Notes the folder that's open (in Files or a dialog), so the next dialog anywhere starts there.</summary>
    public static void Remember(string folder)
    {
        try
        {
            if (Places.IsVirtual(folder) || !Directory.Exists(folder)) return;
            if (folder.TrimEnd('/') == FileOps.TrashFolder.TrimEnd('/') || Archives.SourceOf(folder) != null) return;
            if (SettingsStore.Current.LastFolder == folder) return;
            SettingsStore.Current.LastFolder = folder;
            SettingsStore.Save();
        }
        catch { }
    }

    /// <summary>Where a dialog opens: the folder the app suggests, else the last folder used in Files, else Home.</summary>
    public static string StartFolder(string? suggested)
    {
        if (!string.IsNullOrEmpty(suggested) && Directory.Exists(suggested)) return suggested;
        if (!string.IsNullOrEmpty(suggested) && File.Exists(suggested)) return Path.GetDirectoryName(suggested)!;
        var last = SettingsStore.Current.LastFolder;
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;
        return AppPaths.Home;
    }

    private static string? PathOf(IStorageFolder? f)
    {
        try { return f?.TryGetLocalPath(); } catch { return null; }
    }

    private static List<FileChooserWindow.Filter> Filters(IReadOnlyList<FilePickerFileType>? types)
    {
        var list = new List<FileChooserWindow.Filter>();
        if (types != null)
            foreach (var t in types)
            {
                var pats = t.Patterns?.ToList() ?? new List<string>();
                if (pats.Count == 0 && t.MimeTypes is { Count: > 0 } mimes && mimes.Any(m => m.StartsWith("image/"))) pats.AddRange(FileOps.ImageExtensions.Select(e => "*" + e));
                if (pats.Count == 0) pats.Add("*");
                list.Add(new FileChooserWindow.Filter(t.Name, pats.ToArray()));
            }
        if (list.Count == 0) list.Add(new FileChooserWindow.Filter("All files", new[] { "*" }));
        return list;
    }

    // ─────────── the calls the apps make ───────────
    public static async Task<List<ChosenItem>> Open(Window owner, FilePickerOpenOptions o)
    {
        var w = new FileChooserWindow(FileChooserWindow.Mode.Open, o.Title ?? "Open", StartFolder(PathOf(o.SuggestedStartLocation)), Filters(o.FileTypeFilter))
        {
            AllowMultiple = o.AllowMultiple,
        };
        await w.ShowDialog(owner);
        foreach (var p in w.Result) RecentDocs.Add(p);
        return w.Result.Select(p => new ChosenItem(p)).ToList();
    }

    public static async Task<ChosenItem?> Save(Window owner, FilePickerSaveOptions o)
    {
        var w = new FileChooserWindow(FileChooserWindow.Mode.Save, o.Title ?? "Save", StartFolder(PathOf(o.SuggestedStartLocation)), Filters(o.FileTypeChoices))
        {
            SuggestedName = o.SuggestedFileName ?? "",
            DefaultExtension = o.DefaultExtension,
            ConfirmOverwrite = o.ShowOverwritePrompt ?? true,
        };
        await w.ShowDialog(owner);
        return w.Result.Count > 0 ? new ChosenItem(w.Result[0]) : null;
    }

    public static async Task<List<ChosenItem>> OpenFolder(Window owner, FolderPickerOpenOptions o)
    {
        var w = new FileChooserWindow(FileChooserWindow.Mode.Folder, o.Title ?? "Choose a folder", StartFolder(PathOf(o.SuggestedStartLocation)), new List<FileChooserWindow.Filter>());
        await w.ShowDialog(owner);
        return w.Result.Select(p => new ChosenItem(p)).ToList();
    }
}
