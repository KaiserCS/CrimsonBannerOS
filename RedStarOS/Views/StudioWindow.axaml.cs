// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>The office suite's front door: start a new document, spreadsheet or presentation, or open something recent.</summary>
public partial class StudioWindow : Window
{
    private sealed class RecentItem
    {
        public string Path { get; init; } = "";
        public Avalonia.Controls.Control? Glyph { get; init; }
        public string Name { get; init; } = "";
        public string Folder { get; init; } = "";
        public string Kind { get; init; } = "";
    }

    public StudioWindow()
    {
        InitializeComponent();
        WriterIcon.Content = AppIcons.Create("writer", "Writer", 44);
        SheetsIcon.Content = AppIcons.Create("sheets", "Sheets", 44);
        SlidesIcon.Content = AppIcons.Create("slides", "Slides", 44);
        Activated += (_, _) => RefreshRecent();
        RefreshRecent();
    }

    private void RefreshRecent()
    {
        var items = RecentDocs.List(20).Select(p => new RecentItem
        {
            Path = p,
            Glyph = Icons.Create(FileOps.IconName(p, false), 20),
            Name = System.IO.Path.GetFileName(p),
            Folder = System.IO.Path.GetDirectoryName(p) ?? "",
            Kind = RecentDocs.Describe(p) + ", " + File.GetLastWriteTime(p).ToString("d MMM yyyy")
        }).ToList();
        RecentList.ItemsSource = items;
        EmptyState.IsVisible = items.Count == 0;
        RecentList.IsVisible = items.Count > 0;
        ClearButton.IsVisible = items.Count > 0;
    }

    private void OnWriter(object? s, RoutedEventArgs e) => AppRegistry.Launch("writer");
    private void OnSlides(object? s, RoutedEventArgs e) => AppRegistry.Launch("slides");
    private void OnSheets(object? s, RoutedEventArgs e) => AppRegistry.Launch("sheets");

    private void OnRecentOpen(object? s, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem item && File.Exists(item.Path)) FileOpener.Open(item.Path);
    }

    private void OnRecentKey(object? s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnRecentOpen(s, e);
    }

    private void OnClearRecent(object? s, RoutedEventArgs e)
    {
        SettingsStore.Current.RecentFiles.Clear();
        SettingsStore.Save();
        RefreshRecent();
    }

    private async void OnOpenFile(object? s, RoutedEventArgs e)
    {
        var files = await FileDialogs.Open(this, new FilePickerOpenOptions
        {
            Title = "Open a document, spreadsheet or presentation",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Office files")
                    { Patterns = new[] { "*.docx", "*.txt", "*.md", "*.html", "*.xlsx", "*.csv", "*.tsv", "*.rsslides" } },
                FilePickerFileTypes.All
            }
        });
        var local = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (local == null) return;
        FileOpener.Open(local);
        RecentDocs.Add(local);
        RefreshRecent();
    }
}
