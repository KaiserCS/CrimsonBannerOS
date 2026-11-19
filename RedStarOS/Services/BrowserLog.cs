using System;
using System.IO;

namespace RedStarOS.Services;

/// <summary>Small diagnostic log for Red Star Online (~/.config/redstaros/browser.log).</summary>
public static class BrowserLog
{
    private static readonly object Gate = new();

    public static string FilePath => AppPaths.ConfigFile("browser.log");

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                var f = FilePath;
                if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.Move(f, f + ".old", true);
                File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\n");
            }
        }
        catch { }
    }

    public static void Error(string where, Exception ex) => Write($"ERROR {where}: {ex}");
}
