using System.Windows;
using System.Windows.Controls;

namespace XrayMetro.Views;

public sealed class SettingsRow : ContentControl
{
    private static readonly DependencyPropertyKey IsStackedPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsStacked), typeof(bool), typeof(SettingsRow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsStackedProperty = IsStackedPropertyKey.DependencyProperty;
    public bool IsStacked => (bool)GetValue(IsStackedProperty);
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SettingsRow), new PropertyMetadata(""));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        SetValue(IsStackedPropertyKey, constraint.Width < 420);
        return base.MeasureOverride(constraint);
    }
}
