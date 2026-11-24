// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>A virtual machine's display adapter (VMware's vmwgfx) isn't a GPU to draw with; a real one is.</summary>
public class GraphicsTests
{
    private static (string Dev, string Sys) Fake(params (string Node, string Driver)[] nodes)
    {
        var root = Path.Combine(Path.GetTempPath(), "rs-drm-" + Guid.NewGuid().ToString("N"));
        var dev = Path.Combine(root, "dev");
        var sys = Path.Combine(root, "sys");
        Directory.CreateDirectory(dev);
        foreach (var (node, driver) in nodes)
        {
            File.WriteAllText(Path.Combine(dev, node), "");
            var drivers = Path.Combine(root, "bus", "drivers", driver);
            Directory.CreateDirectory(drivers);
            var device = Path.Combine(sys, node, "device");
            Directory.CreateDirectory(device);
            File.CreateSymbolicLink(Path.Combine(device, "driver"), drivers);
        }
        return (dev, sys);
    }

    [Fact]
    public void VMware_graphics_is_not_a_GPU()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (dev, sys) = Fake(("renderD128", "vmwgfx"));
        Assert.Equal("vmwgfx", Graphics.DriverOf(Path.Combine(dev, "renderD128"), sys));
        Assert.False(Graphics.HasRenderNode(dev, sys));
    }

    [Fact]
    public void A_real_GPU_is_one_even_beside_a_virtual_adapter()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (dev, sys) = Fake(("renderD128", "amdgpu"));
        Assert.True(Graphics.HasRenderNode(dev, sys));
        (dev, sys) = Fake(("renderD128", "qxl"), ("renderD129", "i915"));
        Assert.True(Graphics.HasRenderNode(dev, sys));
    }

    [Fact]
    public void No_render_node_is_no_GPU()
    {
        var (dev, sys) = Fake();
        Assert.False(Graphics.HasRenderNode(dev, sys));
        Assert.False(Graphics.HasRenderNode(Path.Combine(dev, "missing"), sys));
    }
}
