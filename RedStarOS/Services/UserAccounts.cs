// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RedStarOS.Services.DBus;

namespace RedStarOS.Services;

/// <summary>A person's account on this computer.</summary>
public sealed record UserAccount(string Path, string UserName, string RealName, bool Admin, ulong Uid, bool IsMe);

/// <summary>
/// The computer's user accounts through AccountsService (freedesktop.org, the service GNOME, KDE and most login screens
/// use), over the system bus with our own D-Bus client: list, add, remove, administrator or not, passwords. Every change
/// asks polkit, so the administrator password prompt comes up. A new password is hashed here with the system's own
/// libcrypt (libxcrypt, its strongest default method) and only the hash is handed over.
/// </summary>
public static class UserAccounts
{
    public const string Name = "org.freedesktop.Accounts", Root = "/org/freedesktop/Accounts", Iface = "org.freedesktop.Accounts",
        UserIface = "org.freedesktop.Accounts.User";
    private const int Polkit = 120000;            // calls that may wait for the password prompt

    private static Task<DBusConnection> Bus() => DBusConnection.ConnectAsync("unix:path=/run/dbus/system_bus_socket");

    public static async Task<bool> AvailableAsync()
    {
        if (!System.IO.File.Exists("/run/dbus/system_bus_socket")) return false;
        try
        {
            using var bus = await Bus();
            await bus.CallAsync(Name, Root, "org.freedesktop.DBus.Peer", "Ping");
            return true;
        }
        catch { return false; }
    }

    public static async Task<List<UserAccount>> ListAsync()
    {
        using var bus = await Bus();
        var paths = ((await bus.CallAsync(Name, Root, Iface, "ListCachedUsers")).Body.FirstOrDefault() as List<object> ?? new()).Select(p => p.ToString()!).ToList();
        // the signed-in user even if AccountsService hasn't cached them
        try
        {
            var me = (await bus.CallAsync(Name, Root, Iface, "FindUserByName", "s", new object[] { Environment.UserName })).Body[0].ToString()!;
            if (!paths.Contains(me)) paths.Add(me);
        }
        catch (DBusException) { }
        var list = new List<UserAccount>();
        foreach (var path in paths)
        {
            try
            {
                var r = await bus.CallAsync(Name, path, "org.freedesktop.DBus.Properties", "GetAll", "s", new object[] { UserIface });
                if (r.Body.FirstOrDefault() is not Dictionary<object, object> props) continue;
                object? P(string k) => props.TryGetValue(k, out var v) ? (v is Variant vv ? vv.Value : v) : null;
                if (P("SystemAccount") is true) continue;
                var user = P("UserName") as string ?? "";
                list.Add(new UserAccount(path, user, P("RealName") as string ?? "", P("AccountType") is int t && t == 1,
                    P("Uid") is ulong u ? u : 0, user == Environment.UserName));
            }
            catch (DBusException) { }
        }
        return list.OrderByDescending(a => a.IsMe).ThenBy(a => a.UserName).ToList();
    }

    /// <summary>Valid like useradd wants it: lower-case letters, digits, "-" and "_", starting with a letter.</summary>
    public static bool ValidUserName(string name) =>
        name.Length is > 0 and <= 32 && char.IsAsciiLetterLower(name[0]) && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>"Ada Lovelace" → "ada".</summary>
    public static string SuggestUserName(string realName)
    {
        var first = realName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var s = new string(first.ToLowerInvariant().Where(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)).ToArray());
        return s.Length > 0 && char.IsAsciiLetterLower(s[0]) ? s : "user";
    }

    public static async Task<string?> CreateAsync(string userName, string realName, bool admin, string password)
    {
        try
        {
            using var bus = await Bus();
            var path = (await bus.CallAsync(Name, Root, Iface, "CreateUser", "ssi", new object[] { userName, realName, admin ? 1 : 0 }, Polkit)).Body[0].ToString()!;
            if (password.Length > 0) await bus.CallAsync(Name, path, UserIface, "SetPassword", "ss", new object[] { Hash(password), "" }, Polkit);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static Task<string?> SetAdminAsync(UserAccount a, bool admin) => Call(a.Path, "SetAccountType", "i", admin ? 1 : 0);
    public static Task<string?> SetPasswordAsync(UserAccount a, string password) => Call(a.Path, "SetPassword", "ss", Hash(password), "");
    public static Task<string?> SetRealNameAsync(UserAccount a, string name) => Call(a.Path, "SetRealName", "s", name);

    public static async Task<string?> DeleteAsync(UserAccount a, bool removeFiles)
    {
        if (a.IsMe) return "you can't remove the account you're signed in with";
        try
        {
            using var bus = await Bus();
            await bus.CallAsync(Name, Root, Iface, "DeleteUser", "xb", new object[] { (long)a.Uid, removeFiles }, Polkit);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static async Task<string?> Call(string path, string member, string sig, params object[] args)
    {
        try
        {
            using var bus = await Bus();
            await bus.CallAsync(Name, path, UserIface, member, sig, args, Polkit);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    [DllImport("libcrypt.so.1", EntryPoint = "crypt_gensalt")] private static extern IntPtr crypt_gensalt(string? prefix, ulong count, IntPtr rbytes, int nrbytes);
    [DllImport("libcrypt.so.1", EntryPoint = "crypt")] private static extern IntPtr crypt(string phrase, string setting);

    /// <summary>The password hashed by libxcrypt with its preferred method (yescrypt on current systems) and a random salt.</summary>
    public static string Hash(string password)
    {
        var salt = Marshal.PtrToStringUTF8(crypt_gensalt(null, 0, IntPtr.Zero, 0));
        if (string.IsNullOrEmpty(salt) || salt.StartsWith('*')) throw new InvalidOperationException("libcrypt couldn't make a salt");
        var hash = Marshal.PtrToStringUTF8(crypt(password, salt));
        if (string.IsNullOrEmpty(hash) || hash.StartsWith('*')) throw new InvalidOperationException("libcrypt couldn't hash the password");
        return hash;
    }
}
