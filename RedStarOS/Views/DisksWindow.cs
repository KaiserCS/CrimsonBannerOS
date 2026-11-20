// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Disks: the computer's drives and their partitions; open, unmount, format and eject (DiskManager).</summary>
public sealed class DisksWindow : Window
{
    private readonly ListBox _drives = new() { Width = 260 };
    private readonly StackPanel _detail = new() { Spacing = 12, Margin = new Thickness(20) };
    private List<Disk> _disks = new();
    private DiskPart? _part;

    private static readonly Color[] PartColors = { Color.Parse("#C8102E"), Color.Parse("#2B6CB0"), Color.Parse("#2F855A"), Color.Parse("#D69E2E"), Color.Parse("#6B46C1"), Color.Parse("#2C7A7B") };

    public DisksWindow()
    {
        Title = "Disks";
        Width = 900; Height = 600; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _drives.SelectionChanged += (_, _) => { _part = null; ShowDisk(); };
        var refresh = new Button { Content = "Refresh", Margin = new Thickness(8) };
        refresh.Click += (_, _) => _ = Reload();
        var left = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Bottom);
        left.Children.Add(refresh);
        left.Children.Add(_drives);
        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(new ScrollViewer { Content = _detail });
        Content = root;
        RemovableDrives.Changed += OnChanged;
        Closed += (_, _) => RemovableDrives.Changed -= OnChanged;
        Opened += (_, _) => _ = Reload();
    }

    private void OnChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = Reload());

    private async Task Reload()
    {
        var selected = (_drives.SelectedItem as ListBoxItem)?.Tag as string;
        try { _disks = await DiskManager.ListAsync(); }
        catch (Exception ex)
        {
            _detail.Children.Clear();
            _detail.Children.Add(new TextBlock { Text = "UDisks2 isn't available: " + ex.Message + "\nInstall udisks2 (Arch/CachyOS: pacman -S udisks2).", TextWrapping = TextWrapping.Wrap });
            return;
        }
        var items = _disks.Select(d => new ListBoxItem
        {
            Tag = d.Path,
            Content = new StackPanel
            {
                Margin = new Thickness(4),
                Children =
                {
                    new TextBlock { Text = (d.Removable ? "🔌 " : "💽 ") + d.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{FileOps.FormatSize((long)d.Size)} · {d.Device}", Classes = { "caption" } },
                },
            },
        }).ToList();
        _drives.ItemsSource = items;
        _drives.SelectedItem = items.FirstOrDefault(i => i.Tag as string == selected) ?? items.FirstOrDefault();
        if (items.Count == 0) { _detail.Children.Clear(); _detail.Children.Add(new TextBlock { Text = "No disks were found." }); }
    }

    private Disk? Selected => _disks.FirstOrDefault(d => d.Path == (_drives.SelectedItem as ListBoxItem)?.Tag as string);

    private void ShowDisk()
    {
        _detail.Children.Clear();
        if (Selected is not { } d) return;
        _detail.Children.Add(new TextBlock { Text = d.Name, FontSize = 22, FontWeight = FontWeight.SemiBold });
        _detail.Children.Add(new TextBlock { Text = $"{FileOps.FormatSize((long)d.Size)} · {d.Device}" + (d.Removable ? " · removable" : "")
            + (d.InUseBySystem ? " · the computer runs from this disk" : ""), Classes = { "caption" } });

        // the partitions as a bar, in proportion
        var bar = new Grid { Height = 54 };
        ulong total = Math.Max(1, d.Size);
        ulong used = 0;
        int ci = 0;
        foreach (var p in d.Parts)
        {
            var part = p;
            double share = Math.Max(0.04, (double)p.Size / total);
            used += p.Size;
            bar.ColumnDefinitions.Add(new ColumnDefinition(share, GridUnitType.Star));
            var cell = new Border
            {
                Background = new SolidColorBrush(PartColors[ci % PartColors.Length]), Margin = new Thickness(1), CornerRadius = new CornerRadius(4),
                BorderBrush = Brushes.White, BorderThickness = new Thickness(part == _part ? 3 : 0), Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock { Text = (p.Label.Length > 0 ? p.Label : p.FsType.Length > 0 ? p.FsType : "Partition") + "\n" + FileOps.FormatSize((long)p.Size),
                    Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(6, 4), TextTrimming = TextTrimming.CharacterEllipsis },
            };
            cell.PointerPressed += (_, _) => { _part = part; ShowDisk(); };
            Grid.SetColumn(cell, ci++);
            bar.Children.Add(cell);
        }
        if (total > used + total / 50)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition((double)(total - used) / total, GridUnitType.Star));
            var free = new Border { Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), Margin = new Thickness(1), CornerRadius = new CornerRadius(4),
                Child = new TextBlock { Text = "Free\n" + FileOps.FormatSize((long)(total - used)), FontSize = 12, Margin = new Thickness(6, 4) } };
            Grid.SetColumn(free, ci);
            bar.Children.Add(free);
        }
        _detail.Children.Add(bar);
        _part ??= d.Parts.FirstOrDefault();

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
        Button B(string text, Func<Task> act, bool enabled = true)
        {
            var b = new Button { Content = text, IsEnabled = enabled };
            b.Click += async (_, _) => { b.IsEnabled = false; status.Text = "Working…"; await act(); b.IsEnabled = true; };
            return b;
        }
        if (_part is { } sel)
        {
            var size = sel.MountPoint != null && SpaceOf(sel.MountPoint) is { } sp ? $" · {FileOps.FormatSize(sp.Free)} free of {FileOps.FormatSize(sp.Total)}" : "";
            _detail.Children.Add(new TextBlock { Text = (sel.Label.Length > 0 ? sel.Label : "Partition " + sel.Number), FontSize = 16, FontWeight = FontWeight.SemiBold });
            _detail.Children.Add(new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, Text =
                $"{sel.Device} · {(sel.FsType.Length > 0 ? sel.FsType : "no filesystem")} · {FileOps.FormatSize((long)sel.Size)}" +
                (sel.Swap ? " · swap" : sel.MountPoint != null ? " · open at " + sel.MountPoint + size : sel.HasFilesystem ? " · not open" : "") });
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            void Add(Control c) { c.Margin = new Thickness(0, 0, 8, 8); row.Children.Add(c); }
            if (sel.HasFilesystem && sel.MountPoint == null)
                Add(B("Open", async () =>
                {
                    var (at, err) = await DiskManager.MountAsync(sel);
                    status.Text = err != null ? "Couldn't open it: " + err : "Open at " + at;
                    if (at != null) WindowManager.Open(new FileManagerWindow(at), "files");
                    await Reload();
                }));
            if (sel.MountPoint != null)
            {
                Add(B("Show in Files", () => { WindowManager.Open(new FileManagerWindow(sel.MountPoint), "files"); return Task.CompletedTask; }));
                Add(B("Unmount", async () => { var err = await DiskManager.UnmountAsync(sel); status.Text = err ?? "Unmounted."; await Reload(); }, !sel.InUseBySystem));
            }
            Add(B("Format partition…", () => Format(d, sel, status), !sel.InUseBySystem && !(d.InUseBySystem && !d.Removable)));
            _detail.Children.Add(row);
        }
        var diskRow = new WrapPanel();
        void AddD(Control c) { c.Margin = new Thickness(0, 0, 8, 8); diskRow.Children.Add(c); }
        AddD(B("Format the whole drive…", () => Format(d, null, status), !d.InUseBySystem));
        if (d.Removable) AddD(B("Eject", async () => { var err = await DiskManager.EjectAsync(d); status.Text = err ?? "It can be unplugged now."; await Reload(); }));
        _detail.Children.Add(new TextBlock { Text = "DRIVE", Classes = { "eyebrow" }, Margin = new Thickness(0, 8, 0, 0) });
        _detail.Children.Add(diskRow);
        _detail.Children.Add(status);
    }

    private static (long Free, long Total)? SpaceOf(string mount)
    {
        try { var di = new DriveInfo(mount); return (di.AvailableFreeSpace, di.TotalSize); } catch { return null; }
    }

    /// <summary>Asks for a name and a filesystem, warns, and formats a partition (or, with <paramref name="part"/> null,
    /// the whole drive).</summary>
    private async Task Format(Disk d, DiskPart? part, TextBlock status)
    {
        var formats = DiskManager.Formats.Select(f => (f.Type, Label: f.Label + (ProcessUtil.Which(f.Tool) == null ? $"  (needs {f.Tool})" : ""))).ToList();
        var type = new ComboBox { ItemsSource = formats.Select(f => f.Label).ToList(), SelectedIndex = d.Removable ? 0 : 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Text = part?.Label ?? "", PlaceholderText = "Name (optional)", MaxLength = 11 };
        type.SelectionChanged += (_, _) => name.MaxLength = formats[Math.Max(0, type.SelectedIndex)].Type switch { "vfat" => 11, "exfat" => 15, _ => 16 };
        var what = part == null ? $"everything on {d.Name} ({FileOps.FormatSize((long)d.Size)})" : $"everything on {(part.Label.Length > 0 ? part.Label : part.Device)}";
        bool go = false;
        var dlg = new Window
        {
            Title = "Format", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button { Content = "Erase and format", Classes = { "danger" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => { go = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = part == null ? "Format the whole drive" : "Format partition", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"This erases {what}. It can't be undone.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#E53E3E")) },
                type, name,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        await dlg.ShowDialog(this);
        if (!go) { status.Text = ""; return; }
        var fsType = formats[Math.Max(0, type.SelectedIndex)].Type;
        status.Text = "Formatting… (this can take a while; you may be asked for the administrator password)";
        var err = part == null ? await DiskManager.FormatDiskAsync(d, fsType, name.Text ?? "") : await DiskManager.FormatPartAsync(d, part, fsType, name.Text ?? "");
        status.Text = err == null ? "Formatted." : "Couldn't format: " + err;
        await Reload();
    }
}
