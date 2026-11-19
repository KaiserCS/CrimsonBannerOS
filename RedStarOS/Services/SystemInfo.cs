using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RedStarOS.Services;

public static class SystemInfo
{
    public sealed record NetIf(string Name, string Type, string Ipv4, string Mac, bool Up);
    public sealed record BatteryState(int Percent, bool Charging);

    public static List<NetIf> NetworkInterfaces()
    {
        var result = new List<NetIf>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var ip = ni.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .FirstOrDefault() ?? "-";
                var mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("x2")));
                result.Add(new NetIf(ni.Name, ni.NetworkInterfaceType.ToString(), ip, mac, ni.OperationalStatus == OperationalStatus.Up));
            }
        }
        catch { /* no network info available */ }
        return result;
    }

    public static string NetworkSummary()
    {
        var up = NetworkInterfaces().FirstOrDefault(n => n.Up && n.Ipv4 != "-");
        return up == null ? "Offline" : up.Ipv4;
    }

    /// <summary>"85% (Charging)" or null when the machine has no battery.</summary>
    public static string? Battery()
    {
        try
        {
            const string root = "/sys/class/power_supply";
            if (!Directory.Exists(root)) return null;
            foreach (var d in Directory.GetDirectories(root, "BAT*"))
            {
                var cap = File.ReadAllText(Path.Combine(d, "capacity")).Trim();
                var status = File.Exists(Path.Combine(d, "status")) ? File.ReadAllText(Path.Combine(d, "status")).Trim() : "";
                return $"{cap}%" + (status.Length > 0 ? $" ({status})" : "");
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>Battery level and whether it is charging, or null when the machine has no battery.</summary>
    public static BatteryState? BatteryInfo()
    {
        try
        {
            const string root = "/sys/class/power_supply";
            if (!Directory.Exists(root)) return null;
            foreach (var d in Directory.GetDirectories(root, "BAT*"))
            {
                if (!int.TryParse(File.ReadAllText(Path.Combine(d, "capacity")).Trim(), out var cap)) continue;
                var statusFile = Path.Combine(d, "status");
                var status = File.Exists(statusFile) ? File.ReadAllText(statusFile).Trim() : "";
                return new BatteryState(cap, status.Equals("Charging", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { /* ignore */ }
        return null;
    }

    public static async Task<int?> GetVolume()
    {
        if (ProcessUtil.Which("pactl") is { } pactl)
        {
            var (code, text) = await ProcessUtil.RunAsync(pactl, new[] { "get-sink-volume", "@DEFAULT_SINK@" }, 3000);
            var m = Regex.Match(text, @"(\d+)%");
            if (code == 0 && m.Success) return int.Parse(m.Groups[1].Value);
        }
        if (ProcessUtil.Which("amixer") is { } amixer)
        {
            var (code, text) = await ProcessUtil.RunAsync(amixer, new[] { "get", "Master" }, 3000);
            var m = Regex.Match(text, @"\[(\d+)%\]");
            if (code == 0 && m.Success) return int.Parse(m.Groups[1].Value);
        }
        return null;
    }

    public static async Task SetVolume(int percent)
    {
        percent = Math.Clamp(percent, 0, 150);
        if (ProcessUtil.Which("pactl") is { } pactl)
        {
            await ProcessUtil.RunAsync(pactl, new[] { "set-sink-volume", "@DEFAULT_SINK@", percent + "%" }, 3000);
            return;
        }
        if (ProcessUtil.Which("amixer") is { } amixer)
            await ProcessUtil.RunAsync(amixer, new[] { "set", "Master", percent + "%" }, 3000);
    }

    /// <summary>The volume one step up or down (taskbar scroll wheel, volume keys): 0–100, or where it already is if
    /// something else turned it past 100.</summary>
    public static int StepVolume(int current, int delta) =>
        delta > 0 ? Math.Max(current, Math.Min(100, current + delta)) : Math.Max(0, current + delta);

    /// <summary>Turns the volume up or down by <paramref name="delta"/> percent and unmutes it; returns the new
    /// (volume, muted), or null without a sound system.</summary>
    public static async Task<(int Volume, bool Muted)?> ChangeVolume(int delta)
    {
        var now = await GetVolume();
        if (now == null) return null;
        int next = StepVolume(now.Value, delta);
        if (next != now) await SetVolume(next);
        if (await GetMuted() == true) await SetMuted(false);
        return (next, false);
    }

    /// <summary>Mutes or unmutes; returns the new (volume, muted), or null without a sound system.</summary>
    public static async Task<(int Volume, bool Muted)?> ToggleMuted()
    {
        var vol = await GetVolume();
        if (vol == null) return null;
        bool muted = await GetMuted() != true;
        await SetMuted(muted);
        return (vol.Value, muted);
    }

    public static async Task<bool?> GetMuted()
    {
        if (ProcessUtil.Which("pactl") is { } pactl)
        {
            var (code, text) = await ProcessUtil.RunAsync(pactl, new[] { "get-sink-mute", "@DEFAULT_SINK@" }, 3000);
            var m = Regex.Match(text, @"Mute:\s*(yes|no)", RegexOptions.IgnoreCase);
            if (code == 0 && m.Success) return m.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        if (ProcessUtil.Which("amixer") is { } amixer)
        {
            var (code, text) = await ProcessUtil.RunAsync(amixer, new[] { "get", "Master" }, 3000);
            var m = Regex.Match(text, @"\[(on|off)\]", RegexOptions.IgnoreCase);
            if (code == 0 && m.Success) return m.Groups[1].Value.Equals("off", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    public static async Task SetMuted(bool muted)
    {
        if (ProcessUtil.Which("pactl") is { } pactl)
        {
            await ProcessUtil.RunAsync(pactl, new[] { "set-sink-mute", "@DEFAULT_SINK@", muted ? "1" : "0" }, 3000);
            return;
        }
        if (ProcessUtil.Which("amixer") is { } amixer)
            await ProcessUtil.RunAsync(amixer, new[] { "set", "Master", muted ? "mute" : "unmute" }, 3000);
    }

    /// <summary>"wifi", "wired" or "offline" - what the tray's network icon should show.</summary>
    public static string ConnectionKind()
    {
        var up = NetworkInterfaces().FirstOrDefault(n => n.Up && n.Ipv4 != "-");
        if (up == null) return "offline";
        return up.Type.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ? "wifi" : "wired";
    }
}
