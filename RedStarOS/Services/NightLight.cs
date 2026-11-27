// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Threading;

namespace RedStarOS.Services;

/// <summary>
/// Night light, as on Windows: a warmer screen in the evening, by hand or from sunset to sunrise (worked out offline
/// from the time zone's place in the system's zone.tab) or between set hours. The screen's colours go through its gamma
/// ramp: XRandR on X11 (<see cref="X11Desktop.SetGamma"/>), the display hardware inside redstar-comp
/// (<see cref="CompIpc.NightLight"/>). Nothing is drawn, so it costs nothing.
/// </summary>
public static class NightLight
{
    public const int Neutral = 6500, Warmest = 2000, Coolest = 5500;

    /// <summary>The colour temperature on the screen now (6500 K: no tint).</summary>
    public static int AppliedKelvin { get; private set; } = Neutral;
    /// <summary>Whether the screens can be tinted: null until tried, false when none took it.</summary>
    public static bool? Supported { get; private set; }
    /// <summary>On, off or a setting changed (UI thread).</summary>
    public static event Action? Changed;

    public static bool On => SettingsStore.Current.NightLight;

    private static DispatcherTimer? _timer, _fade;
    private static bool? _lastScheduled;
    private static int _fadeFrom, _fadeTo, _fadeStep;
    private const int FadeSteps = 20;

    // ─────────── colour ───────────
    /// <summary>
    /// Red, green and blue multipliers for a colour temperature (1000–6500 K), relative to daylight (6500 K = 1, 1, 1):
    /// Tanner Helland's fit to the black-body colours, normalised so daylight changes nothing.
    /// </summary>
    public static (double R, double G, double B) Multipliers(int kelvin)
    {
        var (r, g, b) = Blackbody(Math.Clamp(kelvin, 1000, Neutral));
        var (r0, g0, b0) = Blackbody(Neutral);
        return (Math.Min(1, r / r0), Math.Min(1, g / g0), Math.Min(1, b / b0));
    }

    private static (double R, double G, double B) Blackbody(int kelvin)
    {
        double t = kelvin / 100.0;
        double r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        double g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        double b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        return (Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
    }

    /// <summary>One entry of a gamma ramp of `size` entries, scaled by a multiplier (0–65535).</summary>
    public static ushort RampValue(int k, int size, double multiplier) =>
        (ushort)Math.Clamp(Math.Round((size > 1 ? k / (double)(size - 1) : 1) * Math.Clamp(multiplier, 0, 1) * 65535), 0, 65535);

    // ─────────── the sun ───────────
    /// <summary>
    /// Sunrise and sunset (UTC) on a day at a place (latitude north, longitude east, in degrees): the sunrise equation
    /// with refraction (−0.833°). Null rise and set: the sun doesn't rise (polar night: dark all day = true) or set.
    /// </summary>
    public static (DateTime? Rise, DateTime? Set, bool DarkAllDay) SunTimes(DateOnly day, double lat, double lon)
    {
        double Rad(double d) => d * Math.PI / 180;
        double jd = day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc).Subtract(DateTime.UnixEpoch).TotalDays + 2440587.5;
        double n = Math.Round(jd - 2451545.0);          // days since J2000.0 (jd is at noon, a whole number)
        double jStar = n - lon / 360.0;
        double m = (357.5291 + 0.98560028 * jStar) % 360;
        double c = 1.9148 * Math.Sin(Rad(m)) + 0.02 * Math.Sin(Rad(2 * m)) + 0.0003 * Math.Sin(Rad(3 * m));
        double lambda = (m + c + 180 + 102.9372) % 360;
        double transit = 2451545.0 + jStar + 0.0053 * Math.Sin(Rad(m)) - 0.0069 * Math.Sin(Rad(2 * lambda));
        double sinDec = Math.Sin(Rad(lambda)) * Math.Sin(Rad(23.4397));
        double cosDec = Math.Cos(Math.Asin(sinDec));
        double cosW = (Math.Sin(Rad(-0.833)) - Math.Sin(Rad(lat)) * sinDec) / (Math.Cos(Rad(lat)) * cosDec);
        if (cosW > 1) return (null, null, true);         // the sun stays below the horizon
        if (cosW < -1) return (null, null, false);       // the midnight sun
        double w = Math.Acos(cosW) * 180 / Math.PI;
        DateTime FromJd(double j) => DateTime.UnixEpoch.AddDays(j - 2440587.5);
        return (FromJd(transit - w / 360), FromJd(transit + w / 360), false);
    }

