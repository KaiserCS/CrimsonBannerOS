// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// The clock's panel. The time (seconds quieter than the hours and minutes) and the date; a month of round days, today
/// filled in the accent, the chosen day ringed, a dot under days with events, the wheel or the arrows to change month;
/// then the chosen day's events with their times, and a way into Calendar.
/// </summary>
public partial class CalendarPanel : UserControl, IShellPanel
{
    public event Action? CloseRequested;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selected = DateTime.Today;
    private DateTime _drawnFor;

    private readonly TextBlock _hm = new() { FontSize = 40, FontWeight = FontWeight.Light, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _sec = new() { FontSize = 20, FontWeight = FontWeight.Light, Opacity = 0.45, Margin = new Thickness(4, 0, 0, 6), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _date = new() { Classes = { "caption" }, FontSize = 13 };
    private readonly TextBlock _monthTitle = new() { FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _days = new();
    private readonly TextBlock _agendaTitle = new() { FontSize = 13.5, FontWeight = FontWeight.SemiBold };
    private readonly StackPanel _agenda = new() { Spacing = 2 };
    private readonly Button _today;

    /// <summary>The 42 days a month's page shows: from the week's first day on or before the 1st, six weeks.</summary>
    public static List<DateTime> PageDays(DateTime month, DayOfWeek firstDay)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        int back = ((int)first.DayOfWeek - (int)firstDay + 7) % 7;
        var start = first.AddDays(-back);
        return Enumerable.Range(0, 42).Select(i => start.AddDays(i)).ToList();
    }

    private static DayOfWeek FirstDay => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    public CalendarPanel()
    {
        InitializeComponent();

        // the time and the date
        var time = new StackPanel { Orientation = Orientation.Horizontal };
        time.Children.Add(_hm);
        time.Children.Add(_sec);
        var head = new StackPanel { Spacing = 0 };
        head.Children.Add(time);
        head.Children.Add(_date);
        Root.Children.Add(head);

        // the month
        var prev = NavButton("chevron-left", "Previous month", () => ShowMonth(_month.AddMonths(-1)));
        var next = NavButton("chevron-right", "Next month", () => ShowMonth(_month.AddMonths(1)));
        _today = new Button { Classes = { "calnav" }, Content = new TextBlock { Text = "Today", FontSize = 12.5 }, Padding = new Thickness(10, 4), Margin = new Thickness(0, 0, 4, 0) };
        _today.Click += (_, _) => { _selected = DateTime.Today; ShowMonth(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)); };
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        nav.Children.Add(_today);
        nav.Children.Add(prev);
        nav.Children.Add(next);
        var bar = new DockPanel { Margin = new Thickness(2, 0, 0, 0) };
        DockPanel.SetDock(nav, Dock.Right);
        bar.Children.Add(nav);
        bar.Children.Add(_monthTitle);

        for (int c = 0; c < 7; c++) _days.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int r = 0; r < 7; r++) _days.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var month = new StackPanel { Spacing = 8 };
        month.Children.Add(bar);
        month.Children.Add(_days);
        var monthCard = new Border { Classes = { "card" }, Padding = new Thickness(12, 10, 12, 10), Child = month };
        monthCard.PointerWheelChanged += (_, e) =>
        {
            if (e.Delta.Y != 0) ShowMonth(_month.AddMonths(e.Delta.Y > 0 ? -1 : 1));
            e.Handled = true;
        };
        Root.Children.Add(monthCard);

        // the chosen day's events
        var agenda = new StackPanel { Spacing = 6 };
        agenda.Children.Add(_agendaTitle);
        agenda.Children.Add(_agenda);
        Root.Children.Add(agenda);

        // into Calendar
        var open = new Button
        {
            Classes = { "calfoot" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { Icons.Glyph("calendar", 16), new TextBlock { Text = "Open Calendar", VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        open.Click += OnOpen;
        Root.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(-18, 0) });
        Root.Children.Add(open);

        _tick.Tick += (_, _) => UpdateClock();
        _tick.Start();
        DetachedFromVisualTree += (_, _) => _tick.Stop();
        UpdateClock();
        ShowMonth(_month);
    }

    private static Button NavButton(string icon, string tip, Action act)
    {
        var b = new Button
        {
            Classes = { "calnav" }, Width = 30, Height = 30, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = Icons.Glyph(icon, 16),
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => act();
        return b;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var c = CultureInfo.CurrentCulture;
        bool twelve = c.DateTimeFormat.ShortTimePattern.Contains('t');
        _hm.Text = now.ToString(twelve ? "h:mm" : "HH:mm", c);
        _sec.Text = now.ToString(":ss", c) + (twelve ? " " + now.ToString("tt", c) : "");
        _date.Text = now.ToString("dddd d MMMM yyyy", c);
        if (now.Date != _drawnFor) ShowMonth(_month);       // past midnight: today has moved
    }

    private void ShowMonth(DateTime month)
    {
        _month = new DateTime(month.Year, month.Month, 1);
        _drawnFor = DateTime.Today;
        var c = CultureInfo.CurrentCulture;
        _monthTitle.Inlines = new Avalonia.Controls.Documents.InlineCollection
        {
            new Avalonia.Controls.Documents.Run(c.DateTimeFormat.GetMonthName(_month.Month)) { FontWeight = FontWeight.SemiBold },
            new Avalonia.Controls.Documents.Run("  " + _month.Year.ToString(c)) { Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) },
        };
        bool thisMonth = _month.Year == DateTime.Today.Year && _month.Month == DateTime.Today.Month;
        _today.IsVisible = !thisMonth || _selected.Date != DateTime.Today;

        _days.Children.Clear();
        // the week's days, starting where this country starts them
        for (int i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)(((int)FirstDay + i) % 7);
            var t = new TextBlock
            {
                Text = c.DateTimeFormat.GetShortestDayName(dow), FontSize = 11.5, Opacity = 0.55,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4),
            };
            Grid.SetColumn(t, i);
            _days.Children.Add(t);
        }
        var withEvents = CalendarStore.All.Select(e => e.Date).ToHashSet();
        var accent = ThemeService.AccentColor;
        var days = PageDays(_month, FirstDay);
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            bool inMonth = d.Month == _month.Month, today = d.Date == DateTime.Today, chosen = d.Date == _selected.Date;
            var number = new TextBlock
            {
                Text = d.Day.ToString(c), FontSize = 13.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = today ? FontWeight.Bold : FontWeight.Normal, Opacity = inMonth ? 1 : 0.32,
            };
            if (today) number.Foreground = Brushes.White;          // otherwise the button's own colour
            var dot = new Ellipse
            {
                Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4),
                Fill = today ? Brushes.White : new SolidColorBrush(accent), IsVisible = withEvents.Contains(d.ToString("yyyy-MM-dd")), IsHitTestVisible = false,
            };
            var cell = new Button
            {
                Classes = { "calday" }, Width = 36, Height = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(18),
                HorizontalAlignment = HorizontalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = new Panel { Children = { number, dot } },
                Background = today ? new SolidColorBrush(accent) : Brushes.Transparent,
                BorderBrush = chosen && !today ? new SolidColorBrush(accent) : Brushes.Transparent, BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 1),
            };
            if (today) cell.Classes.Add("today");
            ToolTip.SetTip(cell, d.ToString("dddd d MMMM", c));
            cell.Click += (_, _) =>
            {
                _selected = d;
                ShowMonth(inMonth ? _month : new DateTime(d.Year, d.Month, 1));
            };
            Grid.SetColumn(cell, i % 7);
            Grid.SetRow(cell, 1 + i / 7);
            _days.Children.Add(cell);
        }
        RefreshAgenda();
    }

    private void RefreshAgenda()
    {
        var day = _selected.Date;
        var c = CultureInfo.CurrentCulture;
        _agendaTitle.Text = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(1) ? "Tomorrow" : day.ToString("dddd d MMMM", c);
        _agenda.Children.Clear();
        var events = CalendarStore.ForDate(day).OrderBy(e => e.Time.Length == 0 ? "" : e.Time).ToList();
        if (events.Count == 0)
        {
            var add = new Button { Classes = { "callink" }, Content = "Nothing planned. Add an event", Padding = new Thickness(0, 2) };
            add.Click += OnOpen;
            _agenda.Children.Add(add);
            return;
        }
        var accent = new SolidColorBrush(ThemeService.AccentColor);
        foreach (var ev in events)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("3,64,*"), Margin = new Thickness(0, 2) };
            var mark = new Border { Background = accent, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 2) };
            var when = new TextBlock { Text = ev.Time.Length > 0 ? ev.Time : "All day", FontSize = 12.5, Opacity = 0.65, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var what = new TextBlock { Text = ev.Title, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(when, 1);
            Grid.SetColumn(what, 2);
            row.Children.Add(mark);
            row.Children.Add(when);
            row.Children.Add(what);
            ToolTip.SetTip(row, ev.ToString());
            _agenda.Children.Add(row);
        }
    }

    private void OnOpen(object? s, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
        AppRegistry.Launch("calendar");
    }
}
