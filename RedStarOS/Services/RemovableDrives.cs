// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RedStarOS.Services.DBus;

namespace RedStarOS.Services;

/// <summary>A drive's filesystem that can be opened: a USB stick, an SD card, a phone in storage mode, a second disk.</summary>
public sealed record DriveVolume(string ObjectPath, string Device, string Label, ulong Size, string? MountPoint, bool Removable, string? DrivePath, bool CanPowerOff);

/// <summary>
/// Drives through UDisks2 (freedesktop.org's disk service) on the system bus, spoken with our own D-Bus client: which
/// filesystems there are, mounted or not, and a notification when a removable one is plugged in, with "Open" (mount
/// it and show it in Files). Another desktop's automounter does this itself; in a Crimson Banner session nothing else
/// would, so drives would otherwise never appear. Mounting a removable drive is allowed for the active session by
/// polkit's default rules; anything else asks for the administrator password (PolkitAgent).
/// </summary>
public static class RemovableDrives
{
    public const string Name = "org.freedesktop.UDisks2", Root = "/org/freedesktop/UDisks2";
    private const string BlockIface = "org.freedesktop.UDisks2.Block", FsIface = "org.freedesktop.UDisks2.Filesystem", DriveIface = "org.freedesktop.UDisks2.Drive";

    private static DBusConnection? _bus;
    private static List<DriveVolume> _volumes = new();
    private static bool _loaded;

    public static IReadOnlyList<DriveVolume> Volumes => _volumes;
    public static bool Running => _bus != null;
    /// <summary>The list changed (on a background thread).</summary>
    public static event Action? Changed;
    /// <summary>A removable drive was plugged in (on a background thread).</summary>
    public static event Action<DriveVolume>? Arrived;

    public static async Task StartAsync()
    {
        if (_bus != null || !System.IO.File.Exists("/run/dbus/system_bus_socket")) return;
        try
        {
            var bus = await DBusConnection.ConnectAsync("unix:path=/run/dbus/system_bus_socket");
            _bus = bus;
            bus.Closed += () => _bus = null;
            bus.Signal += m =>
            {
                if (m.Sender == null) return;
                _ = Refresh(m.Member == "InterfacesAdded");
            };
            await bus.AddMatchAsync($"type='signal',sender='{Name}',interface='org.freedesktop.DBus.ObjectManager'");
            await bus.AddMatchAsync($"type='signal',sender='{Name}',interface='org.freedesktop.DBus.Properties',member='PropertiesChanged'");
            await Refresh(false);
            _loaded = true;
        }
        catch (Exception ex)
        {
            _bus = null;
            Console.Error.WriteLine("drives: UDisks2 unavailable: " + ex.Message);
        }
    }

    private static async Task Refresh(bool maybeNew)
    {
        var bus = _bus;
        if (bus == null) return;
        try
        {
            var reply = await bus.CallAsync(Name, Root, "org.freedesktop.DBus.ObjectManager", "GetManagedObjects");
            var before = _volumes.Select(v => v.ObjectPath).ToHashSet();
            _volumes = Parse(reply.Body.FirstOrDefault() as Dictionary<object, object>);
            Changed?.Invoke();
            if (maybeNew && _loaded)
                foreach (var v in _volumes.Where(v => v.Removable && !before.Contains(v.ObjectPath))) Arrived?.Invoke(v);
        }
        catch (Exception ex) { Console.Error.WriteLine("drives: " + ex.Message); }
    }

    private static object? Val(Dictionary<object, object>? props, string key) =>
        props != null && props.TryGetValue(key, out var v) ? (v is Variant vv ? vv.Value : v) : null;

    /// <summary>A NUL-terminated byte string (UDisks2's paths are "ay").</summary>
    public static string Bytes(object? v) => v is byte[] b ? Encoding.UTF8.GetString(b).TrimEnd('\0') : "";

