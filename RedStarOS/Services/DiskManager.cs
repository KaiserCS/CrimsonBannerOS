// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RedStarOS.Services.DBus;

namespace RedStarOS.Services;

/// <summary>A disk and what's on it.</summary>
public sealed record Disk(string Path, string Name, ulong Size, bool Removable, bool CanPowerOff, string? WholeBlock, string Device,
    IReadOnlyList<DiskPart> Parts)
{
    /// <summary>The computer runs from it (/, /boot, /home, /usr or swap): never formatted from here.</summary>
    public bool InUseBySystem => Parts.Any(p => p.InUseBySystem);
}

/// <summary>One partition (or a whole disk with a filesystem and no partition table).</summary>
public sealed record DiskPart(string Block, string Device, int Number, ulong Offset, ulong Size, string FsType, string Label, string? MountPoint,
    bool HasFilesystem, bool Swap)
{
    public static readonly string[] SystemMounts = { "/", "/boot", "/boot/efi", "/efi", "/home", "/usr", "/var" };
    public bool InUseBySystem => Swap || MountPoint != null && SystemMounts.Contains(MountPoint);
}

/// <summary>
/// The Disks app's view of the computer's drives through UDisks2 (our own D-Bus client on the system bus): drives,
/// their partitions, mounting, formatting a partition or a whole drive, and ejecting. UDisks2 asks polkit, which asks
/// for the administrator password where its rules say so (formatting, internal disks).
/// </summary>
public static class DiskManager
{
    private const string N = RemovableDrives.Name, BlockI = "org.freedesktop.UDisks2.Block", DriveI = "org.freedesktop.UDisks2.Drive",
        PartI = "org.freedesktop.UDisks2.Partition", TableI = "org.freedesktop.UDisks2.PartitionTable", FsI = "org.freedesktop.UDisks2.Filesystem",
        SwapI = "org.freedesktop.UDisks2.Swapspace";
    private const int Slow = 600000;                     // formatting can take minutes, and may wait for the password

    /// <summary>Filesystems offered when formatting: UDisks2's name, what it's for, and the program it needs.</summary>
    public static readonly (string Type, string Label, string Tool)[] Formats =
    {
        ("exfat", "exFAT: works with Linux, Windows and macOS (USB sticks, SD cards)", "mkfs.exfat"),
        ("vfat", "FAT32: works almost everywhere, files up to 4 GB", "mkfs.vfat"),
        ("ext4", "ext4: Linux only, the usual choice for Linux disks", "mkfs.ext4"),
        ("ntfs", "NTFS: for Windows", "mkfs.ntfs"),
    };

    private static Task<DBusConnection> Bus() => DBusConnection.ConnectAsync("unix:path=/run/dbus/system_bus_socket");

    private static object? Val(Dictionary<object, object>? props, string key) =>
        props != null && props.TryGetValue(key, out var v) ? (v is Variant vv ? vv.Value : v) : null;

    private static Dictionary<object, object>? Iface(object o, string name) =>
        o is Dictionary<object, object> d && d.TryGetValue(name, out var p) ? p as Dictionary<object, object> : null;

    public static async Task<List<Disk>> ListAsync()
    {
        using var bus = await Bus();
        var r = await bus.CallAsync(N, RemovableDrives.Root, "org.freedesktop.DBus.ObjectManager", "GetManagedObjects");
        return Parse(r.Body.FirstOrDefault() as Dictionary<object, object>);
    }

