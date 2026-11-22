// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RedStarOS.Services.DBus;

namespace RedStarOS.Services;

/// <summary>One package with an update waiting.</summary>
public sealed record PackageUpdate(string Id, string Name, string Version, string Summary, bool Security);

/// <summary>
/// The system's own package updates through PackageKit (freedesktop.org's package-manager service, which drives
/// pacman, apt, dnf… through its backends), spoken over the system bus with our own D-Bus client. Installing asks
/// polkit, so Crimson Banner's administrator password prompt comes up. Crimson Banner's AppImage has its own updater
/// (UpdateChecker); this is for everything else on the system.
/// </summary>
public static class SystemUpdates
{
    public const string Name = "org.freedesktop.PackageKit", Root = "/org/freedesktop/PackageKit", TxIface = "org.freedesktop.PackageKit.Transaction";
    private const ulong FilterNone = 1UL << 1, OnlyTrusted = 1UL << 1;
    private const uint InfoSecurity = 8;              // PK_INFO_ENUM_SECURITY
    private const uint ExitSuccess = 1;               // PK_EXIT_ENUM_SUCCESS

    /// <summary>Whether PackageKit can be reached (it starts on demand through D-Bus activation).</summary>
    public static async Task<bool> AvailableAsync()
    {
        if (!System.IO.File.Exists("/run/dbus/system_bus_socket")) return false;
        try
        {
            using var bus = await DBusConnection.ConnectAsync("unix:path=/run/dbus/system_bus_socket");
            var r = await bus.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListActivatableNames");
            if (r.Body.FirstOrDefault() is List<object> names && names.Contains(Name)) return true;
            var o = await bus.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s", new object[] { Name });
            return o.Body.FirstOrDefault() is true;
        }
        catch { return false; }
    }

    /// <summary>"name;version;arch;repo" → its parts.</summary>
    public static PackageUpdate Parse(uint info, string id, string summary)
    {
        var p = id.Split(';');
        return new PackageUpdate(id, p.ElementAtOrDefault(0) ?? id, p.ElementAtOrDefault(1) ?? "", summary, info == InfoSecurity);
    }

    /// <summary>One PackageKit transaction: runs <paramref name="start"/> on it and collects its signals until it
    /// finishes. Returns the packages it reported and an error, if any.</summary>
    private static async Task<(List<PackageUpdate> Packages, string? Error)> Transaction(DBusConnection bus, Func<string, Task> start,
        Action<int>? progress, CancellationToken ct)
    {
        var tx = (await bus.CallAsync(Name, Root, "org.freedesktop.PackageKit", "CreateTransaction")).Body[0].ToString()!;
        var packages = new List<PackageUpdate>();
        string? error = null;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSignal(Message m)
        {
            if (m.Path != tx) return;
            switch (m.Member)
            {
                case "Package" when m.Body.Length >= 3:
                    lock (packages) packages.Add(Parse(Convert.ToUInt32(m.Body[0]), m.Body[1] as string ?? "", m.Body[2] as string ?? ""));
                    break;
                case "ErrorCode" when m.Body.Length >= 2:
                    error = m.Body[1] as string;
                    break;
                case "Finished":
                    if (m.Body.FirstOrDefault() is uint exit && exit != ExitSuccess && error == null) error = exit == 3 ? "cancelled" : "it didn't finish";
                    done.TrySetResult(true);
                    break;
                case "PropertiesChanged" when progress != null && m.Body.Length >= 2 && m.Body[1] is Dictionary<object, object> props:
                    if (props.TryGetValue("Percentage", out var v) && (v is Variant vv ? vv.Value : v) is uint pct && pct <= 100) progress((int)pct);
                    break;
            }
        }
        bus.Signal += OnSignal;
        try
        {
            await bus.AddMatchAsync($"type='signal',path='{tx}'");
            // interactive: polkit may ask for the administrator password
            await bus.CallAsync(Name, tx, TxIface, "SetHints", "as", new object[] { new List<object> { "interactive=true", "background=false",
                "locale=" + (Environment.GetEnvironmentVariable("LANG") ?? "C") } });
            await start(tx);
            using (ct.Register(() => { _ = bus.CallAsync(Name, tx, TxIface, "Cancel"); }))
                await done.Task.WaitAsync(TimeSpan.FromHours(2), CancellationToken.None);
        }
        catch (DBusException ex) { error = ex.Message; }
        finally { bus.Signal -= OnSignal; }
        lock (packages) return (packages.ToList(), error);
    }

    /// <summary>Refreshes the package lists (when <paramref name="refresh"/>) and returns the updates waiting.</summary>
    public static async Task<(List<PackageUpdate> Updates, string? Error)> CheckAsync(bool refresh, CancellationToken ct = default)
    {
        using var bus = await DBusConnection.ConnectAsync("unix:path=/run/dbus/system_bus_socket");
        if (refresh)
        {
            var (_, err) = await Transaction(bus, tx => bus.CallAsync(Name, tx, TxIface, "RefreshCache", "b", new object[] { false }), null, ct);
            if (err != null) return (new(), "couldn't refresh the package lists: " + err);
        }
        var (list, error) = await Transaction(bus, tx => bus.CallAsync(Name, tx, TxIface, "GetUpdates", "t", new object[] { FilterNone }), null, ct);
        return (list.GroupBy(p => p.Id).Select(g => g.First()).OrderByDescending(p => p.Security).ThenBy(p => p.Name).ToList(), error);
    }

