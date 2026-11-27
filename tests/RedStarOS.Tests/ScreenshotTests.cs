using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using RedStarOS.Services;
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

    // ─────────── Atlas (apps/atlas), on its example island ───────────
    [AvaloniaTheory]
    [InlineData("map", "night")]
    [InlineData("map-search", "night")]
    [InlineData("map-street", "day")]
    [InlineData("maps", "night")]
    [InlineData("about", "night")]
    public void Atlas_pages(string page, string palette)
    {
        var dir = Path.Combine(Path.GetTempPath(), "atlas-shot-" + Guid.NewGuid().ToString("N"));
        try { ShowAndCapture(CrimsonAtlas.Views.AtlasWindow.Sample(page, dir, palette), "Atlas-" + page); }
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

    private static Window OnWallpaper(Control content, int width, int height)
    {
        var w = new Window { Width = width, Height = height, Content = content };
        try { w.Background = new Avalonia.Media.ImageBrush(new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://RedStarOS/Assets/wallpaper.jpg")))) { Stretch = Avalonia.Media.Stretch.UniformToFill }; }
        catch (Exception) { }
        return w;
    }

    // ─────────── the desktop's icons, Snap Assist and a taskbar preview ───────────
    [AvaloniaFact]
    public void Shell_desktop_icons()
    {
        var dir = Path.Combine(Path.GetTempPath(), "desk-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Projects"));
        Directory.CreateDirectory(Path.Combine(dir, "Photos from the march"));
        File.WriteAllText(Path.Combine(dir, "Meeting notes.txt"), "notes");
        File.WriteAllText(Path.Combine(dir, "Budget 2027.xlsx"), "");
        File.WriteAllText(Path.Combine(dir, "Speech.docx"), "");
        File.WriteAllText(Path.Combine(dir, "Anthem.mp3"), "");
        // a photo, to be shown as itself
        var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(120, 90));
        using (var ctx = bmp.CreateDrawingContext())
        {
            ctx.FillRectangle(new Avalonia.Media.LinearGradientBrush
            {
                StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative), EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
                GradientStops = { new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#E8344B"), 0), new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#2A1A2E"), 1) },
            }, new Avalonia.Rect(0, 0, 120, 90));
            ctx.FillRectangle(Avalonia.Media.Brushes.Black, new Avalonia.Rect(0, 62, 120, 28));
        }
        bmp.Save(Path.Combine(dir, "Sunset.png"));
        try
        {
            var icons = new DesktopIconsView(() => dir, Path.Combine(dir, ".layout.json"));
            var w = OnWallpaper(icons, 760, 460);
            w.Opened += (_, _) =>
            {
                icons.Reload();
                icons.Select(icons.Items.Where(i => i.Name is "Speech.docx" or "Sunset.png"));
            };
            ShowAndCapture(w, "Shell-DesktopIcons");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [AvaloniaFact]
    public void Shell_snap_assist()
    {
        var wins = new List<X11Desktop.XWin>
        {
            new(11, "Files — Documents", "files", 0, false), new(12, "Writer — Speech.docx", "writer", 0, false),
            new(13, "Crimson Online — News", "online", 0, false), new(14, "Terminal", "xterm", 0, false), new(15, "Calculator", "calculator", 0, true),
        };
        var w = new SnapAssist(wins, "right") { Topmost = false };
        ShowAndCapture(w, "Shell-SnapAssist");
    }

    [AvaloniaFact]
    public void Shell_task_view()
    {
        var owner = WindowManager.Desktop;
        WindowManager.Desktop = null;                 // (another test may have left a closed one)
        var calc = WindowManager.Open(new CalculatorWindow(), "calculator");
        var notes = WindowManager.Open(new NotesWindow(), "notes");
        var names = SettingsStore.Current.DesktopNames;
        var saved = new List<string>(names);
        try
        {
            names.Clear(); names.AddRange(new[] { "", "Writing" });
            VirtualDesktops.FromWindowManager(3, 0, null);
            Dispatcher.UIThread.RunJobs();
            var w = new TaskViewWindow { Width = 1280, Height = 720, Topmost = false };
            ShowAndCapture(w, "Shell-TaskView");
        }
        finally
        {
            calc.Close(); notes.Close();
            WindowManager.Desktop = owner;
            names.Clear(); names.AddRange(saved);
            VirtualDesktops.Reset();
        }
    }

    [AvaloniaFact]
    public void Shell_desktop_sign()
    {
        VirtualDesktops.FromWindowManager(4, 1, null);
        try { ShowAndCapture(OnWallpaper(new Border { Padding = new Avalonia.Thickness(24), Child = DesktopWindow.DesktopSign(1) }, 320, 170), "Shell-DesktopSign"); }
        finally { VirtualDesktops.Reset(); }
    }

    [AvaloniaFact]
    public void Settings_night_light()
    {
        var p = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(24) };
        SettingsPages.NightLightSection(p);
        var w = new Window { Width = 720, Height = 360, Content = p };
        w.Bind(Window.BackgroundProperty, w.GetResourceObservable("ShellBackdropBrush"));
        ShowAndCapture(w, "Shell-NightLight");
    }

    [AvaloniaFact]
    public void Shell_taskbar_preview()
    {
        var calc = new CalculatorWindow();
        calc.Show();
        try
        {
            var card = TaskPreview.Card(new NativeTask(calc, "calculator"));
            ShowAndCapture(OnWallpaper(new Border { Padding = new Avalonia.Thickness(16), Child = card, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top }, 280, 230), "Shell-TaskbarPreview");
        }
        finally { calc.Close(); }
    }

    // ─────────── the shell's own parts (the taskbar, the Start menu and the panels), each in a window of its size ───────────
    [AvaloniaTheory]
    [InlineData("Taskbar", 1280, 56)]
    [InlineData("StartMenu", 790, 640)]
    [InlineData("QuickSettingsPanel", 380, 560)]
    [InlineData("NotificationPanel", 380, 560)]
    [InlineData("CalendarPanel", 380, 600)]
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
