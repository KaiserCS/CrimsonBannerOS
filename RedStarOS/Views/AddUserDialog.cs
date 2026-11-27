// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>System Settings → Users → Add a user.</summary>
public sealed class AddUserDialog : Window
{
    private readonly TextBox _real = new() { PlaceholderText = "Full name" };
    private readonly TextBox _user = new() { PlaceholderText = "User name (lower case)" };
    private readonly TextBox _pw = new() { PlaceholderText = "Password", PasswordChar = '●' };
    private readonly TextBox _pw2 = new() { PlaceholderText = "Password again", PasswordChar = '●' };
    private readonly CheckBox _admin = new() { Content = "Administrator (can install software and change the system)" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _ok = new() { Content = "Add user", Classes = { "primary" }, IsDefault = true };
    private bool _userEdited;

    public bool Added { get; private set; }

    public AddUserDialog()
    {
        Title = "Add a user";
        Width = 420; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _real.TextChanged += (_, _) => { if (!_userEdited) _user.Text = UserAccounts.SuggestUserName(_real.Text ?? ""); };
        _user.KeyDown += (_, _) => _userEdited = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        _ok.Click += async (_, _) => await Add();
        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Add a user", FontSize = 18, FontWeight = FontWeight.SemiBold },
                Labelled("Full name", _real), Labelled("User name, in lower case", _user), Labelled("Password", _pw), Labelled("The password again", _pw2),
                _admin, _error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _ok, cancel } },
            },
        };
        _error.Bind(TextBlock.ForegroundProperty, _error.GetResourceObservable("ShellDangerBrush"));
        Opened += (_, _) => _real.Focus();
    }

    /// <summary>A field with its name above it (a placeholder alone goes as soon as you type).</summary>
    private static Control Labelled(string label, TextBox box)
    {
        box.PlaceholderText = null;
        Avalonia.Automation.AutomationProperties.SetName(box, label);
        return new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, Classes = { "caption" } }, box } };
    }

    private async Task Add()
    {
        var user = (_user.Text ?? "").Trim();
        string? problem = !UserAccounts.ValidUserName(user) ? "The user name needs lower-case letters and digits, starting with a letter."
            : (_pw.Text ?? "").Length < 4 ? "Choose a password of at least 4 characters."
            : _pw.Text != _pw2.Text ? "The two passwords aren't the same." : null;
        if (problem != null) { _error.Text = problem; _error.IsVisible = true; return; }
        _ok.IsEnabled = false;
        _error.IsVisible = false;
        var err = await UserAccounts.CreateAsync(user, (_real.Text ?? "").Trim(), _admin.IsChecked == true, _pw.Text ?? "");
        _ok.IsEnabled = true;
        if (err != null) { _error.Text = "Couldn't add the user: " + err; _error.IsVisible = true; return; }
        Added = true;
        Close();
    }
}
