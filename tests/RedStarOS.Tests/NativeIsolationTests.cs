// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

public class NativeIsolationTests
{
    [Fact]
    public void Skia_and_HarfBuzz_load_with_their_own_symbols()
    {
        // twice is harmless (dlopen counts references); on Linux both libraries ship with the shell
        NativeIsolation.Apply();
        NativeIsolation.Apply();
        if (System.OperatingSystem.IsLinux() && File.Exists(Path.Combine(System.AppContext.BaseDirectory, "libHarfBuzzSharp.so")))
            Assert.True(NativeIsolation.Loaded >= 2);
    }
}
