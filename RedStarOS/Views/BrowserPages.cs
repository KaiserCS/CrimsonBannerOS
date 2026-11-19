using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Native internal pages of Red Star Online (redstar://newtab, history, bookmarks, settings, shortcuts).</summary>
public static class BrowserPages
{
    public static string TitleOf(string page) => page switch
    {
        "newtab" => "New tab", "history" => "History", "bookmarks" => "Bookmarks",
        "settings" => "Settings", "shortcuts" => "Keyboard shortcuts", _ => "Red Star Online"
    };

    // ───────────────────────── helpers ─────────────────────────
    private static TextBlock T(string text, double size = 14, FontWeight? w = null, double opacity = 1, bool wrap = false) =>
        new() { Text = text, FontSize = size, FontWeight = w ?? FontWeight.Normal, Opacity = opacity, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };

    private static Button B(string text, Action act, bool primary = false)
    {
        var b = new Button { Content = text };
        b.Classes.Add(primary ? "primary" : "flat");
        b.Click += (_, _) => act();
        return b;
    }

    private static Control Page(string title, Control body, double width = 760)
    {
        var sp = new StackPanel { Spacing = 14, Width = width, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 32, 16, 48) };
        sp.Children.Add(T(title, 26, FontWeight.SemiBold));
        sp.Children.Add(body);
        return new ScrollViewer { Content = sp, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    public static Control Build(string page, Action<string> go, Action clearData, bool priv) => page switch
    {
        "history" => History(go, clearData),
        "bookmarks" => Bookmarks(go),
        "settings" => Settings(clearData),
        "shortcuts" => Shortcuts(),
        _ => NewTab(go, priv)
    };

    private static string Host(string url) { var h = BrowserData.Host(url); return h.StartsWith("www.") ? h[4..] : h; }

    // ───────────────────────── new tab ─────────────────────────
    private static Control NewTab(Action<string> go, bool priv)
    {
        var root = new StackPanel { Spacing = 22, Width = 680, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 90, 16, 40) };
        root.Children.Add(new TextBlock { Text = priv ? "🕶  Private window" : "★  Red Star Online", FontSize = 34, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        var box = new TextBox { PlaceholderText = "Search with " + BrowserData.Settings.SearchEngine + " or type an address", FontSize = 16, Padding = new Thickness(14, 10) };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(box.Text)) { go(box.Text!); e.Handled = true; } };
        root.Children.Add(box);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => box.Focus());

        if (priv)
        {
            root.Children.Add(T("Pages you view in this window leave no history, cookies or site data on this computer once the last private tab is closed. " +
                                "Downloads and bookmarks you make are kept. Sites, your network and your school or workplace can still see your activity.", 13, null, 0.75, true));
            return new ScrollViewer { Content = root };
        }

        var tiles = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var items = new List<(string title, string url)>();
        foreach (var b in BrowserData.Store.Bookmarks.Take(8)) items.Add((b.Title, b.Url));
        foreach (var h in BrowserData.MostVisited(12))
            if (items.Count < 12 && items.All(i => Host(i.url) != Host(h.Url))) items.Add((string.IsNullOrEmpty(h.Title) ? Host(h.Url) : h.Title, h.Url));
        foreach (var (title, url) in items)
        {
            var u = url;
            var letter = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(22), HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb((byte)(80 + Math.Abs(Host(u).GetHashCode() % 120)), (byte)(60 + Math.Abs(Host(u).GetHashCode() / 7 % 120)), (byte)(90 + Math.Abs(Host(u).GetHashCode() / 13 % 120)))),
                Child = new TextBlock { Text = (Host(u).Length > 0 ? Host(u)[..1] : "?").ToUpperInvariant(), Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var col = new StackPanel { Spacing = 6, Width = 104 };
            col.Children.Add(letter);
            col.Children.Add(new TextBlock { Text = title, FontSize = 12, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap });
            var tile = new Border { Child = col, Padding = new Thickness(6, 10), Margin = new Thickness(2), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(tile, u);
            tile.PointerEntered += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(34, 128, 128, 128));
            tile.PointerExited += (_, _) => tile.Background = Brushes.Transparent;
            tile.PointerPressed += (_, _) => go(u);
            tiles.Children.Add(tile);
        }
        if (items.Count > 0) root.Children.Add(tiles);
        else root.Children.Add(T("Sites you visit and bookmark will show up here.", 13, null, 0.6));
        return new ScrollViewer { Content = root };
    }

    // ───────────────────────── history ─────────────────────────
    private static Control History(Action<string> go, Action clearData)
    {
        var body = new StackPanel { Spacing = 10 };
        var bar = new DockPanel();
        var clear = B("Clear browsing data…", clearData);
        DockPanel.SetDock(clear, Dock.Right);
        bar.Children.Add(clear);
        var search = new TextBox { PlaceholderText = "Search history" };
        bar.Children.Add(search);
        body.Children.Add(bar);
        var list = new StackPanel { Spacing = 0 };
        body.Children.Add(list);

        void Fill()
        {
            list.Children.Clear();
            var q = (search.Text ?? "").Trim().ToLowerInvariant();
            var shown = BrowserData.Store.History
                .Where(h => q.Length == 0 || h.Url.ToLowerInvariant().Contains(q) || h.Title.ToLowerInvariant().Contains(q))
                .Take(400).ToList();
            if (shown.Count == 0) { list.Children.Add(T(q.Length > 0 ? "No matching pages." : "Nothing here yet.", 13, null, 0.6)); return; }
            string lastDay = "";
            foreach (var h in shown)
            {
                var when = DateTimeOffset.FromUnixTimeSeconds(h.Time).ToLocalTime();
                var day = when.Date == DateTime.Today ? "Today" : when.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : when.ToString("dddd, d MMMM yyyy");
                if (day != lastDay)
                {
                    list.Children.Add(new TextBlock { Text = day, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
                    lastDay = day;
                }
                var entry = h;
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,Auto"), Margin = new Thickness(0, 2) };
                grid.Children.Add(new TextBlock { Text = when.ToString("HH:mm"), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
                var txt = new StackPanel { Spacing = 0 };
                txt.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(h.Title) ? h.Url : h.Title, TextTrimming = TextTrimming.CharacterEllipsis });
                txt.Children.Add(new TextBlock { Text = Host(h.Url), FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(txt, 1);
                grid.Children.Add(txt);
                var del = B("✕", () => { BrowserData.RemoveHistory(entry); Fill(); });
                Grid.SetColumn(del, 2);
                grid.Children.Add(del);
                var row = new Border { Child = grid, Padding = new Thickness(4, 2), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
                row.PointerEntered += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128));
                row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
                row.PointerPressed += (_, e) => { if (e.Source is not Button && !(e.Source is TextBlock { Text: "✕" })) go(entry.Url); };
                list.Children.Add(row);
            }
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        return Page("History", body);
    }

    // ───────────────────────── bookmarks ─────────────────────────
    private static Control Bookmarks(Action<string> go)
    {
        var body = new StackPanel { Spacing = 10 };
        var list = new StackPanel { Spacing = 2 };
        var note = T("", 12, null, 0.7);
        var export = B("Export as HTML…", () =>
        {
            try
            {
                var dir = Path.Combine(AppPaths.Home, "Downloads"); Directory.CreateDirectory(dir);
                var f = Path.Combine(dir, "redstar-bookmarks.html");
                var sb = new StringBuilder("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n<TITLE>Bookmarks</TITLE>\n<H1>Bookmarks</H1>\n<DL><p>\n");
                foreach (var b in BrowserData.Store.Bookmarks)
                    sb.Append("<DT><A HREF=\"").Append(System.Net.WebUtility.HtmlEncode(b.Url)).Append("\" ADD_DATE=\"").Append(b.Added).Append("\">")
                      .Append(System.Net.WebUtility.HtmlEncode(b.Title)).Append("</A>\n");
                sb.Append("</DL><p>\n");
                File.WriteAllText(f, sb.ToString());
                note.Text = "Saved to " + f;
            }
            catch (Exception ex) { note.Text = "Couldn't export: " + ex.Message; }
        });
        var barChk = new CheckBox { Content = "Show bookmarks bar", IsChecked = BrowserData.Settings.ShowBookmarksBar };
        barChk.IsCheckedChanged += (_, _) => { BrowserData.Settings.ShowBookmarksBar = barChk.IsChecked == true; BrowserData.Save(); };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { barChk, export } };
        body.Children.Add(top);
        body.Children.Add(note);
        body.Children.Add(list);

        void Fill()
        {
            list.Children.Clear();
            if (BrowserData.Store.Bookmarks.Count == 0) { list.Children.Add(T("No bookmarks yet. Press ☆ in the address bar (Ctrl+D) to add one.", 13, null, 0.6)); return; }
            foreach (var b in BrowserData.Store.Bookmarks.ToList())
            {
                var bm = b;
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                var txt = new StackPanel();
                txt.Children.Add(new TextBlock { Text = bm.Title, TextTrimming = TextTrimming.CharacterEllipsis });
                txt.Children.Add(new TextBlock { Text = bm.Url, FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis });
                grid.Children.Add(txt);
                var ren = B("Rename", () =>
                {
                    var t = new TextBox { Text = bm.Title, MinWidth = 260 };
                    txt.Children[0] = t;
                    t.Focus(); t.SelectAll();
                    void Commit() { if (!string.IsNullOrWhiteSpace(t.Text)) bm.Title = t.Text!.Trim(); BrowserData.Save(); Fill(); }
                    t.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); else if (e.Key == Key.Escape) Fill(); };
                    t.LostFocus += (_, _) => Commit();
                });
                Grid.SetColumn(ren, 1); grid.Children.Add(ren);
                var del = B("Delete", () => { BrowserData.RemoveBookmark(bm); Fill(); });
                Grid.SetColumn(del, 2); grid.Children.Add(del);
                var row = new Border { Child = grid, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
                row.PointerEntered += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128));
                row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
                txt.Cursor = new Cursor(StandardCursorType.Hand);
                txt.PointerPressed += (_, _) => go(bm.Url);
                list.Children.Add(row);
            }
        }
        Fill();
        return Page("Bookmarks", body);
    }

    // ───────────────────────── shortcuts ─────────────────────────
    private static Control Shortcuts()
    {
        var rows = new (string, string)[]
        {
            ("Ctrl+T", "New tab"), ("Ctrl+Shift+T", "Reopen closed tab"), ("Ctrl+W", "Close tab"), ("Ctrl+Tab / Ctrl+Shift+Tab", "Next / previous tab"),
            ("Ctrl+1 … 8, Ctrl+9", "Go to tab / last tab"), ("Ctrl+Shift+N", "New private window"),
            ("Ctrl+L / F6", "Focus the address bar"), ("Ctrl+D", "Bookmark this page"), ("Ctrl+Shift+B", "Show / hide the bookmarks bar"),
            ("Ctrl+H", "History"), ("Ctrl+Shift+O", "Bookmarks"), ("Ctrl+J", "Downloads"), ("Ctrl+Shift+Del", "Clear browsing data"),
            ("F5 / Ctrl+R", "Reload"), ("Ctrl+F5 / Ctrl+Shift+R", "Reload ignoring the cache"), ("Alt+← / Alt+→", "Back / forward"), ("Alt+Home", "Home page"),
            ("Ctrl+F", "Find in page"), ("Ctrl++ / Ctrl+- / Ctrl+0", "Zoom in / out / reset"), ("Ctrl+P", "Print"), ("Ctrl+S", "Save page as PDF"),
            ("Ctrl+M", "Mute / unmute tab"), ("F11", "Full screen"), ("F12", "Developer tools"), ("F1", "Keyboard shortcuts"), ("Ctrl+,", "Settings"),
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        int r = 0;
        foreach (var (k, d) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var key = new TextBlock { Text = k, FontFamily = new FontFamily("monospace"), Margin = new Thickness(0, 4) };
            var desc = new TextBlock { Text = d, Margin = new Thickness(0, 4) };
            Grid.SetRow(key, r); Grid.SetRow(desc, r); Grid.SetColumn(desc, 1);
            grid.Children.Add(key); grid.Children.Add(desc);
            r++;
        }
        return Page("Keyboard shortcuts", grid);
    }

    // ───────────────────────── settings ─────────────────────────
    private static Control Settings(Action clearData)
    {
        var s = BrowserData.Settings;
        var body = new StackPanel { Spacing = 18 };

        Control Section(string title, params Control[] kids)
        {
            var sp = new StackPanel { Spacing = 8 };
            sp.Children.Add(T(title, 16, FontWeight.SemiBold));
            foreach (var k in kids) sp.Children.Add(k);
            return new Border { Child = sp, Padding = new Thickness(14), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)) };
        }
        CheckBox Chk(string text, bool val, Action<bool> set)
        {
            var c = new CheckBox { Content = text, IsChecked = val };
            c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); BrowserData.Save(); };
            return c;
        }

        var engines = new ComboBox { ItemsSource = BrowserData.Engines.Select(e => e.Name).ToList(), SelectedItem = s.SearchEngine, MinWidth = 200 };
        engines.SelectionChanged += (_, _) => { if (engines.SelectedItem is string n) { s.SearchEngine = n; BrowserData.Save(); } };
        body.Children.Add(Section("Search", T("Search engine used from the address bar", 12, null, 0.7), engines));

        body.Children.Add(Section("General",
            Chk("Show bookmarks bar", s.ShowBookmarksBar, v => s.ShowBookmarksBar = v),
            Chk("Restore the previous session when Red Star Online starts", s.RestoreSession, v => s.RestoreSession = v),
            Chk("Remember browsing history", s.SaveHistory, v => s.SaveHistory = v)));

        var allowList = new StackPanel { Spacing = 2 };
        void FillAllow()
        {
            allowList.Children.Clear();
            foreach (var h in s.TrackerAllowSites.ToList())
            {
                var host = h;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { T(host, 13), B("Block again", () => { s.TrackerAllowSites.Remove(host); BrowserData.Save(); FillAllow(); }) } };
                allowList.Children.Add(row);
            }
            if (s.TrackerAllowSites.Count > 0) allowList.Children.Insert(0, T("Sites where blocking is turned off:", 12, null, 0.7));
        }
        FillAllow();

        // ── ad blocker status: filter lists, rule counts, update button ──
        var adStatus = new StackPanel { Spacing = 3 };
        void FillAds()
        {
            adStatus.Children.Clear();
            if (AdblockStatus.State.Length == 0) { adStatus.Children.Add(T("Filter lists load when the web engine starts.", 12, null, 0.6)); return; }
            adStatus.Children.Add(T((AdblockStatus.State == "updating" ? "Updating filter lists…  " : "Ready — ")
                + $"{AdblockStatus.Network:N0} blocking rules, {AdblockStatus.Cosmetic:N0} element-hiding rules", 13));
            foreach (var l in AdblockStatus.Lists)
                adStatus.Children.Add(T("   • " + l.Name + " — " + (l.Rules > 0 ? $"{l.Rules:N0} rules, updated {l.Updated:d MMM yyyy HH:mm}" : "not downloaded yet")
                    + (l.Error.Length > 0 ? "  (last update failed: " + l.Error + ")" : ""), 12, null, l.Error.Length > 0 && l.Rules == 0 ? 1 : 0.75, true));
        }
        FillAds();
        void OnAds() => Avalonia.Threading.Dispatcher.UIThread.Post(FillAds);
        AdblockStatus.Changed += OnAds;
        adStatus.DetachedFromVisualTree += (_, _) => AdblockStatus.Changed -= OnAds;
        var update = B("Update filter lists now", () => { if (WebEngine.Connected) WebEngine.Send(new { c = "updatefilters" }); });
        if (WebEngine.Connected) WebEngine.Send(new { c = "filterstatus" });

        var blocked = new TextBox { Text = s.BlockedSites, AcceptsReturn = true, Height = 110, PlaceholderText = "example.com\nanother-site.org", TextWrapping = TextWrapping.NoWrap };
        blocked.LostFocus += (_, _) => { s.BlockedSites = blocked.Text ?? ""; BrowserData.Save(); };
        body.Children.Add(Section("Privacy and safety",
            Chk("Block ads and trackers (EasyList, EasyPrivacy, Peter Lowe's list; YouTube ads removed)", s.BlockTrackers, v => s.BlockTrackers = v),
            adStatus,
            update,
            allowList,
            T("Blocked sites — one address per line. These pages (and their subdomains) will not load.", 12, null, 0.7, true),
            blocked,
            B("Clear browsing data…", clearData)));

        var perms = new StackPanel { Spacing = 2 };
        void FillPerms()
        {
            perms.Children.Clear();
            if (s.SitePermissions.Count == 0) { perms.Children.Add(T("No saved decisions.", 12, null, 0.6)); return; }
            foreach (var kv in s.SitePermissions.OrderBy(k => k.Key).ToList())
            {
                var key = kv.Key; var parts = key.Split('|');
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { T((kv.Value ? "✔ Allowed  " : "✖ Blocked  ") + parts[0] + " — " + (parts.Length > 1 ? parts[1] : ""), 13), B("Forget", () => { s.SitePermissions.Remove(key); BrowserData.Save(); FillPerms(); }) }
                };
                perms.Children.Add(row);
            }
        }
        FillPerms();
        body.Children.Add(Section("Site permissions", T("Camera, microphone, location and notification choices you asked Red Star Online to remember.", 12, null, 0.7, true), perms));

        var zooms = new StackPanel { Spacing = 2 };
        void FillZoom()
        {
            zooms.Children.Clear();
            if (s.SiteZoom.Count == 0) { zooms.Children.Add(T("No sites have a custom zoom.", 12, null, 0.6)); return; }
            foreach (var kv in s.SiteZoom.OrderBy(k => k.Key).ToList())
            {
                var key = kv.Key;
                zooms.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { T(key + " — " + (int)Math.Round(kv.Value * 100) + "%", 13), B("Reset", () => { s.SiteZoom.Remove(key); BrowserData.Save(); FillZoom(); }) } });
            }
        }
        FillZoom();
        body.Children.Add(Section("Page zoom", zooms));

        // bring bookmarks / history from another browser on this computer
        var imports = new StackPanel { Spacing = 6 };
        var importStatus = T("", 12, null, 0.8, true);
        var found = BrowserImport.Detect();
        if (found.Count == 0) imports.Children.Add(T("No other browsers were found on this computer.", 12, null, 0.6));
        foreach (var src in found)
        {
            var sc = src;
            Button? btn = null;
            btn = B("Import", async () =>
            {
                btn!.IsEnabled = false;
                importStatus.Text = "Importing from " + sc.Name + "…";
                var r = await BrowserImport.ImportAsync(sc, true, true);
                btn.IsEnabled = true;
                importStatus.Text = $"{sc.Name}: {r.Bookmarks} new bookmarks, {r.History} history entries." + (r.Note != null ? " " + r.Note : "");
            });
            imports.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { btn, T(sc.Name, 13) } });
        }
        body.Children.Add(Section("Import bookmarks and history", T("Copies from Firefox, Chrome, Chromium, Brave, Edge, Vivaldi or Opera. Nothing is duplicated or removed.", 12, null, 0.7, true), imports, importStatus));
        return Page("Settings", body);
    }

    // ───────────────────────── clear browsing data ─────────────────────────
    public static async Task ShowClearDialog(Window owner)
    {
        var win = new Window { Title = "Clear browsing data", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var range = new ComboBox { ItemsSource = new[] { "Last hour", "Last 24 hours", "Last 7 days", "All time" }, SelectedIndex = 3, MinWidth = 180 };
        var hist = new CheckBox { Content = "Browsing history", IsChecked = true };
        var cook = new CheckBox { Content = "Cookies and other site data (all time)", IsChecked = true };
        var cache = new CheckBox { Content = "Cached images and files (all time)", IsChecked = true };
        var site = new CheckBox { Content = "Site permissions and zoom levels", IsChecked = false };
        bool go = false;
        var ok = new Button { Content = "Clear data", Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) => { go = true; win.Close(); };
        cancel.Click += (_, _) => win.Close();
        win.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Clear browsing data", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Time range", VerticalAlignment = VerticalAlignment.Center }, range } },
                hist, cook, cache, site,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } }
            }
        };
        await win.ShowDialog(owner);
        if (!go) return;
        if (hist.IsChecked == true)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long cutoff = range.SelectedIndex switch { 0 => now - 3600, 1 => now - 86400, 2 => now - 7 * 86400, _ => 0 };
            BrowserData.Store.History.RemoveAll(h => h.Time >= cutoff);
            BrowserData.Save();
        }
        var what = new List<string>();
        if (cook.IsChecked == true) what.AddRange(new[] { "cookies", "localstorage", "indexdb", "serviceworkers", "cachestorage" });
        if (cache.IsChecked == true) what.Add("cache");
        if (what.Count > 0 && WebEngine.Connected) WebEngine.Send(new { c = "clear", what });
        if (site.IsChecked == true) { BrowserData.Settings.SitePermissions.Clear(); BrowserData.Settings.SiteZoom.Clear(); BrowserData.Save(); }
        Notifier.Show("Red Star Online", "Browsing data cleared.", "online");
    }
}
