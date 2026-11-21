// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RedStarOS.Services;
using RedStarOS.Services.Matrix;

namespace RedStarOS.Views;

/// <summary>
/// Messenger: a Matrix client. Sign in to any Matrix server (matrix.org, a community's, your own), then chat in rooms
/// and direct chats with anyone on any server. Matrix is an open, federated standard run by the non-profit Matrix.org
/// Foundation; messages go only to the server you sign in to. End-to-end encrypted chats can't be read yet: they say so.
/// </summary>
public sealed class MessengerWindow : Window
{
    public static MessengerWindow? Instance { get; private set; }

    private static readonly Regex UrlRx = new(@"https?://[^\s<>""]+", RegexOptions.Compiled);
    private static readonly Color[] NameColors =
    {
        Color.Parse("#C8102E"), Color.Parse("#2B6CB0"), Color.Parse("#2F855A"), Color.Parse("#B7791F"), Color.Parse("#6B46C1"),
        Color.Parse("#2C7A7B"), Color.Parse("#C05621"), Color.Parse("#B83280"),
    };

    private readonly ContentControl _host = new();
    // the signed-in view
    private readonly ListBox _rooms = new() { Background = Brushes.Transparent };
    private readonly TextBox _search = new() { PlaceholderText = "Search chats", Margin = new Thickness(10, 6) };
    private readonly TextBlock _me = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Classes = { "caption" }, Margin = new Thickness(10, 0, 10, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _roomName = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _roomInfo = new() { Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _banner = new() { IsVisible = false, Padding = new Thickness(12, 8), Background = new SolidColorBrush(Color.FromArgb(40, 214, 158, 46)) };
    private readonly StackPanel _timeline = new() { Spacing = 2, Margin = new Thickness(16, 10) };
    private readonly ScrollViewer _scroll = new();
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, PlaceholderText = "Write a message… (Enter sends, Shift+Enter: new line)" };
    private readonly TextBlock _typing = new() { Classes = { "caption" }, Margin = new Thickness(16, 0, 16, 2), Height = 16 };
    private readonly Grid _composer = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 4, 12, 12) };
    private readonly StackPanel _invitePanel = new() { Spacing = 10, Margin = new Thickness(24), IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _empty = new() { Text = "Choose a chat, or start one with “New chat”.", Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private Control? _chatView;
    private string? _current;
    private bool _filling;

    public MessengerWindow()
    {
        Title = "Messenger";
        Width = 1000; Height = 680; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Instance = this;
        Content = _host;
        MatrixService.Watching = () => IsActive && WindowState != WindowState.Minimized ? _current : null;
        MatrixService.Changed += OnChanged;
        Closed += (_, _) =>
        {
            MatrixService.Changed -= OnChanged;
            if (ReferenceEquals(Instance, this)) { Instance = null; MatrixService.Watching = null; }
        };
        Activated += (_, _) => { if (_current != null) MatrixService.MarkRead(_current); };
        MatrixService.Resume();
        ShowView();
    }

    // ── opening from elsewhere ──

    public static MessengerWindow Open()
    {
        var w = Instance ?? (MessengerWindow)WindowManager.Open(new MessengerWindow(), "messenger");
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show(); w.Activate();
        return w;
    }

    /// <summary>A notification's "Open": that room.</summary>
    public static void OpenRoom(string roomId) => Dispatcher.UIThread.Post(() => Open().Select(roomId));

    /// <summary>Share → Send to Messenger: the link goes into the message box, to send in the chat you pick.</summary>
    public static MessengerWindow Share(string title, string url)
    {
        var w = Open();
        w._input.Text = (string.IsNullOrWhiteSpace(title) || title.Trim() == url ? "" : title.Trim() + "\n") + url;
        w._input.Focus();
        if (w._current == null && MatrixService.SignedIn) w._status.Text = "Choose a chat, then press Enter to send.";
        return w;
    }

    private void ShowView()
    {
        if (!MatrixService.SignedIn) { _host.Content = SignInView(); _chatView = null; return; }
        _host.Content = _chatView ??= ChatView();
        FillRooms();
        ShowRoom();
    }

    private void OnChanged(string? roomId)
    {
        if (MatrixService.SignedIn != (_chatView != null && _host.Content == _chatView)) { ShowView(); return; }
        if (!MatrixService.SignedIn) return;
        if (roomId == null) FillRooms();
        if (roomId == null || roomId == _current) ShowRoom();
    }

    // ── signing in ──

    private Control SignInView()
    {
        var server = new TextBox { Text = "matrix.org", PlaceholderText = "Server (matrix.org, or your community's)" };
        var user = new TextBox { PlaceholderText = "User name, or @name:server" };
        var pw = new TextBox { PasswordChar = '●', PlaceholderText = "Password" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#E53E3E")) };
        var go = new Button { Content = "Sign in", Classes = { "primary" }, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        user.TextChanged += (_, _) =>
        {
            // "@ada:example.org" says which server
            var t = user.Text ?? "";
            if (t.StartsWith('@') && t.IndexOf(':') is var i and > 0 && i < t.Length - 1) server.Text = t[(i + 1)..];
        };
        go.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(user.Text) || string.IsNullOrEmpty(pw.Text)) { status.Text = "Write your user name and password."; return; }
            go.IsEnabled = false;
            status.Foreground = new SolidColorBrush(Color.Parse("#718096"));
            status.Text = "Signing in…";
            var err = await MatrixService.SignInAsync(server.Text ?? "", user.Text ?? "", pw.Text ?? "");
            go.IsEnabled = true;
            if (err != null) { status.Foreground = new SolidColorBrush(Color.Parse("#E53E3E")); status.Text = err; return; }
            pw.Text = "";
            ShowView();
        };
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 380,
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "💬", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "Messenger", FontSize = 26, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "Chat with anyone on Matrix, the open network for messaging.", TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center, Classes = { "caption" } },
                    server, user, pw, go, status,
                    new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Text =
                        "No account yet? Create one on a Matrix server's website (matrix.org is a free, non-profit one) or run your own server. "
                        + "Messages go only to the server you sign in to. Chats that are end-to-end encrypted can't be read here yet." },
                },
            },
        };
    }

    // ── signed in ──

    private Control ChatView()
    {
        Button Small(string text, string tip, Action act)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => act();
            return b;
        }
        var menu = new Button { Content = "⋯", Padding = new Thickness(10, 2) };
        ToolTip.SetTip(menu, "Account");
        menu.Click += async (_, _) =>
        {
            int c = await Dialogs.Ask(this, "Messenger", "Signed in as " + MatrixService.Login?.UserId + " on " + MatrixService.Login?.Homeserver + ".", "Sign out", "Close");
            if (c == 0) { await MatrixService.SignOutAsync(); _current = null; ShowView(); }
        };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 10, 8, 4) };
        top.Children.Add(_me);
        Grid.SetColumn(menu, 1);
        top.Children.Add(menu);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 0, 10, 4),
            Children = { Small("New chat", "A direct chat with someone (@name:server)", () => _ = NewChat()),
                         Small("New room", "A group chat", () => _ = NewRoom()), Small("Join", "Join a room by its address (#room:server)", () => _ = Join()) },
        };
        var left = new DockPanel { Width = 290 };
        var head = new StackPanel { Children = { top, actions, _search, _status } };
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        left.Children.Add(_rooms);
        _search.TextChanged += (_, _) => FillRooms();
        _rooms.SelectionChanged += (_, _) =>
        {
            if (_filling || (_rooms.SelectedItem as ListBoxItem)?.Tag is not string id || id == _current) return;
            _current = id;
            ShowRoom(scrollToEnd: true);
        };

        // the conversation
        var header = new StackPanel { Margin = new Thickness(16, 10, 16, 8), Children = { _roomName, _roomInfo } };
        _banner.Child = new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
            "🔒 This chat is end-to-end encrypted. Messenger can't read or send encrypted messages yet; use another Matrix client for this one for now." };
        _scroll.Content = _timeline;
        var attach = new Button { Content = "📎", Padding = new Thickness(10, 6), VerticalAlignment = VerticalAlignment.Bottom };
        ToolTip.SetTip(attach, "Send a file or picture");
        attach.Click += async (_, _) => await Attach();
        var send = new Button { Content = "Send", Classes = { "primary" }, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 0) };
        send.Click += (_, _) => SendInput();
        _input.Margin = new Thickness(6, 0, 0, 0);
        _composer.Children.Add(attach);
        Grid.SetColumn(_input, 1); _composer.Children.Add(_input);
        Grid.SetColumn(send, 2); _composer.Children.Add(send);
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { SendInput(); e.Handled = true; }
        };
        _input.TextChanged += (_, _) => { if (_current != null && !string.IsNullOrEmpty(_input.Text)) MatrixService.Typing(_current, true); };

        var right = new DockPanel();
        var rightTop = new StackPanel { Children = { header, _banner } };
        var bottom = new StackPanel { Children = { _typing, _composer } };
        DockPanel.SetDock(rightTop, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        right.Children.Add(rightTop);
        right.Children.Add(bottom);
        var middle = new Grid();
        middle.Children.Add(_scroll);
        middle.Children.Add(_invitePanel);
        middle.Children.Add(_empty);
        right.Children.Add(middle);

        var root = new DockPanel();
        var sep = new Border { Width = 1, Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) };
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(sep, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(sep);
        root.Children.Add(right);
        return root;
    }

    private void Select(string roomId)
    {
        _current = roomId;
        FillRooms();
        ShowRoom(scrollToEnd: true);
    }

    private void FillRooms()
    {
        if (MatrixService.State is not { } st) return;
        _me.Text = MatrixService.Login?.UserId ?? "";
        _status.Text = MatrixService.Status ?? "";
        _status.IsVisible = _status.Text.Length > 0;
        var q = (_search.Text ?? "").Trim();
        var rooms = st.Rooms.Values.Where(r => q.Length == 0 || r.DisplayName(st.Me).Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(r => r.Invite).ThenByDescending(r => r.LastActivity).ToList();
        _filling = true;
        var items = rooms.Select(r => RoomItem(st, r)).ToList();
        _rooms.ItemsSource = items;
        _rooms.SelectedItem = items.FirstOrDefault(i => i.Tag as string == _current);
        _filling = false;
    }

    private static ListBoxItem RoomItem(MatrixState st, MatrixRoom r)
    {
        var name = new TextBlock { Text = r.DisplayName(st.Me), FontWeight = r.Unread > 0 || r.Invite ? FontWeight.Bold : FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var preview = new TextBlock { Text = r.Invite ? "✉ Invitation" : r.Preview(st.Me), Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(4, 4) };
        var avatar = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(ColorFor(r.Id)),
            Child = new TextBlock { Text = Initial(r.DisplayName(st.Me)), Foreground = Brushes.White, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        g.Children.Add(avatar);
        var text = new StackPanel { Children = { name, preview }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1); g.Children.Add(text);
        if (r.Unread > 0)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(r.Highlights > 0 ? Color.Parse("#C8102E") : Color.Parse("#718096")), CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 0), Height = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
                Child = new TextBlock { Text = r.Unread > 99 ? "99+" : r.Unread.ToString(CultureInfo.InvariantCulture), Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
            };
            Grid.SetColumn(badge, 2); g.Children.Add(badge);
        }
        return new ListBoxItem { Tag = r.Id, Content = g };
    }

    private static string Initial(string name) => name.TrimStart('#', '@').FirstOrDefault(char.IsLetterOrDigit) is var c and not '\0' ? char.ToUpperInvariant(c).ToString() : "?";
    private static Color ColorFor(string s) => NameColors[(int)((uint)s.Aggregate(17, (h, ch) => h * 31 + ch) % NameColors.Length)];

    private bool AtBottom => _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 40;

    private void ShowRoom(bool scrollToEnd = false)
    {
        var st = MatrixService.State;
        var room = _current != null ? st?.Rooms.GetValueOrDefault(_current) : null;
        _empty.IsVisible = room == null;
        _scroll.IsVisible = room is { Invite: false };
        _invitePanel.IsVisible = room is { Invite: true };
        _composer.IsVisible = room is { Invite: false, Encrypted: false };
        _banner.IsVisible = room is { Invite: false, Encrypted: true };
        if (room == null || st == null) { _roomName.Text = ""; _roomInfo.Text = ""; _typing.Text = ""; return; }
        _roomName.Text = room.DisplayName(st.Me);
        var members = room.JoinedCount > 0 ? room.JoinedCount : room.Members.Count(m => m.Value.Membership == "join");
        _roomInfo.Text = string.Join(" · ", new[] { room.IsDirect ? "Direct chat" : $"{members} member{(members == 1 ? "" : "s")}", room.Alias, room.Topic }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        _typing.Text = room.Typing.Count switch
        {
            0 => "",
            1 => $"{room.NameOf(room.Typing[0])} is writing…",
            _ => string.Join(", ", room.Typing.Take(3).Select(room.NameOf)) + " are writing…",
        };
        if (room.Invite) { FillInvite(st, room); return; }
        bool stick = scrollToEnd || AtBottom;
        FillTimeline(st, room);
        if (stick) Dispatcher.UIThread.Post(() => _scroll.Offset = new Vector(0, double.MaxValue), DispatcherPriority.Background);
        if (IsActive) MatrixService.MarkRead(room.Id);
    }

    private void FillInvite(MatrixState st, MatrixRoom room)
    {
        _invitePanel.Children.Clear();
        _invitePanel.Children.Add(new TextBlock { Text = (room.Inviter != null ? room.NameOf(room.Inviter) : "Someone") + " invited you", FontSize = 18, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        _invitePanel.Children.Add(new TextBlock { Text = room.IsDirect ? "to a direct chat" : "to " + room.DisplayName(st.Me), Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center });
        var accept = new Button { Content = "Accept", Classes = { "primary" } };
        var decline = new Button { Content = "Decline" };
        var note = new TextBlock { Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center };
        var id = room.Id;
        accept.Click += async (_, _) => { accept.IsEnabled = false; note.Text = await MatrixService.Try(c => c.JoinAsync(id)) ?? "Joining…"; };
        decline.Click += async (_, _) => { decline.IsEnabled = false; note.Text = await MatrixService.Try(c => c.LeaveAsync(id)) ?? "Declined."; };
        _invitePanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children = { decline, accept } });
        _invitePanel.Children.Add(note);
    }

    private void FillTimeline(MatrixState st, MatrixRoom room)
    {
        _timeline.Children.Clear();
        if (room.HasOlder && room.PrevBatch != null)
        {
            var older = new Button { Content = "Earlier messages", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            older.Click += async (_, _) =>
            {
                older.IsEnabled = false;
                var h = _scroll.Extent.Height;
                await MatrixService.LoadOlderAsync(room.Id);
                Dispatcher.UIThread.Post(() => _scroll.Offset = new Vector(0, _scroll.Extent.Height - h), DispatcherPriority.Background);
            };
            _timeline.Children.Add(older);
        }
        MatrixMessage? prev = null;
        DateTime? day = null;
        foreach (var m in room.Timeline)
        {
            var local = m.Time.ToLocalTime();
            if (day != local.Date)
            {
                day = local.Date;
                _timeline.Children.Add(new TextBlock { Text = local.Date == DateTime.Today ? "Today" : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : local.ToString("dddd d MMMM yyyy"),
                    Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 4) });
                prev = null;
            }
            if (m.Kind == MessageKind.Membership)
            {
                _timeline.Children.Add(new TextBlock { Text = m.Body, Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2) });
                prev = null;
                continue;
            }
            bool continued = prev != null && prev.Sender == m.Sender && (m.Time - prev.Time).TotalMinutes < 5;
            _timeline.Children.Add(MessageView(room, m, continued));
            prev = m;
        }
    }

    private Control MessageView(MatrixRoom room, MatrixMessage m, bool continued)
    {
        var panel = new StackPanel { Margin = new Thickness(0, continued ? 0 : 8, 0, 0), Opacity = m.PendingTxn != null ? 0.55 : 1 };
        if (!continued)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock { Text = room.NameOf(m.Sender), FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(ColorFor(m.Sender)) });
            head.Children.Add(new TextBlock { Text = m.Time.ToLocalTime().ToString(SettingsStore.Current.Clock24h ? "HH:mm" : "h:mm tt"), Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Bottom });
            panel.Children.Add(head);
        }
        switch (m.Kind)
        {
            case MessageKind.Encrypted:
                panel.Children.Add(new TextBlock { Text = "🔒 Encrypted message (Messenger can't read encrypted chats yet)", FontStyle = FontStyle.Italic, Classes = { "caption" } });
                break;
            case MessageKind.Deleted:
                panel.Children.Add(new TextBlock { Text = "Message deleted", FontStyle = FontStyle.Italic, Classes = { "caption" } });
                break;
            case MessageKind.Image:
                panel.Children.Add(ImageView(m));
                break;
            case MessageKind.File or MessageKind.Video or MessageKind.Audio:
            {
                var icon = m.Kind switch { MessageKind.Video => "🎞", MessageKind.Audio => "🔊", _ => "📎" };
                var b = new Button { Content = $"{icon} {m.FileName ?? m.Body}" + (m.Size > 0 ? $"  ({FileOps.FormatSize(m.Size)})" : ""), HorizontalAlignment = HorizontalAlignment.Left };
                ToolTip.SetTip(b, "Save to Downloads and open");
                b.Click += async (_, _) => await SaveAndOpen(m, b);
                panel.Children.Add(b);
                break;
            }
            default:
                panel.Children.Add(TextView(m));
                break;
        }
        if (m.Failed)
        {
            var retry = new Button { Content = "Not sent · Try again", Classes = { "flat" }, Foreground = new SolidColorBrush(Color.Parse("#E53E3E")), HorizontalAlignment = HorizontalAlignment.Left };
            var roomId = room.Id;
            retry.Click += async (_, _) => { room.Timeline.Remove(m); await MatrixService.SendTextAsync(roomId, m.Body); };
            panel.Children.Add(retry);
        }
        return panel;
    }

    /// <summary>The text, with its web addresses as links to Crimson Online, selectable for copying.</summary>
    private static Control TextView(MatrixMessage m)
    {
        var t = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        if (m.Kind == MessageKind.Notice) t.Opacity = 0.8;
        if (m.Kind == MessageKind.Emote) t.FontStyle = FontStyle.Italic;
        var body = m.Body;
        int at = 0;
        foreach (Match u in UrlRx.Matches(body))
        {
            if (u.Index > at) t.Inlines!.Add(new Run(body[at..u.Index]));
            var url = u.Value.TrimEnd('.', ',', ')', ';', '!', '?');
            var link = new Run(url) { Foreground = new SolidColorBrush(Color.Parse("#2B6CB0")), TextDecorations = TextDecorations.Underline };
            t.Inlines!.Add(link);
            at = u.Index + url.Length;
        }
        if (at < body.Length) t.Inlines!.Add(new Run(body[at..]));
        if (m.Edited) t.Inlines!.Add(new Run("  (edited)") { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#A0AEC0")) });
        var urls = UrlRx.Matches(body).Select(x => x.Value.TrimEnd('.', ',', ')', ';', '!', '?')).Distinct().ToList();
        if (urls.Count == 0) return t;
        // a click on the message opens its link (the text stays selectable for copying)
        t.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(t, "Double-click to open " + urls[0]);
        t.DoubleTapped += (_, _) => BrowserLauncher.Launch(urls[0]);
        return t;
    }

    private static Control ImageView(MatrixMessage m)
    {
        var img = new Image { MaxWidth = 320, MaxHeight = 260, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        var holder = new Button { Content = new TextBlock { Text = "📷 " + m.Body }, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent };
        ToolTip.SetTip(holder, "Open the picture");
        holder.Click += async (_, _) => await SaveAndOpen(m, holder);
        if (m.Mxc != null)
            _ = Task.Run(async () =>
            {
                var data = await MatrixService.ThumbnailAsync(m.Mxc, 640, 520);
                if (data == null) return;
                Dispatcher.UIThread.Post(() =>
                {
                    try { using var ms = new MemoryStream(data); img.Source = new Bitmap(ms); holder.Content = img; } catch { }
                });
            });
        return holder;
    }

    private static async Task SaveAndOpen(MatrixMessage m, Button b)
    {
        b.IsEnabled = false;
        var path = await MatrixService.DownloadAsync(m);
        b.IsEnabled = true;
        if (path == null) { Notifier.Show("Messenger", "Couldn't download " + (m.FileName ?? "the file") + ".", "messenger"); return; }
        FileOpener.Open(path);
    }

    // ── sending ──

    private void SendInput()
    {
        var text = (_input.Text ?? "").TrimEnd();
        if (_current == null || text.Trim().Length == 0) return;
        if (MatrixService.State?.Rooms.GetValueOrDefault(_current) is { Encrypted: true }) return;
        _input.Text = "";
        MatrixService.Typing(_current, false);
        _ = MatrixService.SendTextAsync(_current, text);
        _status.IsVisible = false;
        Dispatcher.UIThread.Post(() => _scroll.Offset = new Vector(0, double.MaxValue), DispatcherPriority.Background);
    }

    private async Task Attach()
    {
        if (_current == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Send a file", AllowMultiple = true });
        foreach (var f in files)
        {
            if (f.TryGetLocalPath() is not { } path) continue;
            var err = await MatrixService.SendFileAsync(_current, path);
            if (err != null) Notifier.Show("Messenger", $"Couldn't send {Path.GetFileName(path)}: {err}", "messenger");
        }
    }

    private async Task NewChat()
    {
        var who = await Dialogs.Prompt(this, "New chat", "Their Matrix address (like @ada:example.org)");
        if (string.IsNullOrWhiteSpace(who)) return;
        var (id, err) = await MatrixService.DirectChatAsync(who);
        if (err != null) { await Dialogs.ShowMessage(this, "New chat", err); return; }
        _current = id;
        _status.Text = "Chat started; it appears once the server confirms it.";
        _status.IsVisible = true;
    }

    private async Task NewRoom()
    {
        var name = await Dialogs.Prompt(this, "New room", "Name of the group chat");
        if (string.IsNullOrWhiteSpace(name)) return;
        int kind = await Dialogs.Ask(this, "New room", "Who can join “" + name.Trim() + "”?", "Only people I invite", "Anyone with the address", "Cancel");
        if (kind is not (0 or 1)) return;
        string? id = null;
        var err = await MatrixService.Try(async c => id = await c.CreateRoomAsync(name.Trim(), kind == 1, kind == 1 ? name.Trim().ToLowerInvariant().Replace(' ', '-') : null));
        if (err != null) { await Dialogs.ShowMessage(this, "New room", "Couldn't create it: " + err); return; }
        _current = id;
    }

    private async Task Join()
    {
        var addr = await Dialogs.Prompt(this, "Join a room", "The room's address (like #linux:matrix.org)");
        if (string.IsNullOrWhiteSpace(addr)) return;
        string? id = null;
        var err = await MatrixService.Try(async c => id = await c.JoinAsync(addr.Trim()));
        if (err != null) { await Dialogs.ShowMessage(this, "Join a room", "Couldn't join: " + err); return; }
        _current = id;
    }
}
