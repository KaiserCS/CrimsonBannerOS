using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Running processes with CPU and memory use; selected processes can be ended.</summary>
public partial class TaskManagerWindow : Window
{
    public sealed class ProcRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string Pid => Id.ToString();
        public double CpuValue { get; init; }
        public long Bytes { get; init; }
        public string Cpu => CpuValue.ToString("0.0") + " %";
        public string Memory => Bytes >= 1L << 30 ? (Bytes / (double)(1L << 30)).ToString("0.0") + " GB" : (Bytes >> 20) + " MB";
    }

    private static readonly string[] SortModes = { "Memory", "CPU", "Name" };

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _prev = new();
    private double _lastTotalCpu;

    public TaskManagerWindow()
    {
        InitializeComponent();
        SortBox.ItemsSource = SortModes;
        SortBox.SelectedIndex = 0;
        SortBox.SelectionChanged += (_, _) => Refresh();
        FilterBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(); };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        var rows = new List<ProcRow>();
        double total = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                double cpu = 0;
                var t = p.TotalProcessorTime;
                if (_prev.TryGetValue(p.Id, out var old) && now > old.At)
                    cpu = (t - old.Cpu).TotalMilliseconds / (now - old.At).TotalMilliseconds / Environment.ProcessorCount * 100;
                _prev[p.Id] = (t, now);
                total += cpu;
                rows.Add(new ProcRow { Id = p.Id, Name = p.ProcessName, CpuValue = Math.Max(0, cpu), Bytes = p.WorkingSet64 });
            }
            catch { /* process ended or is not accessible */ }
            finally { p.Dispose(); }
        }
        _lastTotalCpu = Math.Min(100, total);

        var q = (FilterBox.Text ?? "").Trim();
        IEnumerable<ProcRow> view = rows.Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        view = SortBox.SelectedIndex switch
        {
            1 => view.OrderByDescending(r => r.CpuValue),
            2 => view.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => view.OrderByDescending(r => r.Bytes)
        };

        var selected = (List.SelectedItem as ProcRow)?.Id;
        var list = view.ToList();
        List.ItemsSource = list;
        if (selected != null) List.SelectedItem = list.FirstOrDefault(r => r.Id == selected);

        CpuLabel.Text = _lastTotalCpu.ToString("0") + " %";
        MemLabel.Text = MemoryText();
        UpLabel.Text = UptimeText();
    }

    private static string MemoryText()
    {
        try
        {
            long total = 0, avail = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) total = KbOf(line);
                else if (line.StartsWith("MemAvailable:")) avail = KbOf(line);
            }
            if (total > 0) return $"{(total - avail) / 1048576.0:0.0} / {total / 1048576.0:0.0} GB";
        }
        catch { /* not Linux */ }
        return "--";
    }

    private static long KbOf(string line) => long.Parse(new string(line.Where(char.IsDigit).ToArray()));

    private static string UptimeText()
    {
        try
        {
            var secs = double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            var t = TimeSpan.FromSeconds(secs);
            return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{t.Hours}h {t.Minutes}m";
        }
        catch { return "--"; }
    }

    private async void OnEnd(object? s, RoutedEventArgs e)
    {
        if (List.SelectedItem is not ProcRow row) return;
        if (row.Id == Environment.ProcessId)
        {
            await Dialogs.ShowMessage(this, "Task Manager", "That is Red Star OS itself. Use Shut down to close it.");
            return;
        }
        var r = await Dialogs.Ask(this, "End task", $"End \"{row.Name}\" (PID {row.Id})? Unsaved work in it will be lost.", "End task", "Cancel");
        if (r != 0) return;
        try { using var p = Process.GetProcessById(row.Id); p.Kill(true); }
        catch (Exception ex) { Notifier.Show("Task Manager", "Could not end " + row.Name + ": " + ex.Message, "taskmgr"); }
        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
