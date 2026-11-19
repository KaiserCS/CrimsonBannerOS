using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Input.Platform;

namespace RedStarOS.Services;

/// <summary>
/// Remembers the last texts copied anywhere on the desktop (Super+V or the clipboard icon in the tray).
/// Kept in memory only — nothing is written to disk — and it can be paused or cleared.
/// </summary>
public static class ClipboardHistory
{
    public const int Max = 30;
    private static readonly List<string> _items = new();
    private static readonly HashSet<string> _pinned = new();
    private static DispatcherTimer? _timer;
    private static TopLevel? _top;
    private static string? _last;
    private static bool _busy;

    public static IReadOnlyList<string> Items => _items;
    public static bool Paused { get; set; }
    public static event Action? Changed;

    public static bool IsPinned(string s) => _pinned.Contains(s);

    public static void Start(TopLevel top)
    {
        _top = top;
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _timer.Tick += async (_, _) =>
        {
            if (Paused || _busy || _top?.Clipboard == null) return;
            _busy = true;
            try
            {
                var text = await _top.Clipboard.TryGetTextAsync();
                if (!string.IsNullOrEmpty(text) && text != _last && text.Length <= 100_000 && text.Trim().Length > 0)
                {
                    _last = text;
                    Add(text);
                }
            }
            catch { }
            finally { _busy = false; }
        };
        _timer.Start();
    }

    private static void Add(string text)
    {
        _items.Remove(text);
        _items.Insert(0, text);
        while (_items.Count > Max)
        {
            int i = _items.FindLastIndex(x => !_pinned.Contains(x));
            if (i < 0) break;
            _items.RemoveAt(i);
        }
        Changed?.Invoke();
    }

    /// <summary>Puts an entry back on the clipboard (it moves to the top of the list).</summary>
    public static async void Use(string text)
    {
        if (_top?.Clipboard == null) return;
        _last = text;
        await _top.Clipboard.SetTextAsync(text);
        Add(text);
    }

    public static void TogglePin(string text) { if (!_pinned.Remove(text)) _pinned.Add(text); Changed?.Invoke(); }
    public static void Remove(string text) { _items.Remove(text); _pinned.Remove(text); Changed?.Invoke(); }
    public static void Clear() { _items.RemoveAll(x => !_pinned.Contains(x)); Changed?.Invoke(); }
}
