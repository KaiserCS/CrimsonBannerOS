using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace RedStarOS.Services;

/// <summary>Small UI helpers shared by the shell panels.</summary>
public static class ShellUi
{
    /// <summary>Toggles a style class (used for the "on" state of quick-setting tiles).</summary>
    public static void SetClass(Control control, string name, bool on)
    {
        if (on) { if (!control.Classes.Contains(name)) control.Classes.Add(name); }
        else control.Classes.Remove(name);
    }

    /// <summary>The user's profile photo, or null to keep the built-in avatar.</summary>
    public static Bitmap? LoadAvatar()
    {
        try
        {
            var avatar = SettingsStore.Current.AvatarPath;
            if (!string.IsNullOrEmpty(avatar) && File.Exists(avatar))
            {
                using var fs = File.OpenRead(avatar);
                return new Bitmap(fs);
            }
        }
        catch { /* keep default avatar */ }
        return null;
    }
}
