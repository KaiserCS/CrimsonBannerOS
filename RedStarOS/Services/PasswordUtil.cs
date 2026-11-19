using System;
using System.Security.Cryptography;
using System.Text;

namespace RedStarOS.Services;

/// <summary>Lock-screen password (salted PBKDF2). With no password set, any input logs in.</summary>
public static class PasswordUtil
{
    public static bool HasPassword => !string.IsNullOrEmpty(SettingsStore.Current.PasswordHash);

    private static byte[] Hash(string pw, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pw), salt, 100_000, HashAlgorithmName.SHA256, 32);

    public static bool Verify(string? pw)
    {
        if (!HasPassword) return true;
        var s = SettingsStore.Current;
        var salt = Convert.FromBase64String(s.PasswordSalt ?? "");
        var expected = Convert.FromBase64String(s.PasswordHash!);
        return CryptographicOperations.FixedTimeEquals(Hash(pw ?? "", salt), expected);
    }

    public static void Set(string? pw)
    {
        var s = SettingsStore.Current;
        if (string.IsNullOrEmpty(pw))
        {
            s.PasswordHash = null;
            s.PasswordSalt = null;
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            s.PasswordSalt = Convert.ToBase64String(salt);
            s.PasswordHash = Convert.ToBase64String(Hash(pw, salt));
        }
        SettingsStore.Save();
    }
}
