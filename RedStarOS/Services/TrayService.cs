using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;

namespace RedStarOS.Services;

public sealed class TrayMenuEntry
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Separator { get; init; }
}

/// <summary>An icon in the app tray (the "notification area" left of the system tray).</summary>
public sealed class TrayEntry
{
    public string Key { get; init; } = "";
    public string Tooltip { get; set; } = "";
    public string? Glyph { get; set; }
    public IImage? Icon { get; set; }
    public int Badge { get; set; }
    public List<TrayMenuEntry> Menu { get; set; } = new();

    /// <summary>Primary click when there is neither a panel nor a menu.</summary>
    public Action? Click { get; set; }
    public Action<string>? MenuSelected { get; set; }

    /// <summary>Builds a panel shown in a flyout on click (used by the notification centre).</summary>
    public Func<Control>? PanelFactory { get; set; }
}

/// <summary>
/// Registry of app-tray icons. Built-in shell items and apps (Electron apps through the
/// shell bridge) add and remove their icons here; the taskbar renders them.
/// </summary>
public static class TrayService
{
    private static readonly List<TrayEntry> _entries = new();

    public static IReadOnlyList<TrayEntry> Entries => _entries;
    public static event Action? Changed;

    public static void Upsert(TrayEntry entry)
    {
        var i = _entries.FindIndex(e => e.Key == entry.Key);
        if (i >= 0) _entries[i] = entry;
        else _entries.Add(entry);
        Changed?.Invoke();
    }

    public static void Remove(string key)
    {
        if (_entries.RemoveAll(e => e.Key == key) > 0) Changed?.Invoke();
    }

    public static TrayEntry? Find(string key) => _entries.FirstOrDefault(e => e.Key == key);
}
