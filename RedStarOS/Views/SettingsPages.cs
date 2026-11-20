using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Hardware pages of Settings: Wi-Fi &amp; network (NetworkManager), sound (PulseAudio / PipeWire), Bluetooth
/// (bluetoothctl), displays (xrandr), printers (CUPS) and default apps. Each page explains what is missing when the
/// system tool it needs isn't installed.
/// </summary>
public static class SettingsPages
{
    // ───────────────────────── helpers ─────────────────────────
    private static StackPanel Page(string title, string subtitle)
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(new TextBlock { Text = title, Classes = { "h1" } });
        p.Children.Add(new TextBlock { Text = subtitle, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        return p;
    }
    private static TextBlock Eyebrow(string t) => new() { Text = t, Classes = { "eyebrow" }, Margin = new Thickness(0, 10, 0, 0) };
    private static TextBlock Note(string t) => new() { Text = t, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private static Button Btn(string text, Action act, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 5) };
        if (primary) b.Classes.Add("primary");
        b.Click += (_, _) => act();
        return b;
    }
    private static StackPanel Row(params Control[] kids)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var k in kids) p.Children.Add(k);
        return p;
    }
    private static Border Card(Control child) => new()
    {
        Child = child, Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.FromArgb(22, 128, 128, 128))
    };
    private static Window? Owner(Control c) => TopLevel.GetTopLevel(c) as Window;
    private static async Task<(int Code, string Out)> Run(string tool, params string[] args)
    {
        var path = ProcessUtil.Which(tool);
        if (path == null) return (-127, tool + " is not installed");
        return await ProcessUtil.RunAsync(path, args, 30000);
    }
    private static Control Missing(string what, string package) =>
        Card(Note($"{what} needs “{package}”, which isn't installed on this system. Install it with your distribution's package manager, then open this page again."));

    private static async Task<string?> AskSecret(Control anchor, string title, string label)
    {
        var owner = Owner(anchor);
        if (owner == null) return null;
        string? result = null;
        var box = new TextBox { PasswordChar = '•', MinWidth = 280 };
        var win = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var ok = new Button { Content = "Connect", IsDefault = true, Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => { result = box.Text ?? ""; win.Close(); };
        cancel.Click += (_, _) => win.Close();
        win.Content = new StackPanel { Margin = new Thickness(20), Spacing = 10, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, box, Row(cancel, ok) } };
        win.Opened += (_, _) => box.Focus();
        await win.ShowDialog(owner);
        return result;
    }

    // ═════════════════════════════════ Wi-Fi & network ═════════════════════════════════
    public static Control Network()
    {
        var p = Page("Wi-Fi & Network", "Wireless networks, airplane mode and saved connections (NetworkManager).");
        var status = Note("Checking…");
        p.Children.Add(Card(status));
        if (ProcessUtil.Which("nmcli") == null)
        {
            p.Children.Add(Missing("Managing Wi-Fi", "network-manager (nmcli)"));
            p.Children.Add(Btn("Show network interfaces", () => WindowManager.Open(new NetworkWindow())));
            return p;
        }
        var wifiOn = new ToggleSwitch { OnContent = "Wi-Fi on", OffContent = "Wi-Fi off" };
        var airplane = new ToggleSwitch { OnContent = "Airplane mode on", OffContent = "Airplane mode off" };
        bool loading = true;
        p.Children.Add(Row(wifiOn, airplane));
        var nets = new StackPanel { Spacing = 4 };
        var saved = new StackPanel { Spacing = 4 };
        var scan = Btn("Scan for networks", () => { }, primary: true);
        p.Children.Add(Eyebrow("NETWORKS NEARBY"));
        p.Children.Add(scan);
        p.Children.Add(nets);
        p.Children.Add(Eyebrow("SAVED CONNECTIONS"));
        p.Children.Add(saved);
        p.Children.Add(Btn("Network interfaces…", () => WindowManager.Open(new NetworkWindow())));

        async Task Refresh(bool rescan)
        {
            loading = true;
            status.Text = SystemInfo.NetworkSummary();
            var (_, radio) = await Run("nmcli", "radio", "wifi");
            wifiOn.IsChecked = radio.Trim() == "enabled";
            var (_, all) = await Run("nmcli", "-t", "-f", "WIFI,WWAN", "radio");
            airplane.IsChecked = all.Trim().Split(':').All(x => x.Trim() == "disabled");
            loading = false;

            nets.Children.Clear();
            nets.Children.Add(Note(rescan ? "Scanning…" : "Loading…"));
            var (code, list) = await Run("nmcli", "-t", "-f", "IN-USE,SSID,SIGNAL,SECURITY", "dev", "wifi", "list", "--rescan", rescan ? "yes" : "auto");
            nets.Children.Clear();
            var seen = new HashSet<string>();
            foreach (var line in list.Split('\n'))
            {
                var f = Regex.Split(line.Trim(), @"(?<!\\):");
                if (f.Length < 4 || f[1].Length == 0 || !seen.Add(f[1])) continue;
                bool inUse = f[0] == "*";
                string ssid = f[1].Replace("\\:", ":"), signal = f[2], sec = f[3];
                var connect = Btn(inUse ? "Disconnect" : "Connect", async () =>
                {
                    if (inUse)
                    {
                        var (_, dev) = await Run("nmcli", "-t", "-f", "DEVICE,TYPE,STATE", "dev");
                        var wl = dev.Split('\n').Select(l => l.Split(':')).FirstOrDefault(x => x.Length >= 3 && x[1] == "wifi" && x[2].StartsWith("connected"));
                        if (wl != null) await Run("nmcli", "dev", "disconnect", wl[0]);
                    }
                    else
                    {
                        var r = await Run("nmcli", "dev", "wifi", "connect", ssid);
                        if (r.Code != 0 && sec.Length > 0 && sec != "--")
                        {
                            var pw = await AskSecret(nets, "Wi-Fi password", $"Password for “{ssid}”:");
                            if (pw == null) return;
                            r = await Run("nmcli", "dev", "wifi", "connect", ssid, "password", pw);
                        }
                        Notifier.Show("Network", r.Code == 0 ? "Connected to " + ssid : "Couldn't connect to " + ssid + ": " + r.Out.Split('\n').Last());
                    }
                    await Refresh(false);
                }, primary: !inUse);
                var label = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { new TextBlock { Text = (inUse ? "✔ " : "") + ssid, FontWeight = inUse ? FontWeight.SemiBold : FontWeight.Normal },
                                 new TextBlock { Text = $"Signal {signal}%  ·  {(sec.Length == 0 || sec == "--" ? "open" : sec)}", FontSize = 12, Opacity = 0.65 } }
                };
                var row = new DockPanel();
                DockPanel.SetDock(connect, Dock.Right);
                row.Children.Add(connect);
                row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Icons.Create("wifi", 18), label } });
                nets.Children.Add(Card(row));
            }
            if (nets.Children.Count == 0) nets.Children.Add(Note(code == 0 ? "No Wi-Fi networks found." : "Wi-Fi isn't available: " + list));

            saved.Children.Clear();
            var (_, cons) = await Run("nmcli", "-t", "-f", "NAME,TYPE", "con", "show");
            foreach (var line in cons.Split('\n'))
            {
                var f = Regex.Split(line.Trim(), @"(?<!\\):");
                if (f.Length < 2 || f[0].Length == 0 || f[1] == "loopback") continue;
                var name = f[0].Replace("\\:", ":");
                var forget = Btn("Forget", async () =>
                {
                    var owner = Owner(saved);
                    if (owner != null && await Dialogs.Ask(owner, "Forget network", $"Forget “{name}”? You'll need the password to connect again.", "Forget", "Cancel") != 0) return;
                    await Run("nmcli", "con", "delete", name);
                    await Refresh(false);
                });
                var row = new DockPanel();
                DockPanel.SetDock(forget, Dock.Right);
                row.Children.Add(forget);
                row.Children.Add(new TextBlock { Text = name + "   (" + f[1].Replace("802-11-wireless", "Wi-Fi").Replace("802-3-ethernet", "wired") + ")", VerticalAlignment = VerticalAlignment.Center });
                saved.Children.Add(Card(row));
            }
            status.Text = SystemInfo.NetworkSummary();
        }

        wifiOn.IsCheckedChanged += async (_, _) => { if (!loading) { await Run("nmcli", "radio", "wifi", wifiOn.IsChecked == true ? "on" : "off"); await Refresh(false); } };
        airplane.IsCheckedChanged += async (_, _) => { if (!loading) { await Run("nmcli", "radio", "all", airplane.IsChecked == true ? "off" : "on"); await Refresh(false); } };
        scan.Click += async (_, _) => await Refresh(true);
        _ = Refresh(false);
        return p;
    }

    // ═════════════════════════════════ Sound ═════════════════════════════════
    private sealed record Device(string Name, string Description);

    private static async Task<(List<Device> Devices, string Default)> AudioDevices(bool sinks)
    {
        var (_, text) = await Run("pactl", "list", sinks ? "sinks" : "sources");
        var list = new List<Device>();
        string? name = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Name: ")) name = line[6..].Trim();
            else if (line.StartsWith("Description: ") && name != null)
            {
                if (sinks || !name.EndsWith(".monitor")) list.Add(new Device(name, line[13..].Trim()));
                name = null;
            }
        }
        var (_, def) = await Run("pactl", sinks ? "get-default-sink" : "get-default-source");
        return (list, def.Trim());
    }

    public static Control Sound()
    {
        var p = Page("Sound", "Output and input devices and volume (PulseAudio or PipeWire).");
        if (ProcessUtil.Which("pactl") == null) { p.Children.Add(Missing("Sound settings", "pulseaudio-utils (pactl)")); return p; }
        var outList = new ComboBox { MinWidth = 320 };
        var inList = new ComboBox { MinWidth = 320 };
        var outVol = new Slider { Minimum = 0, Maximum = 150, Width = 320 };
        var inVol = new Slider { Minimum = 0, Maximum = 150, Width = 320 };
        var outMute = new CheckBox { Content = "Mute" };
        var inMute = new CheckBox { Content = "Mute microphone" };
        var outLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 48 };
        var inLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 48 };
        bool loading = true;
        List<Device> outs = new(), ins = new();

        p.Children.Add(Eyebrow("OUTPUT"));
        p.Children.Add(outList);
        p.Children.Add(Row(Icons.Create("volume", 18), outVol, outLabel, outMute));
        p.Children.Add(Btn("Play test sound", () =>
        {
            foreach (var f in new[] { "/usr/share/sounds/freedesktop/stereo/complete.oga", "/usr/share/sounds/freedesktop/stereo/bell.oga", "/usr/share/sounds/alsa/Front_Center.wav" })
                if (File.Exists(f)) { ProcessUtil.StartDetached(ProcessUtil.Which("paplay") ?? "paplay", new[] { f }); return; }
            SystemSounds.Play("notify");
        }));
        p.Children.Add(Eyebrow("INPUT"));
        p.Children.Add(inList);
        p.Children.Add(Row(Icons.Create("user", 18), inVol, inLabel, inMute));

        async Task Load()
        {
            loading = true;
            (outs, var defOut) = await AudioDevices(true);
            (ins, var defIn) = await AudioDevices(false);
            outList.ItemsSource = outs.Select(d => d.Description).ToList();
            outList.SelectedIndex = Math.Max(0, outs.FindIndex(d => d.Name == defOut));
            inList.ItemsSource = ins.Select(d => d.Description).ToList();
            inList.SelectedIndex = Math.Max(0, ins.FindIndex(d => d.Name == defIn));
            var v = await SystemInfo.GetVolume() ?? 0;
            outVol.Value = v; outLabel.Text = v + "%";
            outMute.IsChecked = await SystemInfo.GetMuted() == true;
            var (_, iv) = await Run("pactl", "get-source-volume", "@DEFAULT_SOURCE@");
            var m = Regex.Match(iv, @"(\d+)%");
            inVol.Value = m.Success ? int.Parse(m.Groups[1].Value) : 0; inLabel.Text = (int)inVol.Value + "%";
            var (_, im) = await Run("pactl", "get-source-mute", "@DEFAULT_SOURCE@");
            inMute.IsChecked = im.Contains("yes");
            loading = false;
        }

        outList.SelectionChanged += async (_, _) => { if (!loading && outList.SelectedIndex >= 0 && outList.SelectedIndex < outs.Count) { await Run("pactl", "set-default-sink", outs[outList.SelectedIndex].Name); await Load(); } };
        inList.SelectionChanged += async (_, _) => { if (!loading && inList.SelectedIndex >= 0 && inList.SelectedIndex < ins.Count) { await Run("pactl", "set-default-source", ins[inList.SelectedIndex].Name); await Load(); } };
        outVol.PropertyChanged += async (_, e) => { if (e.Property == RangeBase.ValueProperty && !loading) { outLabel.Text = (int)outVol.Value + "%"; await SystemInfo.SetVolume((int)outVol.Value); } };
        inVol.PropertyChanged += async (_, e) => { if (e.Property == RangeBase.ValueProperty && !loading) { inLabel.Text = (int)inVol.Value + "%"; await Run("pactl", "set-source-volume", "@DEFAULT_SOURCE@", (int)inVol.Value + "%"); } };
        outMute.IsCheckedChanged += async (_, _) => { if (!loading) await SystemInfo.SetMuted(outMute.IsChecked == true); };
        inMute.IsCheckedChanged += async (_, _) => { if (!loading) await Run("pactl", "set-source-mute", "@DEFAULT_SOURCE@", inMute.IsChecked == true ? "1" : "0"); };
        _ = Load();
        return p;
    }

    // ═════════════════════════════════ Bluetooth ═════════════════════════════════
    public static Control Bluetooth()
    {
        var p = Page("Bluetooth", "Headphones, speakers, keyboards and phones (BlueZ).");
        if (ProcessUtil.Which("bluetoothctl") == null) { p.Children.Add(Missing("Bluetooth", "bluez (bluetoothctl)")); return p; }
        var power = new ToggleSwitch { OnContent = "Bluetooth on", OffContent = "Bluetooth off" };
        var status = Note("");
        var devices = new StackPanel { Spacing = 4 };
        var scan = Btn("Search for devices", () => { }, primary: true);
        bool loading = true;
        p.Children.Add(power);
        p.Children.Add(status);
        p.Children.Add(Eyebrow("DEVICES"));
        p.Children.Add(scan);
        p.Children.Add(devices);

        async Task Load()
        {
            loading = true;
            var (code, show) = await Run("bluetoothctl", "show");
            bool hasAdapter = code == 0 && show.Contains("Controller");
            power.IsEnabled = hasAdapter;
            power.IsChecked = show.Contains("Powered: yes");
            status.Text = hasAdapter ? "" : "No Bluetooth adapter was found.";
            loading = false;
            devices.Children.Clear();
            var (_, list) = await Run("bluetoothctl", "devices");
            foreach (Match m in Regex.Matches(list, @"Device ([0-9A-F:]{17}) (.+)"))
            {
                string mac = m.Groups[1].Value, name = m.Groups[2].Value.Trim();
                var (_, info) = await Run("bluetoothctl", "info", mac);
                bool connected = info.Contains("Connected: yes"), paired = info.Contains("Paired: yes");
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (connected) actions.Children.Add(Btn("Disconnect", async () => { await Run("bluetoothctl", "disconnect", mac); await Load(); }));
                else actions.Children.Add(Btn(paired ? "Connect" : "Pair", async () =>
                {
                    status.Text = (paired ? "Connecting to " : "Pairing with ") + name + "…";
                    if (!paired) { await Run("bluetoothctl", "pair", mac); await Run("bluetoothctl", "trust", mac); }
                    var r = await Run("bluetoothctl", "connect", mac);
                    status.Text = r.Out.Contains("successful") ? "Connected to " + name : "Couldn't connect to " + name + ". Make sure it is in pairing mode.";
                    await Load();
                }, primary: true));
                if (paired) actions.Children.Add(Btn("Remove", async () => { await Run("bluetoothctl", "remove", mac); await Load(); }));
                var row = new DockPanel();
                DockPanel.SetDock(actions, Dock.Right);
                row.Children.Add(actions);
                row.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    Children = { Icons.Create("bluetooth", 18), new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
                        new TextBlock { Text = name, FontWeight = connected ? FontWeight.SemiBold : FontWeight.Normal },
                        new TextBlock { Text = connected ? "Connected" : paired ? "Paired" : "Not paired", FontSize = 12, Opacity = 0.65 } } } }
                });
                devices.Children.Add(Card(row));
            }
            if (devices.Children.Count == 0) devices.Children.Add(Note("No devices yet. Put your device in pairing mode and press “Search for devices”."));
        }

        power.IsCheckedChanged += async (_, _) => { if (!loading) { await Run("bluetoothctl", "power", power.IsChecked == true ? "on" : "off"); await Load(); } };
        scan.Click += async (_, _) =>
        {
            status.Text = "Searching for 10 seconds…";
            await Run("bluetoothctl", "--timeout", "10", "scan", "on");
            status.Text = "";
            await Load();
        };
        _ = Load();
        return p;
    }

    // ═════════════════════════════════ Displays ═════════════════════════════════
    private sealed class Output
    {
        public string Name = ""; public bool Connected, Primary, On;
        public string Current = ""; public string Preferred = ""; public List<string> Modes = new();
        public string Rotation = "normal";
    }

    private static async Task<List<Output>> ReadOutputs()
    {
        var (_, text) = await Run("xrandr", "--query");
        var outs = new List<Output>();
        Output? cur = null;
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line, @"^(\S+) (connected|disconnected)( primary)?( (\d+x\d+)\+\d+\+\d+)?( (left|right|inverted))?");
            if (m.Success)
            {
                cur = new Output { Name = m.Groups[1].Value, Connected = m.Groups[2].Value == "connected", Primary = m.Groups[3].Success, On = m.Groups[5].Success, Current = m.Groups[5].Value,
                    Rotation = m.Groups[7].Success ? m.Groups[7].Value : "normal" };
                if (cur.Connected) outs.Add(cur);
                continue;
            }
            var mm = Regex.Match(line, @"^\s+(\d+x\d+)\S*\s+(.*)$");
            if (mm.Success && cur != null && cur.Connected)
            {
                if (!cur.Modes.Contains(mm.Groups[1].Value)) cur.Modes.Add(mm.Groups[1].Value);
                if (mm.Groups[2].Value.Contains('+') && cur.Preferred.Length == 0) cur.Preferred = mm.Groups[1].Value;
            }
        }
        return outs;
    }

    public static Control Displays()
    {
        if (InputIsolation.InRedStarComp) return DisplaysComp();
        var p = Page("Displays", "Resolution, rotation and arrangement of your screens (xrandr). Changes are undone after 15 seconds unless you keep them.");
        if (ProcessUtil.Which("xrandr") == null || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } && Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland")
        {
            p.Children.Add(Missing("Display settings", "xrandr (an X11 session)"));
            return p;
        }
        var host = new StackPanel { Spacing = 10 };
        p.Children.Add(host);
        p.Children.Add(Eyebrow("TEXT AND APP SIZE"));
        p.Children.Add(Note("Change the size of everything in Crimson Banner OS in Settings → Accessibility."));

        async Task Load()
        {
            host.Children.Clear();
            var outs = await ReadOutputs();
            if (outs.Count == 0) { host.Children.Add(Note("No screens were reported by xrandr.")); return; }
            var primary = outs.FirstOrDefault(o => o.Primary) ?? outs[0];
            foreach (var o in outs)
            {
                var output = o;
                var res = new ComboBox { ItemsSource = output.Modes, SelectedItem = output.On ? output.Current : output.Preferred, MinWidth = 160 };
                var rot = new ComboBox { ItemsSource = new[] { "normal", "left", "right", "inverted" }, SelectedItem = output.Rotation, MinWidth = 120 };
                var place = new ComboBox { MinWidth = 200 };
                var places = new List<string> { "—" };
                if (!ReferenceEquals(output, primary)) places = new List<string> { "Right of " + primary.Name, "Left of " + primary.Name, "Above " + primary.Name, "Below " + primary.Name, "Same as " + primary.Name + " (mirror)" };
                place.ItemsSource = places; place.SelectedIndex = 0; place.IsEnabled = !ReferenceEquals(output, primary);
                var on = new CheckBox { Content = "On", IsChecked = output.On || !outs.Any(x => x.On) };
                var prim = new CheckBox { Content = "Main display", IsChecked = output.Primary };
                var apply = Btn("Apply", async () =>
                {
                    var args = new List<string> { "--output", output.Name };
                    if (on.IsChecked != true) args.Add("--off");
                    else
                    {
                        args.AddRange(new[] { "--mode", res.SelectedItem as string ?? output.Preferred, "--rotate", rot.SelectedItem as string ?? "normal" });
                        if (prim.IsChecked == true) args.Add("--primary");
                        if (!ReferenceEquals(output, primary))
                        {
                            var how = place.SelectedItem as string ?? "";
                            args.Add(how.StartsWith("Left") ? "--left-of" : how.StartsWith("Above") ? "--above" : how.StartsWith("Below") ? "--below" : how.StartsWith("Same") ? "--same-as" : "--right-of");
                            args.Add(primary.Name);
                        }
                    }
                    await ApplyWithConfirm(host, args.ToArray(), output);
                    await Load();
                }, primary: true);
                host.Children.Add(Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Icons.Create("monitor", 20), new TextBlock { Text = output.Name + (output.Primary ? "  (main)" : ""), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } },
                        Row(new TextBlock { Text = "Resolution", Width = 90, VerticalAlignment = VerticalAlignment.Center }, res, new TextBlock { Text = "Rotation", VerticalAlignment = VerticalAlignment.Center }, rot),
                        Row(new TextBlock { Text = "Position", Width = 90, VerticalAlignment = VerticalAlignment.Center }, place),
                        Row(on, prim, apply),
                    }
                }));
            }
        }
        _ = Load();
        return p;
    }

    /// <summary>Applies an xrandr change, then asks to keep it; without an answer within 15 s the old layout returns.</summary>
    private static async Task ApplyWithConfirm(Control anchor, string[] args, Output before)
    {
        var undo = new List<string> { "--output", before.Name };
        if (!before.On) undo.Add("--off");
        else { undo.AddRange(new[] { "--mode", before.Current, "--rotate", before.Rotation }); if (before.Primary) undo.Add("--primary"); }
        var r = await Run("xrandr", args);
        if (r.Code != 0) { Notifier.Show("Displays", "The screen didn't accept that setting: " + r.Out); return; }
        if (!await AskToKeep(anchor)) await Run("xrandr", undo.ToArray());
    }

    /// <summary>"Keep these display settings?" — false when the user reverts or doesn't answer within 15 s.</summary>
    private static async Task<bool> AskToKeep(Control anchor)
    {
        var owner = Owner(anchor);
        if (owner == null) return true;
        bool kept = false;
        var win = new Window { Title = "Keep these display settings?", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = true };
        var label = new TextBlock { Text = "Reverting in 15 seconds…" };
        var keep = new Button { Content = "Keep changes", Classes = { "primary" }, IsDefault = true };
        var revert = new Button { Content = "Revert", IsCancel = true };
        keep.Click += (_, _) => { kept = true; win.Close(); };
        revert.Click += (_, _) => win.Close();
        win.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { new TextBlock { Text = "Keep these display settings?", FontWeight = FontWeight.SemiBold, FontSize = 15 }, label, Row(revert, keep) } };
        int left = 15;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => { left--; label.Text = $"Reverting in {left} seconds…"; if (left <= 0) win.Close(); };
        t.Start();
        await win.ShowDialog(owner);
        t.Stop();
        return kept;
    }

    // ── inside redstar-comp: the compositor sets the screens (also what wlr-randr and kanshi use) ──
    private static readonly (string Label, int Transform)[] Rotations = { ("Normal", 0), ("90°", 1), ("180°", 2), ("270°", 3) };
    private static readonly double[] Scales = { 1, 1.25, 1.5, 1.75, 2, 2.5, 3 };

    /// <summary>Where a screen goes next to another: right of / left of / above / below (public for the tests).</summary>
    public static (int X, int Y) PlaceNextTo(CompIpc.OutputInfo main, (int W, int H) size, string where)
    {
        var (mw, mh) = main.Logical;
        return where switch
        {
            "left" => (main.X - size.W, main.Y),
            "above" => (main.X, main.Y - size.H),
            "below" => (main.X, main.Y + mh),
            _ => (main.X + mw, main.Y),
        };
    }

    private static Control DisplaysComp()
    {
        var p = Page("Displays", "Resolution, refresh rate, scale, rotation and arrangement of your screens. Changes are undone after 15 seconds unless you keep them.");
        var host = new StackPanel { Spacing = 10 };
        p.Children.Add(host);
        p.Children.Add(Eyebrow("TEXT AND APP SIZE"));
        p.Children.Add(Note("Scale makes everything bigger on that screen. To change only Crimson Banner OS, use Settings → Accessibility."));

        async Task Load()
        {
            host.Children.Clear();
            var outs = await CompIpc.Outputs();
            if (outs == null) { host.Children.Add(Note("redstar-comp didn't answer.")); return; }
            if (outs.Count == 0) { host.Children.Add(Note("No screens were reported.")); return; }
            if (outs.All(o => o.Nested))
            {
                host.Children.Add(Note("Crimson Banner OS is running as a window on another desktop, so that desktop decides the size. " +
                    "Use its display settings, or start the \"Crimson Banner OS (Wayland)\" session from the login screen."));
                return;
            }
            var main = outs.FirstOrDefault(o => o.Enabled && o.X == 0 && o.Y == 0) ?? outs.FirstOrDefault(o => o.Enabled) ?? outs[0];
            foreach (var o in outs)
            {
                var output = o;
                var modes = output.Modes.Count > 0 ? output.Modes.ToList() : new List<CompIpc.OutputMode> { new(output.Width, output.Height, output.RefreshMhz, true) };
                var current = modes.FirstOrDefault(m => m.Width == output.Width && m.Height == output.Height && Math.Abs(m.RefreshMhz - output.RefreshMhz) < 500)
                              ?? modes.FirstOrDefault(m => m.Preferred) ?? modes[0];
                var res = new ComboBox { ItemsSource = modes, SelectedItem = current, MinWidth = 220 };
                var scale = new ComboBox { ItemsSource = Scales.Select(x => $"{x * 100:0}%").ToList(), MinWidth = 100,
                    SelectedIndex = Math.Max(0, Array.FindIndex(Scales, x => Math.Abs(x - output.Scale) < 0.01)) };
                var rot = new ComboBox { ItemsSource = Rotations.Select(r => r.Label).ToList(), MinWidth = 100,
                    SelectedIndex = Math.Max(0, Array.FindIndex(Rotations, r => r.Transform == output.Transform)) };
                bool isMain = ReferenceEquals(output, main);
                var places = new[] { ("right", "Right of " + main.Name), ("left", "Left of " + main.Name), ("above", "Above " + main.Name), ("below", "Below " + main.Name) };
                var place = new ComboBox { ItemsSource = isMain ? new List<string> { "—" } : places.Select(x => x.Item2).ToList(), SelectedIndex = 0, MinWidth = 200, IsEnabled = !isMain };
                if (!isMain)
                {
                    // show where it is now
                    var (mw, mh) = main.Logical;
                    place.SelectedIndex = output.X >= main.X + mw ? 0 : output.X + output.Logical.W <= main.X ? 1 : output.Y + output.Logical.H <= main.Y ? 2 : output.Y >= main.Y + mh ? 3 : 0;
                }
                var on = new CheckBox { Content = "On", IsChecked = output.Enabled };
                var apply = Btn("Apply", async () =>
                {
                    var mode = res.SelectedItem as CompIpc.OutputMode ?? current;
                    double sc = Scales[Math.Max(0, scale.SelectedIndex)];
                    int tr = Rotations[Math.Max(0, rot.SelectedIndex)].Transform;
                    var after = output with { Width = mode.Width, Height = mode.Height, Scale = sc, Transform = tr };
                    var (x, y) = isMain ? (output.X, output.Y) : PlaceNextTo(main, after.Logical, places[Math.Max(0, place.SelectedIndex)].Item1);
                    var why = await CompIpc.SetOutput(output.Name, on.IsChecked == true, mode.Width, mode.Height, mode.RefreshMhz, x, y, sc, tr);
                    if (why != null) { Notifier.Show("Displays", "The screen didn't accept that setting: " + why, "settings"); return; }
                    if (await AskToKeep(host)) CompIpc.SaveOutputs();
                    else
                    {
                        var back = await CompIpc.SetOutput(output.Name, output.Enabled, output.Width, output.Height, output.RefreshMhz, output.X, output.Y, output.Scale, output.Transform);
                        if (back != null) Notifier.Show("Displays", "Couldn't go back to the old setting: " + back, "settings");
                    }
                    await Load();
                }, primary: true);
                var title = output.Name + (output.Description.Length > 0 ? " · " + output.Description : "") + (isMain ? "  (main)" : "");
                host.Children.Add(Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Icons.Create("monitor", 20), new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap } } },
                        Row(new TextBlock { Text = "Resolution", Width = 90, VerticalAlignment = VerticalAlignment.Center }, res),
                        Row(new TextBlock { Text = "Scale", Width = 90, VerticalAlignment = VerticalAlignment.Center }, scale, new TextBlock { Text = "Rotation", VerticalAlignment = VerticalAlignment.Center }, rot),
                        Row(new TextBlock { Text = "Position", Width = 90, VerticalAlignment = VerticalAlignment.Center }, place),
                        Row(on, apply),
                    }
                }));
            }
            host.Children.Add(Note("Other tools that set screens on Wayland (wlr-randr, kanshi) work too. What you keep here is used again at the next start."));
        }
        _ = Load();
        return p;
    }

    // ═════════════════════════════════ Printers ═════════════════════════════════
    public static Control Printers()
    {
        var p = Page("Printers", "Printers set up in CUPS: choose the default, print a test page, see the queue.");
        if (ProcessUtil.Which("lpstat") == null) { p.Children.Add(Missing("Printing", "cups (lpstat)")); return p; }
        var list = new StackPanel { Spacing = 4 };
        var queue = new StackPanel { Spacing = 4 };
        p.Children.Add(list);
        var refresh = Btn("Refresh", () => { });
        p.Children.Add(Row(
            Btn("Add a printer…", () =>
            {
                if (ProcessUtil.Which("system-config-printer") is { } scp) ProcessUtil.StartDetached(scp, Array.Empty<string>());
                else BrowserLauncher.Launch("http://localhost:631/admin");
            }, primary: true),
            refresh));
        p.Children.Add(Eyebrow("PRINT QUEUE"));
        p.Children.Add(queue);

        async Task Load()
        {
            list.Children.Clear();
            var (_, printers) = await Run("lpstat", "-p");
            var (_, def) = await Run("lpstat", "-d");
            var defName = Regex.Match(def, @"destination: (\S+)") is { Success: true } dm ? dm.Groups[1].Value : "";
            foreach (Match m in Regex.Matches(printers, @"printer (\S+) (?:is )?(.+?)\."))
            {
                string name = m.Groups[1].Value, state = m.Groups[2].Value;
                bool isDef = name == defName;
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (!isDef) actions.Children.Add(Btn("Make default", async () => { await Run("lpoptions", "-d", name); await Load(); }));
                actions.Children.Add(Btn("Test page", async () =>
                {
                    var test = new[] { "/usr/share/cups/data/testprint", "/usr/share/cups/data/default-testpage.pdf" }.FirstOrDefault(File.Exists);
                    var r = test != null ? await Run("lp", "-d", name, test) : (1, "no CUPS test page found");
                    Notifier.Show("Printers", r.Item1 == 0 ? "Test page sent to " + name : "Couldn't print: " + r.Item2);
                    await Load();
                }));
                var row = new DockPanel();
                DockPanel.SetDock(actions, Dock.Right);
                row.Children.Add(actions);
                row.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    Children = { Icons.Create("printer", 20), new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
                        new TextBlock { Text = name + (isDef ? "  (default)" : ""), FontWeight = isDef ? FontWeight.SemiBold : FontWeight.Normal },
                        new TextBlock { Text = state, FontSize = 12, Opacity = 0.65 } } } }
                });
                list.Children.Add(Card(row));
            }
            if (list.Children.Count == 0) list.Children.Add(Note("No printers are set up yet."));

            queue.Children.Clear();
            var (_, jobs) = await Run("lpstat", "-o");
            foreach (var line in jobs.Split('\n').Where(l => l.Trim().Length > 0))
            {
                var id = line.Split(' ')[0];
                var cancel = Btn("Cancel", async () => { await Run("cancel", id); await Load(); });
                var row = new DockPanel();
                DockPanel.SetDock(cancel, Dock.Right);
                row.Children.Add(cancel);
                row.Children.Add(new TextBlock { Text = line.Trim(), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                queue.Children.Add(Card(row));
            }
            if (queue.Children.Count == 0) queue.Children.Add(Note("Nothing is waiting to print."));
        }
        refresh.Click += async (_, _) => await Load();
        _ = Load();
        return p;
    }

    // ═════════════════════════════════ Default apps ═════════════════════════════════
    public static Control DefaultAppsPage()
    {
        var p = Page("Default apps", "Which program opens links and each kind of file. Crimson Banner apps are used unless you pick another.");
        var linux = DesktopEntries.All().OrderBy(a => a.Name).ToList();
        var systemWide = new CheckBox { Content = "Also make it the default for other programs (xdg-mime)", IsChecked = false };
        foreach (var kind in DefaultApps.Kinds)
        {
            var k = kind;
            var names = new List<string> { k.RedStarName + " (built in)", "The system's default" };
            names.AddRange(linux.Select(a => a.Name));
            var combo = new ComboBox { ItemsSource = names, MinWidth = 280 };
            var cur = DefaultApps.Get(k.Id);
            combo.SelectedIndex = cur == "redstar" ? 0 : cur == "system" ? 1 : Math.Max(0, linux.FindIndex(a => a.FilePath == cur) + 2);
            combo.SelectionChanged += (_, _) =>
            {
                int i = combo.SelectedIndex;
                var value = i <= 0 ? "redstar" : i == 1 ? "system" : linux[i - 2].FilePath;
                DefaultApps.Set(k.Id, value, systemWide.IsChecked == true);
            };
            p.Children.Add(Row(new TextBlock { Text = k.Label, Width = 160, VerticalAlignment = VerticalAlignment.Center }, combo));
        }
        p.Children.Add(systemWide);
        p.Children.Add(Note("Terminal programs (Terminal=true) open in Terminal."));
        p.Children.Add(Note(RedStarOnline.ProgramPath() is { } online
            ? "Crimson Online: " + online + ". Its own settings are under Crimson Online in this window."
            : "Crimson Online (GTK + WebKitGTK) isn't installed: build it with browser/build.sh and run install-session.sh."));
        return p;
    }

    // ═════════════════════════════════ Software updates ═════════════════════════════════
    public static Control Updates()
    {
        var st = SettingsStore.Current;
        var p = Page("Software Updates", $"You have Crimson Banner OS {UpdateChecker.Current.ToString(3)}.");
        var auto = new CheckBox { Content = "Check for updates automatically (once a day)", IsChecked = st.AutoUpdateCheck };
        auto.IsCheckedChanged += (_, _) => { st.AutoUpdateCheck = auto.IsChecked == true; SettingsStore.Save(); };
        p.Children.Add(auto);
        var last = Note(st.LastUpdateCheck is { } t ? $"Last checked {t:d MMM yyyy, HH:mm}." : "Not checked yet.");
        p.Children.Add(last);
        var feed = UpdateChecker.FeedUrl();
        if (feed == null)
            p.Children.Add(Card(Note("No releases feed is known for this build. Open Updates to enter one (a GitHub “releases/latest” API address), or build the AppImage with GH_REPO=user/repo.")));
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var open = Btn("Open Updates…", () => WindowManager.Open(new UpdatesWindow()));
        Button? check = null;
        check = Btn("Check now", async () =>
        {
            check!.IsEnabled = false;
            result.Text = "Checking…";
            var r = await UpdateChecker.CheckAsync();
            check.IsEnabled = true;
            if (r.Error != null) { result.Text = "Couldn't check: " + r.Error; return; }
            st.LastUpdateCheck = DateTime.Now; SettingsStore.Save();
            last.Text = $"Last checked {DateTime.Now:d MMM yyyy, HH:mm}.";
            result.Text = r.Newer ? $"Crimson Banner OS {r.Latest} is available. Use “Open Updates…” to install it." : "You're up to date.";
        }, true);
        p.Children.Add(Row(check, open));
        p.Children.Add(result);
        if (!string.IsNullOrEmpty(st.SkippedVersion))
            p.Children.Add(Row(Note($"Skipped version: {st.SkippedVersion}"), Btn("Don't skip", () => { st.SkippedVersion = null; SettingsStore.Save(); })));

        // everything else on the system: its packages, through PackageKit
        p.Children.Add(Eyebrow("SYSTEM PACKAGES"));
        var sysNote = Note("Looking for PackageKit…");
        var sysList = new StackPanel { Spacing = 2 };
        var bar = new ProgressBar { Maximum = 100, Height = 6, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
        var sysButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(sysNote);
        p.Children.Add(sysButtons);
        p.Children.Add(bar);
        p.Children.Add(sysList);
        List<PackageUpdate> pending = new();
        Button? checkSys = null, install = null;
        async System.Threading.Tasks.Task Check(bool refresh)
        {
            checkSys!.IsEnabled = install!.IsEnabled = false;
            sysNote.Text = refresh ? "Refreshing the package lists…" : "Checking…";
            var (list, err) = await SystemUpdates.CheckAsync(refresh);
            checkSys.IsEnabled = true;
            pending = list;
            sysList.Children.Clear();
            if (err != null) { sysNote.Text = "Couldn't check: " + err; return; }
            int sec = list.Count(u => u.Security);
            sysNote.Text = list.Count == 0 ? "The system is up to date."
                : $"{list.Count} update{(list.Count == 1 ? "" : "s")} waiting" + (sec > 0 ? $", {sec} for security." : ".");
            foreach (var u in list.Take(60))
                sysList.Children.Add(new TextBlock { Text = (u.Security ? "● " : "  ") + u.Name + "  " + u.Version, Classes = { "caption" } });
            if (list.Count > 60) sysList.Children.Add(Note($"…and {list.Count - 60} more."));
            install.IsEnabled = list.Count > 0;
            SettingsStore.Current.LastSystemUpdateCheck = DateTime.Now; SettingsStore.Save();
        }
        checkSys = Btn("Check for system updates", () => _ = Check(true));
        install = Btn("Install updates", async () =>
        {
            install!.IsEnabled = checkSys!.IsEnabled = false;
            bar.IsVisible = true; bar.Value = 0;
            sysNote.Text = "Installing… (you'll be asked for the administrator password)";
            var err = await SystemUpdates.InstallAsync(pending, pct => Avalonia.Threading.Dispatcher.UIThread.Post(() => bar.Value = pct));
            bar.IsVisible = false;
            checkSys.IsEnabled = true;
            if (err != null) { sysNote.Text = "The updates weren't installed: " + err; install.IsEnabled = true; return; }
            sysNote.Text = "Updates installed. Restart if the kernel or graphics drivers were among them.";
            sysList.Children.Clear();
            pending = new();
        }, true);
        install.IsEnabled = false;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            bool pk = await SystemUpdates.AvailableAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (pk)
                {
                    sysButtons.Children.Add(checkSys);
                    sysButtons.Children.Add(install);
                    sysNote.Text = SettingsStore.Current.LastSystemUpdateCheck is { } t ? $"Last checked {t:d MMM yyyy, HH:mm}." : "Not checked yet.";
                    _ = Check(false);
                    return;
                }
                sysNote.Text = "Install PackageKit to see and install the system's updates here (Arch/CachyOS: pacman -S packagekit).";
                if (SystemUpdates.TerminalCommand(ProcessUtil.Which) is { } cmd)
                    sysButtons.Children.Add(Btn("Update in Terminal", () => ProcessUtil.RunInTerminal(cmd.Exe, cmd.Args)));
            });
        });
        return p;
    }

    // ═════════════════════════════════ Diagnostics ═════════════════════════════════
    public static Control DiagnosticsPage()
    {
        var p = Page("Diagnostics", "System facts and error logs, to help find out why something doesn't work. Nothing is sent anywhere.");
        p.Children.Add(Eyebrow("THIS SYSTEM"));
        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
        int row = 0;
        foreach (var (k, v) in Diagnostics.SystemFacts())
        {
            facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var a = new TextBlock { Text = k, Opacity = 0.7, Margin = new Thickness(0, 2) };
            var b = new SelectableTextBlock { Text = v, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2) };
            Grid.SetRow(a, row); Grid.SetRow(b, row); Grid.SetColumn(b, 1);
            facts.Children.Add(a); facts.Children.Add(b);
            row++;
        }
        p.Children.Add(Card(facts));

        p.Children.Add(Eyebrow("GRAPHICS ACCELERATION"));
        var modes = new[] { ("auto", "Automatic — use the GPU where it works"), ("gpu", "Always use the GPU"), ("software", "Software only (most compatible)") };
        var gfx = new ComboBox { ItemsSource = modes.Select(m => m.Item2).ToList(), MinWidth = 300 };
        gfx.SelectedIndex = Math.Max(0, Array.FindIndex(modes, m => m.Item1 == (SettingsStore.Current.Graphics ?? "auto")));
        Avalonia.Automation.AutomationProperties.SetName(gfx, "Graphics acceleration");
        var gfxNote = Note("Automatic: the browser renders pages and decodes video on the GPU whenever the display allows it; "
                           + "Crimson Banner's own windows use the GPU when a compositor (picom) runs as well. Changes apply after Crimson Banner OS restarts.");
        gfx.SelectionChanged += (_, _) =>
        {
            if (gfx.SelectedIndex < 0) return;
            SettingsStore.Current.Graphics = modes[gfx.SelectedIndex].Item1;
            SettingsStore.Save();
            gfxNote.Text = "Saved — restart Crimson Banner OS (Shut down, then start it again) to apply.";
        };
        p.Children.Add(gfx);
        p.Children.Add(gfxNote);

        Control LogBox(string title, string file)
        {
            var text = Diagnostics.Tail(AppPaths.ConfigFile(file), 60).Trim();
            var box = new TextBox
            {
                Text = text.Length == 0 ? "(empty — no errors recorded)" : text, IsReadOnly = true, AcceptsReturn = true,
                FontFamily = new FontFamily("DejaVu Sans Mono,Liberation Mono,monospace"), FontSize = 11.5,
                MaxHeight = 220, TextWrapping = TextWrapping.NoWrap
            };
            Avalonia.Automation.AutomationProperties.SetName(box, title);
            return new StackPanel { Spacing = 4, Children = { Eyebrow(title), box } };
        }
        p.Children.Add(LogBox("ERRORS (crash.log)", "crash.log"));
        p.Children.Add(LogBox("BROWSER (browser.log)", "browser.log"));

        var status = Note("");
        p.Children.Add(Row(
            Btn("Export diagnostics…", () =>
            {
                try
                {
                    var desk = Path.Combine(AppPaths.Home, "Desktop");
                    var folder = Directory.Exists(desk) ? desk : AppPaths.Home;
                    var zip = Diagnostics.Export(folder);
                    status.Text = "Saved " + zip;
                    Notifier.Show("Diagnostics exported", Path.GetFileName(zip) + " — attach it to a bug report.", "files",
                        new NotificationAction("Show in folder", () => WindowManager.Open(new FileManagerWindow(folder), "files")));
                }
                catch (Exception ex) { status.Text = "Couldn't export: " + ex.Message; }
            }, true),
            Btn("Copy system facts", () =>
            {
                var sb = new System.Text.StringBuilder();
                foreach (var (k, v) in Diagnostics.SystemFacts()) sb.Append(k).Append(": ").Append(v).Append('\n');
                ClipboardHistory.Use(sb.ToString());
                status.Text = "Copied.";
            }),
            Btn("Open logs folder", () => WindowManager.Open(new FileManagerWindow(AppPaths.ConfigDir), "files")),
            Btn("Clear logs", () => { Diagnostics.ClearLogs(); status.Text = "Logs cleared. Reopen this page to refresh."; })));
        p.Children.Add(status);
        return p;
    }

    // ═════════════════════════════════ Accessibility ═════════════════════════════════
    public static Control Accessibility()
    {
        var st = SettingsStore.Current;
        var p = Page("Accessibility", "Make Crimson Banner OS easier to see, hear and use with the keyboard.");
        void Changed() { SettingsStore.Save(); WindowManager.RaiseChanged(); }

        p.Children.Add(Eyebrow("SIZE"));
        var sizes = new[] { 1.0, 1.25, 1.5, 1.75, 2.0 };
        var size = new ComboBox
        {
            ItemsSource = sizes.Select(v => $"{v * 100:0}%" + (v == 1.0 ? " (normal)" : "")).ToList(),
            SelectedIndex = Math.Max(0, Array.FindIndex(sizes, v => Math.Abs(v - st.UiScale) < 0.01)), MinWidth = 200
        };
        Avalonia.Automation.AutomationProperties.SetName(size, "Size of text and controls");
        var sizeNote = Note(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR") is { } now ? $"Now: {now}×." : "");
        size.SelectionChanged += (_, _) =>
        {
            if (size.SelectedIndex < 0) return;
            st.UiScale = sizes[size.SelectedIndex]; Changed();
            sizeNote.Text = "The new size is used the next time you sign in (Crimson Online, Writer and the other apps follow it).";
        };
        p.Children.Add(Row(new TextBlock { Text = "Text and controls", Width = 160, VerticalAlignment = VerticalAlignment.Center }, size));
        p.Children.Add(sizeNote);

        p.Children.Add(Eyebrow("SEEING"));
        CheckBox Toggle(string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Content = text, IsChecked = value };
            c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); Changed(); };
            return c;
        }
        p.Children.Add(Toggle("High contrast — black and white text, strong borders, thick focus outline", st.HighContrast, v => { st.HighContrast = v; ThemeService.Apply(); }));
        p.Children.Add(Toggle("Large mouse pointer (at once inside redstar-comp; elsewhere new programs now, everything at next sign-in)", st.LargeCursor, v => { st.LargeCursor = v; AccessibleNames.ApplyPointerSize(); }));
        p.Children.Add(Toggle("Reduce motion — no fades or blinking cursors; web pages are asked to keep still", st.ReduceMotion, v => st.ReduceMotion = v));

        p.Children.Add(Eyebrow("HEARING"));
        var sounds = new CheckBox { Content = "Play system sounds (start-up, sign-in, notifications, errors)", IsChecked = SystemSounds.Enabled };
        sounds.IsCheckedChanged += (_, _) => SystemSounds.Enabled = sounds.IsChecked == true;
        p.Children.Add(sounds);
        p.Children.Add(Row(Btn("Play start-up", () => SystemSounds.Play("startup", true)), Btn("Play notification", () => SystemSounds.Play("notify", true)), Btn("Play error", () => SystemSounds.Play("error", true))));
        p.Children.Add(Note("Notifications always show on screen as well, and stay in the notification centre."));

        p.Children.Add(Eyebrow("KEYBOARD"));
        p.Children.Add(Note("Tab and Shift+Tab move between controls; the one with keyboard focus gets an outline in your accent colour. Space or Enter presses it, Esc closes menus and dialogs. Super opens Start; Alt+Tab switches windows."));
        p.Children.Add(Btn("Show all shortcuts", WelcomeWindow.OpenShortcuts));

        p.Children.Add(Eyebrow("SCREEN READER"));
        p.Children.Add(Card(Note("Buttons and fields carry names for assistive technology (icon buttons use their tooltip). On Linux, Orca reading Crimson Banner apps depends on the accessibility support of the Avalonia toolkit, which is still limited; web pages in Crimson Online are read through Chromium's own support.")));
        return p;
    }

    // ═════════════════════════════════ Red Star Online ═════════════════════════════════
    /// <summary>The browser's settings. Red Star Online reads the same file (browser-data.json) and picks changes up when
    /// one of its windows is next focused. Each handler reads <see cref="BrowserData.Settings"/> afresh, because the file
    /// is reloaded when the browser writes it.</summary>
    public static Control OnlinePage()
    {
        static BrowserSettings S() => BrowserData.Settings;
        var p = Page("Crimson Online", "Search, privacy, blocked sites and imports for the web browser.");
        if (RedStarOnline.ProgramPath() == null)
            p.Children.Add(Card(Note("Crimson Online isn't installed: build it with browser/build.sh and run install-session.sh. These settings are kept for when it is.")));
        CheckBox Toggle(string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Content = text, IsChecked = value };
            c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); BrowserData.Save(); };
            return c;
        }

        p.Children.Add(Eyebrow("SEARCH"));
        var engines = new ComboBox { ItemsSource = BrowserData.Engines.Select(e => e.Name).ToList(), SelectedItem = S().SearchEngine, MinWidth = 200 };
        engines.SelectionChanged += (_, _) => { if (engines.SelectedItem is string n) { S().SearchEngine = n; BrowserData.Save(); } };
        p.Children.Add(Row(new TextBlock { Text = "Search engine", Width = 160, VerticalAlignment = VerticalAlignment.Center }, engines));

        p.Children.Add(Eyebrow("GENERAL"));
        p.Children.Add(Toggle("Show the bookmarks bar", S().ShowBookmarksBar, v => S().ShowBookmarksBar = v));
        p.Children.Add(Toggle("Reopen the previous tabs when Crimson Online starts", S().RestoreSession, v => S().RestoreSession = v));
        p.Children.Add(Toggle("Remember browsing history", S().SaveHistory, v => S().SaveHistory = v));

        p.Children.Add(Eyebrow("PRIVACY AND SAFETY"));
        p.Children.Add(Toggle("Block ads and trackers (EasyList, EasyPrivacy, Peter Lowe's list)", S().BlockTrackers, v => S().BlockTrackers = v));
        p.Children.Add(Note("The lists are downloaded by the browser and refreshed every few days; its menu shows how many rules are in use."));
        var allow = new StackPanel { Spacing = 4 };
        void FillAllow()
        {
            allow.Children.Clear();
            var sites = S().TrackerAllowSites.ToList();
            if (sites.Count == 0) { allow.Children.Add(Note("Blocking is on for every site.")); return; }
            allow.Children.Add(Note("Sites where blocking is turned off:"));
            foreach (var h in sites)
            {
                var host = h;
                allow.Children.Add(Row(new TextBlock { Text = host, VerticalAlignment = VerticalAlignment.Center, Width = 260 },
                    Btn("Block again", () => { S().TrackerAllowSites.Remove(host); BrowserData.Save(); FillAllow(); })));
            }
        }
        FillAllow();
        p.Children.Add(allow);

        p.Children.Add(Note("Blocked sites: one address per line. These sites and their subdomains won't load."));
        var blocked = new TextBox { Text = S().BlockedSites, AcceptsReturn = true, Height = 110, MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left,
            PlaceholderText = "example.com\nanother-site.org", TextWrapping = TextWrapping.NoWrap };
        blocked.LostFocus += (_, _) => { S().BlockedSites = blocked.Text ?? ""; BrowserData.Save(); };
        p.Children.Add(blocked);

        p.Children.Add(Eyebrow("CLEAR BROWSING DATA"));
        var range = new ComboBox { ItemsSource = new[] { "Last hour", "Last 24 hours", "Last 7 days", "All time" }, SelectedIndex = 3, MinWidth = 160 };
        var hist = new CheckBox { Content = "Browsing history (in the time range)", IsChecked = true };
        var cook = new CheckBox { Content = "Cookies and other site data (all time; signs you out of sites)", IsChecked = false };
        var cache = new CheckBox { Content = "Cached images and files (all time)", IsChecked = true };
        var site = new CheckBox { Content = "Site permissions and zoom levels", IsChecked = false };
        var clearNote = Note("");
        p.Children.Add(Row(new TextBlock { Text = "History from", Width = 160, VerticalAlignment = VerticalAlignment.Center }, range));
        p.Children.Add(hist); p.Children.Add(cook); p.Children.Add(cache); p.Children.Add(site);
        p.Children.Add(Btn("Clear data", () =>
        {
            var done = new List<string>();
            if (hist.IsChecked == true)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long cutoff = range.SelectedIndex switch { 0 => now - 3600, 1 => now - 86400, 2 => now - 7 * 86400, _ => 0 };
                BrowserData.Store.History.RemoveAll(h => h.Time >= cutoff);
                done.Add("history");
            }
            if (site.IsChecked == true) { S().SitePermissions.Clear(); S().SiteZoom.Clear(); done.Add("site permissions and zoom"); }
            if (done.Count > 0) BrowserData.FlushNow();
            var what = new List<string>();
            if (cook.IsChecked == true) what.Add("cookies");
            if (cache.IsChecked == true) what.Add("cache");
            if (what.Count > 0)
            {
                if (RedStarOnline.ClearData(what.ToArray())) done.AddRange(what.Select(w => w == "cookies" ? "cookies and site data" : "the cache"));
                else done.Add("(cookies and the cache need Crimson Online installed)");
            }
            clearNote.Text = done.Count > 0 ? "Cleared: " + string.Join(", ", done) + "." : "Nothing was cleared.";
        }, true));
        p.Children.Add(clearNote);

        p.Children.Add(Eyebrow("SITE PERMISSIONS"));
        var perms = new StackPanel { Spacing = 4 };
        void FillPerms()
        {
            perms.Children.Clear();
            var all = S().SitePermissions.OrderBy(k => k.Key).ToList();
            if (all.Count == 0) { perms.Children.Add(Note("No saved choices for camera, microphone, location or notifications.")); return; }
            foreach (var kv in all)
            {
                var key = kv.Key; var parts = key.Split('|');
                perms.Children.Add(Row(new TextBlock { Text = (kv.Value ? "Allowed: " : "Blocked: ") + parts[0] + " — " + (parts.Length > 1 ? parts[1] : ""), VerticalAlignment = VerticalAlignment.Center },
                    Btn("Forget", () => { S().SitePermissions.Remove(key); BrowserData.Save(); FillPerms(); })));
            }
        }
        FillPerms();
        p.Children.Add(perms);

        p.Children.Add(Eyebrow("PAGE ZOOM"));
        var zooms = new StackPanel { Spacing = 4 };
        void FillZoom()
        {
            zooms.Children.Clear();
            var all = S().SiteZoom.OrderBy(k => k.Key).ToList();
            if (all.Count == 0) { zooms.Children.Add(Note("No sites have their own zoom.")); return; }
            foreach (var kv in all)
            {
                var key = kv.Key;
                zooms.Children.Add(Row(new TextBlock { Text = key + " — " + (int)Math.Round(kv.Value * 100) + "%", VerticalAlignment = VerticalAlignment.Center },
                    Btn("Reset", () => { S().SiteZoom.Remove(key); BrowserData.Save(); FillZoom(); })));
            }
        }
        FillZoom();
        p.Children.Add(zooms);

        p.Children.Add(Eyebrow("IMPORT BOOKMARKS AND HISTORY"));
        p.Children.Add(Note("Copies from Firefox, Chrome, Chromium, Brave, Edge, Vivaldi or Opera on this computer. Nothing is duplicated or removed."));
        var importStatus = Note("");
        var found = BrowserImport.Detect();
        if (found.Count == 0) p.Children.Add(Note("No other browsers were found on this computer."));
        foreach (var src in found)
        {
            var sc = src;
            Button? btn = null;
            btn = Btn("Import", async () =>
            {
                btn!.IsEnabled = false;
                importStatus.Text = "Importing from " + sc.Name + "…";
                var r = await BrowserImport.ImportAsync(sc, true, true);
                btn.IsEnabled = true;
                importStatus.Text = $"{sc.Name}: {r.Bookmarks} new bookmarks, {r.History} history entries." + (r.Note != null ? " " + r.Note : "");
            });
            p.Children.Add(Row(btn, new TextBlock { Text = sc.Name, VerticalAlignment = VerticalAlignment.Center }));
        }
        p.Children.Add(importStatus);
        return p;
    }

    // ═════════════════════════════════ Startup apps ═════════════════════════════════
    /// <summary>Programs that start at sign-in (the standard autostart folders; Autostart).</summary>
    public static Control StartupPage()
    {
        var p = Page("Startup apps", "Programs that start when you sign in to Crimson Banner OS.");
        var run = new CheckBox { Content = "Start these programs when I sign in", IsChecked = SettingsStore.Current.RunAutostart };
        run.IsCheckedChanged += (_, _) => { SettingsStore.Current.RunAutostart = run.IsChecked == true; SettingsStore.Save(); };
        p.Children.Add(run);
        p.Children.Add(Note(Autostart.IsSession
            ? "They come from ~/.config/autostart and /etc/xdg/autostart, the same folders other desktops use."
            : "Crimson Banner OS is running on top of another desktop, which starts these itself; switching them on or off here changes it for that desktop too."));
        var list = new StackPanel { Spacing = 6 };
        void Fill()
        {
            list.Children.Clear();
            var all = Autostart.All();
            var desktops = Autostart.Desktops();
            if (all.Count == 0) list.Children.Add(Note("Nothing starts at sign-in."));
            foreach (var e in all)
            {
                var entry = e;
                var notHere = !entry.Hidden && !entry.RunsHere(desktops);
                var box = new CheckBox { Content = entry.Name + (notHere ? "  (only for other desktops)" : ""), IsChecked = !entry.Hidden, IsEnabled = !notHere };
                ToolTip.SetTip(box, (entry.Comment is { Length: > 0 } c ? c + "\n" : "") + entry.Exec);
                box.IsCheckedChanged += (_, _) =>
                {
                    try { Autostart.SetEnabled(entry, box.IsChecked == true); }
                    catch (Exception ex) { Notifier.Show("Startup apps", "Couldn't change it: " + ex.Message, "settings"); }
                    Fill();
                };
                if (entry.UserFile && !entry.HasSystemFile)
                    list.Children.Add(Row(box, Btn("Remove", () => { try { Autostart.Remove(entry); } catch { } Fill(); })));
                else list.Children.Add(box);
            }
        }
        Fill();
        p.Children.Add(Eyebrow("STARTS AT SIGN-IN"));
        p.Children.Add(list);

        p.Children.Add(Eyebrow("ADD A PROGRAM"));
        var apps = DesktopEntries.All().OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var pick = new ComboBox { ItemsSource = apps.Select(a => a.Name).ToList(), MinWidth = 280, PlaceholderText = "Choose an installed program" };
        p.Children.Add(Row(pick, Btn("Add", () =>
        {
            if (pick.SelectedIndex < 0) return;
            try { Autostart.Add(apps[pick.SelectedIndex]); } catch (Exception ex) { Notifier.Show("Startup apps", "Couldn't add it: " + ex.Message, "settings"); }
            pick.SelectedIndex = -1;
            Fill();
        })));
        return p;
    }

    // ═════════════════════════════════ Power & battery ═════════════════════════════════
    /// <summary>Screen off, lock and sleep when idle (IdleManager), the power mode (power-profiles-daemon), the battery.</summary>
    public static Control PowerPage()
    {
        var p = Page("Power & battery", "What happens when you leave the computer, and how it uses power.");
        var s = SettingsStore.Current;
        if (SystemInfo.BatteryInfo() is { } b)
        {
            p.Children.Add(Eyebrow("BATTERY"));
            p.Children.Add(new TextBlock { Text = $"{b.Percent}%" + (b.Charging ? ", charging" : ", on battery"), FontSize = 20, FontWeight = FontWeight.SemiBold });
            p.Children.Add(new ProgressBar { Value = b.Percent, Maximum = 100, Height = 8, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left });
        }

        p.Children.Add(Eyebrow("WHEN I'M AWAY"));
        int[] offChoices = { 0, 1, 2, 5, 10, 15, 30, 60 }, sleepChoices = { 0, 10, 15, 30, 60, 120 };
        string Min(int m) => m == 0 ? "Never" : m < 60 ? $"After {m} minute{(m == 1 ? "" : "s")}" : $"After {m / 60} hour{(m == 60 ? "" : "s")}";
        ComboBox Pick(int[] choices, int current, Action<int> set)
        {
            var c = new ComboBox { ItemsSource = choices.Select(Min).ToList(), SelectedIndex = Math.Max(0, Array.IndexOf(choices, current)), MinWidth = 200 };
            c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) { set(choices[c.SelectedIndex]); SettingsStore.Save(); IdleManager.Apply(); } };
            return c;
        }
        p.Children.Add(Row(new TextBlock { Text = "Turn the screen off", Width = 180, VerticalAlignment = VerticalAlignment.Center },
            Pick(offChoices, s.ScreenOffMinutes, v => SettingsStore.Current.ScreenOffMinutes = v)));
        p.Children.Add(Row(new TextBlock { Text = "Put the computer to sleep", Width = 180, VerticalAlignment = VerticalAlignment.Center },
            Pick(sleepChoices, s.SleepMinutes, v => SettingsStore.Current.SleepMinutes = v)));
        var lockBox = new CheckBox { Content = "Lock the screen when it turns off and before sleeping", IsChecked = s.LockOnScreenOff };
        lockBox.IsCheckedChanged += (_, _) => { SettingsStore.Current.LockOnScreenOff = lockBox.IsChecked == true; SettingsStore.Save(); };
        p.Children.Add(lockBox);
        var keeping = IdleManager.Inhibitors;
        p.Children.Add(Note(!Autostart.IsSession ? "Crimson Banner OS is running on top of another desktop, which decides this itself."
            : keeping.Count > 0 ? "Keeping the screen on right now: " + string.Join(", ", keeping) + "."
            : "Videos playing in Crimson Online, Video and other players keep the screen on."));
        p.Children.Add(Row(Btn("Lock now", ShellActions.Lock), Btn("Sleep now", ShellActions.Suspend)));

        p.Children.Add(Eyebrow("POWER MODE"));
        var modeNote = Note("");
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(modes);
        p.Children.Add(modeNote);
        _ = FillPowerModes(modes, modeNote);
        return p;
    }

    private static readonly (string Key, string Label)[] PowerModes = { ("power-saver", "Power saver"), ("balanced", "Balanced"), ("performance", "Performance") };

    private static async System.Threading.Tasks.Task FillPowerModes(StackPanel modes, TextBlock note)
    {
        var ppc = ProcessUtil.Which("powerprofilesctl");
        if (ppc == null) { note.Text = "Install power-profiles-daemon to choose a power mode (pacman -S power-profiles-daemon, then enable its service)."; return; }
        var (code, current) = await ProcessUtil.RunAsync(ppc, new[] { "get" }, 5000);
        var (_, list) = await ProcessUtil.RunAsync(ppc, new[] { "list" }, 5000);
        if (code != 0) { note.Text = "power-profiles-daemon isn't running (systemctl enable --now power-profiles-daemon)."; return; }
        current = current.Trim();
        modes.Children.Clear();
        foreach (var (key, label) in PowerModes)
        {
            if (!list.Contains(key)) continue;
            var k = key;
            var r = new RadioButton { Content = label, GroupName = "powermode", IsChecked = current == key };
            r.IsCheckedChanged += async (_, _) =>
            {
                if (r.IsChecked != true) return;
                var (c, text) = await ProcessUtil.RunAsync(ppc, new[] { "set", k }, 10000);
                note.Text = c == 0 ? label + " is on." : "Couldn't change it: " + text.Trim();
            };
            modes.Children.Add(r);
        }
        note.Text = "Power saver lowers performance to make the battery last; Performance lets the processor run fastest.";
    }

    // ═════════════════════════════════ Date & time ═════════════════════════════════
    public static Control DateTimePage()
    {
        var p = Page("Date & time", "The time zone, setting the clock from the internet, and how the clock looks.");
        var status = Note("");
        var zone = new ComboBox { MinWidth = 280, MaxDropDownHeight = 420, IsEnabled = false };
        var ntp = new CheckBox { Content = "Set the time automatically (network time)", IsEnabled = false };
        p.Children.Add(Eyebrow("TIME ZONE"));
        p.Children.Add(zone);
        p.Children.Add(ntp);
        p.Children.Add(status);
        p.Children.Add(Eyebrow("CLOCK"));
        var h24 = new CheckBox { Content = "24-hour clock", IsChecked = SettingsStore.Current.Clock24h };
        h24.IsCheckedChanged += (_, _) => { SettingsStore.Current.Clock24h = h24.IsChecked == true; SettingsStore.Save(); };
        p.Children.Add(h24);
        _ = Fill();
        async System.Threading.Tasks.Task Fill()
        {
            var info = await RegionSettings.TimeAsync();
            if (info == null) { status.Text = "timedatectl (systemd) isn't available, so the time zone can't be changed here."; return; }
            var zones = await RegionSettings.TimezonesAsync();
            bool loading = true;
            zone.ItemsSource = zones;
            zone.SelectedItem = info.Timezone;
            zone.IsEnabled = ntp.IsEnabled = true;
            ntp.IsChecked = info.Ntp;
            loading = false;
            status.Text = $"Now: {DateTime.Now:dddd d MMMM, HH:mm} ({info.Timezone})" + (info.Ntp ? info.Synchronized ? ", set from the network." : ", waiting for the network time." : ".");
            zone.SelectionChanged += async (_, _) =>
            {
                if (loading || zone.SelectedItem is not string z || z == info.Timezone) return;
                var err = await RegionSettings.RunAsync("timedatectl", "set-timezone", z);
                status.Text = err == null ? $"Time zone: {z}." : "Couldn't change it: " + err;
                if (err == null) { info = info with { Timezone = z }; TimeZoneInfo.ClearCachedData(); }
            };
            ntp.IsCheckedChanged += async (_, _) =>
            {
                if (loading) return;
                var err = await RegionSettings.RunAsync("timedatectl", "set-ntp", ntp.IsChecked == true ? "true" : "false");
                status.Text = err == null ? (ntp.IsChecked == true ? "The time is set from the network." : "Network time is off.") : "Couldn't change it: " + err;
            };
        }
        return p;
    }

    // ═════════════════════════════════ Region & keyboard ═════════════════════════════════
    public static Control RegionPage()
    {
        var p = Page("Region & keyboard", "Keyboard layouts and the system's language.");
        var s = SettingsStore.Current;
        p.Children.Add(Eyebrow("KEYBOARD LAYOUTS"));
        var list = new StackPanel { Spacing = 6 };
        p.Children.Add(list);
        void Save() { SettingsStore.Save(); RegionSettings.ApplyKeyboard(); Fill(); }
        void Fill()
        {
            list.Children.Clear();
            var cur = SettingsStore.Current.KeyboardLayouts;
            if (cur.Count == 0) list.Children.Add(Note("The system's layout is used. Add one to choose your own."));
            for (int i = 0; i < cur.Count; i++)
            {
                int k = i;
                var row = Row(new TextBlock { Text = RegionSettings.Describe(RegionSettings.Layout.Parse(cur[k])), Width = 300, VerticalAlignment = VerticalAlignment.Center },
                    Btn("Up", () => { if (k > 0) { (cur[k - 1], cur[k]) = (cur[k], cur[k - 1]); Save(); } }),
                    Btn("Remove", () => { cur.RemoveAt(k); Save(); }));
                list.Children.Add(row);
            }
        }
        Fill();
        var layouts = RegionSettings.Layouts();
        var layoutBox = new ComboBox { ItemsSource = layouts.Select(l => l.Name).ToList(), MinWidth = 260, MaxDropDownHeight = 420, PlaceholderText = "Layout" };
        var variantBox = new ComboBox { MinWidth = 220, MaxDropDownHeight = 420, PlaceholderText = "Variant" };
        layoutBox.SelectionChanged += (_, _) =>
        {
            if (layoutBox.SelectedIndex < 0) return;
            var l = layouts[layoutBox.SelectedIndex];
            variantBox.ItemsSource = new[] { "Standard" }.Concat(l.Variants.Select(v => v.Name)).ToList();
            variantBox.SelectedIndex = 0;
        };
        p.Children.Add(Row(layoutBox, variantBox, Btn("Add", () =>
        {
            if (layoutBox.SelectedIndex < 0) return;
            var l = layouts[layoutBox.SelectedIndex];
            var v = variantBox.SelectedIndex > 0 ? l.Variants[variantBox.SelectedIndex - 1].Variant : "";
            var code = new RegionSettings.Layout(l.Code, v).ToString();
            var cur = SettingsStore.Current.KeyboardLayouts;
            if (!cur.Contains(code)) cur.Add(code);
            Save();
        })));
        if (layouts.Count == 0) p.Children.Add(Note("XKB's list of layouts wasn't found (xkeyboard-config)."));

        ComboBox Option(string[] keys, string[] labels, string current, Action<string> set)
        {
            var c = new ComboBox { ItemsSource = labels, SelectedIndex = Math.Max(0, Array.IndexOf(keys, current)), MinWidth = 260 };
            c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) { set(keys[c.SelectedIndex]); SettingsStore.Save(); RegionSettings.ApplyKeyboard(); } };
            return c;
        }
        p.Children.Add(Row(new TextBlock { Text = "Switch layouts with", Width = 160, VerticalAlignment = VerticalAlignment.Center },
            Option(new[] { "grp:alt_shift_toggle", "grp:win_space_toggle", "grp:ctrl_shift_toggle", "grp:caps_toggle" },
                   new[] { "Alt+Shift", "Super+Space", "Ctrl+Shift", "Caps Lock" }, s.KeyboardSwitch, v => SettingsStore.Current.KeyboardSwitch = v)));
        p.Children.Add(Row(new TextBlock { Text = "Compose key", Width = 160, VerticalAlignment = VerticalAlignment.Center },
            Option(new[] { "", "compose:ralt", "compose:menu", "compose:rctrl" }, new[] { "None", "Right Alt", "Menu", "Right Ctrl" },
                   s.KeyboardCompose, v => SettingsStore.Current.KeyboardCompose = v)));
        p.Children.Add(Row(new TextBlock { Text = "Caps Lock", Width = 160, VerticalAlignment = VerticalAlignment.Center },
            Option(new[] { "", "caps:escape", "ctrl:nocaps", "caps:none" }, new[] { "Caps Lock", "Escape", "Another Ctrl", "Nothing" },
                   s.KeyboardCapsLock, v => SettingsStore.Current.KeyboardCapsLock = v)));
        p.Children.Add(Note(InputIsolation.InRedStarComp ? "Changes take effect at once." : "Changes take effect at once for this display, and in full at the next sign-in."));
        var sysNote = Note("");
        p.Children.Add(Btn("Use these layouts at the login screen too", async () =>
        {
            var (l, v, o) = RegionSettings.Xkb(SettingsStore.Current);
            if (l.Length == 0) { sysNote.Text = "Add a layout first."; return; }
            var err = await RegionSettings.RunAsync("localectl", "set-x11-keymap", l, "", v, o);
            sysNote.Text = err == null ? "The login screen and new sessions use them too." : "Couldn't change it: " + err;
        }));
        p.Children.Add(sysNote);

        p.Children.Add(Eyebrow("LANGUAGE"));
        var lang = new ComboBox { MinWidth = 280, MaxDropDownHeight = 420, IsEnabled = false };
        var langNote = Note("");
        p.Children.Add(lang);
        p.Children.Add(langNote);
        _ = FillLang();
        async System.Threading.Tasks.Task FillLang()
        {
            var (current, all) = await RegionSettings.LocalesAsync();
            if (all.Count == 0) { langNote.Text = "localectl (systemd) isn't available, or no languages are generated (see /etc/locale.gen)."; return; }
            bool loading = true;
            lang.ItemsSource = all;
            lang.SelectedItem = all.FirstOrDefault(x => x == current);
            lang.IsEnabled = true;
            loading = false;
            langNote.Text = "The system's language and formats (dates, numbers), from the next sign-in. Only languages generated in /etc/locale.gen are listed.";
            lang.SelectionChanged += async (_, _) =>
            {
                if (loading || lang.SelectedItem is not string l || l == current) return;
                var err = await RegionSettings.RunAsync("localectl", "set-locale", "LANG=" + l);
                langNote.Text = err == null ? $"Language: {l}, from the next sign-in." : "Couldn't change it: " + err;
                if (err == null) current = l;
            };
        }
        return p;
    }

    // ═════════════════════════════════ Users ═════════════════════════════════
    /// <summary>The computer's accounts (AccountsService); every change asks for the administrator password.</summary>
    public static Control UsersPage()
    {
        var p = Page("Users", "The people who can sign in to this computer.");
        var list = new StackPanel { Spacing = 8 };
        var note = Note("Loading…");
        p.Children.Add(note);
        p.Children.Add(list);
        var add = Btn("Add a user…", async () =>
        {
            var d = new AddUserDialog();
            if (TopLevel.GetTopLevel(p) is Window owner) await d.ShowDialog(owner); else d.Show();
            if (d.Added) await Fill();
        }, true);
        p.Children.Add(add);
        add.IsVisible = false;
        _ = Fill();
        async Task Fill()
        {
            if (!await UserAccounts.AvailableAsync())
            {
                note.Text = "Install AccountsService to manage users here (Arch/CachyOS: pacman -S accountsservice).";
                return;
            }
            List<UserAccount> users;
            try { users = await UserAccounts.ListAsync(); }
            catch (Exception ex) { note.Text = "Couldn't list the users: " + ex.Message; return; }
            add.IsVisible = true;
            note.Text = "Administrators can install software and change system settings. Changes ask for the administrator password.";
            list.Children.Clear();
            foreach (var u in users)
            {
                var a = u;
                var name = new TextBlock { Text = (a.RealName.Length > 0 ? a.RealName : a.UserName) + (a.IsMe ? " (you)" : ""), FontWeight = FontWeight.SemiBold };
                var sub = new TextBlock { Text = a.UserName + " · " + (a.Admin ? "Administrator" : "Standard user"), Classes = { "caption" } };
                var status = Note("");
                async Task Run(Task<string?> t, string ok) { var err = await t; status.Text = err == null ? ok : "Couldn't: " + err; if (err == null) await Fill(); }
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                buttons.Children.Add(Btn("Set password…", async () =>
                {
                    if (TopLevel.GetTopLevel(p) is not Window owner) return;
                    var pw = await Dialogs.Prompt(owner, "Set password", "New password for " + a.UserName);
                    if (string.IsNullOrEmpty(pw)) return;
                    var err = await UserAccounts.SetPasswordAsync(a, pw);
                    status.Text = err == null ? "Password changed." : "Couldn't: " + err;
                }));
                if (!a.IsMe)
                {
                    buttons.Children.Add(Btn(a.Admin ? "Make standard user" : "Make administrator",
                        () => _ = Run(UserAccounts.SetAdminAsync(a, !a.Admin), "Changed.")));
                    buttons.Children.Add(Btn("Remove…", async () =>
                    {
                        if (TopLevel.GetTopLevel(p) is not Window owner) return;
                        int choice = await Dialogs.Ask(owner, "Remove " + a.UserName, $"Remove {a.UserName}'s account? Their home folder can be kept or deleted.",
                            "Keep their files", "Delete their files", "Cancel");
                        if (choice is 0 or 1) await Run(UserAccounts.DeleteAsync(a, choice == 1), "Removed.");
                    }));
                }
                list.Children.Add(Card(new StackPanel { Spacing = 4, Children = { name, sub, buttons, status } }));
            }
        }
        return p;
    }
}
