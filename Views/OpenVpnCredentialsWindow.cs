using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XrayMetro.Services;

namespace XrayMetro.Views;

public sealed record OpenVpnCredentials(string Username, string Password, bool Remember);
public sealed class OpenVpnAuthenticationCancelledException : OperationCanceledException;

public sealed class OpenVpnCredentialsWindow : Window
{
    private OpenVpnCredentials? _result;
    private Button _confirm = null!;
    private OpenVpnCredentialsWindow(string username)
    {
        Title = "OpenVPN"; MaxWidth = Math.Max(280, SystemParameters.WorkArea.Width - 48); Width = Math.Min(380, MaxWidth); MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 48); SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "ForegroundBrush");
        StackPanel panel = new() { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = LocalizationService.Get("OpenVpnCredentials"), FontSize = 22, Margin = new Thickness(0, 0, 0, 20) });
        panel.Children.Add(new TextBlock { Text = LocalizationService.Get("Username"), Margin = new Thickness(0, 0, 0, 6) });
        TextBox login = new() { Text = username, MaxLength = 1024, Margin = new Thickness(0, 0, 0, 16) }; panel.Children.Add(login);
        panel.Children.Add(new TextBlock { Text = LocalizationService.Get("Password"), Margin = new Thickness(0, 0, 0, 6) });
        PasswordBox password = new() { MaxLength = 1024, Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 16), FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };
        password.SetResourceReference(BackgroundProperty, "InputBrush"); password.SetResourceReference(ForegroundProperty, "ForegroundBrush"); password.SetResourceReference(BorderBrushProperty, "BorderBrush"); panel.Children.Add(password);
        System.Windows.Controls.Primitives.ToggleButton remember = new() { Content = LocalizationService.Get("RememberCredentials"), Margin = new Thickness(0, 0, 0, 20) }; remember.SetResourceReference(ForegroundProperty, "ForegroundBrush"); panel.Children.Add(remember);
        Button connect = new() { Content = LocalizationService.Get("Connect"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        _confirm = connect;
        connect.Click += (_, _) => { if (login.Text.Trim().Length == 0 || password.Password.Length == 0) return; _result = new(login.Text.Trim(), password.Password, remember.IsChecked == true); DialogResult = true; };
        panel.Children.Add(connect); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Closed += (_, _) => password.Clear();
    }
    public static OpenVpnCredentials Prompt(string username, CancellationToken token, bool editing = false)
    {
        token.ThrowIfCancellationRequested();
        OpenVpnCredentialsWindow window = new(username) { Owner = Application.Current.MainWindow };
        if (editing) window._confirm.Content = LocalizationService.Get("Save");
        using CancellationTokenRegistration registration = token.Register(() => window.Dispatcher.BeginInvoke(new Action(() => { if (window.IsLoaded) window.Close(); })));
        window.ShowDialog();
        token.ThrowIfCancellationRequested();
        return window._result ?? throw new OpenVpnAuthenticationCancelledException();
    }
}
