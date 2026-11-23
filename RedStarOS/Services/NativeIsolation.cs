// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RedStarOS.Services;

/// <summary>
/// Avalonia draws text with SkiaSharp and HarfBuzzSharp, whose native libraries carry their own copies of HarfBuzz and
/// FreeType. The system's copies are in the same process too (fontconfig loads FreeType, FreeType loads HarfBuzz),
/// and with the default symbol lookup a call inside libHarfBuzzSharp can land in the system's libharfbuzz, a
/// different version: the shell crashed in hb_shape_plan_create_cached2 (seen on the live ISO, 23 Nov 2026).
/// Loading both libraries first with RTLD_DEEPBIND, and without adding them to the global scope, makes each use its
/// own copy. .NET's own loading later finds them already loaded. REDSTAR_NO_DEEPBIND=1 turns this off.
/// </summary>
public static class NativeIsolation
{
    private const int RtldNow = 0x2, RtldDeepBind = 0x8;

    [DllImport("libc.so.6", EntryPoint = "dlopen")]
    private static extern IntPtr DlOpen(string file, int mode);

    public static readonly string[] Libraries = { "libHarfBuzzSharp.so", "libSkiaSharp.so" };

    /// <summary>The libraries that were preloaded (for Diagnostics and tests).</summary>
    public static int Loaded { get; private set; }

    public static void Apply()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("REDSTAR_NO_DEEPBIND") == "1") return;
        var dirs = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"),
            Path.Combine(AppContext.BaseDirectory, "runtimes",
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64", "native")
        };
        foreach (var name in Libraries)
        {
            foreach (var dir in dirs)
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                try { if (DlOpen(path, RtldNow | RtldDeepBind) != IntPtr.Zero) Loaded++; }
                catch { /* not glibc: the default loading applies */ }
                break;
            }
        }
    }
}
