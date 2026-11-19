using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// Network and cloud places in Files.
///  • Servers (smb://, sftp://, ftp://, dav(s)://) are mounted with GVfs (<c>gio mount</c>) and show up under
///    /run/user/UID/gvfs; without GVfs, sftp:// falls back to sshfs (mounted in ~/Network/host).
///  • Cloud drives are rclone remotes (set up once with <c>rclone config</c>): mounted in ~/Cloud/remote with
///    <c>rclone mount --vfs-cache-mode writes --daemon</c>.
///  • Removable drives can be ejected (udisksctl).
/// </summary>
public static class RemoteMounts
{
    public sealed record Mount(string Label, string Path, string Kind, string? Uri);   // Kind: smb, sftp, ftp, dav, …

    public static string GvfsDir
    {
        get
        {
            var run = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(run)) run = "/run/user/" + Uid();
            return Path.Combine(run, "gvfs");
        }
    }

    private static string Uid()
    {
        try
        {
            foreach (var l in File.ReadLines("/proc/self/status"))
                if (l.StartsWith("Uid:")) return l.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries)[1];
        }
        catch { }
        return "1000";
    }

    public static bool HasGio => ProcessUtil.Which("gio") != null;
    public static bool HasSshfs => ProcessUtil.Which("sshfs") != null;
    public static bool HasRclone => ProcessUtil.Which("rclone") != null;

    /// <summary>Connected servers: GVfs mounts and sshfs mounts.</summary>
    public static List<Mount> Servers()
    {
        var list = new List<Mount>();
        try
        {
            if (Directory.Exists(GvfsDir))
                foreach (var d in Directory.GetDirectories(GvfsDir))
                {
                    var (label, kind, uri) = ParseGvfsName(Path.GetFileName(d));
                    if (kind is "mtp" or "gphoto2" or "afc") continue;          // phones and cameras show as devices elsewhere
                    list.Add(new Mount(label, d, kind, uri));
                }
        }
        catch { }
        foreach (var (dev, mount, type) in ProcMounts())
            if (type == "fuse.sshfs") list.Add(new Mount(dev.Split(':')[0], mount, "sftp", "sftp://" + dev.Split(':')[0]));
        return list;
    }

    /// <summary>"smb-share:server=nas,share=photos" → ("photos on nas", "smb", "smb://nas/photos").</summary>
    public static (string Label, string Kind, string? Uri) ParseGvfsName(string name)
    {
        var colon = name.IndexOf(':');
        if (colon < 0) return (name, "other", null);
        var kind = name[..colon];
        var kv = new Dictionary<string, string>();
        foreach (var part in name[(colon + 1)..].Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) kv[part[..eq]] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        string Get(string k) => kv.TryGetValue(k, out var v) ? v : "";
        var host = Get("host") is { Length: > 0 } h ? h : Get("server");
        switch (kind)
        {
            case "smb-share":
                return ($"{Get("share")} on {host}", "smb", $"smb://{host}/{Get("share")}");
            case "sftp":
            case "ftp":
                var user = Get("user");
                return ((user.Length > 0 ? user + "@" : "") + host, kind, $"{kind}://{(user.Length > 0 ? user + "@" : "")}{host}{(Get("port") is { Length: > 0 } p ? ":" + p : "")}/");
            case "dav":
            case "davs":
                var ssl = Get("ssl") == "true" || kind == "davs";
                return (host, "dav", $"{(ssl ? "davs" : "dav")}://{host}{Get("prefix")}");
            case "google-drive":
                return ("Google Drive (" + Get("user") + ")", "cloud", null);
            default:
                return (host.Length > 0 ? host : name, kind, null);
        }
    }

    private static IEnumerable<(string Dev, string Mount, string Type)> ProcMounts()
    {
        string[] lines;
        try { lines = File.ReadAllLines("/proc/mounts"); } catch { yield break; }
        foreach (var l in lines)
        {
            var f = l.Split(' ');
            if (f.Length < 3) continue;
            yield return (f[0].Replace("\\040", " "), f[1].Replace("\\040", " "), f[2]);
        }
    }

    /// <summary>Mounts a server. Returns the local folder to open. Credentials go through stdin, never the command line.</summary>
    public static async Task<string> Connect(string uri, string? user, string? password, bool anonymous)
    {
        uri = uri.Trim();
        if (!uri.Contains("://")) uri = "smb://" + uri.TrimStart('/');
        var u = new Uri(uri);
        var scheme = u.Scheme.ToLowerInvariant();
        if (scheme is not ("smb" or "sftp" or "ssh" or "ftp" or "ftps" or "dav" or "davs" or "afp" or "nfs"))
            throw new IOException("Unsupported address. Use smb://, sftp://, ftp://, dav:// or davs://.");
        if (scheme == "ssh") { uri = "sftp" + uri[3..]; scheme = "sftp"; u = new Uri(uri); }

        if (HasGio)
        {
            var before = Servers().Select(m => m.Path).ToHashSet();
            // the user name goes into the address for everything but smb (which asks for it separately)
            var target = uri;
            if (!anonymous && scheme != "smb" && !string.IsNullOrEmpty(user) && string.IsNullOrEmpty(u.UserInfo))
                target = $"{scheme}://{Uri.EscapeDataString(user)}@{u.Authority}{u.PathAndQuery}";
            var args = new List<string> { "mount" };
            if (anonymous) args.Add("-a");
            args.Add(target);
            var input = anonymous ? "" : scheme == "smb"
                ? $"{user}\nWORKGROUP\n{password}\n"
                : $"{password}\n";
            var (code, output) = await RunWithInput(ProcessUtil.Which("gio")!, args, input, 60000);
            if (code != 0 && !output.Contains("already mounted", StringComparison.OrdinalIgnoreCase))
                throw new IOException(CleanGioError(output));
            // the new folder under gvfs
            for (int i = 0; i < 20; i++)
            {
                var now = Servers().FirstOrDefault(m => !before.Contains(m.Path))
                          ?? Servers().FirstOrDefault(m => m.Uri != null && SameServer(m.Uri, uri));
                if (now != null)
                {
                    // smb://host/share/sub/dir → open the sub folder
                    if (scheme == "smb")
                    {
                        var segs = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                        var sub = Path.Combine(new[] { now.Path }.Concat(segs.Skip(1).Select(Uri.UnescapeDataString)).ToArray());
                        if (Directory.Exists(sub)) return sub;
                    }
                    else if (u.AbsolutePath.Length > 1 && Directory.Exists(Path.Combine(now.Path, Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/')))))
                        return Path.Combine(now.Path, Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/')));
                    return now.Path;
                }
                await Task.Delay(150);
            }
            throw new IOException("The server was connected but its folder didn't appear (is gvfs-fuse installed?).");
        }

        if (scheme == "sftp" && HasSshfs)
        {
            var host = u.Host;
            var who = !string.IsNullOrEmpty(u.UserInfo) ? Uri.UnescapeDataString(u.UserInfo) : user;
            var dir = Path.Combine(AppPaths.Home, "Network", host);
            Directory.CreateDirectory(dir);
            var args = new List<string> { (string.IsNullOrEmpty(who) ? "" : who + "@") + host + ":" + (u.AbsolutePath.Length > 1 ? Uri.UnescapeDataString(u.AbsolutePath) : ""), dir,
                                          "-o", "reconnect", "-o", "ServerAliveInterval=15" };
            if (!u.IsDefaultPort && u.Port > 0) { args.Add("-p"); args.Add(u.Port.ToString()); }
            if (!string.IsNullOrEmpty(password)) { args.Add("-o"); args.Add("password_stdin"); }
            var (code, output) = await RunWithInput(ProcessUtil.Which("sshfs")!, args, string.IsNullOrEmpty(password) ? "" : password + "\n", 60000);
            if (code != 0) throw new IOException("sshfs: " + output.Trim());
            return dir;
        }
        throw new IOException(scheme == "sftp"
            ? "Connecting to servers needs GVfs (package gvfs, gvfs-backends / gvfs-fuse) or sshfs."
            : "Connecting to servers needs GVfs (packages gvfs, gvfs-backends / gvfs-smb and gvfs-fuse).");
    }

    private static bool SameServer(string a, string b)
    {
        try
        {
            var x = new Uri(a); var y = new Uri(b);
            if (x.Scheme != y.Scheme || !string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase)) return false;
            if (x.Scheme == "smb") return string.Equals(x.AbsolutePath.Trim('/').Split('/')[0], y.AbsolutePath.Trim('/').Split('/')[0], StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch { return false; }
    }

    private static string CleanGioError(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.EndsWith(":")) ?? "The server didn't answer.";
        line = line.Replace("gio: ", "");
        if (line.Contains("Operation not supported", StringComparison.OrdinalIgnoreCase))
            line += " (the backend for this kind of server may not be installed, e.g. gvfs-smb / gvfs-backends)";
        return line;
    }

    /// <summary>Disconnects a server (GVfs or sshfs).</summary>
    public static async Task Disconnect(Mount m)
    {
        if (m.Path.StartsWith(GvfsDir) && HasGio)
        {
            var (code, output) = await ProcessUtil.RunAsync(ProcessUtil.Which("gio")!, new[] { "mount", "-u", m.Uri ?? m.Path }, 20000);
            if (code != 0 && m.Uri != null) (code, output) = await ProcessUtil.RunAsync(ProcessUtil.Which("gio")!, new[] { "mount", "-u", m.Path }, 20000);
            if (code != 0) throw new IOException(CleanGioError(output));
        }
        else await FuseUnmount(m.Path);
    }

    private static async Task FuseUnmount(string dir)
    {
        var fu = ProcessUtil.Which("fusermount3") ?? ProcessUtil.Which("fusermount") ?? throw new IOException("fusermount isn't installed.");
        var (code, output) = await ProcessUtil.RunAsync(fu, new[] { "-u", dir }, 20000);
        if (code != 0) throw new IOException(output.Trim().Length > 0 ? output.Trim() : "It couldn't be disconnected (is something still using it?).");
    }

    // ─────────── cloud (rclone) ───────────
    public sealed record CloudRemote(string Name, string Folder, bool Mounted);

    public static string CloudDir => Path.Combine(AppPaths.Home, "Cloud");

    /// <summary>The rclone remotes set up with <c>rclone config</c> (cached for 30 s; rclone is slow to start).</summary>
    public static async Task<List<CloudRemote>> CloudRemotes()
    {
        if (!HasRclone) return new();
        if (_remotes == null || (DateTime.Now - _remotesAt).TotalSeconds > 30)
        {
            var (code, output) = await ProcessUtil.RunAsync(ProcessUtil.Which("rclone")!, new[] { "listremotes" }, 8000);
            _remotes = code == 0 ? ParseRemotes(output) : new List<string>();
            _remotesAt = DateTime.Now;
        }
        var mounted = ProcMounts().Where(m => m.Type.StartsWith("fuse.rclone") || m.Type == "fuse.rclone").Select(m => m.Mount).ToHashSet();
        return _remotes.Select(r => new CloudRemote(r, Path.Combine(CloudDir, r), mounted.Contains(Path.Combine(CloudDir, r)))).ToList();
    }

    private static List<string>? _remotes;
    private static DateTime _remotesAt;

    /// <summary>"gdrive:\nbox:\n" → [gdrive, box].</summary>
    public static List<string> ParseRemotes(string output) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.EndsWith(':') && l.Length > 1).Select(l => l.TrimEnd(':')).Where(n => !n.Contains('/')).ToList();

    public static async Task<string> MountCloud(CloudRemote r)
    {
        if (r.Mounted) return r.Folder;
        Directory.CreateDirectory(r.Folder);
        var (code, output) = await ProcessUtil.RunAsync(ProcessUtil.Which("rclone")!,
            new[] { "mount", r.Name + ":", r.Folder, "--vfs-cache-mode", "writes", "--daemon", "--daemon-timeout", "30s" }, 45000);
        if (code != 0) throw new IOException("rclone: " + (output.Trim().Split('\n').LastOrDefault() ?? "the mount failed"));
        return r.Folder;
    }

    public static Task UnmountCloud(CloudRemote r) => FuseUnmount(r.Folder);

    // ─────────── removable drives ───────────
    public static bool IsRemovableMount(string mount) => mount.StartsWith("/media/") || mount.StartsWith("/run/media/");

    /// <summary>Unmounts a drive (and powers it off when it's a USB stick, so it can be pulled out).</summary>
    public static async Task Eject(string mount)
    {
        var dev = ProcMounts().FirstOrDefault(m => m.Mount == mount).Dev;
        if (string.IsNullOrEmpty(dev)) throw new IOException("It isn't mounted.");
        if (ProcessUtil.Which("udisksctl") is { } ud)
        {
            var (code, output) = await ProcessUtil.RunAsync(ud, new[] { "unmount", "-b", dev }, 30000);
            if (code != 0) throw new IOException(output.Contains("busy", StringComparison.OrdinalIgnoreCase)
                ? "The drive is still in use — close any files from it and try again." : output.Trim());
            // whole disk of /dev/sdb1 is /dev/sdb; nvme/mmc partitions end with p1
            var disk = System.Text.RegularExpressions.Regex.Replace(dev, @"(?<=\d)p\d+$|(?<=[a-z])\d+$", "");
            if (disk != dev) _ = ProcessUtil.RunAsync(ud, new[] { "power-off", "-b", disk }, 20000);
        }
        else
        {
            var (code, output) = await ProcessUtil.RunAsync("umount", new[] { mount }, 30000);
            if (code != 0) throw new IOException(output.Trim());
        }
    }

    private static async Task<(int, string)> RunWithInput(string exe, IEnumerable<string> args, string input, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException(Path.GetFileName(exe) + " could not start.");
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        try { await p.StandardInput.WriteAsync(input); p.StandardInput.Close(); } catch { }
        var exited = p.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(timeoutMs)) != exited) { try { p.Kill(true); } catch { } return (-1, "The server took too long to answer."); }
        return (p.ExitCode, (await o) + (await e));
    }
}