    /// <summary>Disks and partitions from UDisks2's GetManagedObjects (public for the tests).</summary>
    public static List<Disk> Parse(Dictionary<object, object>? objects)
    {
        var disks = new List<Disk>();
        if (objects == null) return disks;
        var blocks = objects.Select(o => (Path: o.Key.ToString()!, Ifaces: o.Value)).Where(o => Iface(o.Ifaces, BlockI) != null).ToList();
        foreach (var (path, ifaces) in objects.Select(o => (o.Key.ToString()!, o.Value)))
        {
            var drive = Iface(ifaces, DriveI);
            if (drive == null) continue;
            var mine = blocks.Where(b => Val(Iface(b.Ifaces, BlockI), "Drive")?.ToString() == path).ToList();
            if (mine.Count == 0) continue;
            // the whole disk: the block that isn't a partition
            var whole = mine.FirstOrDefault(b => Iface(b.Ifaces, PartI) == null);
            var parts = new List<DiskPart>();
            foreach (var b in mine)
            {
                var block = Iface(b.Ifaces, BlockI);
                var part = Iface(b.Ifaces, PartI);
                var fs = Iface(b.Ifaces, FsI);
                if (part == null && (fs == null || Iface(b.Ifaces, TableI) != null)) continue;     // the whole disk, unless it holds a filesystem itself
                var mounts = (Val(fs, "MountPoints") as List<object>)?.Select(RemovableDrives.Bytes).Where(s => s.Length > 0).ToList();
                parts.Add(new DiskPart(b.Path, RemovableDrives.Bytes(Val(block, "PreferredDevice") ?? Val(block, "Device")),
                    Val(part, "Number") is uint n ? (int)n : 0, Val(part, "Offset") is ulong off ? off : 0, Val(block, "Size") is ulong sz ? sz : 0,
                    Val(block, "IdType") as string ?? "", Val(block, "IdLabel") as string ?? "", mounts?.FirstOrDefault(), fs != null, Iface(b.Ifaces, SwapI) != null));
            }
            var vendor = Val(drive, "Vendor") as string ?? ""; var model = Val(drive, "Model") as string ?? "";
            ulong size = Val(drive, "Size") is ulong ds && ds > 0 ? ds : whole is { } w && Val(Iface(w.Ifaces, BlockI), "Size") is ulong ws ? ws : 0;
            var name = $"{vendor} {model}".Trim();
            disks.Add(new Disk(path, name.Length > 0 ? name : "Disk", size,
                Val(drive, "Removable") is true || Val(drive, "MediaRemovable") is true || Val(drive, "ConnectionBus") as string == "usb",
                Val(drive, "CanPowerOff") is true, whole.Path, whole.Path != null ? RemovableDrives.Bytes(Val(Iface(whole.Ifaces, BlockI), "Device")) : "",
                parts.OrderBy(p => p.Offset).ToList()));
        }
        return disks.OrderBy(d => d.Removable).ThenBy(d => d.Device).ToList();
    }

    public static async Task<string?> Run(Func<DBusConnection, Task> call)
    {
        try { using var bus = await Bus(); await call(bus); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    private static Dictionary<object, object> NoOptions => new();

    public static async Task<(string? Mount, string? Error)> MountAsync(DiskPart p)
    {
        try
        {
            using var bus = await Bus();
            var r = await bus.CallAsync(N, p.Block, FsI, "Mount", "a{sv}", new object[] { NoOptions }, 120000);
            return (r.FirstString, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public static Task<string?> UnmountAsync(DiskPart p) =>
        Run(bus => bus.CallAsync(N, p.Block, FsI, "Unmount", "a{sv}", new object[] { NoOptions }, 60000));

    private static Dictionary<object, object> FormatOptions(string label) => new()
    {
        ["label"] = new Variant("s", label), ["update-partition-type"] = new Variant("b", true),
    };

    /// <summary>Erases one partition and puts a new filesystem on it.</summary>
    public static async Task<string?> FormatPartAsync(Disk d, DiskPart p, string type, string label)
    {
        if (p.InUseBySystem || d.InUseBySystem && !d.Removable) return "the computer is running from this disk";
        if (p.MountPoint != null && await UnmountAsync(p) is { } err) return err;
        return await Run(bus => bus.CallAsync(N, p.Block, BlockI, "Format", "sa{sv}", new object[] { type, FormatOptions(label) }, Slow));
    }

    /// <summary>Erases the whole drive: a new partition table (GPT) with one partition filling it.</summary>
    public static async Task<string?> FormatDiskAsync(Disk d, string type, string label)
    {
        if (d.InUseBySystem) return "the computer is running from this disk";
        if (d.WholeBlock is not { } whole) return "UDisks2 doesn't offer this disk as a whole";
        foreach (var p in d.Parts.Where(p => p.MountPoint != null))
            if (await UnmountAsync(p) is { } err) return err;
        return await Run(async bus =>
        {
            await bus.CallAsync(N, whole, BlockI, "Format", "sa{sv}", new object[] { "gpt", NoOptions }, Slow);
            await bus.CallAsync(N, whole, TableI, "CreatePartitionAndFormat", "ttssa{sv}sa{sv}",
                new object[] { 0UL, 0UL, "", label, NoOptions, type, FormatOptions(label) }, Slow);
        });
    }

    public static async Task<string?> EjectAsync(Disk d)
    {
        foreach (var p in d.Parts.Where(p => p.MountPoint != null))
            if (await UnmountAsync(p) is { } err) return err;
        if (!d.CanPowerOff) return null;
        return await Run(bus => bus.CallAsync(N, d.Path, DriveI, "PowerOff", "a{sv}", new object[] { NoOptions }, 30000));
    }
}
