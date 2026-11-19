using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace RedStarOS.Views;

/// <summary>Small modal dialogs (Avalonia has no built-in MessageBox).</summary>
public static class Dialogs
{
    public static async Task ShowMessage(Window owner, string title, string message) =>
        await Ask(owner, title, message, "OK");

    /// <summary>Shows a message with custom buttons; returns the index of the pressed one (-1 if Esc/closed).</summary>
    public static async Task<int> Ask(Window owner, string title, string message, params string[] buttons)
    {
        int result = -1;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var win = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 16,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, row }
            }
        };
        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new Button { Content = buttons[i], MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => { result = idx; win.Close(); };
            row.Children.Add(b);
        }
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        await win.ShowDialog(owner);
        return result;
    }

    /// <summary>Asks for a line of text. Returns null when cancelled.</summary>
    public static async Task<string?> Prompt(Window owner, string title, string label, string initial = "")
    {
        string? result = null;
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var win = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = label },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { ok, cancel }
                    }
                }
            }
        };
        void Accept() { result = box.Text ?? ""; win.Close(); }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => win.Close();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await win.ShowDialog(owner);
        return result;
    }
}
