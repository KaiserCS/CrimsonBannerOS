using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using RedStarOS.Views;
using Avalonia.Input.Platform;

namespace RedStarOS.Services;

/// <summary>
/// One "Share" menu for every app: send a link, some text or a file to Messenger, Writer, e-mail or the clipboard.
/// Apps call ShowMenu(anchor, ...) from a button, or ShareLink / ShareFile directly.
/// </summary>
public static class ShareService
{
    private sealed record Target(string Label, string Icon, Action Run);

    private static List<Target> Targets(string title, string? url, string? text, string? file)
    {
        var t = new List<Target>();
        string body = url ?? text ?? file ?? "";
        t.Add(new("Send to Messenger", "chat", () =>
        {
            MessengerWindow.Share(title, file != null ? "file://" + file : body);
            Notifier.Show("Sent to Messenger", title, "messenger");
        }));
        if (file == null)
            t.Add(new("Open in Writer", "document", () =>
                WriterLauncher.OpenHtml(title, url != null
                    ? $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(url)}\">{System.Net.WebUtility.HtmlEncode(url)}</a></p>"
                    : "<p>" + System.Net.WebUtility.HtmlEncode(text ?? "").Replace("\n", "<br>") + "</p>")));
        t.Add(new("E-mail", "chat", () =>
        {
            var args = new List<string> { "--subject", title };
            if (file != null) { args.Add("--attach"); args.Add(file); }
            else { args.Add("--body"); args.Add(body); }
            if (!ProcessUtil.StartDetached("xdg-email", args, AppPaths.Home))
                Notifier.Show("Share", "No e-mail program is set up (xdg-email).");
        }));
        t.Add(new(file != null ? "Copy file path" : url != null ? "Copy link" : "Copy text", "clipboard", () =>
        {
            var top = WindowManager.Active ?? WindowManager.Desktop;
            _ = (top != null ? TopLevel.GetTopLevel(top)?.Clipboard?.SetTextAsync(file ?? body) : null);
            Notifier.Show("Copied", file ?? body);
        }));
        if (file != null)
            t.Add(new("Show in folder", "folder", () => WindowManager.Open(new FileManagerWindow(Path.GetDirectoryName(file)))));
        return t;
    }

    /// <summary>Shows the share menu under <paramref name="anchor"/>.</summary>
    public static void ShowMenu(Control anchor, string title, string? url = null, string? text = null, string? file = null)
    {
        var menu = new MenuFlyout();
        foreach (var target in Targets(title, url, text, file))
        {
            var t = target;
            var mi = new MenuItem { Header = t.Label, Icon = Icons.Create(t.Icon, 16) };
            mi.Click += (_, _) => t.Run();
            menu.Items.Add(mi);
        }
        menu.ShowAt(anchor);
    }

    /// <summary>Share menu without an anchor (from a notification): opens it on the desktop's taskbar.</summary>
    public static void ShareFile(string file)
    {
        if (WindowManager.Desktop is { } d) ShowMenu(d, Path.GetFileName(file), file: file);
    }
}
