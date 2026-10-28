using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XrayMetro.Views;

public static class MetroMotion
{
    public static readonly DependencyProperty AnimateProperty = DependencyProperty.RegisterAttached("Animate", typeof(bool), typeof(MetroMotion), new PropertyMetadata(false, OnChanged));
    public static void SetAnimate(DependencyObject target, bool value) => target.SetValue(AnimateProperty, value);
    public static bool GetAnimate(DependencyObject target) => (bool)target.GetValue(AnimateProperty);
    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)args.NewValue) { element.IsVisibleChanged += OnVisible; element.Loaded += OnLoaded; }
        else { element.IsVisibleChanged -= OnVisible; element.Loaded -= OnLoaded; }
    }
    private static void OnLoaded(object sender, RoutedEventArgs args) { if (sender is FrameworkElement element && element.IsVisible) Run(element); }
    private static void OnVisible(object sender, DependencyPropertyChangedEventArgs args) { if (args.NewValue is true && sender is FrameworkElement element && element.IsLoaded) Run(element); }
    private static void Run(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var movement = new TranslateTransform(); element.RenderTransform = movement;
        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop, EasingFunction = easing });
        movement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { FillBehavior = FillBehavior.Stop, EasingFunction = easing });
    }
}
