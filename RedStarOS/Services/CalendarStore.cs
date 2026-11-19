using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RedStarOS.Services;

public sealed class CalEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Date { get; set; } = "";     // yyyy-MM-dd
    public string Time { get; set; } = "";     // HH:mm or empty (all day)
    public string Title { get; set; } = "";
    public bool Notified { get; set; }

    public DateTime When
    {
        get
        {
            if (!DateTime.TryParse(Date, out var d)) return DateTime.MaxValue;
            if (TimeSpan.TryParse(Time, out var t)) d = d.Date + t;
            return d;
        }
    }

    public override string ToString() => (Time.Length > 0 ? Time + "  " : "All day  ") + Title;
}

public static class CalendarStore
{
    private static List<CalEvent>? _events;
    private static string FilePath => AppPaths.ConfigFile("events.json");

    public static List<CalEvent> All
    {
        get
        {
            if (_events != null) return _events;
            try
            {
                _events = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<CalEvent>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch { _events = new(); }
            return _events;
        }
    }

    public static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(All)); }
        catch (Exception ex) { Console.Error.WriteLine("Could not save events: " + ex.Message); }
    }

    public static IEnumerable<CalEvent> ForDate(DateTime date) =>
        All.Where(e => e.Date == date.ToString("yyyy-MM-dd")).OrderBy(e => e.Time);

    public static void Add(DateTime date, string time, string title)
    {
        All.Add(new CalEvent { Date = date.ToString("yyyy-MM-dd"), Time = time, Title = title });
        Save();
    }

    public static void Remove(CalEvent e)
    {
        All.Remove(e);
        Save();
    }

    /// <summary>Soonest event that has not started yet (all-day events count for today).</summary>
    public static CalEvent? Next(DateTime now) =>
        All.Where(e => e.Time.Length > 0 ? e.When >= now : e.When.Date >= now.Date)
           .OrderBy(e => e.When).FirstOrDefault();

    /// <summary>Timed events whose start has passed within the last 10 minutes and were not announced yet.</summary>
    public static List<CalEvent> TakeDueReminders(DateTime now)
    {
        var due = All.Where(e => !e.Notified && e.Time.Length > 0 && e.When <= now && now - e.When < TimeSpan.FromMinutes(10)).ToList();
        if (due.Count > 0)
        {
            foreach (var e in due) e.Notified = true;
            Save();
        }
        return due;
    }
}
