using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RedStarOS.Services;

/// <summary>
/// Short Red Star system sounds (start-up, notification, error). They are synthesised once into small WAV files
/// in the cache folder — no sound files to ship — and played with paplay / pw-play / aplay. Off by default.
/// </summary>
public static class SystemSounds
{
    private static string Dir => Path.Combine(AppPaths.ConfigDir, "sounds");

    public static bool Enabled
    {
        get => File.Exists(Path.Combine(AppPaths.ConfigDir, "sounds-on"));
        set
        {
            var f = Path.Combine(AppPaths.ConfigDir, "sounds-on");
            try { if (value) File.WriteAllText(f, "1"); else if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    // (frequency Hz, start s, length s) notes
    private static readonly Dictionary<string, (double F, double T, double L)[]> Tunes = new()
    {
        ["startup"] = new[] { (523.25, 0.0, 0.5), (659.25, 0.12, 0.5), (783.99, 0.24, 0.6), (1046.5, 0.36, 0.8) },
        ["notify"] = new[] { (880.0, 0.0, 0.18), (1318.5, 0.12, 0.32) },
        ["error"] = new[] { (220.0, 0.0, 0.18), (196.0, 0.16, 0.26) },
        ["login"] = new[] { (659.25, 0.0, 0.3), (987.77, 0.1, 0.45) },
    };

    /// <summary>Plays a sound (when sounds are on, unless <paramref name="force"/>).</summary>
    public static void Play(string name, bool force = false)
    {
        if (!force && !Enabled) return;
        if (!Tunes.ContainsKey(name)) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var file = Path.Combine(Dir, name + ".wav");
            if (!File.Exists(file)) File.WriteAllBytes(file, Render(Tunes[name]));
            foreach (var player in new[] { "paplay", "pw-play", "aplay" })
                if (ProcessUtil.Which(player) is { } p)
                {
                    ProcessUtil.StartDetached(p, player == "aplay" ? new[] { "-q", file } : new[] { file });
                    return;
                }
        }
        catch { }
    }

    private static byte[] Render((double F, double T, double L)[] notes)
    {
        const int rate = 44100;
        double total = 0;
        foreach (var n in notes) total = Math.Max(total, n.T + n.L);
        int count = (int)(total * rate) + rate / 20;
        var buf = new double[count];
        foreach (var (f, t, l) in notes)
        {
            int start = (int)(t * rate), len = (int)(l * rate);
            for (int i = 0; i < len && start + i < count; i++)
            {
                double x = (double)i / rate;
                double env = Math.Min(1, x / 0.008) * Math.Exp(-x * 5.5 / l);             // quick attack, soft decay
                double s = Math.Sin(2 * Math.PI * f * x) + 0.25 * Math.Sin(4 * Math.PI * f * x); // a little brightness
                buf[start + i] += 0.22 * env * s;
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + count * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(count * 2);
        foreach (var v in buf) w.Write((short)(Math.Clamp(v, -1, 1) * 32000));
        w.Flush();
        return ms.ToArray();
    }
}
