// SPDX-License-Identifier: GPL-3.0-or-later
using RedStarOS.Views;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>Window titles on the taskbar narrow a step at a time as windows open, instead of all vanishing at once.</summary>
public class TaskbarLabelTests
{
    [Fact]
    public void A_few_windows_get_full_titles() => Assert.Equal(130, Taskbar.LabelWidth(900, 4, 2));

    [Fact]
    public void Titles_narrow_in_steps_as_windows_open()
    {
        double prev = double.MaxValue;
        for (int n = 1; n <= 12; n++)
        {
            double w = Taskbar.LabelWidth(900, n + 2, n);
            Assert.True(w <= prev, $"{n} windows: {w} after {prev}");
            Assert.True(w == 0 || (w >= 48 && w % 10 == 0), $"{n} windows: {w}");
            prev = w;
        }
        Assert.True(Taskbar.LabelWidth(900, 8, 6) > 0);       // six windows still have titles (the old rule cut them at five)
    }

    [Fact]
    public void A_crowded_row_or_no_room_is_icons_only()
    {
        Assert.Equal(0, Taskbar.LabelWidth(900, 20, 18));
        Assert.Equal(0, Taskbar.LabelWidth(0, 3, 3));
        Assert.Equal(0, Taskbar.LabelWidth(900, 3, 0));
    }
}
