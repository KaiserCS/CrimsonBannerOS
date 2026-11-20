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

public class DiskManagerTests
{
    private static Variant V(string sig, object v) => new(sig, v);
    private static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    [Fact]
    public void A_disk_lists_its_partitions_in_order_and_knows_when_the_system_runs_from_it()
    {
        var drive = new ObjectPath("/org/freedesktop/UDisks2/drives/Samsung_SSD");
        Dictionary<object, object> Part(string dev, uint n, ulong off, ulong size, string type, string? mount) => new()
        {
            ["org.freedesktop.UDisks2.Block"] = new Dictionary<object, object>
            {
                ["Device"] = V("ay", Z(dev)), ["Size"] = V("t", size), ["IdType"] = V("s", type), ["Drive"] = V("o", drive),
            },
            ["org.freedesktop.UDisks2.Partition"] = new Dictionary<object, object> { ["Number"] = V("u", n), ["Offset"] = V("t", off), ["Size"] = V("t", size) },
            ["org.freedesktop.UDisks2.Filesystem"] = new Dictionary<object, object>
            {
                ["MountPoints"] = V("aay", mount == null ? new List<object>() : new List<object> { Z(mount) }),
            },
        };
        var objects = new Dictionary<object, object>
        {
            [drive] = new Dictionary<object, object>
            {
                ["org.freedesktop.UDisks2.Drive"] = new Dictionary<object, object> { ["Vendor"] = V("s", "Samsung"), ["Model"] = V("s", "SSD"), ["Size"] = V("t", 500_000_000_000UL) },
            },
            [new ObjectPath("/org/freedesktop/UDisks2/block_devices/nvme0n1")] = new Dictionary<object, object>
            {
                ["org.freedesktop.UDisks2.Block"] = new Dictionary<object, object> { ["Device"] = V("ay", Z("/dev/nvme0n1")), ["Drive"] = V("o", drive) },
                ["org.freedesktop.UDisks2.PartitionTable"] = new Dictionary<object, object> { ["Type"] = V("s", "gpt") },
            },
            [new ObjectPath("/org/freedesktop/UDisks2/block_devices/nvme0n1p2")] = Part("/dev/nvme0n1p2", 2, 1_000_000_000, 400_000_000_000, "ext4", "/"),
            [new ObjectPath("/org/freedesktop/UDisks2/block_devices/nvme0n1p1")] = Part("/dev/nvme0n1p1", 1, 1_000_000, 999_000_000, "vfat", "/boot"),
        };
        var d = Assert.Single(DiskManager.Parse(objects));
        Assert.Equal("Samsung SSD", d.Name);
        Assert.Equal("/dev/nvme0n1", d.Device);
        Assert.Equal(new[] { 1, 2 }, d.Parts.Select(p => p.Number));
        Assert.True(d.InUseBySystem);
        Assert.False(d.Removable);
    }
}
