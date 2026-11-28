using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RedStarOS.Services;
using RedStarOS.Views;

namespace RedStarOS;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Diagnostics.Install();                  // unexpected errors → crash.log, the session keeps running
        SettingsStore.Load();
        PointerTrace.Install();                 // Diagnostics → Record pointer activity (or REDSTAR_TRACE_POINTER=file)
        ThemeService.Apply();
        LookAndFeel.ApplyShellFont();            // System Settings → Appearance → fonts
        LookAndFeel.ApplyEnvironment();          // pointer and Qt look for the programs Red Star starts
        AccessibleNames.Install();               // icon-only buttons get names for screen readers
        SettingsWindow.RegisterSearch();         // settings pages show up in Start-menu search

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the desktop shell ("Shutdown") ends the whole session.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new DesktopWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
