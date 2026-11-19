using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Wi-Fi scan and connect through NetworkManager (nmcli).</summary>
public partial class ConnectNetworkWindow : Window
{
    private sealed record Wifi(string Ssid, string Signal, string Security)
    {
        public override string ToString() => $"{Ssid}    {Signal}%    {(string.IsNullOrEmpty(Security) || Security == "--" ? "open" : Security)}";
    }

    public ConnectNetworkWindow()
    {
        InitializeComponent();
        Opened += (_, _) => OnScan(null, new RoutedEventArgs());
    }

    private static string? Nmcli => ProcessUtil.Which("nmcli");

    private async void OnScan(object? s, RoutedEventArgs e)
    {
        if (Nmcli is not { } nmcli) { StatusLabel.Text = "nmcli (NetworkManager) was not found on this system."; return; }
        StatusLabel.Text = "Scanning...";
        var (code, output) = await ProcessUtil.RunAsync(nmcli, new[] { "-t", "-f", "SSID,SIGNAL,SECURITY", "dev", "wifi", "list", "--rescan", "yes" }, 20000);
        if (code != 0) { StatusLabel.Text = "Scan failed: " + output; return; }

        var list = new List<Wifi>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // -t output separates fields with ':' (a literal colon inside the SSID is escaped as \:)
            var parts = line.Replace("\\:", "\u0001").Split(':');
            if (parts.Length < 3) continue;
            var ssid = parts[0].Replace("\u0001", ":");
            if (ssid.Length == 0) continue;
            list.Add(new Wifi(ssid, parts[1], parts[2]));
        }
        Networks.ItemsSource = list.GroupBy(w => w.Ssid).Select(g => g.OrderByDescending(w => int.TryParse(w.Signal, out var sig) ? sig : 0).First()).ToList();
        StatusLabel.Text = list.Count == 0 ? "No networks found." : $"{list.Count} network(s) found.";
    }

    private async void OnConnect(object? s, RoutedEventArgs e)
    {
        if (Networks.SelectedItem is not Wifi wifi) { StatusLabel.Text = "Select a network first."; return; }
        if (Nmcli is not { } nmcli) return;

        var args = new List<string> { "dev", "wifi", "connect", wifi.Ssid };
        if (!string.IsNullOrEmpty(PasswordBox.Text)) { args.Add("password"); args.Add(PasswordBox.Text!); }
        ConnectButton.IsEnabled = false;
        StatusLabel.Text = "Connecting...";
        var (code, output) = await ProcessUtil.RunAsync(nmcli, args, 45000);
        ConnectButton.IsEnabled = true;
        StatusLabel.Text = code == 0 ? "Connected." : "Could not connect: " + output;
        if (code == 0) Notifier.Show("Network", "Connected to " + wifi.Ssid);
    }
}
