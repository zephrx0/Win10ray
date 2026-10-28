using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace XrayMetro.Views;

/// <summary>Centers the visible outline of an MDL2 symbol independently of its font bearings.</summary>
public sealed class GlyphIcon : FrameworkElement
{
    private static readonly Typeface IconTypeface = new("Segoe MDL2 Assets");
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(GlyphIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OutlineChanged));
    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(GlyphIcon),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OutlineChanged));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(GlyphIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    private Geometry? _outline;

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private static void OutlineChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((GlyphIcon)target)._outline = null;

    protected override Size MeasureOverride(Size availableSize) => new(FontSize, FontSize);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _outline = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (string.IsNullOrEmpty(Glyph)) return;
        if (_outline is null)
        {
            _outline = new FormattedText(Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconTypeface, FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip).BuildGeometry(new Point());
            _outline.Freeze();
        }
        Rect ink = _outline.Bounds;
        if (ink.IsEmpty) return;
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - ink.Width) / 2 - ink.X, (ActualHeight - ink.Height) / 2 - ink.Y));
        drawingContext.DrawGeometry(Foreground, null, _outline);
        drawingContext.Pop();
    }
}