    /// <summary>Installs them (polkit asks for the password). Null when it worked, else why not.</summary>
    public static async Task<string?> InstallAsync(IReadOnlyList<PackageUpdate> updates, Action<int>? progress, CancellationToken ct = default)
    {
        if (updates.Count == 0) return null;
        using var bus = await DBusConnection.ConnectAsync("unix:path=/run/dbus/system_bus_socket");
        var ids = updates.Select(u => (object)u.Id).ToList();
        var (_, error) = await Transaction(bus, tx => bus.CallAsync(Name, tx, TxIface, "UpdatePackages", "tas", new object[] { OnlyTrusted, ids }, 600000),
            progress, ct);
        return error;
    }

    private const ulong FilterInstalled = 1UL << 2, FilterNotInstalled = 1UL << 3;

    /// <summary>Fetches the repositories' package lists again (after a repository is added). Null when it worked.</summary>
    public static async Task<string?> RefreshAsync(CancellationToken ct = default)
    {
        using var bus = await DBusConnection.ConnectAsync("unix:path=/run/dbus/system_bus_socket");
        var (_, err) = await Transaction(bus, tx => bus.CallAsync(Name, tx, TxIface, "RefreshCache", "b", new object[] { true }, 300000), null, ct);
        return err;
    }

    /// <summary>Which of these package names are installed (PackageKit's Resolve).</summary>
    public static async Task<HashSet<string>> InstalledAsync(IReadOnlyList<string> names, CancellationToken ct = default)
    {
        var set = new HashSet<string>();
        if (names.Count == 0) return set;
        using var bus = await DBusConnection.ConnectAsync("unix:path=/run/dbus/system_bus_socket");
        var (list, _) = await Transaction(bus, tx => bus.CallAsync(Name, tx, TxIface, "Resolve", "tas",
            new object[] { FilterInstalled, names.Select(n => (object)n).ToList() }), null, ct);
        foreach (var p in list) set.Add(p.Name);
        return set;
    }

    /// <summary>Installs packages by name, from whichever repository has them ([crimson-banner] included); polkit asks
    /// for the password. Null when it worked, else why not.</summary>
    public static async Task<string?> InstallByNameAsync(IReadOnlyList<string> names, Action<int>? progress, CancellationToken ct = default)
    {
        using var bus = await DBusConnection.ConnectAsync("unix:path=/run/dbus/system_bus_socket");
        var (found, err) = await Transaction(bus, tx => bus.CallAsync(Name, tx, TxIface, "Resolve", "tas",
            new object[] { FilterNotInstalled, names.Select(n => (object)n).ToList() }), null, ct);
        if (err != null) return err;
        var ids = found.Where(p => names.Contains(p.Name)).GroupBy(p => p.Name).Select(g => (object)g.First().Id).ToList();
        if (ids.Count == 0) return "No repository has " + string.Join(", ", names) + " (or it's installed already).";
        var (_, error) = await Transaction(bus, tx => bus.CallAsync(Name, tx, TxIface, "InstallPackages", "tas", new object[] { OnlyTrusted, ids }, 600000), progress, ct);
        return error;
    }

    /// <summary>Removes installed packages by name, with what only they needed.</summary>
    public static async Task<string?> RemoveByNameAsync(IReadOnlyList<string> names, Action<int>? progress, CancellationToken ct = default)
    {
        using var bus = await DBusConnection.ConnectAsync("unix:path=/run/dbus/system_bus_socket");
        var (found, err) = await Transaction(bus, tx => bus.CallAsync(Name, tx, TxIface, "Resolve", "tas",
            new object[] { FilterInstalled, names.Select(n => (object)n).ToList() }), null, ct);
        if (err != null) return err;
        var ids = found.Where(p => names.Contains(p.Name)).Select(p => (object)p.Id).Distinct().ToList();
        if (ids.Count == 0) return null;
        var (_, error) = await Transaction(bus, tx => bus.CallAsync(Name, tx, TxIface, "RemovePackages", "tasbb",
            new object[] { OnlyTrusted, ids, true, true }, 600000), progress, ct);
        return error;
    }

    /// <summary>Once a day in a Crimson Banner session: looks for system updates and says so when there are some.</summary>
    public static async void CheckInBackground()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3));
            var last = SettingsStore.Current.LastSystemUpdateCheck;
            if (last is { } t && DateTime.Now - t < TimeSpan.FromHours(20)) return;
            if (!await AvailableAsync()) return;
            var (list, err) = await CheckAsync(refresh: true);
            if (err != null) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SettingsStore.Current.LastSystemUpdateCheck = DateTime.Now;
                SettingsStore.Save();
                if (list.Count == 0) return;
                int sec = list.Count(u => u.Security);
                Notifier.Show("System updates", $"{list.Count} update{(list.Count == 1 ? " is" : "s are")} ready" + (sec > 0 ? $", {sec} for security." : "."),
                    "settings", new NotificationAction("Open Updates", () => Views.SettingsWindow.OpenPage("Software Updates")));
            });
        }
        catch (Exception ex) { Console.Error.WriteLine("updates: " + ex.Message); }
    }

    /// <summary>The package manager's own full upgrade, for a terminal when PackageKit isn't installed.</summary>
    public static (string Exe, List<string> Args)? TerminalCommand(Func<string, string?> which)
    {
        if (which("pacman") != null) return ("sudo", new List<string> { "pacman", "-Syu" });
        if (which("apt") != null) return ("sh", new List<string> { "-c", "sudo apt update && sudo apt full-upgrade" });
        if (which("dnf") != null) return ("sudo", new List<string> { "dnf", "upgrade" });
        if (which("zypper") != null) return ("sudo", new List<string> { "zypper", "dup" });
        return null;
    }
}
