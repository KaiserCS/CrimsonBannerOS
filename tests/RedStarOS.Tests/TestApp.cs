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
    /// With REDSTAR_SCREENSHOTS set, windows are really drawn (Skia) so ScreenshotTests can save pictures of them;
    /// otherwise the faster drawing-free headless renderer is used.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REDSTAR_SCREENSHOTS"))
            ? AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions())
            : AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
