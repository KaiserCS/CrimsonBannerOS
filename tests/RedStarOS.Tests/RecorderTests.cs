// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using RedStarOS.Services;
using RedStarOS.Views;
using Xunit;

namespace RedStarOS.Tests;

public class RecorderTests
{
    [Fact]
    public void Wayland_recording_uses_wf_recorder_with_the_area()
    {
        var (exe, a) = ScreenRecorderWindow.Command(true, "/usr/bin/wf-recorder", "/tmp/r.mp4", new PixelRect(10, 20, 300, 200), new PixelRect(0, 0, 1920, 1080), true, ":0");
        Assert.Equal("/usr/bin/wf-recorder", exe);
        Assert.Equal(new[] { "-g", "10,20 300x200", "--audio", "-f", "/tmp/r.mp4" }, a);
    }

    [Fact]
    public void X11_recording_uses_ffmpeg_with_even_sizes()
    {
        var (_, a) = ScreenRecorderWindow.Command(false, "ffmpeg", "/tmp/r.mp4", new PixelRect(5, 6, 301, 201), new PixelRect(0, 0, 1920, 1080), false, ":1");
        Assert.Contains("300x200", a);
        Assert.Contains(":1+5,6", a);
        Assert.DoesNotContain("pulse", a);
        Assert.Equal("/tmp/r.mp4", a[^1]);
    }

    [Fact]
    public void Voice_memos_record_to_ogg_opus_and_finish_the_file_on_stop()
    {
        var a = VoiceMemosWindow.RecordArgs("/tmp/m.ogg");
        Assert.Equal("-e", a[0]);                     // end-of-stream on Ctrl+C: the file is closed properly
        Assert.Contains("opusenc", a);
        Assert.Contains("location=/tmp/m.ogg", a);
    }

    [Fact]
    public void Times_read_like_a_clock()
    {
        Assert.Equal("0:05", Recorder.Format(System.TimeSpan.FromSeconds(5)));
        Assert.Equal("2:05", Recorder.Format(System.TimeSpan.FromSeconds(125)));
        Assert.Equal("1:02:05", Recorder.Format(System.TimeSpan.FromSeconds(3725)));
    }
}
