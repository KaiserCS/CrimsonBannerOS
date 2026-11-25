// SPDX-License-Identifier: GPL-3.0-or-later
using System.Linq;
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>A new account, the live session and the login screen start in Crimson Banner OS's own look.</summary>
public class DefaultsTests
{
    [Fact]
    public void A_new_account_is_dark_with_the_crimson_accent()
    {
        var s = new Settings();
        Assert.Equal("Dark", s.Theme);
        Assert.Equal("Crimson", ThemeService.Accents.Single(a => a.Hex.Equals(s.Accent, System.StringComparison.OrdinalIgnoreCase)).Name);
    }
}
