using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>XAML access to the vector icons: &lt;v:IconView Kind="settings" Size="18" /&gt;.</summary>
public sealed class IconView : Decorator
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<IconView, string?>(nameof(Kind));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<IconView, double>(nameof(Size), 18);
    public static readonly StyledProperty<IBrush?> BrushProperty = AvaloniaProperty.Register<IconView, IBrush?>(nameof(Brush));

    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public IBrush? Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == SizeProperty || change.Property == BrushProperty)
            Child = Kind == null ? null : Icons.Glyph(Kind, Size, Brush);
    }
}
