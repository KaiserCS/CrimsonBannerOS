using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Account Settings: profile photo, lock-screen password and account type.</summary>
public partial class AccountWindow : Window
{
    public AccountWindow()
    {
        InitializeComponent();
        UserLabel.Text = $"{Environment.UserName}@{Environment.MachineName}";

        var s = SettingsStore.Current;
        AdminRadio.IsChecked = s.AccountType == "Admin";
        DevRadio.IsChecked = s.AccountType == "Developer";
        ChildRadio.IsChecked = s.AccountType == "Child";

        if (!string.IsNullOrEmpty(s.AvatarPath) && File.Exists(s.AvatarPath))
        {
            try { using var fs = File.OpenRead(s.AvatarPath); Avatar.Source = new Bitmap(fs); }
            catch { /* keep default */ }
        }
    }

    private async void OnChangePhoto(object? s, RoutedEventArgs e)
    {
        var files = await FileDialogs.Open(this, new FilePickerOpenOptions
        {
            Title = "Change Profile Photo",
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);

            // Keep a private copy so the photo survives the original being moved.
            var ext = Path.GetExtension(files[0].Name);
            var dest = AppPaths.ConfigFile("avatar" + (string.IsNullOrEmpty(ext) ? ".png" : ext));
            await File.WriteAllBytesAsync(dest, ms.ToArray());

            ms.Position = 0;
            Avatar.Source = new Bitmap(ms);
            SettingsStore.Current.AvatarPath = dest;
            SettingsStore.Save();
            StatusLabel.Text = "Profile photo updated.";
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Profile Photo", "Could not load that image: " + ex.Message);
        }
    }

    private void OnChangePassword(object? s, RoutedEventArgs e)
    {
        if (!PasswordUtil.Verify(CurrentBox.Text))
        {
            StatusLabel.Text = "Current password is incorrect.";
            return;
        }
        if ((NewBox.Text ?? "") != (RetypeBox.Text ?? ""))
        {
            StatusLabel.Text = "The new passwords do not match.";
            return;
        }
        PasswordUtil.Set(NewBox.Text);
        CurrentBox.Text = NewBox.Text = RetypeBox.Text = "";
        StatusLabel.Text = PasswordUtil.HasPassword ? "Password changed." : "Password removed (the lock screen accepts any input).";
    }

    private void OnChangeType(object? s, RoutedEventArgs e)
    {
        var type = ChildRadio.IsChecked == true ? "Child" : DevRadio.IsChecked == true ? "Developer" : "Admin";
        SettingsStore.Current.AccountType = type;
        SettingsStore.Save();
        StatusLabel.Text = type == "Child"
            ? "Account type: Child. Online apps and installed Linux apps are hidden from the Start menu."
            : $"Account type: {type}.";
    }
}
