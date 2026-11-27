// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// The top of a small app's window (Calculator, Clock, Notes, Voice Memos…): its picture and name on the left, and its
/// own actions on the right (children given in XAML, docked right in the order written). One construction, so the
/// utilities look like the rest of the system rather than each their own way.
/// </summary>
public sealed class AppHeader : DockPanel
{
    public string? AppId { get; set; }
    public string Heading { get; set; } = "";
    /// <summary>A line under the name (optional): what it's showing now.</summary>
    public string? Subtitle
    {
        get => _subtitle.Text;
        set { _subtitle.Text = value; _subtitle.IsVisible = !string.IsNullOrEmpty(value); }
    }

    private readonly TextBlock _subtitle = new() { Classes = { "caption" }, IsVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
    private bool _built;

    public AppHeader()
    {
        LastChildFill = false;
        Margin = new Thickness(0, 0, 0, 14);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_built) return;
        _built = true;
        // the actions written in XAML keep their order, right-aligned
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in new System.Collections.Generic.List<Control>(Children)) { Children.Remove(c); actions.Children.Add(c); }
        var icon = AppIcons.Create(AppId, Heading, 30);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var name = new TextBlock { Text = Heading, FontSize = 17, FontWeight = FontWeight.SemiBold };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            icon,
            new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, _subtitle } },
        } };
        SetDock(title, Dock.Left);
        SetDock(actions, Dock.Right);
        Children.Add(title);
        Children.Add(actions);
    }
}
