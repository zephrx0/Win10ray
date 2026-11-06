using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace XrayMetro.Views;

/// <summary>Let DWM animate the frame; fade content only when opening or returning from the tray.</summary>
public static class WindowMotion
{
    public static void Attach(Window window) => _ = new Controller(window);

    private sealed class Controller
    {
        private readonly Window _window;
        private WindowState _previousState;
        private int _generation;

        public Controller(Window window)
        {
            _window = window;
            _previousState = window.WindowState;
            window.SourceInitialized += OnSourceInitialized;
            window.Loaded += OnLoaded;
            window.IsVisibleChanged += OnVisibleChanged;
            window.StateChanged += OnStateChanged;
            window.Closed += OnClosed;
        }

        private void OnSourceInitialized(object? sender, EventArgs args)
        {
            // Keep native open/minimize/maximize/restore transitions, including keyboard and caption actions.
            int forceDisable = 0;
            DwmSetWindowAttribute(new WindowInteropHelper(_window).Handle, 3, ref forceDisable, sizeof(int));
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => FadeIn();

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (args.NewValue is true && _window.IsLoaded) FadeIn();
            else if (args.NewValue is false) Stop();
        }

        private void OnStateChanged(object? sender, EventArgs args)
        {
            WindowState previous = _previousState;
            _previousState = _window.WindowState;
            if (previous == WindowState.Minimized) FadeIn();
        }

        private void FadeIn()
        {
            if (!_window.IsVisible || _window.WindowState == WindowState.Minimized ||
                !SystemParameters.ClientAreaAnimation || _window.Content is not UIElement content) return;
            // Animate the rendered content, never HWND opacity or layout dimensions. DWM keeps the frame smooth.
            int generation = ++_generation;
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += (_, _) =>
            {
                if (_generation == generation) content.BeginAnimation(UIElement.OpacityProperty, null);
            };
            content.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private void Stop()
        {
            ++_generation;
            if (_window.Content is UIElement content) content.BeginAnimation(UIElement.OpacityProperty, null);
        }

        private void OnClosed(object? sender, EventArgs args)
        {
            Stop();
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Loaded -= OnLoaded;
            _window.IsVisibleChanged -= OnVisibleChanged;
            _window.StateChanged -= OnStateChanged;
            _window.Closed -= OnClosed;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
