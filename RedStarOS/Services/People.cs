// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;

namespace RedStarOS.Services;

/// <summary>The people on this computer, as the shell names them.</summary>
public static class People
{
    /// <summary>The name a person goes by: the first part of their /etc/passwd comment (GECOS), else their login.</summary>
    public static string ShownName(string login, string? passwd = null)
    {
        try
        {
            passwd ??= File.ReadAllText("/etc/passwd");
            foreach (var line in passwd.Split('\n'))
            {
                var f = line.Split(':');
                if (f.Length >= 5 && f[0] == login)
                {
                    var real = f[4].Split(',')[0].Trim();
                    return real.Length > 0 ? real : login;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return login;
    }
}
