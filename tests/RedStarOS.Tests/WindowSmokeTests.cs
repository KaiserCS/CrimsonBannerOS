using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>
/// Every window must open and close without throwing. This catches broken XAML (bad names, bad key gestures,
/// missing resources) that compiles fine but crashes the app when the window is opened.
/// </summary>
public class WindowSmokeTests
{
    public static IEnumerable<object[]> Windows() =>
        typeof(App).Assembly.GetTypes()
            .Where(t => typeof(Window).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.Name)
            .Select(t => new object[] { t.Name });

    // windows that need hardware or native libraries the build machine may not have
    private static bool Environmental(Exception e) =>
        e is DllNotFoundException or EntryPointNotFoundException ||
        e.GetType().Name.Contains("VLC", StringComparison.OrdinalIgnoreCase) ||
        e.Message.Contains("libvlc", StringComparison.OrdinalIgnoreCase) ||      // VideoWindow without VLC installed
        (e.InnerException != null && Environmental(e.InnerException));

    [AvaloniaTheory]
    [MemberData(nameof(Windows))]
    public void Opens_and_closes(string typeName)
    {
        var type = typeof(App).Assembly.GetTypes().First(t => t.Name == typeName);
        Window? w = null;
        try
        {
            w = (Window)Activator.CreateInstance(type)!;
            w.Show();
            Dispatcher.UIThread.RunJobs();
            w.Close();
            Dispatcher.UIThread.RunJobs();
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null && Environmental(tie.InnerException)) { }
        catch (Exception e) when (Environmental(e)) { }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw new Xunit.Sdk.XunitException($"{typeName} failed to open: {tie.InnerException}");
        }
    }
}
