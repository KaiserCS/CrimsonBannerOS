using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace RedStarOS.Services;

/// <summary>Small in-memory cache of site icons for tabs.</summary>
public static class Favicons
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static Bitmap? TryGet(string url) => Cache.TryGetValue(url, out var b) ? b : null;

    public static async Task<Bitmap?> GetAsync(string url)
    {
        if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://") || url.StartsWith("https://"))) return null;
        if (Cache.TryGetValue(url, out var hit)) return hit;
        Bitmap? bmp = null;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length > 0 && bytes.Length < 512 * 1024)
                bmp = Bitmap.DecodeToWidth(new MemoryStream(bytes), 32);
        }
        catch { }
        if (Cache.Count > 300) Cache.Clear();
        Cache[url] = bmp;
        return bmp;
    }
}
