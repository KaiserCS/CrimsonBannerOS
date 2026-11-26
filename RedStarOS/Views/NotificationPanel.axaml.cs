using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Notification centre, opened from the bell in the app tray.</summary>
public partial class NotificationPanel : UserControl, IShellPanel
{
    // required by IShellPanel; the notification list never closes its own flyout
    public event Action? CloseRequested { add { } remove { } }

    public sealed class Row
    {
        public string Glyph { get; init; } = "";
        public IBrush Badge { get; init; } = AppIcons.Plate;
        public IImage? Picture { get; init; }
        public bool HasPicture => Picture != null;
        public string Title { get; init; } = "";
        public string Message { get; init; } = "";
        public string Time { get; init; } = "";
    }

    public NotificationPanel()
    {
        InitializeComponent();
        Notifier.MarkAllRead();
        Render();
        Notifier.Changed += Render;
        DetachedFromVisualTree += (_, _) => Notifier.Changed -= Render;
    }

    private void Render()
    {
        var today = DateTime.Today;
        var rows = Notifier.History.Select(n =>
        {
            var app = n.AppId != null ? AppRegistry.Find(n.AppId) : null;
            return new Row
            {
                Glyph = AppIcons.GlyphFor(n.AppId, n.Title),
                Badge = AppIcons.Plate,
                Picture = AppIcons.Picture(n.AppId),
                Title = app != null && app.Name != n.Title ? $"{app.Name} · {n.Title}" : n.Title,
                Message = n.Message,
                Time = n.When.Date == today ? n.When.ToString("HH:mm") : n.When.ToString("ddd d MMM")
            };
        }).ToList();

        List.ItemsSource = rows;
        EmptyState.IsVisible = rows.Count == 0;
        List.IsVisible = rows.Count > 0;

        DndButton.Content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6,
            Children = { Icons.Glyph(Notifier.DoNotDisturb ? "bell-off" : "bell", 15), new TextBlock { Text = Notifier.DoNotDisturb ? "Do not disturb" : "Alerts on", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } },
        };
        EmptyIcon.Content ??= Icons.Glyph("bell", 32);
        DndNote.IsVisible = Notifier.DoNotDisturb;
    }

    private void OnDnd(object? s, RoutedEventArgs e) => Notifier.SetDoNotDisturb(!Notifier.DoNotDisturb);
    private void OnClear(object? s, RoutedEventArgs e) => Notifier.Clear();
}
