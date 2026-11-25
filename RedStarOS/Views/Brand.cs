// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RedStarOS.Views;

/// <summary>Crimson Banner OS's mark (branding/: the crimson banner with the star), for the shell's own screens.</summary>
public static class Brand
{
    private static Bitmap? _mark;

    /// <summary>The mark, <paramref name="size"/> pixels tall, or nothing if the picture can't be loaded.</summary>
    public static Control Mark(double size)
    {
        try { _mark ??= new Bitmap(AssetLoader.Open(new Uri("avares://RedStarOS/Assets/mark.png"))); }
        catch (Exception) { return new Border { Width = size, Height = size }; }
        var img = new Image { Source = _mark, Width = size, Height = size, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.HighQuality);
        return img;
    }
}
