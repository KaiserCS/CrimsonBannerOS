using System.Text;
using Avalonia.Headless.XUnit;
using RedStarOS.Services;
using RedStarOS.Views;
using Xunit;

namespace RedStarOS.Tests;

[Collection("environment")]     // reads XDG_DATA_HOME (wallpapers), which the file tests change
public class FirstRunTests
{
    [Fact]
    public void MozLz4_decodes_literals_and_overlapping_matches()
    {
        // "abc" then a 9-byte match at offset 3 → "abcabcabcabc", then a literal-only tail "!"
        var block = new byte[] { 0x35, (byte)'a', (byte)'b', (byte)'c', 0x03, 0x00, 0x10, (byte)'!' };
        var file = Encoding.ASCII.GetBytes("mozLz40\0").Concat(BitConverter.GetBytes(13)).Concat(block).ToArray();
        Assert.Equal("abcabcabcabc!", Encoding.ASCII.GetString(BrowserImport.MozLz4(file)));
    }

    [Fact]
    public void MozLz4_rejects_other_files() =>
        Assert.Throws<InvalidDataException>(() => BrowserImport.MozLz4(Encoding.ASCII.GetBytes("{\"not\":\"lz4\"}")));

    [Fact]
    public void Detect_does_not_throw() => Assert.NotNull(BrowserImport.Detect());

    [Fact]
    public void Diagnostics_export_writes_a_zip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs-diag-" + Guid.NewGuid().ToString("N"));
        Diagnostics.Log("test", new InvalidOperationException("boom"));
        var zip = Diagnostics.Export(dir);
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Contains(z.Entries, e => e.Name == "system.txt");
        Assert.Contains(z.Entries, e => e.Name == "crash.log");
        Directory.Delete(dir, true);
    }

    [AvaloniaFact]
    public void Settings_pages_build()
    {
        Assert.NotNull(SettingsPages.Updates());
        Assert.NotNull(SettingsPages.DiagnosticsPage());
        Assert.NotNull(SettingsPages.Accessibility());
    }

    [Fact]
    public void Online_wallpapers_save_under_Pictures_Wallpapers_with_a_safe_name()
    {
        var r = new WallpaperSearch.Result("wallhaven", "ab/12?x", "General wallpaper", "https://th.example/a.jpg",
            "https://w.example/full/wallhaven-ab12.png", 3840, 2160, "https://wallhaven.cc/w/ab12", null, null, "image/png");
        var p = WallpaperSearch.LocalPath(r);
        Assert.EndsWith(Path.Combine("Pictures", "Wallpapers", "wallhaven-ab12x.png"), p);
        Assert.Equal("4K", r.Quality);
        Assert.Equal("3840 × 2160", r.Resolution);
    }

    [AvaloniaFact]
    public void Red_Star_collection_is_4K()
    {
        var files = WallpaperService.ImagesIn(WallpaperService.CollectionFolder);
        Assert.True(files.Count(f => f.EndsWith("-4k.jpg")) >= 5, "expected the five 4K Red Star wallpapers");
        // read the size from the JPEG header (headless Avalonia doesn't decode images: its bitmaps are 1×1)
        foreach (var f in files.Where(f => f.EndsWith("-4k.jpg")))
            Assert.Equal((3840, 2160), JpegSize(File.ReadAllBytes(f)));
    }

    private static (int W, int H) JpegSize(byte[] b)
    {
        int i = 2;                                              // after FFD8
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            byte marker = b[i + 1];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)   // start of frame
                return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            i += 2 + ((b[i + 2] << 8) | b[i + 3]);              // skip this segment
        }
        return (0, 0);
    }

    [AvaloniaFact]
    public void High_contrast_turns_on_and_off()
    {
        SettingsStore.Current.HighContrast = true;
        ThemeService.Apply();
        Assert.True(Avalonia.Application.Current!.Resources.ContainsKey("HcFocusBrush"));
        SettingsStore.Current.HighContrast = false;
        ThemeService.Apply();
        Assert.False(Avalonia.Application.Current!.Resources.ContainsKey("HcFocusBrush"));
    }
}
