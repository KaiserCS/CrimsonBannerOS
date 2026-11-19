using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Clock panel: big clock, month calendar and the agenda of the selected day.</summary>
public partial class CalendarPanel : UserControl, IShellPanel
{
    public event Action? CloseRequested;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private DateTime Selected => MonthCalendar.SelectedDate ?? DateTime.Today;

    public CalendarPanel()
    {
        InitializeComponent();
        MonthCalendar.SelectedDate = DateTime.Today;
        MonthCalendar.SelectedDatesChanged += (_, _) => RefreshAgenda();

        _tick.Tick += (_, _) => UpdateClock();
        _tick.Start();
        DetachedFromVisualTree += (_, _) => _tick.Stop();

        UpdateClock();
        RefreshAgenda();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        BigTime.Text = now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        BigDate.Text = now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    private void RefreshAgenda()
    {
        var day = Selected;
        AgendaHeader.Text = (day.Date == DateTime.Today ? "TODAY" : day.ToString("ddd d MMM", CultureInfo.CurrentCulture).ToUpperInvariant());
        Agenda.Children.Clear();

        var events = CalendarStore.ForDate(day).ToList();
        if (events.Count == 0)
        {
            Agenda.Children.Add(new TextBlock { Text = "No events", Classes = { "caption" } });
            return;
        }
        foreach (var ev in events)
        {
            var row = new Border { Classes = { "card" }, Padding = new Thickness(10, 6) };
            row.Child = new TextBlock { Text = ev.ToString(), FontSize = 13, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            Agenda.Children.Add(row);
        }
    }

    private void OnOpen(object? s, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
        AppRegistry.Launch("calendar");
    }
}
