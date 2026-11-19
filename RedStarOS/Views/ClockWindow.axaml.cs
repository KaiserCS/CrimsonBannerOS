using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Stopwatch with laps and a countdown timer.</summary>
public partial class ClockWindow : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch _sw = new();
    private int _lapCount;
    private TimeSpan _lastLap;

    private DateTime _timerEnd;
    private TimeSpan _timerLeft;
    private bool _timerRunning;

    public ClockWindow()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        UpdateTimerPreview();
        MinBox.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) UpdateTimerPreview(); };
        SecBox.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) UpdateTimerPreview(); };
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";

    private void Tick()
    {
        SwLabel.Text = Fmt(_sw.Elapsed);
        if (!_timerRunning) return;
        _timerLeft = _timerEnd - DateTime.Now;
        if (_timerLeft <= TimeSpan.Zero)
        {
            _timerRunning = false;
            _timerLeft = TimeSpan.Zero;
            TimerStart.Content = "Start";
            TimerInputs.IsEnabled = true;
            TimerLabel.Text = "00:00";
            Notifier.Show("Timer", "Time is up!", "clock");
        }
        else TimerLabel.Text = TimerText(_timerLeft);
    }

    private static string TimerText(TimeSpan t)
    {
        var secs = (int)Math.Ceiling(t.TotalSeconds);
        return $"{secs / 60:00}:{secs % 60:00}";
    }

    // ------------------------------------------------------------ stopwatch

    private void OnSwStart(object? s, RoutedEventArgs e)
    {
        if (_sw.IsRunning) { _sw.Stop(); SwStart.Content = "Resume"; }
        else { _sw.Start(); SwStart.Content = "Pause"; }
    }

    private void OnSwLap(object? s, RoutedEventArgs e)
    {
        if (!_sw.IsRunning) return;
        var now = _sw.Elapsed;
        Laps.Items.Insert(0, $"Lap {++_lapCount}     {Fmt(now - _lastLap)}     total {Fmt(now)}");
        _lastLap = now;
    }

    private void OnSwReset(object? s, RoutedEventArgs e)
    {
        _sw.Reset();
        SwStart.Content = "Start";
        SwLabel.Text = "00:00.00";
        Laps.Items.Clear();
        _lapCount = 0;
        _lastLap = TimeSpan.Zero;
    }

    // ------------------------------------------------------------ timer

    private TimeSpan Chosen => TimeSpan.FromMinutes((double)(MinBox.Value ?? 0)) + TimeSpan.FromSeconds((double)(SecBox.Value ?? 0));

    private void UpdateTimerPreview()
    {
        if (!_timerRunning && TimerLabel != null) TimerLabel.Text = TimerText(Chosen);
    }

    private void OnTimerStart(object? s, RoutedEventArgs e)
    {
        if (_timerRunning)
        {
            _timerRunning = false;                         // pause
            TimerStart.Content = "Resume";
            return;
        }
        var left = _timerLeft > TimeSpan.Zero && (string?)TimerStart.Content == "Resume" ? _timerLeft : Chosen;
        if (left <= TimeSpan.Zero) return;
        _timerEnd = DateTime.Now + left;
        _timerRunning = true;
        TimerStart.Content = "Pause";
        TimerInputs.IsEnabled = false;
    }

    private void OnTimerReset(object? s, RoutedEventArgs e)
    {
        _timerRunning = false;
        _timerLeft = TimeSpan.Zero;
        TimerStart.Content = "Start";
        TimerInputs.IsEnabled = true;
        UpdateTimerPreview();
    }

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        base.OnClosed(e);
    }
}