    /// <summary>Where a time zone is (its main city), from zone1970.tab / zone.tab; null when it isn't listed.</summary>
    public static (double Lat, double Lon)? PlaceOf(string tz, string? tabText = null)
    {
        string? text = tabText;
        if (text == null)
            foreach (var f in new[] { "/usr/share/zoneinfo/zone1970.tab", "/usr/share/zoneinfo/zone.tab" })
                if (File.Exists(f)) { try { text = File.ReadAllText(f); break; } catch { } }
        if (text == null) return null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith('#')) continue;
            var f = line.Split('\t');
            if (f.Length >= 3 && f[2].Trim() == tz && ParseCoordinates(f[1]) is { } p) return p;
        }
        return null;
    }

    /// <summary>ISO 6709 as zone.tab writes it: ±DDMM±DDDMM or ±DDMMSS±DDDMMSS.</summary>
    public static (double Lat, double Lon)? ParseCoordinates(string s)
    {
        int split = s.IndexOfAny(new[] { '+', '-' }, 1);
        if (split < 0) return null;
        double? Part(string p, int degDigits)
        {
            if (p.Length < 1 + degDigits + 2) return null;
            int sign = p[0] == '-' ? -1 : 1;
            var digits = p[1..];
            if (!int.TryParse(digits[..degDigits], out var d) || !int.TryParse(digits.Substring(degDigits, 2), out var m)) return null;
            int sec = digits.Length >= degDigits + 4 && int.TryParse(digits.Substring(degDigits + 2, 2), out var ss) ? ss : 0;
            return sign * (d + m / 60.0 + sec / 3600.0);
        }
        var lat = Part(s[..split], 2);
        var lon = Part(s[split..], 3);
        return lat is { } a && lon is { } o ? (a, o) : null;
    }

    /// <summary>Today's sunset and tomorrow's sunrise (local), for the settings page; null when they can't be worked out.</summary>
    public static (DateTime Sunset, DateTime Sunrise)? NextNight(DateTime now)
    {
        if (PlaceOf(TimeZoneInfo.Local.Id) is not { } p) return null;
        var today = SunTimes(DateOnly.FromDateTime(now), p.Lat, p.Lon);
        // before today's sunrise the night is the one that began yesterday
        if (today.Rise is { } r && now < r.ToLocalTime() && SunTimes(DateOnly.FromDateTime(now.AddDays(-1)), p.Lat, p.Lon).Set is { } ySet)
            return (ySet.ToLocalTime(), r.ToLocalTime());
        var tomorrow = SunTimes(DateOnly.FromDateTime(now.AddDays(1)), p.Lat, p.Lon);
        if (today.Set is not { } set || tomorrow.Rise is not { } rise) return null;
        return (set.ToLocalTime(), rise.ToLocalTime());
    }

    // ─────────── the schedule ───────────
    /// <summary>
    /// Whether the schedule wants night light at a moment (public for the tests): "sunset" (from the place), "hours"
    /// (from–to, across midnight when to is earlier), or null for "off" (only by hand).
    /// </summary>
    public static bool? Scheduled(DateTime localNow, string schedule, string from, string to, (double Lat, double Lon)? place)
    {
        switch (schedule)
        {
            case "hours":
                if (!TimeOnly.TryParse(from, CultureInfo.InvariantCulture, out var f) || !TimeOnly.TryParse(to, CultureInfo.InvariantCulture, out var t)) return null;
                var now = TimeOnly.FromDateTime(localNow);
                return f <= t ? now >= f && now < t : now >= f || now < t;
            case "sunset":
                if (place is not { } p) return null;
                var (rise, set, dark) = SunTimes(DateOnly.FromDateTime(localNow), p.Lat, p.Lon);
                if (rise is not { } r || set is not { } s) return dark;
                var utc = localNow.Kind == DateTimeKind.Utc ? localNow : localNow.ToUniversalTime();
                return utc < r || utc >= s;
            default:
                return null;
        }
    }

    // ─────────── on the screen ───────────
    /// <summary>Starts following the settings and the schedule (once the window manager or compositor is there).</summary>
    public static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Evaluate();
        _timer.Start();
        Evaluate(animate: false);
    }

    /// <summary>Back to neutral colours (signing out, the shell closing): an X server keeps a ramp after we've gone.</summary>
    public static void Stop()
    {
        _timer?.Stop(); _timer = null;
        _fade?.Stop();
        if (AppliedKelvin != Neutral) Send(Neutral);
        AppliedKelvin = Neutral;
    }

    /// <summary>Turns it on or off by hand (the schedule takes over again at its next change).</summary>
    public static void Set(bool on)
    {
        SettingsStore.Current.NightLight = on;
        SettingsStore.Save();
        Evaluate();
        Changed?.Invoke();
    }

    public static void Toggle() => Set(!On);

    /// <summary>A new warmth (the settings slider): shown at once while it's on.</summary>
    public static void SetKelvin(int kelvin)
    {
        SettingsStore.Current.NightLightKelvin = Math.Clamp(kelvin, Warmest, Coolest);
        SettingsStore.Save();
        if (On) Show(SettingsStore.Current.NightLightKelvin, animate: false);
        Changed?.Invoke();
    }

    /// <summary>Applies the schedule (only when it changes, so turning it off by hand in the evening sticks) and the setting.</summary>
    public static void Evaluate(bool animate = true)
    {
        var s = SettingsStore.Current;
        var want = Scheduled(DateTime.Now, s.NightLightSchedule, s.NightLightFrom, s.NightLightTo,
            s.NightLightSchedule == "sunset" ? PlaceOf(TimeZoneInfo.Local.Id) : null);
        if (want is bool w && w != _lastScheduled)
        {
            bool first = _lastScheduled == null;
            _lastScheduled = w;
            if (!first || w != s.NightLight) { s.NightLight = w; SettingsStore.Save(); Changed?.Invoke(); }
        }
        if (want == null) _lastScheduled = null;
        Show(s.NightLight ? s.NightLightKelvin : Neutral, animate);
    }

    /// <summary>For the tests: forget the schedule's last state.</summary>
    public static void Reset() { _lastScheduled = null; _fade?.Stop(); AppliedKelvin = Neutral; }

    private static void Show(int kelvin, bool animate)
    {
        if (kelvin == AppliedKelvin && _fade?.IsEnabled != true) return;
        _fade?.Stop();
        if (!animate || SettingsStore.Current.ReduceMotion)
        {
            AppliedKelvin = kelvin;
            Send(kelvin);
            return;
        }
        // a two-second fade, as on Windows
        _fadeFrom = AppliedKelvin; _fadeTo = kelvin; _fadeStep = 0;
        _fade ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _fade.Tick -= FadeTick;
        _fade.Tick += FadeTick;
        _fade.Start();
    }

    private static void FadeTick(object? sender, EventArgs e)
    {
        _fadeStep++;
        double k = _fadeFrom + (_fadeTo - _fadeFrom) * Math.Min(1, _fadeStep / (double)FadeSteps);
        AppliedKelvin = (int)Math.Round(k);
        Send(AppliedKelvin);
        if (_fadeStep >= FadeSteps) _fade?.Stop();
    }

    private static void Send(int kelvin)
    {
        var (r, g, b) = Multipliers(kelvin);
        if (CompIpc.Connected)
            _ = CompIpc.NightLight(r, g, b).ContinueWith(t =>
            {
                if (t.Result is { } res) Dispatcher.UIThread.Post(() => Report(res.Applied, res.Failed));
            });
        else if (X11Desktop.Available) X11Desktop.SetGamma(r, g, b, Report);
    }

    private static void Report(int applied, int failed)
    {
        bool? was = Supported;
        Supported = applied > 0 || (failed == 0 ? Supported : false);
        if (was != Supported) Changed?.Invoke();
    }
}
