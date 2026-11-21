// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RedStarOS.Services;
using RedStarOS.Services.Software;

namespace RedStarOS.Views;

/// <summary>
/// Software: Crimson Banner OS's own programs (our catalogue, published with each release), Flatpaks from Flathub and
/// AppImages from AppImageHub, with what's installed and its updates in one place. Free software only.
/// </summary>
public sealed class SoftwareWindow : Window
{
    private enum Tab { CrimsonBanner, Flatpak, AppImage, Installed }

    private Tab _tab = Tab.CrimsonBanner;
    private readonly Dictionary<Tab, ToggleButton> _tabs = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search", Width = 260, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox _list = new() { Width = 380 };
    private readonly StackPanel _detail = new() { Spacing = 12, Margin = new Thickness(24, 20) };
    private readonly StackPanel _banner = new() { Spacing = 8, Margin = new Thickness(12, 8, 12, 0) };
    private readonly CheckBox _unstated = new() { Content = "Include AppImages that don't state a licence", Margin = new Thickness(12, 4) };

    private List<SoftwareItem> _ours = new(), _flatpaks = new(), _appimages = new(), _installed = new();
    private string? _oursNote, _appimageNote;
    private bool _loadedFlatpak, _loadedAppImage;
    private readonly Dictionary<string, (int Pct, CancellationTokenSource Cts)> _busy = new();
    private readonly Dictionary<string, AppImages.Asset> _appImageUpdates = new();
    private HashSet<string> _flatpakUpdates = new();
    private List<Flatpak.Installed> _flatpakInstalled = new();
    private HashSet<string> _pacmanInstalled = new();
    private SoftwareItem? _selected;

    public SoftwareWindow()
    {
        Title = "Software";
        Width = 1040; Height = 700; MinWidth = 760; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (t, label) in new[] { (Tab.CrimsonBanner, "Crimson Banner"), (Tab.Flatpak, "Flatpak"), (Tab.AppImage, "AppImage"), (Tab.Installed, "Installed") })
        {
            var b = new ToggleButton { Content = label, Padding = new Thickness(14, 6) };
            b.Click += (_, _) => _ = Switch(t);
            _tabs[t] = b;
            tabs.Children.Add(b);
        }
        var top = new DockPanel { Margin = new Thickness(12, 10) };
        DockPanel.SetDock(_search, Dock.Right);
        top.Children.Add(_search);
        top.Children.Add(tabs);
        _search.TextChanged += (_, _) => Fill();
        _unstated.IsCheckedChanged += (_, _) => Fill();

        _list.ItemTemplate = new FuncDataTemplate<SoftwareItem>((item, _) => Row(item), supportsRecycling: false);
        _list.SelectionChanged += (_, _) => { _selected = _list.SelectedItem as SoftwareItem; ShowDetail(); };

