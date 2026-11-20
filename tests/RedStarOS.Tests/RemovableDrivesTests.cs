// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RedStarOS.Services;
using RedStarOS.Services.DBus;
using Xunit;

namespace RedStarOS.Tests;

public class RemovableDrivesTests
{
    private static Variant V(string sig, object v) => new(sig, v);
    private static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    private static Dictionary<object, object> Objects() => new()
    {
        // a USB stick, not mounted
        [new ObjectPath("/org/freedesktop/UDisks2/block_devices/sdb1")] = new Dictionary<object, object>
        {
            ["org.freedesktop.UDisks2.Block"] = new Dictionary<object, object>
            {
                ["Device"] = V("ay", Z("/dev/sdb1")), ["IdLabel"] = V("s", "STICK"), ["Size"] = V("t", 16_000_000_000UL),
                ["HintIgnore"] = V("b", false), ["HintSystem"] = V("b", false), ["Drive"] = V("o", new ObjectPath("/org/freedesktop/UDisks2/drives/Stick")),
            },
            ["org.freedesktop.UDisks2.Filesystem"] = new Dictionary<object, object> { ["MountPoints"] = V("aay", new List<object>()) },
        },
        [new ObjectPath("/org/freedesktop/UDisks2/drives/Stick")] = new Dictionary<object, object>
        {
            ["org.freedesktop.UDisks2.Drive"] = new Dictionary<object, object>
            {
                ["Removable"] = V("b", true), ["ConnectionBus"] = V("s", "usb"), ["CanPowerOff"] = V("b", true), ["Vendor"] = V("s", "Acme"), ["Model"] = V("s", "Stick"),
            },
        },
        // the computer's own root disk: left out
        [new ObjectPath("/org/freedesktop/UDisks2/block_devices/nvme0n1p2")] = new Dictionary<object, object>
        {
            ["org.freedesktop.UDisks2.Block"] = new Dictionary<object, object>
            {
                ["Device"] = V("ay", Z("/dev/nvme0n1p2")), ["HintSystem"] = V("b", true), ["Drive"] = V("o", new ObjectPath("/")),
            },
            ["org.freedesktop.UDisks2.Filesystem"] = new Dictionary<object, object> { ["MountPoints"] = V("aay", new List<object> { Z("/") }) },
        },
        // a mounted SD card without a label
        [new ObjectPath("/org/freedesktop/UDisks2/block_devices/mmcblk0p1")] = new Dictionary<object, object>
        {
            ["org.freedesktop.UDisks2.Block"] = new Dictionary<object, object>
            {
                ["Device"] = V("ay", Z("/dev/mmcblk0p1")), ["Size"] = V("t", 32_000_000_000UL), ["HintSystem"] = V("b", false), ["Drive"] = V("o", new ObjectPath("/")),
            },
            ["org.freedesktop.UDisks2.Filesystem"] = new Dictionary<object, object> { ["MountPoints"] = V("aay", new List<object> { Z("/run/media/me/SD") }) },
        },
    };

    [Fact]
    public void Removable_and_extra_drives_are_listed_and_the_systems_own_disk_is_not()
    {
        var list = RemovableDrives.Parse(Objects());
        Assert.Equal(2, list.Count);
        var stick = list.Single(v => v.Device == "/dev/sdb1");
        Assert.Equal("STICK", stick.Label);
        Assert.True(stick.Removable);
        Assert.True(stick.CanPowerOff);
        Assert.Null(stick.MountPoint);
        var sd = list.Single(v => v.Device == "/dev/mmcblk0p1");
        Assert.Equal("/run/media/me/SD", sd.MountPoint);
        Assert.EndsWith("drive", sd.Label);
    }
}
