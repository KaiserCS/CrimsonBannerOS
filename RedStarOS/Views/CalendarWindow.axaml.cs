using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Month calendar with saved events and reminders (was Form19).</summary>
public partial class CalendarWindow : Window
{
    private DateTime Selected => MonthCalendar.SelectedDate ?? DateTime.Today;

    public CalendarWindow()
    {
        InitializeComponent();
        MonthCalendar.SelectedDate = DateTime.Today;
        MonthCalendar.SelectedDatesChanged += (_, _) => RefreshEvents();
        TitleBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OnAdd(null, new RoutedEventArgs()); e.Handled = true; } };
        EventList.SelectionChanged += (_, _) => DeleteBtn.IsEnabled = EventList.SelectedItem is CalEvent;
        RefreshEvents();
    }

    private void OnToday(object? s, RoutedEventArgs e)
    {
        MonthCalendar.DisplayDate = DateTime.Today;
        MonthCalendar.SelectedDate = DateTime.Today;
    }

    private void RefreshEvents()
    {
        DateLabel.Text = Selected.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
        var events = CalendarStore.ForDate(Selected).ToList();
        EventList.ItemsSource = events;
        EventsEmpty.IsVisible = events.Count == 0;
        DeleteBtn.IsEnabled = false;
    }

    private void OnAdd(object? s, RoutedEventArgs e)
    {
        var title = (TitleBox.Text ?? "").Trim();
        if (title.Length == 0) { StatusLabel.Text = "Enter a title."; return; }

        var time = (TimeBox.Text ?? "").Trim();
        if (time.Length > 0)
        {
            if (!TimeSpan.TryParseExact(time, new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var ts) || ts.TotalHours >= 24)
            {
                StatusLabel.Text = "Time must look like 14:30.";
                return;
            }
            time = ts.ToString("hh\\:mm");
        }

        CalendarStore.Add(Selected, time, title);
        TitleBox.Text = "";
        TimeBox.Text = "";
        StatusLabel.Text = "";
        RefreshEvents();
    }

    private void OnDelete(object? s, RoutedEventArgs e)
    {
        if (EventList.SelectedItem is CalEvent ev)
        {
            CalendarStore.Remove(ev);
            RefreshEvents();
        }
    }
}
