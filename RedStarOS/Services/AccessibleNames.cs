using System;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RedStarOS.Views;

namespace RedStarOS.Services;

/// <summary>
/// Gives icon-only buttons a name for assistive technology: the tooltip text when there is one ("Back", "Reload"),
/// otherwise the icon's name. Controls that already have a name, or show text, are left alone.
/// </summary>
public static class AccessibleNames
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Control.LoadedEvent.AddClassHandler<Button>((b, _) => Name(b), RoutingStrategies.Direct);
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((c, _) => { if (c is Button b) Name(b); });
    }

    private static void Name(Button b)
    {
        var current = AutomationProperties.GetName(b);
        if (!string.IsNullOrEmpty(current) && !b.Classes.Contains("auto-named")) return;
        if (b.Content is string) return;                          // the text is the name already
        string? name = ToolTip.GetTip(b) switch
        {
            string s => s,
            TextBlock t => t.Text,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(name) && b.Content is IconView iv && !string.IsNullOrEmpty(iv.Kind))
            name = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(iv.Kind.Replace('-', ' '));
        if (string.IsNullOrWhiteSpace(name)) return;
        AutomationProperties.SetName(b, name);
        b.Classes.Add("auto-named");                              // may be refreshed when the tooltip changes
    }

    /// <summary>Large pointer: tells X programs started from now on (xrdb Xcursor.size); Red Star itself follows at next sign-in.</summary>
    public static void ApplyPointerSize()
    {
        var xrdb = ProcessUtil.Which("xrdb");
        if (xrdb == null || Environment.GetEnvironmentVariable("DISPLAY") == null) return;
        var size = SettingsStore.Current.LargeCursor ? 48 : 24;
        try
        {
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "redstar-xcursor.ad");
            System.IO.File.WriteAllText(tmp, $"Xcursor.size: {size}\n");
            _ = ProcessUtil.RunAsync(xrdb, new[] { "-merge", tmp }, 5000);
        }
        catch { }
    }
}
