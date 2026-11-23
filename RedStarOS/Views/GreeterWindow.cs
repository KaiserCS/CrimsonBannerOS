// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RedStarOS.Services;
using RedStarOS.Services.Greeter;

namespace RedStarOS.Views;

/// <summary>
/// Crimson Banner OS's login screen (RedStarOS --greeter, started by greetd inside redstar-comp; packaging/greeter).
/// It looks like the lock screen: the clock, then a card with the person, their password and the desktop to start.
/// Several people: their pictures above the card, to pick from. Shut down, restart and sleep sit at the bottom.
/// </summary>
public sealed class GreeterWindow : Window
{
    private readonly IGreetd _greetd;
    private readonly GreeterFlow _flow;
    private readonly bool _demo;
    private readonly List<LoginUser> _users;
    private readonly List<LoginSession> _sessions;
    private readonly GreeterData.State _state;
    private readonly string _statePath;

    private LoginUser? _user;
    private bool _busy, _questionOpen;

    private readonly TextBlock _clock = new() { Foreground = Brushes.White, FontSize = 88, FontWeight = FontWeight.Light, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _date = new() { Foreground = Brushes.White, Opacity = 0.85, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _people = new() { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _avatar = new() { Width = 96, Height = 96, CornerRadius = new CornerRadius(48), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, BorderBrush = new SolidColorBrush(Color.Parse("#40FFFFFF")), BorderThickness = new Thickness(2) };
    private readonly TextBlock _name = new() { Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBox _userBox = new() { PlaceholderText = "User name", MinHeight = 40, CornerRadius = new CornerRadius(10), VerticalContentAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly TextBox _answer = new() { PasswordChar = '●', PlaceholderText = "Password", MinHeight = 40, CornerRadius = new CornerRadius(10), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { Foreground = new SolidColorBrush(Color.Parse("#FF8A8A")), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 13, IsVisible = false };
    private readonly TextBlock _notes = new() { Foreground = Brushes.White, Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    private readonly Button _signIn = new() { Content = "Sign in", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 40, FontWeight = FontWeight.SemiBold };
    private readonly ComboBox _session = new() { MinWidth = 240 };
    private readonly Border _card = new() { Width = 360, CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(Color.Parse("#C0141418")), BorderBrush = new SolidColorBrush(Color.Parse("#2EFFFFFF")), BorderThickness = new Thickness(1), Padding = new Thickness(28, 26) };
    private readonly TranslateTransform _shake = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public GreeterWindow(IGreetd greetd, bool demo)
    {
        _greetd = greetd;
        _flow = new GreeterFlow(greetd);
        _demo = demo;
        _statePath = demo ? Path.Combine(Path.GetTempPath(), "crimson-greeter-demo.json") : GreeterData.StatePath;
        _state = GreeterData.Load(_statePath);
        _users = GreeterData.SystemUsers();
        if (demo && _users.Count == 0) _users.Add(new LoginUser(Environment.UserName, "", 1000, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        _sessions = GreeterData.Sessions(GreeterData.WaylandDirs, GreeterData.X11Dirs, ProcessUtil.Which);

        Title = "Crimson Banner OS";
        WindowDecorations = WindowDecorations.None;
        if (demo) { Width = 1100; Height = 720; WindowStartupLocation = WindowStartupLocation.CenterScreen; }
        else
        {
            WindowState = WindowState.FullScreen; Topmost = true;
            // on X11 with no window manager (the login screen's fallback when redstar-comp can't start), full screen
            // isn't honoured: the window covers the screen itself
            Opened += (_, _) => CoverScreen();
        }
        try { Background = new ImageBrush(new Bitmap(AssetLoader.Open(new Uri("avares://RedStarOS/Assets/login-bg.jpg")))) { Stretch = Stretch.UniformToFill }; }
        catch { Background = new SolidColorBrush(Color.Parse("#1A0A0C")); }

        _card.RenderTransform = _shake;
        _signIn.Classes.Add("primary");
        _signIn.Click += (_, _) => _ = Submit();
        _answer.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = Submit(); } };
        _userBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _answer.Focus(); } };
        _session.ItemsSource = _sessions.Select(s => s.Shown).ToList();

        var card = new StackPanel { Spacing = 12 };
        card.Children.Add(_avatar);
        card.Children.Add(_name);
        card.Children.Add(_userBox);
        card.Children.Add(_answer);
        card.Children.Add(_message);
        card.Children.Add(_notes);
        card.Children.Add(_signIn);
        _card.Child = card;

        var middle = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 26 };
        middle.Children.Add(new StackPanel { Children = { _clock, _date } });
        middle.Children.Add(_people);
        middle.Children.Add(_card);

        // the desktop to start, bottom left; power, bottom right
        var desktop = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(28) };
        desktop.Children.Add(new TextBlock { Text = "Desktop", Foreground = Brushes.White, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center });
        desktop.Children.Add(_session);
        if (_sessions.Count == 0) desktop.Children.Add(new TextBlock { Text = "No desktops are installed.", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });

        var power = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(28) };
        power.Children.Add(PowerButton("Sleep", "suspend"));
        power.Children.Add(PowerButton("Restart", "reboot"));
        power.Children.Add(PowerButton("Shut down", "poweroff"));

        var brand = new TextBlock
        {
            Text = "Crimson Banner OS" + (demo ? " · preview: any password works, nothing is started" : "") + "\n" + HostName(),
            Foreground = Brushes.White, Opacity = 0.75, FontSize = 13, Margin = new Thickness(28), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };

        var root = new Grid();
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#70000000")) });
        root.Children.Add(brand);
        root.Children.Add(middle);
        root.Children.Add(desktop);
        root.Children.Add(power);
        Content = root;

        BuildPeople();
        var first = _users.FirstOrDefault(u => u.Name == _state.LastUser) ?? _users.FirstOrDefault();
        Choose(first);

        _tick.Tick += (_, _) => UpdateClock();
        _tick.Start();
        UpdateClock();
        Opened += (_, _) => (_user == null ? _userBox : _answer).Focus();
        Closed += (_, _) => { _tick.Stop(); _greetd.Dispose(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { _answer.Text = ""; _ = _flow.CancelAsync(); _questionOpen = false; Ready(); } };
    }

    private static string HostName()
    {
        try { return File.ReadAllText("/etc/hostname").Trim(); } catch { return Environment.MachineName; }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        _clock.Text = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        _date.Text = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
    }

    // ── people ──

    private void BuildPeople()
    {
        _people.Children.Clear();
        _people.IsVisible = _users.Count > 1 || _users.Count == 0;
        if (_users.Count <= 1 && _users.Count != 0) return;
        foreach (var u in _users.Take(8))
            _people.Children.Add(PersonTile(u.Shown, () => Choose(u), AvatarFor(u, 56)));
        _people.Children.Add(PersonTile("Someone else", () => Choose(null), Letter("+", 56)));
    }

    private Control PersonTile(string label, Action pick, Control picture)
    {
        var b = new Button { Classes = { "flat" }, Padding = new Thickness(8), Background = Brushes.Transparent };
        b.Content = new StackPanel
        {
            Spacing = 6, Width = 92,
            Children = { picture, new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 12, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center } },
        };
        b.Click += (_, _) => pick();
        return b;
    }

    private void Choose(LoginUser? u)
    {
        _ = _flow.CancelAsync();
        _questionOpen = false;
        _user = u;
        _avatar.Child = u == null ? Letter("?", 96) : AvatarFor(u, 96);
        _name.Text = u?.Shown ?? "";
        _name.IsVisible = u != null;
        _userBox.IsVisible = u == null;
        _userBox.Text = "";
        _answer.Text = "";
        _answer.PlaceholderText = "Password";
        _message.IsVisible = false;
        _notes.IsVisible = false;
        var last = u != null && _state.Sessions.TryGetValue(u.Name, out var s) ? s : null;
        var pick = GreeterData.Preferred(_sessions, last);
        _session.SelectedIndex = pick == null ? -1 : _sessions.IndexOf(pick);
        Dispatcher.UIThread.Post(() => (u == null ? _userBox : _answer).Focus());
    }

    private static Control AvatarFor(LoginUser u, double size)
    {
        if (u.Avatar() is { } path)
            try { using var f = File.OpenRead(path); return new Image { Source = Bitmap.DecodeToWidth(f, (int)(size * 2)), Width = size, Height = size, Stretch = Stretch.UniformToFill }; }
            catch { /* fall through to the letter */ }
        return Letter(u.Shown.Length > 0 ? u.Shown[..1].ToUpperInvariant() : "?", size);
    }

    private static Control Letter(string text, double size) => new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = new SolidColorBrush(Color.Parse("#C8102E")),
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = size * 0.42, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    // ── signing in ──

    private async Task Submit()
    {
        if (_busy) return;
        var name = _user?.Name ?? (_userBox.Text ?? "").Trim();
        if (name.Length == 0) { _userBox.Focus(); return; }
        if (_session.SelectedIndex < 0 || _session.SelectedIndex >= _sessions.Count) { Fail("Choose a desktop to start first."); return; }
        _busy = true;
        Busy(true);
        try
        {
            FlowStep step;
            if (!_questionOpen)
            {
                step = await _flow.BeginAsync(name);
                // the first question is nearly always the password, which has just been typed
                if (step.Kind == FlowStep.Kinds.Question) step = await _flow.AnswerAsync(_answer.Text ?? "");
            }
            else step = await _flow.AnswerAsync(_answer.Text ?? "");
            await Handle(step, name);
        }
        catch (Exception ex)
        {
            _questionOpen = false;
            Fail("The login service didn't answer: " + ex.Message);
        }
        finally { _busy = false; Busy(false); }
    }

    private async Task Handle(FlowStep step, string name)
    {
        _notes.Text = string.Join("\n", step.Notes ?? Array.Empty<string>());
        _notes.IsVisible = _notes.Text.Length > 0;
        switch (step.Kind)
        {
            case FlowStep.Kinds.Question:
                // a further question (a second factor, a new password): ask it in the same box
                _questionOpen = true;
                _answer.Text = "";
                _answer.PasswordChar = step.Secret ? '●' : '\0';
                _answer.PlaceholderText = step.Message;
                _message.IsVisible = false;
                _answer.Focus();
                break;
            case FlowStep.Kinds.Authenticated:
                var session = _sessions[_session.SelectedIndex];
                var (cmd, env) = GreeterData.Command(session);
                var started = await _flow.StartAsync(cmd, env);
                if (started.Kind != FlowStep.Kinds.Started) { _questionOpen = false; Fail(started.Message ?? ""); return; }
                _state.LastUser = name;
                _state.Sessions[name] = session.Id;
                GreeterData.Save(_statePath, _state);
                if (_demo) { _questionOpen = false; Ready(); _notes.Text = $"Preview: {name} would now get {session.Name} ({string.Join(' ', cmd)})."; _notes.IsVisible = true; return; }
                // greetd starts the desktop once the login screen has gone
                Close();
                break;
            case FlowStep.Kinds.Failed:
                _questionOpen = false;
                Fail(step.Message ?? "Signing in didn't work.");
                break;
        }
    }

    private void Ready()
    {
        _answer.PasswordChar = '●';
        _answer.PlaceholderText = "Password";
        _message.IsVisible = false;
    }

    private void Fail(string why)
    {
        _message.Text = why;
        _message.IsVisible = true;
        _answer.Text = "";
        _answer.PasswordChar = '●';
        _answer.PlaceholderText = "Password";
        _answer.Focus();
        int step = 0;
        var offsets = new[] { -14, 14, -10, 10, -5, 5, 0 };
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        t.Tick += (_, _) => { _shake.X = offsets[step++]; if (step >= offsets.Length) t.Stop(); };
        t.Start();
    }

    private void Busy(bool on)
    {
        _answer.IsEnabled = !on;
        _userBox.IsEnabled = !on;
        _signIn.IsEnabled = !on;
        _signIn.Content = on ? "Signing in…" : "Sign in";
    }

    // ── power ──

    private Button PowerButton(string label, string verb)
    {
        var b = new Button { Content = label, Classes = { "flat" }, Foreground = Brushes.White };
        b.Click += async (_, _) =>
        {
            if (_demo) { _notes.Text = $"Preview: this would {label.ToLowerInvariant()} the computer."; _notes.IsVisible = true; return; }
            var (code, output) = await ProcessUtil.RunAsync("systemctl", new[] { verb }, 20000);
            if (code != 0) Fail($"Couldn't {label.ToLowerInvariant()}: {output.Split('\n').LastOrDefault()}");
        };
        return b;
    }

    private void CoverScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;
        var b = screen.Bounds;
        if (Math.Abs(Bounds.Width * screen.Scaling - b.Width) < 2 && Math.Abs(Bounds.Height * screen.Scaling - b.Height) < 2) return;
        Position = b.Position;
        Width = b.Width / screen.Scaling;
        Height = b.Height / screen.Scaling;
    }
}
