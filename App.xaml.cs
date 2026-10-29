using System;
using System.Globalization;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using XrayMetro.Models;
using XrayMetro.Services;
using XrayMetro.ViewModels;
using XrayMetro.Views;

namespace XrayMetro;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsMutex, _exiting;
    private int _fatal;
    private readonly SystemProxyService _proxy = new();
    private XrayProcessService? _core;
    private MainViewModel? _viewModel;
    private TrayService? _tray;
    private MainWindow? _window;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--watchdog")
        {
            if (int.TryParse(e.Args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) &&
                long.TryParse(e.Args[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
                await SystemProxyService.RunWatchdogAsync(pid, ticks);
            Shutdown(); return;
        }
        _instance = new Mutex(false, @"Local\XrayMetro.Instance." + WindowsIdentity.GetCurrent().User!.Value);
        try { _ownsMutex = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        if (!_ownsMutex)
        {
            MessageBox.Show(LocalizationService.Get("AlreadyRunning"), "Win10ray", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, _) => EmergencyCleanup();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyCleanup();
        TaskScheduler.UnobservedTaskException += (_, args) => { args.SetObserved(); Dispatcher.BeginInvoke(new Action(() => FatalExit())); };
        SessionEnding += (_, _) => { if (_window is not null) _window.AllowClose = true; EmergencyCleanup(); };
        try
        {
            await Task.Run(_proxy.RecoverIfNeeded);
            var settings = new SettingsService();
            AppState state;
            try { state = await settings.LoadAsync(); }
            catch (ProfileLockedException exception)
            {
                if (MessageBox.Show(exception.Message, "Win10ray", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                { Shutdown(); return; }
                await settings.ResetLockedProfileAsync();
                state = new AppState();
                await settings.SaveAsync(state);
            }
            ThemeService.Apply(state.Settings.DarkTheme, "#0078D7");
            _core = new XrayProcessService();
            _viewModel = new MainViewModel(state, settings, _core, _proxy);
            _viewModel.UpdateReadyToExit += RequestExit;
            _ = UpdateService.CleanupCompletedAsync();
            _window = new MainWindow(_viewModel);
            MainWindow = _window;
            _window.ExitRequested += RequestExit;
            _tray = new TrayService(_window, _viewModel, RequestExit);
            if (!state.Settings.StartMinimized) _window.Show();
            await _viewModel.InitializeAsync();
        }
        catch (Exception)
        {
            EmergencyCleanup();
            MessageBox.Show(LocalizationService.Get("StartupFailed"), "Win10ray", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public async void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        try { if (_viewModel is not null) await _viewModel.ShutdownAsync(); }
        catch { EmergencyCleanup(); }
        finally
        {
            _tray?.Dispose();
            if (_window is not null) { _window.AllowClose = true; _window.Close(); }
            Shutdown();
        }
    }
    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        FatalExit();
    }
    private void FatalExit()
    {
        if (Interlocked.Exchange(ref _fatal, 1) != 0) return;
        EmergencyCleanup();
        MessageBox.Show(LocalizationService.Get("FatalError"), "Win10ray", MessageBoxButton.OK, MessageBoxImage.Error);
        _tray?.Dispose();
        Shutdown(1);
    }
    private void EmergencyCleanup()
    {
        try { _viewModel?.EmergencyStop(); } catch { }
        // Job object also closes on forced process termination; the independent watchdog clears the proxy.
        try { _core?.EmergencyStop(); } catch { }
        _proxy.EmergencyDisable();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            EmergencyCleanup();
            _tray?.Dispose(); _core?.Dispose();
            _instance?.ReleaseMutex();
        }
        _instance?.Dispose();
        base.OnExit(e);
    }
}