        var left = new DockPanel();
        DockPanel.SetDock(_banner, Dock.Top);
        DockPanel.SetDock(_unstated, Dock.Bottom);
        left.Children.Add(_banner);
        left.Children.Add(_unstated);
        left.Children.Add(_list);

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        body.Children.Add(new ScrollViewer { Content = _detail });

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var rule = new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) };
        DockPanel.SetDock(rule, Dock.Top);
        root.Children.Add(rule);
        root.Children.Add(body);
        Content = root;

        Opened += async (_, _) =>
        {
            await RefreshInstalled();
            await LoadOurs(fetch: true);
            await Switch(Tab.CrimsonBanner);
        };
        Closed += (_, _) => { foreach (var b in _busy.Values) b.Cts.Cancel(); };
    }

    // ── tabs and lists ──

    private async Task Switch(Tab t)
    {
        _tab = t;
        foreach (var (k, b) in _tabs) b.IsChecked = k == t;
        _unstated.IsVisible = t == Tab.AppImage;
        _search.Text = "";
        if (t == Tab.Flatpak && !_loadedFlatpak) await LoadFlatpak(prepare: false);
        if (t == Tab.AppImage && !_loadedAppImage) await LoadAppImages(fetch: false);
        if (t == Tab.Installed) await RefreshInstalled();
        Fill();
    }

    private IEnumerable<SoftwareItem> Current() => _tab switch
    {
        Tab.CrimsonBanner => _ours,
        Tab.Flatpak => _flatpaks,
        Tab.AppImage => _appimages.Where(i => _unstated.IsChecked == true || i.Licence == FreeLicences.Verdict.Free),
        _ => _installed,
    };

    private void Fill()
    {
        var q = (_search.Text ?? "").Trim();
        var items = Current();
        if (q.Length > 0)
            items = items.Where(i => i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                     || i.Summary.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                     || i.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
        var list = items.ToList();
        var keep = _selected?.Id;
        _list.ItemsSource = list;
        _list.SelectedItem = list.FirstOrDefault(i => i.Id == keep && i.Source == _selected?.Source) ?? list.FirstOrDefault();
        FillBanner(list.Count);
        if (list.Count == 0) { _selected = null; ShowDetail(); }
        var updates = _installed.Count(i => i.UpdateAvailable);
        _tabs[Tab.Installed].Content = updates > 0 ? $"Installed · {updates} update{(updates == 1 ? "" : "s")}" : "Installed";
    }

    private void FillBanner(int count)
    {
        _banner.Children.Clear();
        void Note(string text) => _banner.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12 });
        Button Action(string text, Func<Task> run)
        {
            var b = new Button { Content = text };
            b.Click += async (_, _) => { b.IsEnabled = false; try { await run(); } finally { b.IsEnabled = true; } };
            _banner.Children.Add(b);
            return b;
        }
        switch (_tab)
        {
            case Tab.CrimsonBanner:
                if (_oursNote != null) Note(_oursNote);
                if (_ours.Count == 0)
                    Note("Programs made for Crimson Banner OS that are too big or too specialised for every install will be offered here. Nothing has been published yet.");
                Action("Check for new programs", () => LoadOurs(fetch: true));
                break;
            case Tab.Flatpak:
                if (!Flatpak.Available)
                {
                    Note("Flatpak isn't installed. It's free software (LGPL) from freedesktop.org, and Flathub, run by the GNOME Foundation with KDE, is where most Flatpaks come from.");
                    Action("Install Flatpak", InstallFlatpakItself);
                }
                else if (_flatpaks.Count == 0)
                {
                    Note("Flathub's catalogue isn't on this computer yet.");
                    Action("Set up Flathub", () => LoadFlatpak(prepare: true));
                }
                else
                {
                    Note($"{count} free programs from Flathub. Programs that aren't free software aren't shown.");
                    Action("Refresh", () => LoadFlatpak(prepare: true));
                }
                break;
            case Tab.AppImage:
                if (_appimageNote != null) Note(_appimageNote);
                Note($"{count} programs from AppImageHub, downloaded from each project's own latest release into your Applications folder.");
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                _banner.Children.Add(row);
                var add = new Button { Content = "Add an AppImage file…" };
                add.Click += async (_, _) => await AddAppImageFile();
                var refresh = new Button { Content = "Refresh" };
                refresh.Click += async (_, _) => { refresh.IsEnabled = false; await LoadAppImages(fetch: true); refresh.IsEnabled = true; };
                row.Children.Add(add); row.Children.Add(refresh);
                break;
            case Tab.Installed:
                if (_installed.Count == 0) Note("Nothing installed from here yet.");
                if (_installed.Any(i => i.UpdateAvailable)) Action("Update all", UpdateAll);
                else if (_installed.Count > 0) Note("Everything is up to date.");
                Action("Check for updates", async () => { await RefreshInstalled(checkUpdates: true); Fill(); });
                break;
        }
    }

    private Control Row(SoftwareItem item)
    {
        var status = item.UpdateAvailable ? "Update available" : item.Installed ? "Installed" : null;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Summary.Length > 0) text.Children.Add(new TextBlock { Text = item.Summary, FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
        if (status != null || _tab == Tab.Installed)
            text.Children.Add(new TextBlock { Text = _tab == Tab.Installed ? SourceName(item.Source) + (status == "Update available" ? " · update available" : "") : status!, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(item.UpdateAvailable ? "#DD6B20" : "#38A169")) });
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*"), Margin = new Thickness(4) };
        var icon = IconFor(item, 36);
        g.Children.Add(icon);
        Grid.SetColumn(text, 1);
        text.Margin = new Thickness(10, 0, 0, 0);
        g.Children.Add(text);
        return g;
    }

    private static string SourceName(SoftwareSource s) => s switch
    {
        SoftwareSource.CrimsonBanner => "Crimson Banner",
        SoftwareSource.Flatpak => "Flatpak",
        _ => "AppImage",
    };

    // ── the selected program ──

    private void ShowDetail()
    {
        _detail.Children.Clear();
        var item = _selected;
        if (item == null) return;

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        head.Children.Add(IconFor(item, 64));
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        title.Children.Add(new TextBlock { Text = item.Name, FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (item.Developer is { Length: > 0 } dev) title.Children.Add(new TextBlock { Text = dev, Opacity = 0.75 });
        head.Children.Add(title);
        _detail.Children.Add(head);

        if (item.Summary.Length > 0) _detail.Children.Add(new TextBlock { Text = item.Summary, FontSize = 15, TextWrapping = TextWrapping.Wrap });

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        _detail.Children.Add(buttons);
        if (_busy.TryGetValue(Key(item), out var busy))
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = busy.Pct, Width = 240, Height = 8, IsIndeterminate = busy.Pct <= 0, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(bar);
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, _) => busy.Cts.Cancel();
            buttons.Children.Add(cancel);
        }
        else
        {
            void Btn(string text, Func<Task> act, bool accent = false)
            {
                var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(16, 6) };
                if (accent) b.Classes.Add("primary");
                b.Click += async (_, _) => await act();
                buttons.Children.Add(b);
            }
            if (!item.Installed)
            {
                var method = Method(item);
                if (method != null) Btn("Install", () => Do(item, "Installing", ct => Install(item, method, ct)), true);
                else buttons.Children.Add(new TextBlock { Text = "This computer has no way to install it yet (it needs " + string.Join(" or ", item.Methods.Select(m => m.Kind).Distinct()) + ").", TextWrapping = TextWrapping.Wrap });
            }
            else
            {
                if (CanOpen(item)) Btn("Open", () => { Open(item); return Task.CompletedTask; }, !item.UpdateAvailable);
                if (item.UpdateAvailable) Btn("Update", () => Do(item, "Updating", ct => Update(item, ct)), true);
                Btn("Remove", () => Do(item, "Removing", ct => Remove(item, ct)));
            }
        }

        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions() };
        void Fact(string k, string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            var r = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var kt = new TextBlock { Text = k, Opacity = 0.65, Margin = new Thickness(0, 2, 16, 2) };
            var vt = new SelectableTextBlock { Text = v, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2) };
            Grid.SetRow(kt, r); Grid.SetRow(vt, r); Grid.SetColumn(vt, 1);
            facts.Children.Add(kt); facts.Children.Add(vt);
        }
        Fact("From", SourceName(item.Source) + (item.Source == SoftwareSource.Flatpak ? " (" + (item.Methods.FirstOrDefault()?.Remote ?? Flatpak.Flathub) + ")" : ""));
        Fact("Licence", item.License is { Length: > 0 } l ? l + (item.Licence == FreeLicences.Verdict.Free ? " (free software)" : "") : "Not stated: check the project's own page before relying on it");
        Fact("Version", item.Installed && item.InstalledVersion is { Length: > 0 } iv && iv != item.Version
            ? iv + (item.Version is { Length: > 0 } nv ? $" (latest {nv})" : "") : item.Version ?? item.InstalledVersion);
        Fact("Installed in", item.Installed ? item.InstalledWhere : null);
        Fact("Website", item.Homepage);
        Fact("Categories", item.Categories.Count > 0 ? string.Join(", ", item.Categories.Take(6)) : null);
        _detail.Children.Add(facts);

        if (item.Description.Length > 0 && item.Description != item.Summary)
            _detail.Children.Add(new SelectableTextBlock { Text = item.Description, TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
    }

    private static string Key(SoftwareItem i) => i.Source + ":" + i.Id;

    private static InstallMethod? Method(SoftwareItem item) =>
        item.Source == SoftwareSource.CrimsonBanner ? OurCatalog.Usable(item, ProcessUtil.Which) : item.Methods.FirstOrDefault();

    private async Task Do(SoftwareItem item, string verb, Func<CancellationToken, Task<string?>> work)
    {
        var key = Key(item);
        if (_busy.ContainsKey(key)) return;
        var cts = new CancellationTokenSource();
        _busy[key] = (0, cts);
        ShowDetail();
        string? error;
        try { error = await work(cts.Token); }
        catch (OperationCanceledException) { error = "cancelled"; }
        catch (Exception ex) { error = ex.Message; }
        _busy.Remove(key);
        if (error == null) Notifier.Show("Software", $"{verb} {item.Name}: done.", "software");
        else if (error != "cancelled") Notifier.Show("Software", $"{verb} {item.Name} didn't work: {error}", "software");
        await RefreshInstalled();
        Fill();
        ShowDetail();
    }

    private Action<int> ProgressFor(SoftwareItem item) => pct => Dispatcher.UIThread.Post(() =>
    {
        var key = Key(item);
        if (!_busy.TryGetValue(key, out var b)) return;
        _busy[key] = (pct, b.Cts);
        if (_selected != null && Key(_selected) == key) ShowDetail();
    });

    // ── installing, updating, removing, opening ──

    private Task<string?> Install(SoftwareItem item, InstallMethod m, CancellationToken ct) => m.Kind switch
    {
        InstallMethod.Flatpak => Flatpak.InstallAsync(m, ProgressFor(item), ct),
        InstallMethod.AppImage => AppImages.InstallAsync(item, m, ProgressFor(item), ct),
        InstallMethod.Pacman => SystemUpdates.InstallByNameAsync(new[] { m.Package! }, ProgressFor(item), ct),
        _ => Task.FromResult<string?>("unknown kind of install"),
    };

    private async Task<string?> Update(SoftwareItem item, CancellationToken ct)
    {
        if (FlatpakRow(item) is { } fp) return await Flatpak.UpdateAsync(fp.Id, fp.Installation, ProgressFor(item), ct);
        if (AppImageRecord(item) is { } rec)
        {
            var m = item.Methods.FirstOrDefault(x => x.Kind == InstallMethod.AppImage) ?? new InstallMethod(InstallMethod.AppImage) { GitHub = rec.GitHub };
            return await AppImages.InstallAsync(new SoftwareItem { Source = item.Source, Id = rec.Id, Name = rec.Name, Version = item.Version }, m, ProgressFor(item), ct);
        }
        return "There's no update for it here.";
    }

    private async Task<string?> Remove(SoftwareItem item, CancellationToken ct)
    {
        if (FlatpakRow(item) is { } fp) return await Flatpak.RemoveAsync(fp.Id, fp.Installation, ct);
        if (AppImageRecord(item) is { } rec) return AppImages.Remove(rec);
        if (item.Methods.FirstOrDefault(m => m.Kind == InstallMethod.Pacman) is { Package: { } pkg } && _pacmanInstalled.Contains(pkg))
            return await SystemUpdates.RemoveByNameAsync(new[] { pkg }, ProgressFor(item), ct);
        return "It isn't installed in a way Software can remove.";
    }

    private bool CanOpen(SoftwareItem item) => FlatpakRow(item) != null || AppImageRecord(item) != null;

    private void Open(SoftwareItem item)
    {
        if (FlatpakRow(item) is { } fp) Flatpak.Run(fp.Id);
        else if (AppImageRecord(item) is { } rec) AppImages.Run(rec);
    }

    private Flatpak.Installed? FlatpakRow(SoftwareItem item)
    {
        var refId = item.Methods.FirstOrDefault(m => m.Kind == InstallMethod.Flatpak)?.Ref?.Split('/') is { Length: >= 2 } parts ? parts[1]
                  : item.Methods.FirstOrDefault(m => m.Kind == InstallMethod.Flatpak)?.Ref;
        var id = item.Source == SoftwareSource.Flatpak ? item.Id : refId;
        return id == null ? null : _flatpakInstalled.FirstOrDefault(f => f.Id == id);
    }

    private static AppImages.Record? AppImageRecord(SoftwareItem item) =>
        AppImages.InstalledRecords().FirstOrDefault(r => r.Id == item.Id);

    private async Task UpdateAll()
    {
        foreach (var item in _installed.Where(i => i.UpdateAvailable).ToList())
            await Do(item, "Updating", ct => Update(item, ct));
    }

    private async Task InstallFlatpakItself()
    {
        var err = await SystemUpdates.InstallByNameAsync(new[] { "flatpak" }, null);
        if (err != null) { Notifier.Show("Software", "Couldn't install Flatpak: " + err, "software"); return; }
        await LoadFlatpak(prepare: true);
    }

    private async Task AddAppImageFile()
    {
        var r = await FileDialogs.Open(this, new FilePickerOpenOptions
        {
            Title = "Add an AppImage",
            FileTypeFilter = new[] { new FilePickerFileType("AppImages") { Patterns = new[] { "*.AppImage", "*.appimage" } } },
        });
        var path = r.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        var err = await AppImages.AddFileAsync(path, CancellationToken.None);
        Notifier.Show("Software", err == null ? $"{Path.GetFileName(path)} is in your Applications folder and the Start menu." : "Couldn't add it: " + err, "software");
        await RefreshInstalled();
        Fill();
    }

    // ── loading ──

    private async Task LoadOurs(bool fetch)
    {
        (_ours, _oursNote) = await OurCatalog.LoadAsync(SettingsStore.Current.SoftwareCatalogUrl, fetch);
        MarkInstalled(_ours);
        if (_tab == Tab.CrimsonBanner) Fill();
    }

    private async Task LoadFlatpak(bool prepare)
    {
        if (!Flatpak.Available) { _loadedFlatpak = true; return; }
        if (prepare || Flatpak.Catalogue().Count == 0)
        {
            _banner.Children.Clear();
            _banner.Children.Add(new TextBlock { Text = "Fetching Flathub's catalogue…", Opacity = 0.8 });
            var err = await Flatpak.PrepareAsync(CancellationToken.None);
            if (err != null) Notifier.Show("Software", err, "software");
        }
        _flatpaks = await Task.Run(() => Flatpak.Catalogue());
        _loadedFlatpak = true;
        MarkInstalled(_flatpaks);
        if (_tab == Tab.Flatpak) Fill();
    }

    private async Task LoadAppImages(bool fetch)
    {
        (_appimages, _appimageNote) = await AppImages.CatalogueAsync(fetch, CancellationToken.None);
        _loadedAppImage = true;
        MarkInstalled(_appimages);
        if (_tab == Tab.AppImage) Fill();
    }

    /// <summary>What's installed from each source, and (when asked) which have updates.</summary>
    private async Task RefreshInstalled(bool checkUpdates = false)
    {
        _flatpakInstalled = await Flatpak.InstalledAsync();
        if (checkUpdates)
        {
            _flatpakUpdates = await Flatpak.UpdatesAsync();
            _appImageUpdates.Clear();
            foreach (var (k, v) in await AppImages.UpdatesAsync(CancellationToken.None)) _appImageUpdates[k] = v;
        }
        var pacmanNames = _ours.SelectMany(i => i.Methods).Where(m => m.Kind == InstallMethod.Pacman && m.Package != null).Select(m => m.Package!).Distinct().ToList();
        try { _pacmanInstalled = pacmanNames.Count > 0 && ProcessUtil.Which("pacman") != null ? await SystemUpdates.InstalledAsync(pacmanNames) : new(); }
        catch (Exception) { _pacmanInstalled = new(); }

        var installed = new List<SoftwareItem>();
        // our own programs, however they were installed
        foreach (var i in _ours) { MarkOne(i); if (i.Installed) installed.Add(i); }
        // every Flatpak app, with what Flathub's catalogue knows about it
        var catalogue = _flatpaks.Count > 0 ? _flatpaks : (_loadedFlatpak ? new() : await Task.Run(() => Flatpak.Catalogue()));
        foreach (var f in _flatpakInstalled)
        {
            if (installed.Any(i => FlatpakRow(i)?.Id == f.Id)) continue;
            var known = catalogue.FirstOrDefault(c => c.Id == f.Id);
            installed.Add(new SoftwareItem
            {
                Source = SoftwareSource.Flatpak, Id = f.Id, Name = known?.Name ?? f.Name, Summary = known?.Summary ?? "", Description = known?.Description ?? "",
                License = known?.License, Developer = known?.Developer, Homepage = known?.Homepage, Version = known?.Version, IconPath = known?.IconPath,
                Categories = known?.Categories ?? new(), Methods = { new InstallMethod(InstallMethod.Flatpak) { Remote = f.Origin, Ref = f.Id } },
                Installed = true, InstalledVersion = f.Version, InstalledWhere = f.Installation == "system" ? "the system (all users)" : "your account",
                UpdateAvailable = _flatpakUpdates.Contains(f.Id),
            });
        }
        // AppImages Software installed or was given
        foreach (var r in AppImages.InstalledRecords())
        {
            if (installed.Any(i => i.Id == r.Id)) continue;
            var known = _appimages.FirstOrDefault(c => c.Id == r.Id);
            installed.Add(new SoftwareItem
            {
                Source = SoftwareSource.AppImage, Id = r.Id, Name = r.Name, Summary = known?.Summary ?? Path.GetFileName(r.File), Description = known?.Description ?? "",
                License = known?.License, Developer = known?.Developer, Homepage = known?.Homepage, IconPath = r.Icon, IconUrl = known?.IconUrl,
                Version = _appImageUpdates.TryGetValue(r.Id, out var up) ? up.Tag : r.Version,
                Methods = { new InstallMethod(InstallMethod.AppImage) { GitHub = r.GitHub } },
                Installed = true, InstalledVersion = r.Version, InstalledWhere = r.File, UpdateAvailable = _appImageUpdates.ContainsKey(r.Id),
            });
        }
        _installed = installed.OrderByDescending(i => i.UpdateAvailable).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        MarkInstalled(_flatpaks);
        MarkInstalled(_appimages);
    }

    private void MarkInstalled(IEnumerable<SoftwareItem> items) { foreach (var i in items) MarkOne(i); }

    private void MarkOne(SoftwareItem i)
    {
        var fp = FlatpakRow(i);
        var rec = fp == null ? AppImageRecord(i) : null;
        var pacman = i.Methods.FirstOrDefault(m => m.Kind == InstallMethod.Pacman)?.Package is { } pkg && _pacmanInstalled.Contains(pkg);
        i.Installed = fp != null || rec != null || pacman;
        i.InstalledVersion = fp?.Version ?? rec?.Version;
        i.InstalledWhere = fp != null ? (fp.Installation == "system" ? "the system (all users)" : "your account") : rec?.File ?? (pacman ? "the system (pacman)" : null);
        i.UpdateAvailable = (fp != null && _flatpakUpdates.Contains(fp.Id)) || (rec != null && _appImageUpdates.ContainsKey(rec.Id))
                            || (i.Source == SoftwareSource.CrimsonBanner && rec != null && i.Version != null && rec.Version != i.Version);
    }

    // ── icons ──

    private static readonly Dictionary<string, Bitmap?> Bitmaps = new();
    private static readonly HttpClient IconHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static string IconCache => Path.Combine(Path.GetDirectoryName(OurCatalog.CachePath)!, "software-icons");

    private static Control IconFor(SoftwareItem item, double size)
    {
        var holder = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 5), ClipToBounds = true };
        if (LoadLocal(item.IconPath) is { } bmp) { holder.Child = new Image { Source = bmp, Stretch = Stretch.Uniform }; return holder; }
        holder.Background = new SolidColorBrush(Color.Parse(AppIcons.NeutralColor));
        holder.Child = new TextBlock { Text = item.Name.Length > 0 ? item.Name[..1].ToUpperInvariant() : "?", FontSize = size * 0.45, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (item.IconUrl is { } url && url.StartsWith("https://", StringComparison.Ordinal))
            _ = FetchIcon(url).ContinueWith(t =>
            {
                if (t.Result is { } path) Dispatcher.UIThread.Post(() =>
                {
                    if (LoadLocal(path) is { } b) { holder.Background = null; holder.Child = new Image { Source = b, Stretch = Stretch.Uniform }; }
                });
            }, TaskScheduler.Default);
        return holder;
    }

    private static Bitmap? LoadLocal(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return null;
        lock (Bitmaps)
        {
            if (Bitmaps.TryGetValue(path, out var b)) return b;
            try { using var s = File.OpenRead(path); b = Bitmap.DecodeToWidth(s, 128); }
            catch (Exception) { b = null; }
            Bitmaps[path] = b;
            return b;
        }
    }

    private static async Task<string?> FetchIcon(string url)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant() + ".png";
        var path = Path.Combine(IconCache, name);
        if (File.Exists(path)) return path;
        try
        {
            var bytes = await IconHttp.GetByteArrayAsync(url);
            if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != (byte)'P') return null;   // PNG only
            Directory.CreateDirectory(IconCache);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception) { return null; }
    }
}
