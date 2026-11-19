using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace RedStarOS;

/// <summary>Small OS-integration helpers that replace WinForms / Win32 specifics.</summary>
internal static class SystemUtil
{
    /// <summary>Open a URL or file/folder with the desktop's default handler (xdg-open on Linux).</summary>
    public static void OpenExternal(string target)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start(new ProcessStartInfo("xdg-open", Quote(target)) { UseShellExecute = false });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start(new ProcessStartInfo("open", Quote(target)) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open '{target}': {ex.Message}");
        }
    }

    /// <summary>
    /// Start a fresh copy of this program (replacement for Application.Restart()).
    /// Inside an AppImage the APPIMAGE variable points at the real .AppImage file,
    /// which is what we must relaunch (the internal mount disappears on exit).
    /// </summary>
    public static bool RelaunchSelf()
    {
        try
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            var exe = !string.IsNullOrEmpty(appImage) && File.Exists(appImage)
                ? appImage
                : Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Send plain text to the default printer using CUPS (lp).</summary>
    public static bool PrintText(string text, out string message)
    {
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"redstar-print-{Guid.NewGuid():N}.txt");
            File.WriteAllText(tmp, text);
            var psi = new ProcessStartInfo("lp", Quote(tmp))
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p == null) { message = "Could not start the 'lp' print command."; return false; }
            p.WaitForExit(15000);
            var err = p.StandardError.ReadToEnd().Trim();
            message = p.ExitCode == 0 ? "Document sent to the default printer." : (err.Length > 0 ? err : "Printing failed.");
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            message = "Printing needs CUPS ('lp') to be installed. " + ex.Message;
            return false;
        }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
