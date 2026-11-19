using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RedStarOS.Services;
using Avalonia.Input.Platform;

namespace RedStarOS.Views;

/// <summary>Red Star Online: native tabs, address bar and menus around Chromium pages rendered by the web engine.</summary>
public partial class WebBrowserWindow : Window
{
    public static WebBrowserWindow? Instance { get; private set; }
    public const string HomeUrl = "redstar://newtab";
    private static int _nextGroup = 1;

    private static readonly double[] ZoomSteps = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5 };

    private readonly List<WebTab> _tabs = new();
    private readonly Stack<string> _closed = new();
    private readonly bool _priv;
    private readonly int _group;
    private WebTab _cur = null!;
    private bool _fullscreen, _syncUrl, _noSession, _pageFullscreen;
    private WebTab? _writerWait;
    private WindowState _prevState;
    private string? _printPath;

    public WebBrowserWindow() : this(false) { }

    public WebBrowserWindow(bool priv)
    {
        _priv = priv;
        _group = priv ? _nextGroup++ : 0;
        InitializeComponent();
        if (!priv) Instance = this;
        if (priv)
        {
            PrivateBadge.IsVisible = true;
            Title = "Private browsing — Red Star Online";
        }

        UrlBox.KeyDown += OnUrlKey;
        UrlBox.TextChanged += (_, _) => { if (!_syncUrl && UrlBox.IsFocused) ShowSuggestions(); };
        UrlBox.GotFocus += (_, _) => Dispatcher.UIThread.Post(() => UrlBox.SelectAll());
        UrlBox.LostFocus += (_, _) => DispatcherTimer.RunOnce(() => { if (!UrlBox.IsFocused) SuggestPopup.IsOpen = false; }, TimeSpan.FromMilliseconds(180));
        SuggestPopup.PlacementTarget = UrlBox;
        SuggestList.Focusable = false;
        SuggestList.AddHandler(PointerPressedEvent, OnSuggestPressed, RoutingStrategies.Tunnel);

        FindBox.TextChanged += (_, _) => { if (_cur != null) _cur.Find(FindBox.Text ?? "", true, false, CaseChk.IsChecked == true); };
        CaseChk.IsCheckedChanged += (_, _) => { if (_cur != null) _cur.Find(FindBox.Text ?? "", true, false, CaseChk.IsChecked == true); };
        FindBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _cur?.Find(FindBox.Text ?? "", !e.KeyModifiers.HasFlag(KeyModifiers.Shift), true, CaseChk.IsChecked == true); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
        };

        AddHandler(KeyDownEvent, OnWindowKey, RoutingStrategies.Tunnel);
        WebEngine.GlobalMessage += OnGlobal;
        WebEngine.Disconnected += OnEngineLost;
        BrowserData.Changed += OnDataChanged;
        Activated += (_, _) => PushConfig();
        var openFolder = new Button { Content = "📂 Open Downloads folder", Tag = "downloads", HorizontalAlignment = HorizontalAlignment.Left };
        openFolder.Classes.Add("flat");
        openFolder.Click += OnCmd;
        DlFlyout.Content = new StackPanel
        {
            Width = 380, Spacing = 6,
            Children = { new TextBlock { Text = "Downloads", FontWeight = FontWeight.SemiBold }, DlList, openFolder }
        };
        DownloadsBtn.Flyout = DlFlyout;
        RebuildBar();
        Closed += (_, _) =>
        {
            if (!_noSession) SaveSession();
            WebEngine.GlobalMessage -= OnGlobal;
            WebEngine.Disconnected -= OnEngineLost;
            BrowserData.Changed -= OnDataChanged;
            BrowserData.FlushNow();
            foreach (var t in _tabs.ToList()) t.Close();
            _tabs.Clear();
            if (ReferenceEquals(Instance, this)) { Instance = null; TrayService.Remove("online"); }
        };
    }

    // ───────────────────────── startup ─────────────────────────
    public async Task<bool> StartAsync(string? url)
    {
        if (!await WebEngine.EnsureStartedAsync()) return false;
        PushConfig();
        if (!_priv) SetupTray();
        if (string.IsNullOrEmpty(url) && !_priv && BrowserData.Settings.RestoreSession && BrowserData.Store.Session.Count > 0)
        {
            foreach (var u in BrowserData.Store.Session.ToList()) NewTab(u, false);
            if (_tabs.Count > 0) SwitchTo(_tabs[0]);
        }
        else NewTab(string.IsNullOrEmpty(url) ? HomeUrl : url);
        return true;
    }

    private void PushConfig()
    {
        if (!WebEngine.Connected) return;
        var s = BrowserData.Settings;
        WebEngine.Send(new
        {
            c = "config", blockTrackers = s.BlockTrackers, allowSites = s.TrackerAllowSites,
            blockedSites = s.BlockedSites.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            theme = ThemeService.IsDark ? "dark" : "light"
        });
    }

    private void OnDataChanged()
    {
        PushConfig();
        RebuildBar();
        if (_cur != null) UpdateChrome();
    }

    public void OpenUrl(string url, bool newTab = true)
    {
        if (newTab || _cur == null) NewTab(url); else Go(url);
    }

    public void Raise()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
    }

    private void SetupTray()
    {
        TrayService.Upsert(new TrayEntry
        {
            Key = "online", Tooltip = "Red Star Online", Glyph = "🌐",
            Menu = new List<TrayMenuEntry>
            {
                new() { Id = "show", Label = "Show Red Star Online" },
                new() { Id = "newtab", Label = "New tab" },
                new() { Id = "private", Label = "New private window" },
                new() { Id = "downloads", Label = "Downloads folder" },
                new() { Separator = true },
                new() { Id = "close", Label = "Close browser" },
            },
            MenuSelected = id => Dispatcher.UIThread.Post(() =>
            {
                switch (id)
                {
                    case "show": Raise(); break;
                    case "newtab": Raise(); NewTab(HomeUrl); break;
                    case "private": BrowserLauncher.LaunchPrivate(); break;
                    case "downloads": WindowManager.Open(new FileManagerWindow(DownloadsDir())); break;
                    case "close": Close(); break;
                }
            }),
            Click = () => Dispatcher.UIThread.Post(Raise)
        });
    }

    // ───────────────────────── tabs ─────────────────────────
    private static bool IsHttp(string u) => u.StartsWith("http://") || u.StartsWith("https://");

    private WebTab NewTab(string? url, bool activate = true, int adopt = 0)
    {
        var t = new WebTab(_priv) { Group = _group };
        t.Changed += () => OnTabChanged(t);
        t.NewTabRequested += (u, disp, child) => NewTab(u, disp != "background-tab", child);
        t.ClosedByPage += () => Dispatcher.UIThread.Post(() => { WebPipWindow.EndFor(t); CloseTab(t); });
        t.AuthRequested += (req, host, realm, proxy) => _ = AskLogin(t, req, host, realm, proxy);
        t.FullscreenRequested += on => OnPageFullscreen(t, on);
        t.PipChanged += (on, vw, vh, paused) => OnPip(t, on, vw, vh, paused);
        t.PipFailed += why => Toast(why == "novideo" ? "There is no video on this page." : "Picture-in-picture didn't start (" + why + ").");
        t.EngineError += msg => { BrowserLog.Write("engine: " + msg); Toast(msg); };
        t.ContextMenuRequested += info => ShowContext(t, info);
        t.ChallengeSeen += _ => Notifier.Show("Human verification on this page",
            "If the check doesn't respond, open the page in a separate window, complete it there and close that window — the tab reloads, already verified.",
            "online", new NotificationAction("Open in a window", () => t.PopOut()));
        t.PermissionRequested += (req, perm, u) => _ = AskPermission(t, req, perm, u);
        t.CopyRequested += text => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        t.FindChanged += () => { if (ReferenceEquals(t, _cur)) UpdateFindInfo(); };
        t.PageData += (kind, title, html, pageUrl, error) => Dispatcher.UIThread.Post(() => OnPageData(t, kind, title, html, pageUrl, error));
        t.PdfDone += (path, ok) => Dispatcher.UIThread.Post(() => OnPdfDone(path, ok));
        _tabs.Add(t);

        string first;
        if (adopt > 0) first = url ?? "about:blank";
        else if (string.IsNullOrWhiteSpace(url) || url.StartsWith("redstar://", StringComparison.OrdinalIgnoreCase))
        {
            var page = string.IsNullOrWhiteSpace(url) ? "newtab" : url![10..].Trim('/').ToLowerInvariant();
            t.Internal = page.Length == 0 ? "newtab" : page;
            _internalBase[t] = "about:blank";
            first = "about:blank";
        }
        else first = WebTab.Normalize(url);

        int w = Math.Max(300, (int)Surface.Bounds.Width), h = Math.Max(300, (int)Surface.Bounds.Height);
        if (w <= 300) { w = 1100; h = 700; }
        if (adopt > 0) t.Adopt(adopt, first, w, h); else t.Open(first, w, h);
        if (activate || _cur == null) SwitchTo(t); else RebuildTabs();
        return t;
    }

    private void SwitchTo(WebTab t)
    {
        if (_cur != null && !ReferenceEquals(_cur, t)) { _cur.StopFind(); FindBar.IsVisible = false; SuggestPopup.IsOpen = false; }
        _cur = t;
        Surface.Tab = t;
        RebuildTabs();
        UpdateChrome();
        if (t.Internal.Length == 0 && t.Url.Length > 0 && t.Url != "about:blank") Surface.Focus();
        else if (t.Internal == "newtab") { /* the page focuses its own search box */ }
        else if (t.Internal.Length == 0) UrlBox.Focus();
        t.Focus();
    }

    private void CloseTab(WebTab t)
    {
        int i = _tabs.IndexOf(t);
        if (i < 0) return;
        if (!string.IsNullOrEmpty(t.Url) && IsHttp(t.Url) && !t.Private) _closed.Push(t.Url);
        _tabs.RemoveAt(i);
        _shown.Remove(t); _lastRec.Remove(t); _zoomHost.Remove(t); _internalBase.Remove(t);
        if (ReferenceEquals(_writerWait, t)) _writerWait = null;
        WebPipWindow.EndFor(t);
        t.Close();
        if (_tabs.Count == 0)
        {
            if (!_priv && ReferenceEquals(Instance, this)) { BrowserData.Store.Session.Clear(); BrowserData.Save(); }
            _noSession = true;
            Close();
            return;
        }
        if (ReferenceEquals(t, _cur)) SwitchTo(_tabs[Math.Min(i, _tabs.Count - 1)]); else RebuildTabs();
    }

    private readonly HashSet<string> _favAsked = new();

    private Control TabIcon(WebTab tab)
    {
        if (tab.Loading) return Icons.Create("loading", 14);
        if (tab.Internal.Length > 0) return Icons.Create(tab.Internal == "newtab" ? "plus" : "star", 14);
        if (_priv && tab.Favicon.Length == 0) return Icons.Create("private", 14);
        if (tab.Favicon.Length > 0 && !_priv)
        {
            var bmp = Favicons.TryGet(tab.Favicon);
            if (bmp != null) return new Image { Source = bmp, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            if (_favAsked.Add(tab.Favicon)) _ = Favicons.GetAsync(tab.Favicon).ContinueWith(_ => Dispatcher.UIThread.Post(RebuildTabs));
        }
        return Icons.Create("globe", 14);
    }

    private void RebuildTabs()
    {
        ShellTabs.Build(TabsPanel, _tabs.Select(t =>
        {
            var tab = t;
            return new TabSpec
            {
                Title = tab.Internal.Length > 0 ? BrowserPages.TitleOf(tab.Internal) : string.IsNullOrEmpty(tab.Title) ? tab.Url : tab.Title,
                Icon = TabIcon(tab),
                Active = ReferenceEquals(tab, _cur),
                Tooltip = tab.Internal.Length > 0 ? null : tab.Url,
                TrailingIcon = tab.Muted ? "volume-mute" : tab.Audible ? "volume" : null,
                TrailingClick = () => tab.SetMuted(!tab.Muted),
                Select = () => SwitchTo(tab),
                Close = () => CloseTab(tab),
            };
        }));
    }

    private readonly Dictionary<WebTab, (string, bool, string, bool, bool, string)> _shown = new();
    private readonly Dictionary<WebTab, string> _lastRec = new();
    private readonly Dictionary<WebTab, string> _zoomHost = new();
    private readonly Dictionary<WebTab, string> _internalBase = new();   // the tab's URL when its internal page was opened

    private void OnTabChanged(WebTab t)
    {
        // an internal page (History, Settings...) stays until the tab really goes somewhere else
        if (t.Internal.Length > 0 && IsHttp(t.Url) && (!_internalBase.TryGetValue(t, out var b) || b != t.Url)) t.Internal = "";
        if (t.Id != 0 && !t.Private) TrackHistory(t);
        TrackZoom(t);
        if (!t.Loading && !_priv) SaveSession();
        if (ReferenceEquals(t, _cur)) UpdateChrome();
        var now = (t.Title, t.Loading, t.Favicon, t.Audible, t.Muted, t.Internal);
        if (_shown.TryGetValue(t, out var old) && old == now) return;
        _shown[t] = now;
        RebuildTabs();
    }

    private void TrackHistory(WebTab t)
    {
        if (t.Loading || !IsHttp(t.Url) || t.ErrorText != null) return;
        if (_lastRec.TryGetValue(t, out var last) && last == t.Url) { BrowserData.UpdateTitle(t.Url, t.Title); return; }
        _lastRec[t] = t.Url;
        BrowserData.RecordVisit(t.Url, t.Title);
    }

    private void TrackZoom(WebTab t)
    {
        var host = BrowserData.Host(t.Url);
        if (host.Length == 0 || !IsHttp(t.Url)) return;
        var z = BrowserData.Settings.SiteZoom;
        double stored = z.TryGetValue(host, out var v) ? v : 1.0;
        if (!_zoomHost.TryGetValue(t, out var h) || h != host)
        {
            _zoomHost[t] = host;
            if (Math.Abs(t.Zoom - stored) > 0.01) t.SetZoom(stored);
            return;
        }
        if (Math.Abs(t.Zoom - stored) > 0.01 && !_priv)
        {
            if (Math.Abs(t.Zoom - 1) < 0.01) z.Remove(host); else z[host] = t.Zoom;
            BrowserData.Save();
        }
    }

    private void SaveSession()
    {
        if (_priv || !ReferenceEquals(Instance, this) || _tabs.Count == 0) return;
        var urls = _tabs.Where(x => IsHttp(x.Url)).Select(x => x.Url).ToList();
        if (urls.SequenceEqual(BrowserData.Store.Session)) return;
        BrowserData.Store.Session = urls;
        BrowserData.Save();
    }

    // ───────────────────────── page / chrome ─────────────────────────
    private readonly Flyout DlFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedRight };
    private readonly StackPanel DlList = new() { Spacing = 6 };
    private (WebTab?, string) _pageShown;

    private void UpdatePage()
    {
        var t = _cur;
        if (t == null) return;
        if (t.Internal.Length == 0)
        {
            if (PagePanel.IsVisible) { PagePanel.IsVisible = false; PageHost.Content = null; }
            _pageShown = (null, "");
            return;
        }
        if (_pageShown.Item1 == t && _pageShown.Item2 == t.Internal && PagePanel.IsVisible) return;
        _pageShown = (t, t.Internal);
        PageHost.Content = BrowserPages.Build(t.Internal, Go, () => _ = BrowserPages.ShowClearDialog(this), _priv);
        PagePanel.IsVisible = true;
    }

    private void ShowInternal(string page)
    {
        if (_cur == null) return;
        _cur.Internal = page;
        _internalBase[_cur] = _cur.Url;
        _pageShown = (null, "");
        UpdateChrome();
        RebuildTabs();
        _shown.Remove(_cur);
    }

    private void Go(string input)
    {
        if (_cur == null) return;
        var u = input.Trim();
        if (u.StartsWith("redstar://", StringComparison.OrdinalIgnoreCase))
        {
            var page = u[10..].Trim('/').ToLowerInvariant();
            ShowInternal(page.Length == 0 ? "newtab" : page);
            return;
        }
        SuggestPopup.IsOpen = false;
        _cur.Internal = "";
        _cur.Navigate(u);
        UpdateChrome();
        _shown.Remove(_cur);
        RebuildTabs();
        Surface.Focus();
    }

    private static bool IsCertError(int code) => (code <= -200 && code >= -299) || code == -501;

    private void UpdateChrome()
    {
        if (_cur == null) return;
        var t = _cur;
        UpdatePage();
        if (!UrlBox.IsFocused)
        {
            _syncUrl = true;
            UrlBox.Text = t.Internal.Length > 0 ? (t.Internal == "newtab" ? "" : "redstar://" + t.Internal) : t.Url == "about:blank" ? "" : t.Url;
            _syncUrl = false;
        }
        bool realPage = t.Url.Length > 0 && t.Url != "about:blank";
        BackBtn.IsEnabled = t.CanBack || (t.Internal.Length > 0 && realPage);
        FwdBtn.IsEnabled = t.CanForward && t.Internal.Length == 0;
        ReloadIcon.IsVisible = !t.Loading;
        StopIcon.IsVisible = t.Loading;
        ToolTip.SetTip(ReloadBtn, t.Loading ? "Stop loading (Esc)" : "Reload (F5 / Ctrl+R)");
        ReloadBtn.Tag = t.Loading ? "stop" : "reload";
        Progress.IsVisible = t.Loading && t.Internal.Length == 0;
        var pageTitle = t.Internal.Length > 0 ? BrowserPages.TitleOf(t.Internal) : string.IsNullOrEmpty(t.Title) ? "New tab" : t.Title;
        Title = pageTitle + (_priv ? " — Private — Red Star Online" : " — Red Star Online");
        StatusPill.IsVisible = !string.IsNullOrEmpty(t.Status) && t.Internal.Length == 0;
        StatusText.Text = t.Status;
        ZoomLabel.Text = Math.Abs(t.Zoom - 1) < 0.01 ? "" : (int)Math.Round(t.Zoom * 100) + "%";
        StarBtn.Content = Icons.Create(BrowserData.FindBookmark(t.Url) != null ? "star-filled" : "star", 18);
        StarBtn.IsEnabled = t.Internal.Length == 0 && realPage;
        AudioBtn.IsVisible = t.Audible || t.Muted;
        AudioBtn.Content = Icons.Create(t.Muted ? "volume-mute" : "volume", 18);

        // padlock / site information
        string icon; string tip;
        if (t.Internal.Length > 0) { icon = "star"; tip = "Red Star Online page"; }
        else if (t.InReader) { icon = "book"; tip = "Reader view — use the menu's Reader mode again to go back"; }
        else if (t.ErrorText != null && IsCertError(t.ErrorCode)) { icon = "warning"; tip = "Connection is not private"; }
        else if (t.Url.StartsWith("https://")) { icon = "lock"; tip = "Connection is secure"; }
        else if (t.Url.StartsWith("http://")) { icon = "warning"; tip = "Not secure — this page doesn't use HTTPS"; }
        else { icon = "globe"; tip = "Site information"; }
        var site = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Icons.Create(icon, 16) } };
        if (t.Blocked > 0)
        {
            site.Children.Add(Icons.Create("shield", 14));
            site.Children.Add(new TextBlock { Text = t.Blocked.ToString(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            tip += $" · {t.Blocked} tracker(s) blocked";
        }
        SiteBtn.Content = site;
        ToolTip.SetTip(SiteBtn, tip);

        bool err = t.ErrorText != null && t.Internal.Length == 0;
        ErrorPanel.IsVisible = err;
        if (err)
        {
            bool cert = IsCertError(t.ErrorCode), blocked = t.ErrorCode == -9999;
            ErrorTitle.Text = cert ? "Your connection isn't private" : blocked ? "This site is blocked" : t.Alive ? "This page can't be reached" : "The page stopped working";
            ErrorDetail.Text = (cert ? "The site's security certificate couldn't be verified, so someone could be intercepting your connection. Don't enter passwords or personal details on this page.\n\n" : "")
                               + t.ErrorText + (string.IsNullOrEmpty(t.ErrorUrl) ? "" : "\n" + t.ErrorUrl);
            ProceedBtn.IsVisible = cert;
            TryBtn.IsVisible = !blocked;
        }
    }

    private void OnSiteInfo(object? sender, RoutedEventArgs e)
    {
        var t = _cur;
        if (t == null) return;
        var panel = new StackPanel { Width = 320, Spacing = 8 };
        var host = BrowserData.Host(t.Url);
        if (t.Internal.Length > 0 || host.Length == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Red Star Online page", FontWeight = FontWeight.SemiBold });
            panel.Children.Add(new TextBlock { Text = "This page is built into the browser and never leaves your computer.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        }
        else
        {
            bool https = t.Url.StartsWith("https://");
            panel.Children.Add(new TextBlock { Text = host, FontWeight = FontWeight.SemiBold, FontSize = 16 });
            panel.Children.Add(new TextBlock
            {
                Text = https ? "🔒 Connection is secure (HTTPS)" : "⚠ Connection is not secure — others on the network could read or change what you send here.",
                TextWrapping = TextWrapping.Wrap
            });
            var s = BrowserData.Settings;
            panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)) });
            panel.Children.Add(new TextBlock { Text = $"🛡  {t.Blocked} ad/tracker request(s) blocked on this page", TextWrapping = TextWrapping.Wrap });
            var chk = new CheckBox { Content = "Block ads and trackers on this site", IsChecked = s.BlockTrackers && !s.TrackerAllowSites.Contains(host) };
            chk.IsEnabled = s.BlockTrackers;
            chk.IsCheckedChanged += (_, _) =>
            {
                if (chk.IsChecked == true) s.TrackerAllowSites.Remove(host);
                else if (!s.TrackerAllowSites.Contains(host)) s.TrackerAllowSites.Add(host);
                BrowserData.Save();
                t.Reload();
            };
            panel.Children.Add(chk);
            if (!s.BlockTrackers) panel.Children.Add(new TextBlock { Text = "Blocking is off for all sites (Settings).", FontSize = 12, Opacity = 0.65 });

            var saved = s.SitePermissions.Where(k => k.Key.StartsWith(host + "|")).ToList();
            if (saved.Count > 0)
            {
                panel.Children.Add(new TextBlock { Text = "Permissions", FontWeight = FontWeight.SemiBold });
                foreach (var kv in saved)
                {
                    var key = kv.Key;
                    var row = new DockPanel();
                    var forget = new Button { Content = "Reset" }; forget.Classes.Add("flat");
                    DockPanel.SetDock(forget, Dock.Right);
                    forget.Click += (_, _) => { s.SitePermissions.Remove(key); BrowserData.Save(); (forget.Parent as Control)?.SetValue(IsVisibleProperty, false); };
                    row.Children.Add(forget);
                    row.Children.Add(new TextBlock { Text = (kv.Value ? "✔ " : "✖ ") + NiceName(key.Split('|')[1]), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                    panel.Children.Add(row);
                }
            }
            if (Math.Abs(t.Zoom - 1) > 0.01)
            {
                var reset = new Button { Content = $"Reset zoom ({(int)Math.Round(t.Zoom * 100)}%)" }; reset.Classes.Add("flat");
                reset.Click += (_, _) => t.SetZoom(1);
                panel.Children.Add(reset);
            }
        }
        new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft }.ShowAt(SiteBtn);
    }

    // ───────────────────────── address bar suggestions ─────────────────────────
    private readonly Dictionary<string, string> _sugUrls = new();

    private void ShowSuggestions()
    {
        var text = UrlBox.Text ?? "";
        _sugUrls.Clear();
        var items = new List<string>();
        foreach (var s in BrowserData.Suggest(text, 7))
        {
            var host = BrowserData.Host(s.Url);
            var line = s.Text + "   ·   " + host;
            if (_sugUrls.TryAdd(line, s.Url)) items.Add(line);
        }
        if (text.Trim().Length > 0 && !text.StartsWith("redstar://"))
        {
            var line = "🔍 " + text.Trim() + "   ·   search with " + BrowserData.Settings.SearchEngine;
            if (_sugUrls.TryAdd(line, text.Trim())) items.Add(line);
        }
        if (items.Count == 0) { SuggestPopup.IsOpen = false; return; }
        SuggestList.ItemsSource = items;
        SuggestList.SelectedIndex = -1;
        SuggestPopup.Width = Math.Max(300, UrlBox.Bounds.Width);
        SuggestPopup.IsOpen = true;
    }

    private void OnSuggestPressed(object? sender, PointerPressedEventArgs e)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true);
        if (item?.DataContext is string line && _sugUrls.TryGetValue(line, out var url))
        {
            e.Handled = true;
            SuggestPopup.IsOpen = false;
            Go(url);
        }
    }

    private void OnUrlKey(object? sender, KeyEventArgs e)
    {
        bool open = SuggestPopup.IsOpen;
        int n = (SuggestList.ItemsSource as List<string>)?.Count ?? 0;
        if (e.Key == Key.Down && n > 0)
        {
            if (!open) ShowSuggestions();
            SuggestList.SelectedIndex = Math.Min(n - 1, SuggestList.SelectedIndex + 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && open && n > 0)
        {
            SuggestList.SelectedIndex = Math.Max(-1, SuggestList.SelectedIndex - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            string target = UrlBox.Text ?? "";
            if (open && SuggestList.SelectedItem is string line && _sugUrls.TryGetValue(line, out var u)) target = u;
            SuggestPopup.IsOpen = false;
            Go(target);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (open) SuggestPopup.IsOpen = false; else { UpdateChrome(); Surface.Focus(); }
            e.Handled = true;
        }
    }

    // ───────────────────────── bookmarks bar ─────────────────────────
    private void RebuildBar()
    {
        BarPanel.Children.Clear();
        var list = BrowserData.Store.Bookmarks;
        BookmarksBar.IsVisible = BrowserData.Settings.ShowBookmarksBar && list.Count > 0 && !_fullscreen;
        foreach (var b in list.ToList())
        {
            var bm = b;
            var title = string.IsNullOrWhiteSpace(bm.Title) ? bm.Url : bm.Title;
            var btn = new Button { Content = "★ " + (title.Length > 24 ? title[..23] + "…" : title), Padding = new Thickness(8, 2) };
            btn.Classes.Add("flat");
            ToolTip.SetTip(btn, bm.Url);
            btn.Click += (_, _) => Go(bm.Url);
            btn.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
            {
                if (e.GetCurrentPoint(btn).Properties.IsMiddleButtonPressed) { e.Handled = true; NewTab(bm.Url, false); }
            }, RoutingStrategies.Tunnel);
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "Open in new tab" }; open.Click += (_, _) => NewTab(bm.Url);
            var rem = new MenuItem { Header = "Remove bookmark" }; rem.Click += (_, _) => BrowserData.RemoveBookmark(bm);
            menu.Items.Add(open); menu.Items.Add(rem);
            btn.ContextMenu = menu;
            BarPanel.Children.Add(btn);
        }
    }

    // ───────────────────────── commands ─────────────────────────
    private void OnCmd(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string tag) Exec(tag);
    }

    private void Exec(string tag)
    {
        switch (tag)
        {
            case "newtab": NewTab(HomeUrl); break;
            case "private": BrowserLauncher.LaunchPrivate(); break;
            case "reopen": if (_closed.Count > 0) NewTab(_closed.Pop()); break;
            case "back":
                // leaving an internal page opened over a web page just shows that page again
                if (_cur != null && _cur.Internal.Length > 0 && _cur.Url.Length > 0 && _cur.Url != "about:blank") { _cur.Internal = ""; UpdateChrome(); RebuildTabs(); }
                else _cur?.Back();
                break;
            case "forward": _cur?.Forward(); break;
            case "reload": if (_cur != null && _cur.Internal.Length == 0) _cur.Reload(); break;
            case "stop": _cur?.Stop(); break;
            case "home": Go(HomeUrl); break;
            case "bookmark":
                if (_cur != null && _cur.Internal.Length == 0 && IsHttp(_cur.Url)) BrowserData.ToggleBookmark(_cur.Url, _cur.Title);
                break;
            case "bookmarks": ShowInternal("bookmarks"); break;
            case "history": ShowInternal("history"); break;
            case "settings": ShowInternal("settings"); break;
            case "shortcuts": ShowInternal("shortcuts"); break;
            case "togglebar": BrowserData.Settings.ShowBookmarksBar = !BrowserData.Settings.ShowBookmarksBar; BrowserData.Save(); break;
            case "cleardata": _ = BrowserPages.ShowClearDialog(this); break;
            case "find": ShowFind(); break;
            case "findnext": _cur?.Find(FindBox.Text ?? "", true, true, CaseChk.IsChecked == true); break;
            case "findprev": _cur?.Find(FindBox.Text ?? "", false, true, CaseChk.IsChecked == true); break;
            case "findclose": CloseFind(); break;
            case "zoomin": StepZoom(1); break;
            case "zoomout": StepZoom(-1); break;
            case "zoomreset": _cur?.SetZoom(1); break;
            case "fullscreen": ToggleFullscreen(); break;
            case "downloads": WindowManager.Open(new FileManagerWindow(DownloadsDir())); DlFlyout.Hide(); break;
            case "downloadspanel": DlFlyout.ShowAt(DownloadsBtn); break;
            case "external": if (!string.IsNullOrEmpty(_cur?.Url) && IsHttp(_cur!.Url)) OpenPath(_cur.Url); break;
            case "mute": if (_cur != null) _cur.SetMuted(!_cur.Muted); break;
            case "pip":
                if (_cur == null || _cur.Internal.Length > 0) break;
                if (_cur.InPip) { WebPipWindow.EndFor(_cur); _cur.PipExit(); } else _cur.PictureInPicture();
                break;
            case "devtools": _cur?.DevTools(); break;
            case "reader":
                if (!WebPageOpen(out var rt, "Reader mode")) break;
                rt.Reader(ThemeService.IsDark);       // toggles: the engine goes back when already in reader view
                break;
            case "writer":
                if (!WebPageOpen(out var wt, "Open in Writer")) break;
                _writerWait = wt;
                Toast("Preparing “" + ShortTitle(wt) + "” for Writer…");
                BrowserLog.Write("writer: extract " + wt.Url);
                wt.ExtractPage("writer");
                DispatcherTimer.RunOnce(() =>
                {
                    if (ReferenceEquals(_writerWait, wt))
                    {
                        _writerWait = null;
                        BrowserLog.Write("writer: no reply for " + wt.Url);
                        _ = Dialogs.ShowMessage(this, "Open in Writer", "The page didn't answer in time. Wait until it has finished loading and try again.");
                    }
                }, TimeSpan.FromSeconds(20));
                break;
            case "messenger":
                if (!WebPageOpen(out var mt, "Send to Messenger")) break;
                try
                {
                    MessengerWindow.Share(ShortTitle(mt), mt.Url);
                    _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(mt.Url);
                    Notifier.Show("Sent to Messenger", ShortTitle(mt), "messenger");
                }
                catch (Exception ex)
                {
                    BrowserLog.Error("messenger", ex);
                    _ = Dialogs.ShowMessage(this, "Send to Messenger", "Couldn't open Messenger: " + ex.Message);
                }
                break;
            case "share":
                if (WebPageOpen(out var st, "Share")) ShareService.ShowMenu(StarBtn, ShortTitle(st), url: st.Url);
                break;
            case "savepdf": _ = SavePdfAsync(false); break;
            case "print": _ = SavePdfAsync(true); break;
            case "proceed":
                if (_cur != null && !string.IsNullOrEmpty(_cur.ErrorUrl))
                {
                    var url = _cur.ErrorUrl!;
                    _cur.AllowCert(BrowserData.Host(url));
                    _cur.Navigate(url);
                }
                break;
        }
        if (tag is not ("find" or "findnext" or "findprev" or "bookmarks" or "history" or "settings" or "shortcuts" or "newtab" or "home" or "writer" or "messenger" or "share" or "private" or "cleardata" or "savepdf") && _cur?.Internal.Length == 0)
            Surface.Focus();
    }

    private async Task SavePdfAsync(bool print)
    {
        if (!WebPageOpen(out _, print ? "Printing" : "Save as PDF")) return;
        var name = new string((string.IsNullOrWhiteSpace(_cur.Title) ? "page" : _cur.Title).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (name.Length == 0) name = "page";
        if (print)
        {
            _printPath = Path.Combine(Path.GetTempPath(), "redstar-print-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
            _cur.SavePdf(_printPath);
            return;
        }
        var f = await FileDialogs.Save(this, new FilePickerSaveOptions
        {
            Title = "Save page as PDF", SuggestedFileName = name + ".pdf", DefaultExtension = "pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF document") { Patterns = new[] { "*.pdf" } } }
        });
        var path = f?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) _cur.SavePdf(path);
    }

    private void OnPdfDone(string path, bool ok)
    {
        if (!ok) { Notifier.Show("Red Star Online", "Couldn't create the PDF for this page.", "online"); return; }
        if (path == _printPath)
        {
            _printPath = null;
            if (HasDefaultPrinter())
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo("lp", new[] { path }) { UseShellExecute = false, RedirectStandardError = true });
                    p?.WaitForExit(5000);
                    if (p is { HasExited: true, ExitCode: 0 }) { Toast("Sent to the default printer"); return; }
                }
                catch (Exception ex) { BrowserLog.Error("print", ex); }
            }
            // no printer configured (or lp failed): open the PDF so it can be printed from the viewer
            Toast("No default printer — opening the PDF so you can print it from there");
            OpenPath(path);
            return;
        }
        Notifier.Show("PDF saved", System.IO.Path.GetFileName(path), "online",
            new NotificationAction("Open", () => FileOpener.Open(path)),
            new NotificationAction("Show in folder", () => WindowManager.Open(new FileManagerWindow(System.IO.Path.GetDirectoryName(path)))));
    }

    private void OnPageData(WebTab t, string kind, string title, string html, string pageUrl, string error)
    {
        if (kind != "writer") return;
        if (ReferenceEquals(_writerWait, t)) _writerWait = null;
        BrowserLog.Write($"writer: pagedata {html.Length} chars, error='{error}' for {pageUrl}");
        if (error.Length > 0) { _ = Dialogs.ShowMessage(this, "Open in Writer", "Couldn't read this page: " + error); return; }
        if (string.IsNullOrWhiteSpace(html)) { _ = Dialogs.ShowMessage(this, "Open in Writer", "There is no readable text on this page."); return; }
        try
        {
            var src = string.IsNullOrEmpty(pageUrl) ? t.Url : pageUrl;
            var body = html + "<p><i>Source: <a href=\"" + System.Net.WebUtility.HtmlEncode(src) + "\">" + System.Net.WebUtility.HtmlEncode(src) + "</a></i></p>";
            WriterLauncher.OpenHtml(string.IsNullOrWhiteSpace(title) ? ShortTitle(t) : title, body);
            Toast("Opened in Writer");
        }
        catch (Exception ex)
        {
            BrowserLog.Error("writer", ex);
            _ = Dialogs.ShowMessage(this, "Open in Writer", "Writer couldn't open the page: " + ex.Message);
        }
    }

    /// <summary>The current tab shows a web page (not an internal page or an error); tells the user otherwise.</summary>
    private bool WebPageOpen(out WebTab tab, string what)
    {
        tab = _cur;
        if (_cur != null && _cur.Internal.Length == 0 && IsHttp(_cur.Url) && _cur.ErrorText == null) return true;
        Toast(what + " works on web pages — open a site first.");
        return false;
    }

    private static string ShortTitle(WebTab t) =>
        !string.IsNullOrWhiteSpace(t.Title) && t.Title != "New tab" ? t.Title.Trim() : BrowserData.Host(t.Url);

    private DispatcherTimer? _toastTimer;
    private void Toast(string text)
    {
        ToastText.Text = text;
        ToastPill.IsVisible = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) => { _toastTimer?.Stop(); ToastPill.IsVisible = false; };
        _toastTimer.Start();
    }

    private void OnPip(WebTab t, bool on, int w, int h, bool paused)
    {
        if (on) WebPipWindow.Open(t, w, h, paused, () => { Raise(); if (_tabs.Contains(t)) SwitchTo(t); t.PipExit(); });
        else WebPipWindow.EndFor(t);
        UpdateChrome();
    }

    private void OnPageFullscreen(WebTab t, bool on)
    {
        if (!ReferenceEquals(t, _cur)) return;
        if (on && !_fullscreen) { _pageFullscreen = true; ToggleFullscreen(); }
        else if (!on && _fullscreen && _pageFullscreen) { _pageFullscreen = false; ToggleFullscreen(); }
    }

    private async Task AskLogin(WebTab t, int req, string host, string realm, bool proxy)
    {
        var user = new TextBox { PlaceholderText = "Username", MinWidth = 280 };
        var pass = new TextBox { PlaceholderText = "Password", PasswordChar = '•', MinWidth = 280 };
        bool ok = false;
        var dlg = new Window { Title = "Sign in", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var signIn = new Button { Content = "Sign in", IsDefault = true }; signIn.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        signIn.Click += (_, _) => { ok = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = (proxy ? "The proxy " : "") + host + " is asking you to sign in", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                new TextBlock { Text = string.IsNullOrEmpty(realm) ? "" : "“" + realm + "”", Opacity = 0.7, IsVisible = realm.Length > 0 },
                new TextBlock { Text = "Your connection to this site is not private.", Opacity = 0.7, IsVisible = !proxy && !t.Url.StartsWith("https://") },
                user, pass,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, signIn } }
            }
        };
        dlg.Opened += (_, _) => user.Focus();
        await dlg.ShowDialog(this);
        t.AnswerAuth(req, ok ? user.Text ?? "" : null, ok ? pass.Text ?? "" : null);
    }

    private static bool HasDefaultPrinter()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("lpstat", "-d") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return false;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return o.Contains("destination:") && !o.Contains("no system default");
        }
        catch { return false; }
    }

    private static string DownloadsDir()
    {
        var d = Path.Combine(AppPaths.Home, "Downloads");
        return Directory.Exists(d) ? d : AppPaths.Home;
    }

    private static void OpenPath(string p)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", "\"" + p + "\"") { UseShellExecute = false }); } catch { }
    }

    private void StepZoom(int d)
    {
        if (_cur == null) return;
        int i = Array.FindIndex(ZoomSteps, z => z >= _cur.Zoom - 0.001);
        if (i < 0) i = ZoomSteps.Length - 1;
        if (d < 0 && Math.Abs(ZoomSteps[i] - _cur.Zoom) > 0.001) i++;   // between steps: go to the next lower one
        _cur.SetZoom(ZoomSteps[Math.Clamp(i + d, 0, ZoomSteps.Length - 1)]);
    }

    private void ShowFind()
    {
        FindBar.IsVisible = true;
        FindBox.Focus(); FindBox.SelectAll();
        if (!string.IsNullOrEmpty(FindBox.Text)) _cur?.Find(FindBox.Text!, true, false, CaseChk.IsChecked == true);
    }

    private void CloseFind()
    {
        FindBar.IsVisible = false;
        _cur?.StopFind();
        Surface.Focus();
    }

    private void UpdateFindInfo()
    {
        if (_cur == null) return;
        FindInfo.Text = string.IsNullOrEmpty(FindBox.Text) ? "" : _cur.FindCount == 0 ? "No matches" : $"{_cur.FindActive} of {_cur.FindCount}";
    }

    private void ToggleFullscreen()
    {
        if (_fullscreen && _pageFullscreen) { _pageFullscreen = false; _cur?.ExitFullscreen(); }
        if (!_fullscreen) { _prevState = WindowState; _fullscreen = true; WindowState = WindowState.FullScreen; TabBar.IsVisible = false; ToolBar.IsVisible = false; BookmarksBar.IsVisible = false; }
        else { _fullscreen = false; WindowState = _prevState == WindowState.Minimized ? WindowState.Normal : _prevState; TabBar.IsVisible = true; ToolBar.IsVisible = true; RebuildBar(); }
    }

    private void OnWindowKey(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool handled = true;
        if (e.Key == Key.F5) { if (_cur?.Internal.Length == 0) _cur.Reload(ctrl); }
        else if (e.Key == Key.F11) ToggleFullscreen();
        else if (e.Key == Key.F12) Exec("devtools");
        else if (e.Key == Key.F1) Exec("shortcuts");
        else if (ctrl && !shift && !alt && e.Key == Key.OemComma) Exec("settings");
        else if (e.Key == Key.F6) UrlBox.Focus();
        else if (e.Key == Key.Escape && _fullscreen && !UrlBox.IsFocused) ToggleFullscreen();
        else if (alt && e.Key == Key.Left) Exec("back");
        else if (alt && e.Key == Key.Right) _cur?.Forward();
        else if (alt && e.Key == Key.Home) Go(HomeUrl);
        else if (ctrl)
        {
            switch (e.Key)
            {
                case Key.T: if (shift) Exec("reopen"); else NewTab(HomeUrl); break;
                case Key.W: if (_cur != null) CloseTab(_cur); break;
                case Key.L: UrlBox.Focus(); break;
                case Key.R: if (_cur?.Internal.Length == 0) _cur.Reload(shift); break;
                case Key.F: if (UrlBox.IsFocused || FindBox.IsFocused || _cur?.Internal.Length == 0) ShowFind(); else handled = false; break;
                case Key.D: Exec("bookmark"); break;
                case Key.H: Exec("history"); break;
                case Key.J: Exec("downloadspanel"); break;
                case Key.N: if (shift) Exec("private"); else NewTab(HomeUrl); break;
                case Key.O: if (shift) Exec("bookmarks"); else handled = false; break;
                case Key.B: if (shift) Exec("togglebar"); else handled = false; break;
                case Key.Delete: if (shift) Exec("cleardata"); else handled = false; break;
                case Key.P: Exec("print"); break;
                case Key.S: if (_cur?.Internal.Length == 0) Exec("savepdf"); else handled = false; break;
                case Key.M: Exec("mute"); break;
                case Key.OemPlus: case Key.Add: StepZoom(1); break;
                case Key.OemMinus: case Key.Subtract: StepZoom(-1); break;
                case Key.D0: case Key.NumPad0: _cur?.SetZoom(1); break;
                case Key.Tab: CycleTab(shift ? -1 : 1); break;
                case Key.PageDown: CycleTab(1); break;
                case Key.PageUp: CycleTab(-1); break;
                case Key.D1: case Key.D2: case Key.D3: case Key.D4: case Key.D5: case Key.D6: case Key.D7: case Key.D8:
                {
                    int n = e.Key - Key.D1;
                    if (n < _tabs.Count) SwitchTo(_tabs[n]);
                    break;
                }
                case Key.D9: SwitchTo(_tabs[^1]); break;
                default: handled = false; break;
            }
        }
        else handled = false;
        if (handled) e.Handled = true;
    }

    private void CycleTab(int d)
    {
        if (_tabs.Count < 2) return;
        int i = _tabs.IndexOf(_cur);
        SwitchTo(_tabs[(i + d + _tabs.Count) % _tabs.Count]);
    }

    // ───────────────────────── context menu ─────────────────────────
    private void ShowContext(WebTab t, ContextInfo c)
    {
        if (!ReferenceEquals(t, _cur) || t.Internal.Length > 0) return;
        var menu = new ContextMenu();
        MenuItem Item(string header, Action act, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => act();
            return mi;
        }
        void Copy(string text) => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);

        if (c.Link.Length > 0)
        {
            menu.Items.Add(Item("Open link in new tab", () => NewTab(c.Link, false)));
            menu.Items.Add(Item("Open link in this tab", () => t.Navigate(c.Link)));
            menu.Items.Add(Item("Copy link address", () => Copy(c.Link)));
            if (c.Link.StartsWith("http")) menu.Items.Add(Item("Send link to Messenger", () => { MessengerWindow.Share(c.Sel.Length > 0 ? c.Sel : BrowserData.Host(c.Link), c.Link); Notifier.Show("Sent to Messenger", c.Link, "messenger"); }));
            menu.Items.Add(new Separator());
        }
        if (c.Media == "image" && c.Src.Length > 0)
        {
            menu.Items.Add(Item("Open image in new tab", () => NewTab(c.Src, false)));
            menu.Items.Add(Item("Copy image address", () => Copy(c.Src)));
            menu.Items.Add(new Separator());
        }
        if (c.Editable)
        {
            menu.Items.Add(Item("Cut", () => t.Edit("cut"), c.Sel.Length > 0));
            menu.Items.Add(Item("Copy", () => t.Edit("copy"), c.Sel.Length > 0));
            menu.Items.Add(Item("Paste", () => _ = Surface.PasteAsync()));
            menu.Items.Add(Item("Select all", () => t.Edit("selectAll")));
        }
        else
        {
            if (c.Sel.Length > 0)
            {
                menu.Items.Add(Item("Copy", () => t.Edit("copy")));
                var q = c.Sel.Trim(); var shortQ = q.Length > 28 ? q[..27] + "…" : q;
                menu.Items.Add(Item($"Search {BrowserData.Settings.SearchEngine} for “{shortQ}”", () => NewTab(string.Format(BrowserData.SearchTemplate, Uri.EscapeDataString(q)), false)));
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(Item("Back", () => t.Back(), t.CanBack));
            menu.Items.Add(Item("Forward", () => t.Forward(), t.CanForward));
            menu.Items.Add(Item("Reload", () => t.Reload()));
            menu.Items.Add(Item("Open in a separate window (for verification checks)", () => t.PopOut(), t.Url.StartsWith("http")));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Select all", () => t.Edit("selectAll")));
            menu.Items.Add(Item(t.InReader ? "Leave reader mode" : "Reader mode", () => Exec("reader")));
            menu.Items.Add(Item("Open page in Writer", () => Exec("writer")));
            menu.Items.Add(Item("Send page to Messenger", () => Exec("messenger")));
            menu.Items.Add(Item("Save as PDF…", () => Exec("savepdf")));
            menu.Items.Add(Item("View page source", () => NewTab("view-source:" + t.Url, false)));
            menu.Items.Add(Item("Inspect", () => t.DevTools()));
        }
        menu.Open(Surface);
    }

    // ───────────────────────── permissions / downloads / engine ─────────────────────────
    private static readonly string[] AutoAllow = { "clipboard-sanitized-write", "fullscreen", "pointerLock", "window-management" };
    private static readonly string[] AutoDeny = { "openExternal", "idle-detection", "unknown" };

    private static string NiceName(string perm) => perm switch
    {
        "media" => "use your camera or microphone",
        "geolocation" => "know your location",
        "notifications" => "show notifications",
        "clipboard-read" => "read your clipboard",
        "midi" or "midiSysex" => "use MIDI devices",
        "display-capture" => "capture your screen",
        "mediaKeySystem" => "play protected content",
        _ => "use “" + perm + "”"
    };

    private async Task AskPermission(WebTab t, int req, string perm, string url)
    {
        if (AutoAllow.Contains(perm)) { t.AnswerPermission(req, true); return; }
        if (AutoDeny.Contains(perm)) { t.AnswerPermission(req, false); return; }
        string host = BrowserData.Host(url);
        if (host.Length == 0) host = url;
        var key = host + "|" + perm;
        if (!_priv && BrowserData.Settings.SitePermissions.TryGetValue(key, out var remembered)) { t.AnswerPermission(req, remembered); return; }
        var a = await Dialogs.Ask(this, "Permission request", $"{host} wants to {NiceName(perm)}.", "Allow", "Block");
        bool allow = a == 0;
        t.AnswerPermission(req, allow);
        if (!_priv && (a == 0 || a == 1)) { BrowserData.Settings.SitePermissions[key] = allow; BrowserData.Save(); }
    }

    private void OnGlobal(JsonElement m)
    {
        var type = m.TryGetProperty("t", out var tp) ? tp.GetString() : null;
        if (type == "download") OnDownload(m);
        else if (type == "filters") AdblockStatus.Update(m);
        else if (type == "hello") PushConfig();
    }

    private sealed class Dl { public int Id; public string Name = "", Path = ""; public long Received, Total; public string State = "progress"; }
    private readonly List<Dl> _dls = new();

    private void OnDownload(JsonElement m)
    {
        int id = m.TryGetProperty("id", out var ip) && ip.TryGetInt32(out var i) ? i : 0;
        var state = m.TryGetProperty("state", out var sp) ? sp.GetString() ?? "" : "";
        var d = _dls.FirstOrDefault(x => x.Id == id);
        if (state == "start")
        {
            d = new Dl
            {
                Id = id,
                Name = m.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "",
                Path = m.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "",
                Total = m.TryGetProperty("total", out var tp) && tp.TryGetInt64(out var tt) ? tt : 0
            };
            _dls.Insert(0, d);
            RefreshDownloads(true);
            return;
        }
        if (d == null) return;
        if (m.TryGetProperty("received", out var rp) && rp.TryGetInt64(out var rr)) d.Received = rr;
        if (m.TryGetProperty("total", out var t2) && t2.TryGetInt64(out var t3) && t3 > 0) d.Total = t3;
        if (state is "done" or "cancelled" or "interrupted")
        {
            d.State = state;
            if (state == "done")
            {
                var file = d.Path;
                Notifier.Show("Download finished", d.Name + " saved to Downloads", "online",
                    new NotificationAction("Open", () => { if (!string.IsNullOrEmpty(file)) FileOpener.Open(file); }),
                    new NotificationAction("Show in folder", () => WindowManager.Open(new FileManagerWindow(string.IsNullOrEmpty(file) ? DownloadsDir() : System.IO.Path.GetDirectoryName(file)))));
            }
            RefreshDownloads(true);
        }
        else RefreshDownloads(false);
    }

    private void RefreshDownloads(bool show)
    {
        DlList.Children.Clear();
        foreach (var d in _dls.Take(8))
        {
            var dl = d;
            string info = dl.State switch
            {
                "done" => "Finished",
                "cancelled" => "Cancelled",
                "interrupted" => "Failed",
                _ => dl.Total > 0 ? $"{FileOps.FormatSize(dl.Received)} of {FileOps.FormatSize(dl.Total)}" : FileOps.FormatSize(dl.Received)
            };
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = dl.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold });
            if (dl.State == "progress")
                row.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = dl.Total > 0 ? dl.Received * 100.0 / dl.Total : 0, IsIndeterminate = dl.Total <= 0, Height = 4 });
            row.Children.Add(new TextBlock { Text = info, Classes = { "caption" } });
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (dl.State == "done")
            {
                var open = new Button { Content = "Open", Classes = { "flat" } };
                open.Click += (_, _) => FileOpener.Open(dl.Path);
                var show2 = new Button { Content = "Show in folder", Classes = { "flat" } };
                show2.Click += (_, _) =>
                {
                    WindowManager.Open(new FileManagerWindow(System.IO.Path.GetDirectoryName(dl.Path) ?? DownloadsDir()));
                    DlFlyout.Hide();
                };
                btns.Children.Add(open); btns.Children.Add(show2);
            }
            else if (dl.State == "progress")
            {
                var cancel = new Button { Content = "Cancel", Classes = { "flat" } };
                cancel.Click += (_, _) => WebEngine.Send(new { c = "dlcancel", id = dl.Id });
                btns.Children.Add(cancel);
            }
            row.Children.Add(btns);
            DlList.Children.Add(new Border { Child = row, Padding = new Thickness(0, 2, 0, 6), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) });
        }
        int active = _dls.Count(x => x.State == "progress");
        if (TrayService.Find("online") is { } tray) { tray.Badge = active; TrayService.Upsert(tray); }
        if (show && !DlFlyout.IsOpen) DlFlyout.ShowAt(DownloadsBtn);
    }

    private void OnEngineLost()
    {
        foreach (var t in _tabs) t.MarkDead("The web engine stopped. Press “Try again” to restart it.");
    }
}
