using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using XrayMetro.Models;
using XrayMetro.Services;

namespace XrayMetro.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly SubscriptionService _subscriptions;
    private readonly AutoStartService _autoStart = new();
    private readonly XrayProcessService _core;
    private readonly OpenVpnProcessService _openVpn = new();
    private readonly Dictionary<string, (string Username, string Password)> _openVpnSessionCredentials = new(StringComparer.Ordinal);
    private readonly ISystemProxyService _proxy;
    private readonly ITunSessionService _tun;
    private readonly ServerHealthService _health;
    private readonly TimeSpan _healthInterval;
    private readonly AppState _state;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _autoStartGate = new(1, 1);
    private readonly ConcurrentQueue<string> _pendingLogs = new();
    private readonly Queue<string> _logLines = new();
    private readonly DispatcherTimer _logTimer;
    private CancellationTokenSource? _statsCancellation;
    private Task? _statsTask;
    private CancellationTokenSource? _healthCancellation, _connectCancellation;
    private CancellationTokenSource? _startupCancellation;
    private Exception? _lastConnectionError;
    private Task? _healthTask;
    private int _sessionGeneration, _serversTab;
    private long _trafficCarry, _sessionBytes;
    private string _searchText = "", _coreVersion = "...";
    private ServerProfile? _selectedServer;
    private SubscriptionProfile? _selectedSubscription;
    private bool _isConnected, _isBusy, _closing, _isRenaming;
    private int _selectedPage, _autoStartVersion;
    private string _message = "", _logs = "", _renameText = "", _subscriptionUrl = "";
    private bool _isUpdating;
    private double _updateProgress;
    private string _updateStatus = "";
    private ClientRelease? _clientRelease;
    public event Action? UpdateReadyToExit;
    private string _statusText = LocalizationService.Get("Disconnected"), _statusDetail = "";
    private string _uploadSpeed = FormatBytes(0) + LocalizationService.Pick("/с", "/s"), _downloadSpeed = FormatBytes(0) + LocalizationService.Pick("/с", "/s"), _sessionTraffic = FormatBytes(0);

    public MainViewModel(AppState state, SettingsService settingsService, XrayProcessService core, ISystemProxyService proxy, ServerHealthService? health = null, TimeSpan? healthInterval = null, ITunSessionService? tun = null)
    {
        _state = state; _settingsService = settingsService; _core = core; _proxy = proxy;
        _subscriptions = new SubscriptionService(core.HttpPort);
        _health = health ?? new ServerHealthService();
        _tun = tun ?? new TunServiceClient();
        _healthInterval = healthInterval ?? TimeSpan.FromSeconds(15);
        if (_healthInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(healthInterval));
        _state.Settings.UseSystemProxy = !_state.Settings.UseTun;
        Servers = new(state.Servers); Subscriptions = new(state.Subscriptions);
        ServerView = new ListCollectionView(Servers);
        ServerView.Filter = item => item is ServerProfile server && (SearchText.Length == 0 || server.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) || server.Address.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || server.GroupName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
        ServerView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ServerProfile.GroupName)));
        UpdateServerGroups();
        _selectedServer = Servers.FirstOrDefault(s => s.Id == state.Settings.LastServerId) ?? Servers.FirstOrDefault();
        _renameText = _selectedServer?.Name ?? "";
        _selectedSubscription = Subscriptions.FirstOrDefault();
        NavigateCommand = new(p => { if (int.TryParse(p?.ToString(), out int page)) SelectedPage = page; });
        SelectServersTabCommand = new(p => { if (int.TryParse(p?.ToString(), out int tab) && tab is 0 or 1) ServersTab = tab; });
        CancelConnectionCommand = new(_ => { _state.Settings.ResumeConnection = false; _openVpnSessionCredentials.Clear(); _connectCancellation?.Cancel(); _startupCancellation?.Cancel(); Persist(); }, _ => IsBusy && (_connectCancellation is not null || _startupCancellation is not null));
        StartRenameCommand = new(_ => IsRenaming = true, _ => CanEditConnection && SelectedServer is not null);
        CancelRenameCommand = new(_ => { IsRenaming = false; RenameText = SelectedServer?.Name ?? ""; });
        ToggleConnectionCommand = new(_ => ToggleConnectionAsync(), ReportError, _ => !IsBusy && !IsRefreshingSubscriptions && (IsConnected || SelectedServer is not null));
        ImportClipboardCommand = new(_ => ImportClipboardAsync(), ReportError, _ => CanEditConnection);
        ImportOpenVpnFileCommand = new(p => ImportOpenVpnFilesAsync(p as string[]), ReportError, _ => CanEditConnection);
        EditOpenVpnCredentialsCommand = new(p => EditOpenVpnCredentialsAsync(p as ServerProfile), ReportError, p => CanEditConnection && p is ServerProfile { Protocol: "openvpn" });
        ExportOpenVpnCommand = new(p => ExportOpenVpnAsync(p as ServerProfile), ReportError, p => p is ServerProfile { Protocol: "openvpn" });
        PingAllCommand = new(_ => PingAllAsync(), ReportError, _ => Servers.Count > 0);
        DeleteServerCommand = new(_ => DeleteServerAsync(), ReportError, _ => CanEditConnection && SelectedServer is not null);
        RenameServerCommand = new(_ => RenameServerAsync(), ReportError, _ => CanEditConnection && SelectedServer is not null);
        AddSubscriptionCommand = new(_ => AddSubscriptionAsync(), ReportError, _ => CanEditConnection);
        UpdateSubscriptionsCommand = new(_ => RefreshSubscriptionsAsync(null, false), ReportError, _ => !IsRefreshingSubscriptions && !IsBusy && !_closing && Subscriptions.Count > 0);
        UpdateSubscriptionCommand = new(p => RefreshSubscriptionsAsync(p as SubscriptionProfile, false), ReportError, p => p is SubscriptionProfile s && !s.IsUpdating && !IsRefreshingSubscriptions && !IsBusy && !_closing);
        DeleteSubscriptionCommand = new(_ => DeleteSubscriptionAsync(), ReportError, _ => CanEditConnection && SelectedSubscription is not null);
        CopyLogsCommand = new(_ => { try { Clipboard.SetText(Logs); Message = LocalizationService.Pick("Логи скопированы", "Copied"); } catch { Message = LocalizationService.Pick("Буфер обмена занят. Повторите копирование.", "Clipboard is busy. Try again."); } });
        ClearLogsCommand = new(parameter => { _logLines.Clear(); while (_pendingLogs.TryDequeue(out _)) { } Logs = ""; });
        DismissMessageCommand = new(_ => Message = "");
        CheckClientUpdatesCommand = new(_ => CheckClientUpdatesAsync(), ReportError, _ => !IsUpdating && !_closing);
        InstallClientUpdateCommand = new(_ => InstallClientUpdateAsync(), ReportError, _ => !IsUpdating && !IsBusy && !_closing && HasClientUpdate);
        ShowQrCommand = new(p => Views.QrWindow.ShowAsync(p is SubscriptionProfile subscription ? subscription.Url : p is ServerProfile server ? ShareLinkExporter.Export(server) : ""), ReportError, p => p is SubscriptionProfile or ServerProfile);
        _core.LogReceived += EnqueueLog;
        _core.UnexpectedExit += OnCoreExit;
        _openVpn.LogReceived += EnqueueLog;
        _openVpn.UnexpectedExit += OnCoreExit;
        _tun.LogReceived += EnqueueLog;
        _tun.ConnectionLost += OnTunLost;
        _logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => DrainLogs(), Application.Current.Dispatcher);
        _logTimer.Start();
        if (!string.IsNullOrEmpty(settingsService.LoadWarning)) Message = settingsService.LoadWarning;
        InitializeSubscriptionScheduler();
        InitializeProcessRouting();
    }

    public ObservableCollection<ServerProfile> Servers { get; }
    public ObservableCollection<SubscriptionProfile> Subscriptions { get; }
    public ICollectionView ServerView { get; }
    public int ServersTab { get => _serversTab; set => Set(ref _serversTab, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value?.Trim() ?? "")) ServerView.Refresh(); } }
    public string ClientVersion => HardwareIdService.ClientVersion;
    public string CoreVersion { get => _coreVersion; private set => Set(ref _coreVersion, value); }
    private string _openVpnCoreVersion = "...";
    public string OpenVpnCoreVersion { get => _openVpnCoreVersion; private set => Set(ref _openVpnCoreVersion, value); }
    public AsyncCommand CheckClientUpdatesCommand { get; }
    public AsyncCommand InstallClientUpdateCommand { get; }
    public AsyncCommand ShowQrCommand { get; }
    public bool IsUpdating { get => _isUpdating; private set { if (Set(ref _isUpdating, value)) { CheckClientUpdatesCommand.Notify(); InstallClientUpdateCommand.Notify(); } } }
    public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public bool HasClientUpdate => _clientRelease is not null;
    public bool CheckUpdatesOnStartup { get => _state.Settings.CheckUpdatesOnStartup; set { _state.Settings.CheckUpdatesOnStartup = value; OnPropertyChanged(); Persist(); } }
    public string[] UpdateChannels { get; } = [LocalizationService.Get("StableChannel"), LocalizationService.Get("ExperimentalChannel")];
    public int UpdateChannelIndex
    {
        get => _state.Settings.ExperimentalUpdates ? 1 : 0;
        set
        {
            if (value is < 0 or > 1 || value == UpdateChannelIndex) return;
            if (IsUpdating || value == 1 && MessageBox.Show(LocalizationService.Get("ExperimentalWarning"), "Win10ray", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { OnPropertyChanged(); return; }
            _state.Settings.ExperimentalUpdates = value == 1;
            _clientRelease = null; OnPropertyChanged(); OnPropertyChanged(nameof(HasClientUpdate)); InstallClientUpdateCommand.Notify(); Persist();
            _ = CheckClientUpdatesAsync();
        }
    }
    public RelayCommand SelectServersTabCommand { get; }
    public RelayCommand CancelConnectionCommand { get; }
    public RelayCommand StartRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }
    public bool IsRenaming { get => _isRenaming; set => Set(ref _isRenaming, value); }
    public ICommand ConnectionActionCommand => IsBusy && (_connectCancellation is not null || _startupCancellation is not null) ? CancelConnectionCommand : ToggleConnectionCommand;
    public RelayCommand NavigateCommand { get; }
    public AsyncCommand ToggleConnectionCommand { get; }
    public AsyncCommand ImportClipboardCommand { get; }
    public AsyncCommand ImportOpenVpnFileCommand { get; }
    public AsyncCommand PingAllCommand { get; }
    public AsyncCommand DeleteServerCommand { get; }
    public AsyncCommand RenameServerCommand { get; }
    public AsyncCommand AddSubscriptionCommand { get; }
    public AsyncCommand UpdateSubscriptionsCommand { get; }
    public AsyncCommand DeleteSubscriptionCommand { get; }
    public RelayCommand CopyLogsCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public RelayCommand DismissMessageCommand { get; }

    public int SelectedPage { get => _selectedPage; set => Set(ref _selectedPage, value); }
    public ServerProfile? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (value is null && _selectedServer is not null && Servers.Contains(_selectedServer)) return;
            if (IsConnected && value?.Id != _selectedServer?.Id) return;
            if (!Set(ref _selectedServer, value)) return;
            IsRenaming = false;
            RenameText = value?.Name ?? "";
            _state.Settings.LastServerId = value?.Id;
            NotifyCommands(); Persist();
        }
    }
    public SubscriptionProfile? SelectedSubscription { get => _selectedSubscription; set { if (Set(ref _selectedSubscription, value)) NotifyCommands(); } }
    public bool IsConnected { get => _isConnected; private set { if (Set(ref _isConnected, value)) StateChanged(); } }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) StateChanged(); } }
    public bool IsIdle => !IsBusy;
    public bool CanEditConnection => !IsConnected && !IsBusy && !IsRefreshingSubscriptions && !_closing;
    public string ConnectionButtonText => LocalizationService.Get(IsBusy ? _connectCancellation is not null || _startupCancellation is not null ? "Cancel" : IsConnected ? "Disconnecting" : "Connecting" : IsConnected ? "Disconnect" : "Connect");
    public string[] ThemeChoices { get; } = [LocalizationService.Get("Dark"), LocalizationService.Get("Light")];
    public int ThemeIndex { get => DarkTheme ? 0 : 1; set { if (value is 0 or 1) DarkTheme = value == 0; } }
    public string[] ConnectionModes { get; } = [LocalizationService.Get("SystemProxy"), "TUN"];
    public int ConnectionModeIndex
    {
        get => _state.Settings.UseTun ? 1 : 0;
        set
        {
            if (!CanEditConnection || value is < 0 or > 1) return;
            _state.Settings.UseTun = value == 1;
            _state.Settings.UseSystemProxy = value == 0;
            OnPropertyChanged(); OnPropertyChanged(nameof(UseSystemProxy)); OnPropertyChanged(nameof(UseTun)); OnPropertyChanged(nameof(CanEditProcessRules)); NotifyCommands(); Persist();
        }
    }
    public bool UseTun { get => _state.Settings.UseTun; set { if (CanEditConnection) ConnectionModeIndex = value ? 1 : 0; } }
    public bool SendHwid { get => _state.Settings.SendHwid; set { _state.Settings.SendHwid = value; OnPropertyChanged(); Persist(); } }
    public bool AutoSwitchServers { get => _state.Settings.AutoSwitchServers; set { _state.Settings.AutoSwitchServers = value; OnPropertyChanged(); Persist(); } }
    public string ServerCount => Servers.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string StatusText { get => _statusText; private set { if (Set(ref _statusText, value)) OnPropertyChanged(nameof(HasConnectionError)); } }
    public bool HasConnectionError => StatusText == LocalizationService.Get("Error");
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public string UploadSpeed { get => _uploadSpeed; private set => Set(ref _uploadSpeed, value); }
    public string DownloadSpeed { get => _downloadSpeed; private set => Set(ref _downloadSpeed, value); }
    public string SessionTraffic { get => _sessionTraffic; private set => Set(ref _sessionTraffic, value); }
    public string Message { get => _message; set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => Message.Length > 0;
    public string Logs { get => _logs; private set => Set(ref _logs, value); }
    public string RenameText { get => _renameText; set => Set(ref _renameText, value); }
    public string SubscriptionUrl { get => _subscriptionUrl; set => Set(ref _subscriptionUrl, value); }
    public bool UseSystemProxy { get => _state.Settings.UseSystemProxy; set { if (CanEditConnection) ConnectionModeIndex = value ? 0 : 1; } }
    public bool BlockAds { get => _state.Settings.BlockAds; set { if (!CanEditConnection) return; _state.Settings.BlockAds = value; OnPropertyChanged(); Persist(); } }
    public bool BypassRussia { get => _state.Settings.BypassRussia; set { if (!CanEditConnection) return; _state.Settings.BypassRussia = value; OnPropertyChanged(); Persist(); } }
    public bool MinimizeToTray { get => _state.Settings.MinimizeToTray; set { _state.Settings.MinimizeToTray = value; OnPropertyChanged(); Persist(); } }
    public bool CloseToTray { get => _state.Settings.CloseToTray; set { _state.Settings.CloseToTray = value; OnPropertyChanged(); Persist(); } }
    public bool UseDnsOverHttps { get => _state.Settings.UseDnsOverHttps; set { if (!CanEditConnection) return; _state.Settings.UseDnsOverHttps = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDnsError)); Persist(); } }
    public string DnsOverHttpsUrl { get => _state.Settings.DnsOverHttpsUrl; set { if (!CanEditConnection) return; _state.Settings.DnsOverHttpsUrl = value?.Trim() ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(HasDnsError)); Persist(); } }
    public bool HasDnsError => UseDnsOverHttps && !DnsEndpoint.IsValid(DnsOverHttpsUrl);
    public bool StartMinimized { get => _state.Settings.StartMinimized; set { _state.Settings.StartMinimized = value; OnPropertyChanged(); Persist(); } }
    public bool AutoConnect { get => _state.Settings.AutoConnect; set { _state.Settings.AutoConnect = value; OnPropertyChanged(); Persist(); } }
    public bool AutoStart
    {
        get => _state.Settings.AutoStart;
        set
        {
            if (value == AutoStart) return;
            _state.Settings.AutoStart = value;
            OnPropertyChanged();
            _ = ChangeAutoStartAsync(value, ++_autoStartVersion);
        }
    }
    public bool DarkTheme { get => _state.Settings.DarkTheme; set { _state.Settings.DarkTheme = value; OnPropertyChanged(); OnPropertyChanged(nameof(ThemeIndex)); ApplyTheme(); Persist(); } }
    public string AccentColor => "#0078D7";

    public async Task InitializeAsync()
    {
        Task version = RefreshVersionAsync();
        try { _state.Settings.AutoStart = await Task.Run(() => { _autoStart.MigrateRegistration(); return _autoStart.IsEnabled(); }); OnPropertyChanged(nameof(AutoStart)); }
        catch (Exception exception) { ReportError(exception); }
        await RestoreConnectionAsync();
        await version;
        if (AutoUpdateSubscriptions && !_closing) _ = RefreshSubscriptionsAsync(null, true, true);
        if (CheckUpdatesOnStartup && !_closing) _ = CheckClientUpdatesAsync(silent: true);
    }

    private async Task CheckClientUpdatesAsync(bool silent = false)
    {
        if (IsUpdating || _closing) return;
        IsUpdating = true; UpdateStatus = LocalizationService.Get("CheckingClientUpdates");
        try
        {
            using var service = new UpdateService();
            _clientRelease = await service.CheckAsync(ClientVersion, _lifetime.Token, _state.Settings.ExperimentalUpdates);
            OnPropertyChanged(nameof(HasClientUpdate));
            InstallClientUpdateCommand.Notify();
            UpdateStatus = _clientRelease is null ? LocalizationService.Get("ClientUpToDate") : LocalizationService.Get("ClientUpdateAvailable") + _clientRelease.Version;
            if (silent && _clientRelease is not null) Message = UpdateStatus;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { UpdateStatus = LocalizationService.Get("ClientUpdateFailed"); }
        finally { IsUpdating = false; }
    }

    private async Task InstallClientUpdateAsync()
    {
        if (_clientRelease is null || IsUpdating || _closing) return;
        if (_clientRelease.IsExperimental && MessageBox.Show(LocalizationService.Get("ExperimentalInstallWarning"), "Win10ray", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        IsUpdating = true; UpdateProgress = 0; UpdateStatus = LocalizationService.Get("ClientUpdateDownload");
        try
        {
            await UpdateLauncher.PrepareAsync(_clientRelease, new Progress<double>(value => UpdateProgress = value), _lifetime.Token);
            _startupCancellation?.Cancel();
            UpdateReadyToExit?.Invoke();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { UpdateStatus = LocalizationService.Get("ClientUpdateInstallFailed"); }
        finally { IsUpdating = false; }
    }

    public async Task RestoreConnectionAsync()
    {
        if (_closing || SelectedServer is null || !(AutoConnect || _state.Settings.ResumeConnection)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, timeout.Token);
        _startupCancellation = startup;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IsBusy = true; StatusText = LocalizationService.Get("WaitingNetwork");
                await NetworkReadinessService.WaitAsync(startup.Token);
                startup.Token.ThrowIfCancellationRequested();
                IsBusy = false;
                await ToggleConnectionAsync();
                if (IsConnected || startup.IsCancellationRequested) break;
                if (_lastConnectionError is TunServiceException tunFailure && !tunFailure.IsTransient) break;
                if (_lastConnectionError is Views.OpenVpnAuthenticationCancelledException) break;
                if (_lastConnectionError is System.IO.FileNotFoundException or UnauthorizedAccessException or FormatException) break;
                if (_lastConnectionError?.Message is string message && (message == LocalizationService.Get("TunUacCancelled") || message == LocalizationService.Get("TunMissing") || message == LocalizationService.Get("TunInstallFailed") || message == LocalizationService.Get("InvalidDoh"))) break;
                IsBusy = true;
                await Task.Delay(1000 * (attempt + 1), startup.Token);
            }
        }
        catch (OperationCanceledException) when (startup.IsCancellationRequested) { }
        finally
        {
            _startupCancellation = null; IsBusy = false;
            if (timeout.IsCancellationRequested && !_closing) { StatusText = LocalizationService.Get("Error"); Message = LocalizationService.Get("StartupNetworkTimeout"); }
            else if (!IsConnected && startup.IsCancellationRequested && !_closing) StatusText = LocalizationService.Get("Disconnected");
        }
    }

    public async Task RefreshVersionAsync()
    {
        try { OpenVpnCoreVersion = await _openVpn.ReadVersionAsync(_lifetime.Token); } catch { OpenVpnCoreVersion = LocalizationService.Get("Unavailable"); }
        try { CoreVersion = await _core.ReadVersionAsync(_lifetime.Token); }
        catch (System.IO.FileNotFoundException) { CoreVersion = LocalizationService.Get("CoreMissing"); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or System.IO.IOException) { CoreVersion = LocalizationService.Get("Unavailable"); }
    }

    private void ApplyTheme() => ThemeService.Apply(DarkTheme, AccentColor);
    private void StateChanged()
    {
        OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(CanEditConnection)); OnPropertyChanged(nameof(ConnectionButtonText)); NotifyCommands();
        OnPropertyChanged(nameof(ConnectionActionCommand)); CancelConnectionCommand?.Notify();
    }
    private void NotifyCommands()
    {
        CheckClientUpdatesCommand?.Notify(); InstallClientUpdateCommand?.Notify();
        OnPropertyChanged(nameof(CanEditConnection));
        OnPropertyChanged(nameof(CanEditProcessRules));
        AddProcessRuleCommand?.Notify(); DeleteProcessRuleCommand?.Notify(); BrowseProcessCommand?.Notify(); SelectRunningProcessCommand?.Notify();
        ToggleConnectionCommand?.Notify(); ImportClipboardCommand?.Notify(); PingAllCommand?.Notify();
        ImportOpenVpnFileCommand?.Notify();
        EditOpenVpnCredentialsCommand?.Notify();
        DeleteServerCommand?.Notify(); RenameServerCommand?.Notify(); AddSubscriptionCommand?.Notify();
        UpdateSubscriptionsCommand?.Notify(); UpdateSubscriptionCommand?.Notify(); DeleteSubscriptionCommand?.Notify();
        StartRenameCommand?.Notify();
    }
    private async Task ChangeAutoStartAsync(bool enabled, int version)
    {
        await _autoStartGate.WaitAsync();
        try
        {
            if (version != _autoStartVersion) return;
            await Task.Run(() => _autoStart.SetEnabled(enabled));
            await SaveAsync();
        }
        catch (Exception exception)
        {
            if (version == _autoStartVersion)
            {
                try { _state.Settings.AutoStart = await Task.Run(_autoStart.IsEnabled); } catch { }
            }
            ReportError(exception);
        }
        finally { OnPropertyChanged(nameof(AutoStart)); _autoStartGate.Release(); }
    }
    private async void Persist()
    {
        if (_closing) return;
        try { await SaveAsync(); } catch (Exception exception) { ReportError(exception); }
    }
    private Task SaveAsync()
    {
        _state.Servers = Servers.ToList(); _state.Subscriptions = Subscriptions.ToList();
        return _settingsService.SaveAsync(_state);
    }

    public async Task ToggleConnectionAsync()
    {
        if (_closing) return;
        await _connectionGate.WaitAsync(_lifetime.Token);
        if (_closing) { _connectionGate.Release(); return; }
        _lastConnectionError = null;
        if (!IsConnected) _connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _startupCancellation?.Token ?? _lifetime.Token);
        IsBusy = true;
        try
        {
            if (IsConnected) { _state.Settings.ResumeConnection = false; _openVpnSessionCredentials.Clear(); await DisconnectCoreAsync(); await SaveAsync(); return; }
            Message = "";
            ServerProfile server = SelectedServer ?? throw new InvalidOperationException(LocalizationService.Pick("Сначала выберите сервер.", "Choose a server first."));
            _trafficCarry = _sessionBytes = 0;
            await ConnectAvailableAsync(server, false, _connectCancellation!.Token);
        }
        catch (Exception exception)
        {
            _lastConnectionError = exception;
            await DisconnectAfterFailureAsync();
            if (exception is not OperationCanceledException) { StatusText = LocalizationService.Get("Error"); StatusDetail = LocalizationService.Pick("Подключение не установлено", "Connection failed"); ReportError(exception); }
        }
        finally
        {
            _connectCancellation?.Dispose(); _connectCancellation = null;
            IsBusy = false; _connectionGate.Release();
        }
    }

    private async Task ConnectAvailableAsync(ServerProfile server, bool skipCurrent, CancellationToken token, bool interactive = true)
    {
        if (HasDnsError) throw new InvalidOperationException(LocalizationService.Get("InvalidDoh"));
        await _core.CheckEnvironmentAsync(BypassRussia || BlockAds, token);
        if (UseTun)
        {
            StatusDetail = LocalizationService.Get("PreparingTun");
            var preparation = Stopwatch.StartNew();
            await _tun.PrepareAsync(token);
            EnqueueLog($"TUN service ready: {preparation.ElapsedMilliseconds} ms.");
        }
        bool autoSwitch = AutoSwitchServers && server.SubscriptionId is not null;
        ServerProfile[] candidates = autoSwitch ? ServerFailoverService.Candidates(Servers.ToArray(), server, skipCurrent) : skipCurrent ? [] : [server];
        StatusText = LocalizationService.Get("Connecting");
        int attemptNumber = 0;
        ServerProfile available = await ServerFailoverService.ConnectAsync(candidates, async (candidate, cancellation) =>
        {
            ++attemptNumber;
            StatusDetail = LocalizationService.Pick($"Проверка сервера {attemptNumber} из {candidates.Length}", $"Checking server {attemptNumber} of {candidates.Length}");
            if (autoSwitch && attemptNumber > 1 && !(candidate.Protocol == "openvpn" && candidate.Network.StartsWith("udp", StringComparison.Ordinal)))
            {
                candidate.IsPinging = true;
                try { candidate.LatencyMs = await _health.PingAsync(candidate, cancellation); candidate.PingFailed = candidate.LatencyMs is null; }
                finally { candidate.IsPinging = false; }
                if (candidate.PingFailed) return false;
            }
            bool ready = false;
            try
            {
                string? endpoint = null;
                await _openVpn.StopAsync();
                if (candidate.Protocol == "openvpn")
                {
                    OpenVpnOptions options = OpenVpnProfileParser.Read(candidate.OpenVpnConfig);
                    string? username = candidate.OpenVpnUsername.Length > 0 ? candidate.OpenVpnUsername : options.Username;
                    string? password = candidate.OpenVpnPassword.Length > 0 ? candidate.OpenVpnPassword : options.Password;
                    if (_openVpnSessionCredentials.TryGetValue(candidate.SourceKey, out var remembered))
                    { username = remembered.Username; password = remembered.Password; }
                    if (options.RequiresCredentials && (username.Length == 0 || password.Length == 0))
                    {
                        if (!interactive) throw new InvalidOperationException(LocalizationService.Get("OpenVpnCredentialsRequired"));
                        Views.OpenVpnCredentials credentials = Views.OpenVpnCredentialsWindow.Prompt(username, cancellation);
                        username = credentials.Username; password = credentials.Password;
                        if (credentials.Remember) { candidate.OpenVpnUsername = username; candidate.OpenVpnPassword = password; }
                    }
                    if (options.RequiresCredentials) _openVpnSessionCredentials[candidate.SourceKey] = (username, password);
                    StatusDetail = LocalizationService.Get("OpenVpnConnecting");
                    await _openVpn.StartAsync(candidate, username, password, cancellation, UseDnsOverHttps, DnsOverHttpsUrl);
                }
                if (UseTun && !IPAddress.TryParse(candidate.Host, out _))
                {
                    using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    dnsTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    IPAddress[] addresses;
                    try { addresses = await Dns.GetHostAddressesAsync(candidate.Host, dnsTimeout.Token); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    { throw new InvalidOperationException(LocalizationService.Pick("Не удалось определить IP-адрес сервера.", "Could not resolve the server address.")); }
                    endpoint = (addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault())?.ToString()
                        ?? throw new InvalidOperationException(LocalizationService.Pick("Не удалось определить IP-адрес сервера.", "Could not resolve the server address."));
                }
                string config = await Task.Run(() => new XrayConfigBuilder().Build(candidate, _state.Settings, _core.ApiPort, _core.SocksPort, _core.HttpPort, endpoint, candidate.Protocol == "openvpn" ? _openVpn.SocksPort : null), cancellation);
                var startup = Stopwatch.StartNew();
                await _core.StartAsync(config, Secrets(candidate).Append(DnsOverHttpsUrl), BypassRussia || BlockAds, cancellation);
                EnqueueLog($"Xray ready: {startup.ElapsedMilliseconds} ms.");
                if (autoSwitch && !await _health.CheckTunnelAsync(_core.HttpPort, cancellation))
                {
                    candidate.PingFailed = true; candidate.LatencyMs = null;
                    return false;
                }
                ready = true;
                return true;
            }
            catch (Exception exception) when (autoSwitch && !cancellation.IsCancellationRequested && exception is not Views.OpenVpnAuthenticationCancelledException && exception is InvalidOperationException or FormatException or SocketException or OperationCanceledException)
            {
                candidate.PingFailed = true; candidate.LatencyMs = null;
                return false;
            }
            finally { if (!ready) { await _core.StopAsync(); await _openVpn.StopAsync(); } }
        }, token);
        token.ThrowIfCancellationRequested();
        if (UseTun) await _tun.StartAsync(token, UseDnsOverHttps, DnsOverHttpsUrl, BlockAds, ProcessRules.ToArray(), DefaultRouteDirect);
        else await Task.Run(_proxy.Enable, token);
        if (!_core.IsRunning) throw new InvalidOperationException(LocalizationService.Pick("Ядро Xray остановилось во время подключения.", "Xray stopped during connection."));
        available.PingFailed = false;
        SelectedServer = available;
        IsConnected = true; StatusText = LocalizationService.Get("Connected");
        _state.Settings.ResumeConnection = true;
        StatusDetail = UseTun ? "TUN" : LocalizationService.Get("SystemProxy");
        UploadSpeed = DownloadSpeed = FormatBytes(0) + LocalizationService.Pick("/с", "/s"); SessionTraffic = FormatBytes(_trafficCarry);
        EnqueueLog(LocalizationService.Pick("Подключение установлено.", "Connected."));
        if (available.Id != server.Id) Message = LocalizationService.Get("ServerSwitched");
        _statsCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _statsTask = PollStatsAsync(_statsCancellation.Token);
        int generation = ++_sessionGeneration;
        if (available.SubscriptionId is not null)
        {
            _healthCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _healthTask = MonitorConnectionAsync(generation, _healthCancellation.Token);
        }
        await SaveAsync();
    }

    private async Task MonitorConnectionAsync(int generation, CancellationToken token)
    {
        try
        {
            int failures = 0;
            using var timer = new PeriodicTimer(_healthInterval);
            while (await timer.WaitForNextTickAsync(token))
            {
                if (!AutoSwitchServers) { failures = 0; continue; }
                if (await _health.CheckTunnelAsync(_core.HttpPort, token)) { failures = 0; continue; }
                if (++failures < 3) continue;
                // Return before recovery stops and joins this monitor.
                _ = Application.Current.Dispatcher.BeginInvoke(new Action(async () => await RecoverConnectionAsync(generation)));
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { EnqueueLog(LocalizationService.Pick("Проверка соединения временно недоступна.", "Connection checks are temporarily unavailable.")); }
    }

    private async Task RecoverConnectionAsync(int generation)
    {
        if (_closing) return;
        try { await _connectionGate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_closing || !IsConnected || generation != _sessionGeneration) return;
            ServerProfile? previous = SelectedServer;
            bool switchServer = AutoSwitchServers && previous?.SubscriptionId is not null;
            _connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            IsBusy = true;
            _trafficCarry = _sessionBytes;
            await DisconnectCoreAsync();
            if (!switchServer) throw new InvalidOperationException(LocalizationService.Pick("Соединение с сервером потеряно.", "The connection was lost."));
            EnqueueLog(LocalizationService.Pick("Сервер недоступен. Проверка остальных серверов подписки.", "Server unavailable. Checking other servers in the subscription."));
            await ConnectAvailableAsync(previous!, true, _connectCancellation.Token, interactive: false);
        }
        catch (Exception exception)
        {
            await DisconnectAfterFailureAsync();
            if (exception is not OperationCanceledException) { StatusText = LocalizationService.Get("Error"); ReportError(exception); }
        }
        finally
        {
            _connectCancellation?.Dispose(); _connectCancellation = null;
            IsBusy = false; _connectionGate.Release();
        }
    }
    private static IEnumerable<string> Secrets(ServerProfile server) =>
        new[] { server.Credential, server.PublicKey, server.ShortId, server.Encryption, server.Extra, server.Path, server.SpiderX, server.ServiceName }
            .Where(value => !string.IsNullOrEmpty(value) && value is not ("/" or "none" or "auto")).Concat(OpenVpnProcessService.Secrets(server));
    private async Task DisconnectCoreAsync()
    {
        ++_sessionGeneration;
        _healthCancellation?.Cancel();
        if (_healthTask is not null) await _healthTask;
        _healthCancellation?.Dispose(); _healthCancellation = null; _healthTask = null;
        _statsCancellation?.Cancel();
        if (_statsTask is not null) await _statsTask;
        _statsCancellation?.Dispose(); _statsCancellation = null; _statsTask = null;
        await _tun.StopAsync();
        Exception? proxyError = null;
        try { await Task.Run(_proxy.Disable); } catch (Exception exception) { proxyError = exception; }
        await _core.StopAsync();
        await _openVpn.StopAsync();
        IsConnected = false; UploadSpeed = DownloadSpeed = FormatBytes(0) + LocalizationService.Pick("/с", "/s");
        StatusText = LocalizationService.Pick("Отключено", "Disconnected"); StatusDetail = LocalizationService.Pick("Соединение завершено", "Disconnected");
        EnqueueLog(LocalizationService.Pick("Ядро остановлено. Соединение завершено.", "Disconnected."));
        if (proxyError is not null) throw proxyError;
    }
    private async Task DisconnectAfterFailureAsync()
    {
        try { await DisconnectCoreAsync(); }
        catch (Exception exception) { ReportError(exception); _proxy.EmergencyDisable(); }
        IsConnected = false;
    }
    private void OnCoreExit(int code)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_closing || !IsConnected || IsBusy) return;
            await RecoverConnectionAsync(_sessionGeneration);
        }));
    }
    private void OnTunLost()
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            await _connectionGate.WaitAsync();
            try
            {
                if (!IsConnected) return;
                await DisconnectAfterFailureAsync();
                StatusText = LocalizationService.Get("Error");
                Message = LocalizationService.Get("TunLost");
            }
            finally { _connectionGate.Release(); }
        }));
    }
    private async Task PollStatsAsync(CancellationToken token)
    {
        long previousUp = 0, previousDown = 0;
        var watch = Stopwatch.StartNew(); double previousTime = 0; int failures = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    TrafficSnapshot snapshot = await _core.QueryStatsAsync(token);
                    double now = watch.Elapsed.TotalSeconds, seconds = Math.Max(.1, now - previousTime);
                    UploadSpeed = FormatBytes((long)(Math.Max(0, snapshot.UplinkBytes - previousUp) / seconds)) + LocalizationService.Pick("/с", "/s");
                    DownloadSpeed = FormatBytes((long)(Math.Max(0, snapshot.DownlinkBytes - previousDown) / seconds)) + LocalizationService.Pick("/с", "/s");
                    _sessionBytes = _trafficCarry + snapshot.UplinkBytes + snapshot.DownlinkBytes;
                    SessionTraffic = FormatBytes(_sessionBytes);
                    previousUp = snapshot.UplinkBytes; previousDown = snapshot.DownlinkBytes; previousTime = now; failures = 0;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch { UploadSpeed = DownloadSpeed = "-"; if (++failures == 3) EnqueueLog(LocalizationService.Pick("Статистика временно недоступна. Ожидается ответ API xray.", "Waiting for Xray statistics.")); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private static string FormatBytes(long bytes)
    {
        string[] units = LocalizationService.IsRussian ? ["Б", "КБ", "МБ", "ГБ", "ТБ"] : ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private async Task ImportClipboardAsync()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch { throw new InvalidOperationException(LocalizationService.Pick("Буфер обмена занят. Повторите импорт.", "Clipboard is busy. Try again.")); }
        ImportResult result;
        if (OpenVpnImportService.TryGetUrl(text, out Uri? uri))
        {
            using OpenVpnImportService importer = new(SubscriptionsThroughConnection && IsConnected ? _core.HttpPort : null);
            result = new([await importer.FetchAsync(uri!, _lifetime.Token)], []);
        }
        else result = await Task.Run(() => new LinkParser().ParseMany(text), _lifetime.Token);
        if (!CanEditConnection) return;
        int count = 0;
        foreach (ServerProfile server in result.Profiles)
            if (!Servers.Any(existing => existing.SourceKey == server.SourceKey)) { Servers.Add(server); count++; }
        RefreshServers(); await SaveAsync();
        Message = LocalizationService.Pick($"Добавлено: {count}", $"Added: {count}") +
            (result.Errors.Count > 0 ? LocalizationService.Pick($". Отклонено: {result.Errors.Count}. {result.Errors[0]}", $". Rejected: {result.Errors.Count}. {result.Errors[0]}") : "");
    }
    private async Task PingAllAsync()
    {
        using var limit = new SemaphoreSlim(8);
        await Task.WhenAll(Servers.ToArray().Select(async server =>
        {
            if (server.Protocol == "openvpn" && server.Network.StartsWith("udp", StringComparison.Ordinal)) { server.LatencyMs = null; server.PingFailed = false; return; }
            await limit.WaitAsync(_lifetime.Token);
            server.IsPinging = true; server.PingFailed = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var socket = new TcpClient(); var watch = Stopwatch.StartNew();
                await socket.ConnectAsync(server.Host, server.Port, timeout.Token);
                server.LatencyMs = Math.Max(1, (int)watch.ElapsedMilliseconds);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException or ArgumentException) { server.LatencyMs = null; server.PingFailed = true; }
            finally { server.IsPinging = false; limit.Release(); }
        }));
    }
    private async Task DeleteServerAsync()
    {
        if (SelectedServer is null) return;
        Servers.Remove(SelectedServer); SelectedServer = Servers.FirstOrDefault(); RefreshServers(); await SaveAsync();
    }
    private async Task RenameServerAsync()
    {
        if (SelectedServer is null) return;
        string name = RenameText.Trim();
        if (name.Length is < 1 or > 100) throw new InvalidOperationException(LocalizationService.Pick("Название должно содержать от 1 до 100 символов.", "Name must contain 1 to 100 characters."));
        SelectedServer.Name = name; SelectedServer.HasCustomName = true; IsRenaming = false; ServerView.Refresh(); await SaveAsync(); Message = LocalizationService.Pick("Название сохранено", "Saved");
    }
    private void RefreshServers()
    {
        if (SelectedServer is null || !Servers.Contains(SelectedServer)) SelectedServer = Servers.FirstOrDefault();
        UpdateServerGroups(); ServerView.Refresh();
        OnPropertyChanged(nameof(ServerCount)); OnPropertyChanged(nameof(HasServers)); NotifyCommands();
    }
    public bool HasServers => Servers.Count > 0;
    private void UpdateServerGroups()
    {
        foreach (ServerProfile server in Servers)
            server.GroupName = Subscriptions.FirstOrDefault(subscription => subscription.Id == server.SubscriptionId)?.Name ?? LocalizationService.Get("Imported");
    }
    private async Task AddSubscriptionAsync()
    {
        string url = SubscriptionUrl.Trim();
        if (Subscriptions.Any(s => s.Url == url)) throw new InvalidOperationException(LocalizationService.Pick("Эта подписка уже добавлена.", "Subscription already exists."));
        ImportResult result = await _subscriptions.FetchAsync(url, _lifetime.Token, SendHwid, SubscriptionsThroughConnection && IsConnected);
        if (!CanEditConnection) return;
        if (result.Profiles.Count == 0) throw new InvalidOperationException(LocalizationService.Pick("Подписка не содержит поддерживаемых серверов.", "No supported servers in this subscription."));
        var subscription = new SubscriptionProfile { Url = url, Name = new Uri(url).Host, UpdatedAt = DateTimeOffset.Now };
        Subscriptions.Add(subscription); ApplySubscription(subscription, result.Profiles);
        SelectedSubscription = subscription; SubscriptionUrl = "";
        RefreshServers(); await SaveAsync();
        Message = LocalizationService.Pick($"Добавлено: {result.Profiles.Count}. Отклонено: {result.Errors.Count}.", $"Added: {result.Profiles.Count}. Rejected: {result.Errors.Count}.");
    }
    private void ApplySubscription(SubscriptionProfile subscription, IReadOnlyList<ServerProfile> incoming)
    {
        ServerProfile[] old = Servers.Where(s => s.SubscriptionId == subscription.Id).ToArray();
        Guid? selectedId = SelectedServer?.Id;
        foreach (ServerProfile server in old) Servers.Remove(server);
        foreach (ServerProfile server in incoming.DistinctBy(s => s.SourceKey))
        {
            server.FlagCode = CountryFlagService.CountryCode(server.Name);
            ServerProfile? previous = old.FirstOrDefault(s => s.SourceKey == server.SourceKey);
            server.SubscriptionId = subscription.Id;
            if (previous is not null)
            {
                previous.FlagCode = server.FlagCode;
                if (!previous.HasCustomName) previous.Name = server.Name;
                Servers.Add(previous);
            }
            else Servers.Add(server);
        }
        subscription.UpdatedAt = DateTimeOffset.Now;
        if (!IsConnected) SelectedServer = Servers.FirstOrDefault(s => s.Id == selectedId) ?? Servers.FirstOrDefault();
    }
    private async Task DeleteSubscriptionAsync()
    {
        if (SelectedSubscription is null) return;
        foreach (ServerProfile server in Servers.Where(s => s.SubscriptionId == SelectedSubscription.Id).ToArray()) Servers.Remove(server);
        Subscriptions.Remove(SelectedSubscription); SelectedSubscription = Subscriptions.FirstOrDefault();
        RefreshServers(); await SaveAsync();
    }
    private void EnqueueLog(string line)
    {
        if (_pendingLogs.Count < 1500) _pendingLogs.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
    }
    private void DrainLogs()
    {
        bool changed = false;
        while (_pendingLogs.TryDequeue(out string? line)) { _logLines.Enqueue(line); changed = true; }
        while (_logLines.Count > 600) _logLines.Dequeue();
        if (changed) Logs = string.Join(Environment.NewLine, _logLines);
    }
    public void ReportError(Exception exception)
    {
        if (exception is OperationCanceledException) return;
        string text = exception is InvalidOperationException or FormatException or System.IO.FileNotFoundException
            ? exception.Message : LocalizationService.Get("OperationFailed");
        Message = new LogSanitizer(Servers.SelectMany(Secrets).Append(DnsOverHttpsUrl)).Sanitize(text);
        EnqueueLog(Message);
    }
    public async Task ShutdownAsync()
    {
        _closing = true; _lifetime.Cancel(); NotifyCommands();
        StopSubscriptionScheduler();
        if (_subscriptionTask is not null) await _subscriptionTask;
        await _connectionGate.WaitAsync();
        try { await DisconnectAfterFailureAsync(); await _autoStartGate.WaitAsync(); _autoStartGate.Release(); await SaveAsync(); }
        finally
        {
            _core.LogReceived -= EnqueueLog; _core.UnexpectedExit -= OnCoreExit;
            _openVpn.LogReceived -= EnqueueLog; _openVpn.UnexpectedExit -= OnCoreExit; _openVpn.Dispose();
            _openVpnSessionCredentials.Clear();
            _tun.LogReceived -= EnqueueLog; _tun.ConnectionLost -= OnTunLost;
            _logTimer.Stop(); _subscriptions.Dispose(); _connectionGate.Release();
        }
    }
    public void EmergencyStop() { _tun.EmergencyStop(); _openVpn.EmergencyStop(); }
}
