// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Threading.Tasks;

namespace RedStarOS.Services.Software;

/// <summary>
/// Checks that our catalogue was signed with the project's key, so the list (and the checksums of the downloads in
/// it) can't be swapped on the way or on the hosting. It uses gpgv (GnuPG's verifier, which pacman needs too) with
/// the public key shipped with the shell (software/crimson-banner.gpg, written by tools/publish-app.sh).
/// </summary>
public static class CatalogSignature
{
    public static string KeyringPath => Path.Combine(AppContext.BaseDirectory, "software", "crimson-banner.gpg");

    /// <summary>Whether this build has the project's key; a build without it can't check, and says so.</summary>
    public static bool HasKey(string? keyring = null) => File.Exists(keyring ?? KeyringPath) && new FileInfo(keyring ?? KeyringPath).Length > 0;

    /// <summary>Null when <paramref name="signature"/> is a good signature of <paramref name="data"/> by the key in
    /// <paramref name="keyring"/>, else why not.</summary>
    public static async Task<string?> VerifyAsync(byte[] data, byte[] signature, string? keyring = null)
    {
        keyring = Path.GetFullPath(keyring ?? KeyringPath);
        if (!HasKey(keyring)) return "this build has no key to check it with";
        var gpgv = ProcessUtil.Which("gpgv");
        if (gpgv == null) return "gpgv (GnuPG) isn't installed, so it can't be checked";
        var dir = Directory.CreateTempSubdirectory("crimson-verify-");
        try
        {
            var d = Path.Combine(dir.FullName, "catalog.json");
            var s = Path.Combine(dir.FullName, "catalog.json.sig");
            await File.WriteAllBytesAsync(d, data);
            await File.WriteAllBytesAsync(s, signature);
            var home = Directory.CreateDirectory(Path.Combine(dir.FullName, "home"));
            var (code, output) = await ProcessUtil.RunAsync(gpgv, new[] { "--homedir", home.FullName, "--keyring", keyring, s, d }, 20000);
            return code == 0 ? null : "its signature doesn't match the project's key" + (output.Contains("BAD signature") ? " (it was changed)" : "");
        }
        finally { try { dir.Delete(true); } catch { } }
    }
}
