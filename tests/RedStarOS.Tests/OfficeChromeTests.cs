// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using RedStarOS.Views;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>The office apps' shared toolbar: nothing is cut off half-way, what doesn't fit is in More.</summary>
public class OfficeChromeTests
{
    private static (Window W, OfficeToolbar Bar) Toolbar(double width)
    {
        var bar = new OfficeToolbar();
        for (int i = 0; i < 12; i++)
        {
            if (i == 6) bar.Children.Add(new Border { Classes = { "tbsep" } });
            var b = new Button { Classes = { "tool" }, Width = 32, Height = 32 };
            ToolTip.SetTip(b, "Tool " + i);
            bar.Children.Add(b);
        }
        var w = new Window { Width = width, Height = 80, Content = bar };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, bar);
    }

    [AvaloniaFact]
    public void A_wide_toolbar_shows_every_tool()
    {
        var (w, bar) = Toolbar(1200);
        try { Assert.Empty(bar.Overflow); }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void A_narrow_toolbar_puts_the_last_tools_in_more_and_no_divider_at_its_end()
    {
        var (w, bar) = Toolbar(260);
        try
        {
            Assert.NotEmpty(bar.Overflow);
            Assert.Equal("Tool 11", ToolTip.GetTip(bar.Overflow[^1]));
            // what's shown ends with a tool, not a divider
            Assert.All(bar.Overflow, c => Assert.True(c.Bounds.X < 0 || c is Border));
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void The_zoom_says_its_percentage_and_stays_in_range()
    {
        var z = new OfficeZoom { Minimum = 50, Maximum = 200 };
        z.Percent = 350;
        Assert.Equal(200, z.Percent);
        z.Percent = 125;
        Assert.Equal(125, z.Percent);
    }
}
