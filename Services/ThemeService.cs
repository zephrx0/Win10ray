using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace XrayMetro.Services;

public static class ThemeService
{
    public static void Apply(bool dark, string accent)
    {
        Application app = Application.Current;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Apply(dark, accent));
            return;
        }
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(accent); }
        catch (FormatException) { color = Color.FromRgb(0, 120, 215); }
        if (!dark && color == Color.FromRgb(0, 120, 215)) color = Color.FromRgb(0, 103, 184);
        var theme = new ResourceDictionary { Source = new Uri($"/Win10ray;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        ResourceDictionary? previous = app.Resources.MergedDictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) == true ||
            dictionary.Source?.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (previous is null) app.Resources.MergedDictionaries.Insert(0, theme);
        else app.Resources.MergedDictionaries[app.Resources.MergedDictionaries.IndexOf(previous)] = theme;
        var accentBrush = new SolidColorBrush(color);
        accentBrush.Freeze();
        app.Resources["AccentBrush"] = accentBrush;
        double brightness = (color.R * 299 + color.G * 587 + color.B * 114) / 1000d;
        app.Resources["AccentForegroundBrush"] = brightness > 165 ? Brushes.Black : Brushes.White;
    }
}
