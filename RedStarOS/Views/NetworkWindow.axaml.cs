using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Shows this machine's real network interfaces.</summary>
public partial class NetworkWindow : Window
{
    public NetworkWindow()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        HostLabel.Text = "Computer name: " + System.Environment.MachineName;
        var rows = SystemInfo.NetworkInterfaces()
            .Select(n => $"{(n.Up ? "●" : "○")}  {n.Name}  ({n.Type})   IPv4: {n.Ipv4}   MAC: {n.Mac}")
            .ToList();
        if (rows.Count == 0) rows.Add("No network interfaces found.");
        Interfaces.ItemsSource = rows;
    }

    private void OnRefresh(object? s, RoutedEventArgs e) => Refresh();
    private void OnWifi(object? s, RoutedEventArgs e) => WindowManager.Open(new ConnectNetworkWindow());

    private void OnSystemSettings(object? s, RoutedEventArgs e)
    {
        foreach (var tool in new[] { "nm-connection-editor", "gnome-control-center", "systemsettings", "systemsettings5", "xfce4-settings-manager" })
        {
            var path = ProcessUtil.Which(tool);
            if (path == null) continue;
            var args = tool == "gnome-control-center" ? new[] { "network" } : System.Array.Empty<string>();
            ProcessUtil.StartDetached(path, args);
            return;
        }
        Notifier.Show("Network", "No system network settings tool was found.");
    }
}
