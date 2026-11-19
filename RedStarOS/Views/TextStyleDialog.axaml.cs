using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace RedStarOS.Views;

/// <summary>Font / colour picker used by the Writer (replaces WinForms FontDialog and ColorDialog).</summary>
public partial class TextStyleDialog : Window
{
    private static readonly (string Name, string Hex)[] Palette =
    {
        ("Black", "#000000"), ("Dark red", "#8B0000"), ("Red", "#E00000"), ("Orange", "#FF8C00"),
        ("Gold", "#DAA520"), ("Green", "#008000"), ("Teal", "#008080"), ("Blue", "#0000CD"),
        ("Purple", "#800080"), ("Gray", "#808080"), ("White", "#FFFFFF")
    };

    private const string KeepCurrent = "(keep current font)";

    public bool Accepted { get; private set; }
    public string? SelectedFamily { get; private set; }
    public double SelectedSize { get; private set; }
    public Color SelectedColor { get; private set; }

    public TextStyleDialog() : this(false, "", 16, Colors.Black) { }

    public TextStyleDialog(bool colourMode, string currentFamily, double currentSize, Color currentColor)
    {
        InitializeComponent();
        Title = colourMode ? "Font Color" : "Font Size";
        FontPanel.IsVisible = !colourMode;
        ColorPanel.IsVisible = colourMode;

        var families = new List<string>();
        try { families = FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n).ToList(); }
        catch { /* fall through to defaults */ }
        if (families.Count == 0) families = new List<string> { "sans-serif", "serif", "monospace" };
        families.Insert(0, KeepCurrent);
        FamilyBox.ItemsSource = families;
        FamilyBox.SelectedIndex = 0;
        SizeBox.Value = (decimal)currentSize;

        ColorBox.ItemsSource = Palette.Select(p => p.Name).ToList();
        ColorBox.SelectedIndex = 0;
        SelectedColor = currentColor;
        SelectedFamily = null;
        SelectedSize = currentSize;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var chosen = FamilyBox.SelectedItem as string;
        SelectedFamily = chosen == null || chosen == KeepCurrent ? null : chosen;
        SelectedSize = (double)(SizeBox.Value ?? (decimal)SelectedSize);

        if (ColorPanel.IsVisible)
        {
            if (!string.IsNullOrWhiteSpace(HexBox.Text) && Color.TryParse(HexBox.Text.Trim(), out var custom))
                SelectedColor = custom;
            else if (ColorBox.SelectedIndex >= 0)
                SelectedColor = Color.Parse(Palette[ColorBox.SelectedIndex].Hex);
        }

        Accepted = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
