using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XrayMetro.Views;

public sealed class ConnectionSpinner : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(ConnectionSpinner), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(ConnectionSpinner), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(nameof(Angle), typeof(double), typeof(ConnectionSpinner), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private bool _animating;
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Angle => (double)GetValue(AngleProperty);
    public ConnectionSpinner()
    {
        Loaded += (_, _) => Update(); Unloaded += (_, _) => Update(); IsVisibleChanged += (_, _) => Update();
    }
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((ConnectionSpinner)target).Update();
    private void Update()
    {
        bool run = IsActive && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation;
        if (_animating == run) return;
        _animating = run;
        if (run)
        {
            var animation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(850)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(animation, 30); BeginAnimation(AngleProperty, animation);
        }
        else BeginAnimation(AngleProperty, null);
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (!IsActive || ActualWidth < 4 || ActualHeight < 4) return;
        double radius = Math.Min(ActualWidth, ActualHeight) / 2 - 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var pen = new Pen(Foreground, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        context.PushOpacity(0.18); context.DrawEllipse(null, pen, center, radius, radius); context.Pop();
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            double radians = 255 * Math.PI / 180;
            path.ArcTo(new Point(center.X + Math.Sin(radians) * radius, center.Y - Math.Cos(radians) * radius), new Size(radius, radius), 0, true, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        context.PushTransform(new RotateTransform(Angle, center.X, center.Y)); context.DrawGeometry(null, pen, geometry); context.Pop();
    }
}