    /// <summary>The filesystems worth showing, from GetManagedObjects (public for the tests).</summary>
    public static List<DriveVolume> Parse(Dictionary<object, object>? objects)
    {
        var list = new List<DriveVolume>();
        if (objects == null) return list;
        Dictionary<object, object>? Iface(object o, string name) =>
            o is Dictionary<object, object> d && d.TryGetValue(name, out var p) ? p as Dictionary<object, object> : null;
        foreach (var (path, ifaces) in objects)
        {
            var block = Iface(ifaces, BlockIface);
            var fs = Iface(ifaces, FsIface);
            if (block == null || fs == null) continue;
            if (Val(block, "HintIgnore") is true) continue;
            var drivePath = Val(block, "Drive")?.ToString();
            Dictionary<object, object>? drive = null;
            if (drivePath is { Length: > 1 } && objects.TryGetValue(new ObjectPath(drivePath), out var dObj)) drive = Iface(dObj, DriveIface);
            bool removable = Val(drive, "Removable") is true || Val(drive, "MediaRemovable") is true || Val(drive, "ConnectionBus") as string == "usb";
            bool system = Val(block, "HintSystem") is true;
            if (system && !removable) continue;                      // the computer's own disks are under / already
            var mounts = (Val(fs, "MountPoints") as List<object>)?.Select(Bytes).Where(s => s.Length > 0).ToList() ?? new List<string>();
            ulong size = Val(block, "Size") is ulong s ? s : 0;
            var label = Val(block, "IdLabel") as string is { Length: > 0 } l ? l
                : Val(block, "HintName") as string is { Length: > 0 } h ? h
                : string.Join(' ', new[] { Val(drive, "Vendor") as string, Val(drive, "Model") as string }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim() is { Length: > 0 } vm
                    ? (size > 0 ? $"{FileOps.FormatSize((long)size)} {vm}" : vm)
                : size > 0 ? $"{FileOps.FormatSize((long)size)} drive" : "Drive";
            list.Add(new DriveVolume(path.ToString()!, Bytes(Val(block, "PreferredDevice") ?? Val(block, "Device")), label, size,
                mounts.FirstOrDefault(), removable, drivePath is { Length: > 1 } ? drivePath : null, Val(drive, "CanPowerOff") is true));
        }
        return list.OrderBy(v => v.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Mounts it (UDisks2 picks /run/media/USER/LABEL) and returns where.</summary>
    public static async Task<string> Mount(DriveVolume v)
    {
        if (v.MountPoint != null) return v.MountPoint;
        var bus = _bus ?? throw new InvalidOperationException("UDisks2 isn't running");
        // up to two minutes: polkit may ask for the administrator password
        var reply = await bus.CallAsync(Name, v.ObjectPath, FsIface, "Mount", "a{sv}", new object[] { new Dictionary<object, object>() }, 120000);
        return reply.FirstString ?? throw new InvalidOperationException("UDisks2 didn't say where it mounted the drive");
    }

    /// <summary>Unmounts it and, for a USB drive, powers it off so it can be pulled out.</summary>
    public static async Task Eject(DriveVolume v)
    {
        var bus = _bus ?? throw new InvalidOperationException("UDisks2 isn't running");
        if (v.MountPoint != null)
            await bus.CallAsync(Name, v.ObjectPath, FsIface, "Unmount", "a{sv}", new object[] { new Dictionary<object, object>() }, 60000);
        if (v.DrivePath != null && v.CanPowerOff)
        {
            try { await bus.CallAsync(Name, v.DrivePath, DriveIface, "PowerOff", "a{sv}", new object[] { new Dictionary<object, object>() }, 30000); }
            catch (DBusException) { }                                 // unmounted is enough to pull it out
        }
    }

    /// <summary>The volume mounted at <paramref name="mount"/>, if UDisks2 knows it.</summary>
    public static DriveVolume? AtMount(string mount) => _volumes.FirstOrDefault(v => v.MountPoint == mount);
}
