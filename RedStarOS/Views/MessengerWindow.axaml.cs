using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Family chat. Messages are kept on this computer (~/.config/redstaros/messages.json);
/// there is no network connection to other people.
/// </summary>
public partial class MessengerWindow : Window
{
    public static MessengerWindow? Instance { get; private set; }
    private readonly ObservableCollection<string> _messages = new();
    private static readonly System.Text.RegularExpressions.Regex UrlRx = new(@"https?://[^\s<>""]+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static string FilePath => AppPaths.ConfigFile("messages.json");

    public MessengerWindow()
    {
        InitializeComponent();
        Instance = this;
        Closed += (_, _) => { if (ReferenceEquals(Instance, this)) Instance = null; };
        // Links in messages open in Red Star Online (double-click, or Enter on the selected message).
        Messages.DoubleTapped += (_, _) => OpenLinkIn(Messages.SelectedItem as string);
        Messages.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenLinkIn(Messages.SelectedItem as string); e.Handled = true; } };
        try
        {
            var saved = File.Exists(FilePath) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath)) : null;
            if (saved != null) foreach (var m in saved) _messages.Add(m);
        }
        catch { /* start with an empty history */ }

        // Earlier versions stored two sample messages; drop them if they are still in the history.
        _messages.Remove("Hey how are you?");
        _messages.Remove("We are going to a party next week wanna join?");
        Messages.ItemsSource = _messages;

        // Ctrl+Enter sends.
        Input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { OnSend(null, new RoutedEventArgs()); e.Handled = true; }
        };
    }

    /// <summary>Posts a shared web page to the chat (used by the browser's "Send link to Messenger").</summary>
    public static MessengerWindow Share(string title, string url)
    {
        var w = Instance;
        if (w == null)
        {
            w = new MessengerWindow();
            WindowManager.Open(w, "messenger");
        }
        w.Post("🔗 " + (string.IsNullOrWhiteSpace(title) ? url : title.Trim()) + "\n" + url);
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show(); w.Activate();
        return w;
    }

    public void Post(string text)
    {
        _messages.Add($"[{DateTime.Now:HH:mm}] {text}");
        Save();
        Messages.ScrollIntoView(_messages.Count - 1);
    }

    private static void OpenLinkIn(string? message)
    {
        if (message == null) return;
        var m = UrlRx.Match(message);
        if (m.Success) BrowserLauncher.Launch(m.Value.TrimEnd('.', ',', ')', ';'));
    }

    private void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(new System.Collections.Generic.List<string>(_messages))); }
        catch { /* not critical */ }
    }

    private void OnSend(object? s, RoutedEventArgs e)
    {
        var text = (Input.Text ?? "").Trim();
        if (text.Length == 0) return;
        Input.Text = "";
        Post(text);
    }

    private async void OnClear(object? s, RoutedEventArgs e)
    {
        if (await Dialogs.Ask(this, "Clear History", "Delete all messages?", "Delete", "Cancel") != 0) return;
        _messages.Clear();
        Save();
    }

    private void OnLeave(object? s, RoutedEventArgs e) => Close();
}
