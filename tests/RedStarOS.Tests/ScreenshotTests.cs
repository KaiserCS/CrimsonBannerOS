using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using RedStarOS.Views;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>
/// Windows that need arguments (so <see cref="WindowSmokeTests"/> can't create them) must open too, and with
/// REDSTAR_SCREENSHOTS=folder every window is also rendered (Skia) and saved as a PNG — CI uploads them, so a
/// change's effect on every screen can be looked at without running the app.
/// </summary>
public class ScreenshotTests
{
    private static string? ShotDir => Environment.GetEnvironmentVariable("REDSTAR_SCREENSHOTS") is { Length: > 0 } d ? d : null;

    private static bool Environmental(Exception e) =>
        e is DllNotFoundException or EntryPointNotFoundException ||
        e.GetType().Name.Contains("VLC", StringComparison.OrdinalIgnoreCase) ||
        (e.InnerException != null && Environmental(e.InnerException));

    /// <summary>Shows the window, saves a picture of it when screenshots are on, closes it.</summary>
    private static void ShowAndCapture(Window w, string name)
    {
        w.Show();
        Dispatcher.UIThread.RunJobs();
        if (ShotDir is { } dir)
        {
            // let thumbnails, timers and layout settle a little
            for (int i = 0; i < 5; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
            var frame = w.CaptureRenderedFrame();          // null with the headless (non-Skia) renderer
            if (frame != null)
            {
                Directory.CreateDirectory(dir);
                frame.Save(Path.Combine(dir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ─────────── every window with a plain constructor (pictures only; WindowSmokeTests already opens them) ───────────
    [AvaloniaTheory]
    [MemberData(nameof(WindowSmokeTests.Windows), MemberType = typeof(WindowSmokeTests))]
    public void Every_window(string typeName)
    {
        if (ShotDir == null) return;
        var type = typeof(App).Assembly.GetTypes().First(t => t.Name == typeName);
        try { ShowAndCapture((Window)Activator.CreateInstance(type)!, typeName); }
        catch (TargetInvocationException tie) when (tie.InnerException != null && Environmental(tie.InnerException)) { }
        catch (Exception e) when (Environmental(e)) { }
    }

    // ─────────── windows that take arguments ───────────
    private static readonly List<FileChooserWindow.Filter> DocFilters = new()
    {
        new("Documents", new[] { "*.docx", "*.txt", "*.md" }),
        new("All files", new[] { "*" }),
    };

    private static string Sandbox()
    {
        var dir = Path.Combine(Path.GetTempPath(), "redstar-shots-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "Projects"));
        Directory.CreateDirectory(Path.Combine(dir, "Photos"));
        File.WriteAllText(Path.Combine(dir, "Report.docx"), "x");
        File.WriteAllText(Path.Combine(dir, "Notes.txt"), "notes");
        File.WriteAllText(Path.Combine(dir, "Budget.csv"), "a,b\n1,2");
        File.WriteAllText(Path.Combine(dir, "IMG_0001.jpg"), "not really a picture");
        return dir;
    }

    [AvaloniaFact]
    public void Open_dialog()
    {
        var dir = Sandbox();
        ShowAndCapture(new FileChooserWindow(FileChooserWindow.Mode.Open, "Open document", dir, DocFilters) { AllowMultiple = true }, "FileChooser-Open");
    }

    [AvaloniaFact]
    public void Save_dialog()
    {
        var dir = Sandbox();
        ShowAndCapture(new FileChooserWindow(FileChooserWindow.Mode.Save, "Save document", dir, DocFilters) { SuggestedName = "Report", DefaultExtension = "docx" }, "FileChooser-Save");
    }

    [AvaloniaFact]
    public void Folder_dialog()
    {
        var dir = Sandbox();
        ShowAndCapture(new FileChooserWindow(FileChooserWindow.Mode.Folder, "Choose a folder", dir, new List<FileChooserWindow.Filter>()), "FileChooser-Folder");
    }

    [AvaloniaFact]
    public void Batch_rename()
    {
        var dir = Sandbox();
        var files = Directory.GetFiles(dir).OrderBy(f => f).ToList();
        ShowAndCapture(new BatchRenameWindow(files), "BatchRename");
    }

    [AvaloniaFact]
    public void Files_in_a_folder()
    {
        var dir = Sandbox();
        ShowAndCapture(new FileManagerWindow(dir), "FileManager-folder");
    }
}
