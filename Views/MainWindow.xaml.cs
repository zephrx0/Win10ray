using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using XrayMetro.Services;
using XrayMetro.ViewModels;

namespace XrayMetro.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    public event Action? ExitRequested;
    public bool AllowClose { get; set; }
    private HwndSource? _source;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Icon = (System.Windows.Media.ImageSource)FindResource("BrandMark");
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => { viewModel.PropertyChanged -= OnViewModelChanged; _source?.RemoveHook(WindowMessage); };
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        ConnectionHeader.SizeChanged += (_, _) => UpdateConnectionLayout();
    }

    private void OpenVpnDragOver(object sender, DragEventArgs args)
    {
        string[]? files = args.Data.GetData(DataFormats.FileDrop) as string[];
        args.Effects = files is { Length: > 0 } && Array.TrueForAll(files, file => file.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase)) && _viewModel.ImportOpenVpnFileCommand.CanExecute(files) ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }
    private void OpenVpnDrop(object sender, DragEventArgs args)
    {
        if (args.Data.GetData(DataFormats.FileDrop) is string[] files && Array.TrueForAll(files, file => file.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase)) && _viewModel.ImportOpenVpnFileCommand.CanExecute(files))
            _viewModel.ImportOpenVpnFileCommand.Execute(files);
        args.Handled = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowMessage);
        FitToWorkArea();
    }


    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF applies the monitor's DPI. Only reflow logical layout, never scale it twice.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { FitToWorkArea(); UpdateResponsiveLayout(); }));
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007E) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitToWorkArea));
        return IntPtr.Zero;
    }

    private void FitToWorkArea()
    {
        IntPtr monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double availableWidth = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX;
        double availableHeight = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY;
        MinWidth = Math.Min(420, availableWidth); MinHeight = Math.Min(320, availableHeight);
        if (WindowState != WindowState.Normal) return;
        Width = Math.Min(Width, availableWidth); Height = Math.Min(Height, availableHeight);
    }

    private void UpdateResponsiveLayout()
    {
        bool compact = ActualWidth < 660;
        SidebarColumn.Width = new GridLength(compact ? 64 : ActualWidth < 800 ? 200 : 240);
        BrandText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (TextBlock text in FindVisualChildren<TextBlock>(Sidebar))
            if (Grid.GetColumn(text) == 1) text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (Button button in FindVisualChildren<Button>(Sidebar))
        {
            button.Padding = new Thickness(compact ? 12 : 16, 0, compact ? 12 : 16, 0);
            button.ToolTip = System.Windows.Automation.AutomationProperties.GetName(button);
        }
        PageHost.Margin = compact ? new Thickness(20, 20, 12, 20) : new Thickness(28, 24, 20, 24);
        UpdateConnectionLayout();
    }

    private void UpdateConnectionLayout()
    {
        bool compact = ConnectionHeader.ActualWidth < 420;
        Grid.SetRow(ConnectionButton, compact ? 1 : 0);
        Grid.SetColumn(ConnectionButton, compact ? 0 : 1);
        Grid.SetColumnSpan(ConnectionButton, compact ? 2 : 1);
        Grid.SetColumnSpan(StatusPanel, compact ? 2 : 1);
        StatusPanel.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        ConnectionButton.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        ConnectionButton.Margin = compact ? new Thickness(0, 16, 0, 0) : new Thickness(16, 0, 0, 0);
        ConnectionButton.Width = ConnectionHeader.ActualWidth > 0 ? Math.Min(180, ConnectionHeader.ActualWidth) : 180;
    }

    private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T result) yield return result;
            foreach (T descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    private void MoveNavigationIndicator()
    {
        double position = _viewModel.SelectedPage * 48;
        if (SidebarSelection.RenderTransform is not TranslateTransform movement) return;
        if (!SystemParameters.ClientAreaAnimation) { movement.Y = position; return; }
        movement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(position, TimeSpan.FromMilliseconds(190)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.SelectedPage))
        {
            MoveNavigationIndicator();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                foreach (UIElement child in PageHost.Children)
                    if (child is ScrollViewer { IsVisible: true } page) page.ScrollToTop();
                foreach (Button button in FindVisualChildren<Button>(Sidebar))
                    if (button.CommandParameter?.ToString() == _viewModel.SelectedPage.ToString(CultureInfo.InvariantCulture)) button.BringIntoView();
            }));
        }
        if (args.PropertyName == nameof(MainViewModel.StatusText) && ConnectionStatus.IsVisible && SystemParameters.ClientAreaAnimation)
            ConnectionStatus.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop });
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (ShowActivated) Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        if (_viewModel.CloseToTray) Hide();
        else ExitRequested?.Invoke();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is not null) MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray) Hide();
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void ExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
    private void LogsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { IsKeyboardFocusWithin: false } textBox) textBox.ScrollToEnd();
    }
}

public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int page && int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int expected) && page == expected ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
