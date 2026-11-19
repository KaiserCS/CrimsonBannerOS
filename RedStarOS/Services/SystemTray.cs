// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RedStarOS.Services.DBus;

namespace RedStarOS.Services;

/// <summary>
/// Other programs' tray icons (Discord, Steam, update notifiers…) on the taskbar, next to Red Star's own: the
/// <see cref="StatusNotifierHost"/> finds them on D-Bus, this turns them into <see cref="TrayEntry"/>s.
/// </summary>
public static class SystemTray
{
    public const string KeyPrefix = "sni:";
    private static StatusNotifierHost? _host;
    private static readonly Dictionary<string, (string Look, IImage? Image)> _icons = new();

    public static bool Running => _host != null;
    public static bool IsWatcher => _host?.IsWatcher == true;

    public static async void Start()
    {
        if (_host != null || DBusConnection.SessionAddress() == null) return;
        try
        {
            var bus = await DBusConnection.ConnectAsync();
            var host = new StatusNotifierHost(bus);
            host.Changed += i => { if (i != null) Dispatcher.UIThread.Post(() => Publish(host, i)); };
            host.Removed += k => Dispatcher.UIThread.Post(() => { TrayService.Remove(KeyPrefix + k); _icons.Remove(k); });
            bus.Closed += () => Dispatcher.UIThread.Post(() =>
            {
                foreach (var e in TrayService.Entries.Where(e => e.Key.StartsWith(KeyPrefix)).ToList()) TrayService.Remove(e.Key);
                _host = null;
            });
            _host = host;
            await host.StartAsync();
            Console.Error.WriteLine(host.IsWatcher ? "tray: showing other programs' icons (Red Star is the watcher)"
                                                   : "tray: showing other programs' icons (joined the running watcher)");
        }
        catch (Exception ex)
        {
            _host = null;
            Console.Error.WriteLine("tray: other programs' icons unavailable: " + ex.Message);
        }
    }

    private static void Publish(StatusNotifierHost host, SniItem item)
    {
        var key = KeyPrefix + item.Key;
        if (item.Hidden) { TrayService.Remove(key); return; }
        var entry = new TrayEntry
        {
            Key = key,
            Tooltip = item.Tooltip,
            Icon = Icon(item),
            Glyph = "apps",
            Badge = item.NeedsAttention ? 1 : 0,
            MenuOnClick = item.ItemIsMenu,
            Click = () => _ = ClickAsync(host, item),
            MiddleClick = () => _ = host.SecondaryActivateAsync(item),
            Scroll = d => _ = host.ScrollAsync(item, d),
        };
        if (item.MenuPath.Length > 0)
        {
            entry.MenuProvider = async () => (await host.GetMenuAsync(item))?.Select(ToEntry).ToList();
            entry.MenuSelected = id => { if (int.TryParse(id, out var n)) _ = host.MenuClickedAsync(item, n); };
        }
        else entry.ContextClick = () => _ = host.ContextMenuAsync(item);
        TrayService.Upsert(entry);
    }

    private static async Task ClickAsync(StatusNotifierHost host, SniItem item)
    {
        // a program without an Activate action wants its menu on a left click; the taskbar handles MenuOnClick
        if (!await host.ActivateAsync(item) && item.MenuPath.Length == 0) await host.ContextMenuAsync(item);
    }

    private static TrayMenuEntry ToEntry(SniMenuItem m) => new()
    {
        Id = m.Id.ToString(),
        Label = m.Label,
        Separator = m.Separator,
        Enabled = m.Enabled,
        Checked = m.Toggle.Length > 0 ? m.Checked : null,
        Children = m.Children.Count > 0 ? m.Children.Select(ToEntry).ToList() : null,
    };

    // ── icons ──

    /// <summary>The icon: by name from the program's own folder or the icon themes, else the pixels it sent.</summary>
    private static IImage? Icon(SniItem item)
    {
        var name = item.NeedsAttention && item.AttentionIconName.Length > 0 ? item.AttentionIconName : item.IconName;
        var pix = item.NeedsAttention && item.AttentionPixmap != null ? item.AttentionPixmap : item.Pixmap;
        var look = $"{item.IconThemePath}|{name}|{(pix == null ? 0 : pix.Width + "x" + pix.Height + ":" + Checksum(pix.Argb))}";
        if (_icons.TryGetValue(item.Key, out var c) && c.Look == look) return c.Image;   // same icon: no taskbar rebuild

        IImage? img = null;
        if (name.Length > 0 && IconFile(item.IconThemePath, name) is { } file)
            try { img = new Bitmap(file); } catch { }
        if (img == null && pix != null) img = ToBitmap(pix);
        _icons[item.Key] = (look, img);
        return img;
    }

    private static int Checksum(byte[] b)
    {
        unchecked
        {
            int h = 17;
            for (int i = 0; i < b.Length; i += 7) h = h * 31 + b[i];
            return h ^ b.Length;
        }
    }

    /// <summary>A PNG for a tray icon name: an absolute path, the program's IconThemePath, then the icon themes.</summary>
    public static string? IconFile(string themePath, string name)
    {
        if (Path.IsPathRooted(name)) return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(name) ? name : null;
        if (themePath.Length > 0 && Directory.Exists(themePath))
        {
            var direct = Path.Combine(themePath, name + ".png");
            if (File.Exists(direct)) return direct;
            try
            {
                var found = Directory.EnumerateFiles(themePath, name + ".png", SearchOption.AllDirectories).ToList();
                if (found.Count > 0) return found.OrderBy(f => SizeScore(f)).First();
            }
            catch { }
        }
        return DesktopEntries.IconPath(name, 22, "status,apps,panel,devices,actions");
    }

    /// <summary>How far a theme folder's size ("…/24x24/…") is from tray size: lower is better.</summary>
    private static int SizeScore(string file)
    {
        foreach (var part in file.Split('/'))
            if (part.Split('x') is [var w, var h] && int.TryParse(w, out var n) && w == h) return n >= 22 ? n - 22 : 1000 - n;
        return 500;
    }

    /// <summary>ARGB32 in network byte order (StatusNotifierItem) to premultiplied BGRA (Avalonia).</summary>
    public static byte[] ToBgraPremultiplied(SniPixmap p)
    {
        var dst = new byte[p.Width * p.Height * 4];
        for (int i = 0; i < p.Width * p.Height; i++)
        {
            int s = i * 4;
            byte a = p.Argb[s], r = p.Argb[s + 1], g = p.Argb[s + 2], b = p.Argb[s + 3];
            dst[s] = (byte)(b * a / 255);
            dst[s + 1] = (byte)(g * a / 255);
            dst[s + 2] = (byte)(r * a / 255);
            dst[s + 3] = a;
        }
        return dst;
    }

    private static WriteableBitmap? ToBitmap(SniPixmap p)
    {
        try
        {
            var px = ToBgraPremultiplied(p);
            var bmp = new WriteableBitmap(new PixelSize(p.Width, p.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var fb = bmp.Lock();
            int row = p.Width * 4;
            for (int y = 0; y < p.Height; y++)
                Marshal.Copy(px, y * row, fb.Address + y * fb.RowBytes, row);
            return bmp;
        }
        catch { return null; }
    }
}
