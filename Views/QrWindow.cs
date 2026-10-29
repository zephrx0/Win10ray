using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using XrayMetro.Services;

namespace XrayMetro.Views;

public sealed class QrWindow : Window
{
    private byte[]? _png;
    private string _link;
    private QrWindow(string link, byte[] png)
    {
        _link = link; _png = png;
        Title = LocalizationService.Get("ShowQr"); Width = 430; Height = 500; MinWidth = 340; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Segoe UI");
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "ForegroundBrush");
        Grid grid = new() { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TextBlock heading = new() { Text = "QR", FontSize = 28, Margin = new Thickness(0, 0, 0, 16) }; grid.Children.Add(heading);
        BitmapImage image = new(); using (var stream = new MemoryStream(png)) { image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); }
        Image view = new() { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 20) }; Grid.SetRow(view, 1); grid.Children.Add(view);
        WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Center };
        Button copy = new() { Content = LocalizationService.Get("CopyLink"), Margin = new Thickness(0, 0, 8, 8) };
        copy.Click += (_, _) => { try { Clipboard.SetText(_link); copy.Content = LocalizationService.Get("Copied"); } catch { MessageBox.Show(this, LocalizationService.Get("ClipboardBusy"), "Win10ray"); } };
        Button save = new() { Content = LocalizationService.Get("SavePng"), Margin = new Thickness(0, 0, 0, 8) };
        save.Click += async (_, _) =>
        {
            SaveFileDialog dialog = new() { Filter = "PNG|*.png", FileName = "Win10ray-QR.png", DefaultExt = ".png" };
            if (dialog.ShowDialog(this) != true || _png is null) return;
            try { await File.WriteAllBytesAsync(dialog.FileName, _png); } catch { MessageBox.Show(this, LocalizationService.Get("OperationFailed"), "Win10ray"); }
        };
        buttons.Children.Add(copy); buttons.Children.Add(save); Grid.SetRow(buttons, 2); grid.Children.Add(buttons); Content = grid;
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Closed += (_, _) => { view.Source = null; _link = ""; if (_png is not null) Array.Clear(_png); _png = null; };
    }
    public static async Task ShowAsync(string link)
    {
        Window? owner = Application.Current.MainWindow;
        if (MessageBox.Show(owner, LocalizationService.Get("QrWarning"), "Win10ray", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        byte[] png = await Task.Run(() => QrCodeService.CreatePng(link));
        new QrWindow(link, png) { Owner = owner }.ShowDialog();
    }
}
