using Avalonia;
using Avalonia.Headless;
using RedStarOS.Tests;

// The app is Linux-only (see Program.cs); so are its tests.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace RedStarOS.Tests;

/// <summary>Runs the real App (styles, resources, theme) without a screen.</summary>
public static class TestAppBuilder
{
    static TestAppBuilder()
    {
        // keep the tests away from the real user's settings, history and documents
        var home = Path.Combine(Path.GetTempPath(), "redstar-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(home, ".config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(home, ".local", "share"));   // Trash, wallpapers…
    }

    /// <summary>
    /// Windows are really drawn (Skia), always: tests that look at pixels (a taskbar preview, an icon on its tile) or
    /// at a picture's real size (the image viewer) need it, and ./check.sh must run them exactly as CI does
    /// (PRINCIPLES.md, 4). REDSTAR_SCREENSHOTS only decides whether ScreenshotTests also save their pictures.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
