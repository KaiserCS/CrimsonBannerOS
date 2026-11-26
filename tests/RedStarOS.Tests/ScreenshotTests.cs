using System.Reflection;
using Avalonia;
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
        e.Message.Contains("libvlc", StringComparison.OrdinalIgnoreCase) ||      // VideoWindow without VLC installed
        (e.InnerException != null && Environmental(e.InnerException));

    /// <summary>Shows the window, saves a picture of it when screenshots are on, closes it.</summary>
    private static void ShowAndCapture(Window w, string name)
    {
        // REDSTAR_SCREENSHOT_THEME=Light (or Dark): every picture in that theme (CI takes both)
        if (Environment.GetEnvironmentVariable("REDSTAR_SCREENSHOT_THEME") is { Length: > 0 } theme
            && RedStarOS.Services.SettingsStore.Current.Theme != theme)
        {
            RedStarOS.Services.SettingsStore.Current.Theme = theme;
            RedStarOS.Services.ThemeService.Apply();
        }
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

    [AvaloniaFact]
    public void CD_Player_with_a_disc()
    {
        if (ShotDir == null) return;
        // an album with a cover, drawn here: a red field with a white square
        var dir = Path.Combine(Path.GetTempPath(), "redstar-cd-" + Guid.NewGuid().ToString("N")[..8], "Kraftwerk", "Radio-Activity");
        Directory.CreateDirectory(dir);
        var cover = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(300, 300));
        using (var ctx = cover.CreateDrawingContext())
        {
            ctx.FillRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(196, 20, 28)), new Avalonia.Rect(0, 0, 300, 300));
            ctx.FillRectangle(Avalonia.Media.Brushes.White, new Avalonia.Rect(40, 40, 90, 90));
        }
        cover.Save(Path.Combine(dir, "cover.png"));
        var names = new[] { "01 Geiger Counter", "02 Radioactivity", "03 Radioland", "04 Airwaves", "05 Intermission", "06 News", "07 The Voice of Energy" };
        foreach (var n in names) File.WriteAllText(Path.Combine(dir, n + ".flac"), "");
        var album = RedStarOS.Services.MusicLibrary.Scan(Path.GetDirectoryName(dir)!)[0];
        var w = new CdPlayerWindow(null);
        w.LoadAlbum(album, "YOUR MUSIC LIBRARY", play: false, first: 1);
        ShowAndCapture(w, "CdPlayerWindow-disc");
    }

    // ─────────── Planner (apps/planner), on its example economy ───────────
    [AvaloniaTheory]
    [InlineData("overview", null)]
    [InlineData("economy", null)]
    [InlineData("councils", null)]
    [InlineData("reports", null)]
    [InlineData("plan", null)]
    [InlineData("quotas", 3)]
    [InlineData("ledger", null)]
    public void Planner_pages(string page, int? period)
    {
        var w = CrimsonPlanner.Views.PlannerWindow.Sample(page, period);
        ShowAndCapture(w, "Planner-" + page);
    }

    // ─────────── People's Archive (apps/archive), with a sample shelf ───────────
    [AvaloniaTheory]
    [InlineData("shelf")]
    [InlineData("find")]
    [InlineData("sources")]
    public void Archive_pages(string page)
    {
        var dir = Path.Combine(Path.GetTempPath(), "archive-shot-" + Guid.NewGuid().ToString("N"));
        try { ShowAndCapture(PeoplesArchive.Views.ArchiveWindow.Sample(page, dir), "Archive-" + page); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [AvaloniaFact]
    public void Archive_reading_room()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archive-shot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lib = PeoplesArchive.Model.Samples.Shelf(dir);
            var book = lib.Books.First(b => b.Title.StartsWith("Manifesto"));
            ShowAndCapture(new PeoplesArchive.Views.ReaderWindow(lib, book, save: false), "Archive-reader");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ─────────── Software's pages, with sample programs (CI has no catalogue to read) ───────────
    [AvaloniaTheory]
    [InlineData("explore")]
    [InlineData("crimson")]
    [InlineData("installed")]
    [InlineData("search")]
    [InlineData("detail")]
    [InlineData("explore-light")]
    public void Software_pages(string page)
    {
        var light = page.EndsWith("-light");
        var before = RedStarOS.Services.SettingsStore.Current.Theme;
        if (light) { RedStarOS.Services.SettingsStore.Current.Theme = "Light"; RedStarOS.Services.ThemeService.Apply(); }
        try
        {
            var w = new SoftwareWindow();
            w.ShowSample(light ? page[..^"-light".Length] : page);
            ShowAndCapture(w, "Software-" + page);
        }
        finally
        {
            if (light) { RedStarOS.Services.SettingsStore.Current.Theme = before; RedStarOS.Services.ThemeService.Apply(); }
        }
    }

    // ─────────── the shell's own parts (the taskbar, the Start menu and the panels), each in a window of its size ───────────
    [AvaloniaTheory]
    [InlineData("Taskbar", 1280, 56)]
    [InlineData("StartMenu", 640, 720)]
    [InlineData("QuickSettingsPanel", 380, 560)]
    [InlineData("NotificationPanel", 380, 560)]
    [InlineData("CalendarPanel", 380, 480)]
    public void Shell_parts(string typeName, int width, int height)
    {
        var type = typeof(App).Assembly.GetTypes().First(t => t.Name == typeName);
        Control part;
        // pictures only: a part that needs something this machine hasn't got is skipped, and says why
        try { part = (Control)Activator.CreateInstance(type)!; }
        catch (Exception e) { Console.WriteLine($"Shell-{typeName}: not drawn ({(e.InnerException ?? e).Message})"); return; }
        // on the desktop's wallpaper, as they're seen; the panels in the flyout that holds them in the shell
        Control content = part;
        if (typeName != "Taskbar")
        {
            var flyout = new Border { Margin = new Avalonia.Thickness(12), CornerRadius = new Avalonia.CornerRadius(14), BorderThickness = new Avalonia.Thickness(1), ClipToBounds = true, Child = part };
            flyout.Bind(Border.BackgroundProperty, flyout.GetResourceObservable("FlyoutPresenterBackground"));
            flyout.Bind(Border.BorderBrushProperty, flyout.GetResourceObservable("FlyoutBorderThemeBrush"));
            content = flyout;
        }
        var w = new Window { Width = width, Height = height, Content = content };
        try { w.Background = new Avalonia.Media.ImageBrush(new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://RedStarOS/Assets/wallpaper.jpg")))) { Stretch = Avalonia.Media.Stretch.UniformToFill }; }
        catch (Exception) { }
        try { ShowAndCapture(w, "Shell-" + typeName); }
        catch (Exception e) { Console.WriteLine($"Shell-{typeName}: not drawn ({e.Message})"); }
    }
}
