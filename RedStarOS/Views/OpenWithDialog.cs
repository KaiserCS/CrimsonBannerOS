using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// "Open with → Other application…": every installed program, the ones that say they handle this kind of file
/// first, with a search box and "Always use this app for …".
/// </summary>
public static class OpenWithDialog
{
    public sealed record Choice(LinuxApp? App, bool RedStar, bool Always);

    private static readonly Dictionary<string, IImage?> IconCache = new();

    /// <summary>A Linux program's icon (PNG from the icon theme), or a generic one.</summary>
    public static Control AppIcon(LinuxApp app, double size)
    {
        IImage? img = null;
        if (!string.IsNullOrEmpty(app.Icon))
        {
            if (!IconCache.TryGetValue(app.Icon, out img))
            {
                try
                {
                    if (DesktopEntries.IconPath(app.Icon, 48) is { } p && !p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        using var fs = File.OpenRead(p);
                        img = Bitmap.DecodeToWidth(fs, (int)(size * 2), BitmapInterpolationMode.HighQuality);
                    }
                }
                catch { img = null; }
                IconCache[app.Icon] = img;
            }
        }
        return img != null ? new Image { Source = img, Width = size, Height = size } : Icons.Create("apps", size);
    }

    public static async Task<Choice?> Show(Window owner, string file)
    {
        var kind = DefaultApps.KindFor(file);
        var suggested = DesktopEntries.AppsFor(file);
        var others = DesktopEntries.All().Where(a => !suggested.Contains(a)).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var redstar = FileOpener.RedStarAppFor(file);

        Choice? result = null;
        var search = new TextBox { PlaceholderText = "Search programs", Margin = new Thickness(0, 0, 0, 8) };
        var list = new ListBox { Height = 330, Background = Brushes.Transparent };
        var always = new CheckBox { Content = kind != null ? $"Always use this app for {kind.Label.ToLowerInvariant()}" : "Always use this app", IsVisible = kind != null };
        var open = new Button { Content = "Open", Classes = { "primary" }, MinWidth = 90, IsDefault = true, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };

        Control Row(string name, string? sub, Control icon) => new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(2, 3),
            Children =
            {
                icon,
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = name },
                        new TextBlock { Text = sub ?? "", FontSize = 11.5, Opacity = 0.6, IsVisible = !string.IsNullOrEmpty(sub) }
                    }
                }
            }
        };
        Control Header(string text) => new TextBlock { Text = text, Classes = { "eyebrow" }, Margin = new Thickness(4, 8, 0, 2), IsHitTestVisible = false };

        void Fill()
        {
            var q = (search.Text ?? "").Trim();
            bool Match(string n) => q.Length == 0 || n.Contains(q, StringComparison.OrdinalIgnoreCase);
            var items = new List<Control>();
            if (redstar != null && Match(redstar))
            {
                items.Add(Header("RED STAR"));
                items.Add(new ListBoxItem { Tag = "redstar", Content = Row(redstar, "Built in", Icons.Create("star-filled", 28, new SolidColorBrush(Color.Parse("#C8102E")))) });
            }
            var s = suggested.Where(a => Match(a.Name)).ToList();
            if (s.Count > 0)
            {
                items.Add(Header("SUGGESTED"));
                items.AddRange(s.Select(a => new ListBoxItem { Tag = a, Content = Row(a.Name, null, AppIcon(a, 28)) }));
            }
            var o = others.Where(a => Match(a.Name)).ToList();
            if (o.Count > 0)
            {
                items.Add(Header(s.Count > 0 || redstar != null ? "ALL PROGRAMS" : "PROGRAMS"));
                items.AddRange(o.Select(a => new ListBoxItem { Tag = a, Content = Row(a.Name, null, AppIcon(a, 28)) }));
            }
            if (items.Count == 0) items.Add(new TextBlock { Text = "No programs match.", Opacity = 0.6, Margin = new Thickness(6), IsHitTestVisible = false });
            list.ItemsSource = items;
            if (items.OfType<ListBoxItem>().FirstOrDefault() is { } first) list.SelectedItem = first;
        }

        var win = new Window
        {
            Title = "Open with", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18), Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"Open “{Path.GetFileName(file)}” with:", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    search, list, always,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { open, cancel } }
                }
            }
        };
        void Accept()
        {
            if (list.SelectedItem is not ListBoxItem { Tag: { } tag }) return;
            result = tag is LinuxApp a ? new Choice(a, false, always.IsChecked == true) : new Choice(null, true, always.IsChecked == true);
            win.Close();
        }
        list.SelectionChanged += (_, _) =>
        {
            // headers can't be chosen
            if (list.SelectedItem is not ListBoxItem) list.SelectedItem = null;
            open.IsEnabled = list.SelectedItem is ListBoxItem;
        };
        list.DoubleTapped += (_, _) => Accept();
        open.Click += (_, _) => Accept();
        cancel.Click += (_, _) => win.Close();
        search.TextChanged += (_, _) => Fill();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) { list.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };
        win.Opened += (_, _) => search.Focus();
        Fill();
        await win.ShowDialog(owner);
        return result;
    }

    /// <summary>Opens the files as chosen, and remembers the choice when "Always" was ticked.</summary>
    public static void Run(Choice c, IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        if (c.Always && DefaultApps.KindFor(files[0]) is { } kind)
            DefaultApps.Set(kind.Id, c.RedStar ? "redstar" : c.App!.FilePath, systemWide: false);
        foreach (var f in files) RecentDocs.Add(f);
        if (c.RedStar) { foreach (var f in files.Take(10)) FileOpener.OpenInRedStar(f); }
        else if (c.App != null) DesktopEntries.LaunchWith(c.App, files);
    }
}
