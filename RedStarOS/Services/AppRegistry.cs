using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using RedStarOS.Views;

namespace RedStarOS.Services;

/// <summary>
/// One entry per built-in app. Either <paramref name="Create"/> builds a window, or
/// <paramref name="Run"/> starts something else (such as the Electron browser).
/// </summary>
public sealed record AppInfo(
    string Id,
    string Name,
    string Category,
    Func<Window>? Create,
    bool RestrictedForChild = false,
    Action? Run = null)
{
    public void Start()
    {
        if (Run != null) Run();
        else if (Create != null) WindowManager.Open(Create(), Id);
    }
}

public static class AppRegistry
{
    public static readonly IReadOnlyList<AppInfo> Apps = new List<AppInfo>
    {
        new("studio",      "Red Star Studio",     "RSOffice",    () => new StudioWindow()),
        new("writer",      "Red Star Writer",     "RSOffice",    () => new RichWriterWindow(), false, () => WriterLauncher.Launch()),
        new("sheets",      "Red Star Sheets",     "RSOffice",    () => new SpreadsheetWindow()),
        new("slides",      "Red Star Slides",     "RSOffice",    () => new SlidesWindow()),
        new("pong",        "Ping Pong",           "RSGames",     () => new PongWindow()),
        new("online",      "Red Star Online",     "RSOnline",    null, true, () => BrowserLauncher.Launch()),
        new("messenger",   "Red Star Messenger",  "RSOnline",    () => new MessengerWindow(), true),
        new("video",       "Red Star Video",      "RSMedia",     null, false, () => VideoLauncher.Launch()),
        new("terminal",    "Terminal",            "RSUtilities", null, true, () => TerminalLauncher.Launch()),
        new("files",       "File Manager",        "RSUtilities", () => new FileManagerWindow()),
        new("calculator",  "Calculator",          "RSUtilities", () => new CalculatorWindow()),
        new("notes",       "Notes",               "RSUtilities", () => new NotesWindow()),
        new("imageviewer", "Image Viewer",        "RSUtilities", () => new ImageViewerWindow()),
        new("camera",      "Camera",              "RSUtilities", () => new CameraWindow()),
        new("paint",       "Red Star Paint",      "RSMedia",     () => new PaintWindow()),
        new("clock",       "Clock",               "RSUtilities", () => new ClockWindow()),
        new("taskmgr",     "Task Manager",        "System",      () => new TaskManagerWindow()),
        new("calendar",    "Calendar",            "RSUtilities", () => new CalendarWindow()),
        new("settings",    "Advanced Settings",   "System",      () => new SettingsWindow()),
    };

    // Which app a window belongs to (icon + taskbar pinning). Settings sub-dialogs count as Settings.
    private static readonly Dictionary<string, string> WindowApps = new()
    {
        ["StudioWindow"] = "studio", ["WebBrowserWindow"] = "online", ["VideoWindow"] = "video", ["WriterWindow"] = "writer", ["RichWriterWindow"] = "writer", ["TerminalWindow"] = "terminal", ["SpreadsheetWindow"] = "sheets", ["SlidesWindow"] = "slides", ["SlideShowWindow"] = "slides", ["SheetChartWindow"] = "sheets",
        ["PongWindow"] = "pong", ["MessengerWindow"] = "messenger", ["FileManagerWindow"] = "files",
        ["CalculatorWindow"] = "calculator", ["NotesWindow"] = "notes", ["ImageViewerWindow"] = "imageviewer",
        ["CameraWindow"] = "camera", ["PaintWindow"] = "paint", ["ClockWindow"] = "clock", ["TaskManagerWindow"] = "taskmgr", ["CalendarWindow"] = "calendar", ["SettingsWindow"] = "settings",
        ["UpdatesWindow"] = "settings", ["NetworkWindow"] = "settings", ["ConnectNetworkWindow"] = "settings",
        ["AccountWindow"] = "settings", ["AppearanceWindow"] = "settings", ["WallpaperPickerWindow"] = "settings"
    };

    public static string? IdForWindow(Window w) =>
        WindowApps.TryGetValue(w.GetType().Name, out var id) ? id : null;

    public static AppInfo? Find(string id) => Apps.FirstOrDefault(a => a.Id == id);

    public static void Launch(string id) => Find(id)?.Start();
}
