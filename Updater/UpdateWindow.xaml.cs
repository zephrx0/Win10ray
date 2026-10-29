using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using XrayMetro.Services;

namespace Win10ray.Updater;

public partial class UpdateWindow : Window
{
    private readonly string _mode, _argument;
    private readonly CancellationTokenSource _lifetime = new();
    private string _target = "", _currentVersion = "0.0.0";
    private ClientRelease? _release;
    private bool _installing, _busy;
    private Mutex? _instance;
    public static string Text(string ru, string en) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? ru : en;

    public UpdateWindow(string mode, string argument)
    {
        _mode = mode; _argument = argument;
        InitializeComponent();
        CheckButton.Content = Text("Проверить", "Check"); InstallButton.Content = Text("Обновить", "Update");
        ChannelSelector.ItemsSource = new[] { Text("Стабильный", "Stable"), Text("Экспериментальный", "Experimental") }; ChannelSelector.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            if (_mode == "--apply") await ApplyHandoffAsync();
            else
            {
                try
                {
                    _target = Path.GetFullPath(_argument); UpdatePackage.ValidateTarget(_target);
                    _currentVersion = FileVersionInfo.GetVersionInfo(Path.Combine(_target, "Win10ray.exe")).ProductVersion?.Split('+')[0] ?? throw new InvalidDataException();
                    UpdateService.ParseVersion(_currentVersion);
                    VersionLabel.Text = _currentVersion;
                    await CheckAsync();
                }
                catch { StatusLabel.Text = Text("Не удалось открыть папку клиента.", "Could not open the client folder."); }
            }
        };
        Closing += ClosingWindow;
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); } };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy; CheckButton.IsEnabled = !busy; InstallButton.IsEnabled = !busy; ChannelSelector.IsEnabled = !busy;
        Progress.IsIndeterminate = busy; Progress.Value = 0;
    }
    private async void CheckClicked(object sender, RoutedEventArgs e) => await CheckAsync();
    private async Task CheckAsync()
    {
        if (_busy) return;
        SetBusy(true); StatusLabel.Text = Text("Проверка обновлений...", "Checking for updates...");
        try
        {
            using var service = new UpdateService();
            _release = await service.CheckAsync(_currentVersion, _lifetime.Token, ChannelSelector.SelectedIndex == 1);
            InstallButton.Visibility = _release is null ? Visibility.Collapsed : Visibility.Visible;
            StatusLabel.Text = _release is null ? Text("Установлена актуальная версия.", "You are up to date.") : Text("Доступна версия ", "Version available: ") + _release.Version;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { StatusLabel.Text = Text("Не удалось проверить обновления. Проверьте доступ к GitHub.", "Could not check for updates. Check access to GitHub."); }
        finally { SetBusy(false); }
    }

    private async void InstallClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _release is null) return;
        if (_release.IsExperimental && !ConfirmExperimental()) return;
        SetBusy(true);
        try
        {
            string directory = UpdatePackage.Under(UpdateService.CacheDirectory, Guid.NewGuid().ToString("N"));
            using var service = new UpdateService();
            StatusLabel.Text = Text("Загрузка обновления...", "Downloading update...");
            Progress.IsIndeterminate = false;
            string archive = await service.DownloadAsync(_release, directory, new Progress<double>(value => Progress.Value = value), _lifetime.Token);
            await ApplyAsync(new(_target, archive, _release, 0, 0, _release.IsExperimental), directory, false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (InstallerException exception) { ShowInstallerError(exception); }
        catch { StatusLabel.Text = Text("Обновление не установлено. Проверьте подключение и доступ к папке клиента.", "Update was not installed. Check the connection and access to the client folder."); }
        finally { SetBusy(false); }
    }

    private async Task ApplyHandoffAsync()
    {
        SetBusy(true); CheckButton.Visibility = Visibility.Collapsed; ChannelSelector.Visibility = Visibility.Collapsed;
        try
        {
            string request = Path.GetFullPath(_argument);
            string directory = Path.GetDirectoryName(request)!;
            if (Path.GetFileName(request) != "handoff.json" || !directory.StartsWith(Path.GetFullPath(UpdateService.CacheDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            var handoff = JsonSerializer.Deserialize<UpdateHandoff>(await File.ReadAllTextAsync(request, _lifetime.Token)) ?? throw new InvalidDataException();
            UpdateService.Validate(handoff.Release);
            if (Path.GetFullPath(handoff.ArchivePath) != Path.Combine(directory, handoff.Release.CacheFileName)) throw new InvalidDataException();
            using Process parent = Process.GetProcessById(handoff.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != handoff.ParentStartTicks || !string.Equals(parent.MainModule?.FileName, Path.Combine(Path.GetFullPath(handoff.TargetDirectory), "Win10ray.exe"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            await ApplyAsync(handoff, directory, true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (InstallerException exception) { ShowInstallerError(exception); }
        catch { StatusLabel.Text = Text("Не удалось завершить обновление. Закройте клиент и запустите установщик вручную.", "Could not complete the update. Exit the client and run the installer manually."); }
        finally { SetBusy(false); }
    }

    private async Task ApplyAsync(UpdateHandoff handoff, string directory, bool signalParent)
    {
        UpdatePackage.ValidateTarget(handoff.TargetDirectory);
        if (handoff.Release.IsExperimental && !handoff.ExperimentalConfirmed && !ConfirmExperimental()) throw new OperationCanceledException();
        if (_instance is null)
        {
            string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(handoff.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
            var instance = new Mutex(false, @"Local\Win10ray.Update." + identity);
            bool acquired;
            try { acquired = instance.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { instance.Dispose(); throw new IOException("Another updater is already running."); }
            _instance = instance;
        }
        StatusLabel.Text = Text("Проверка файлов...", "Verifying files...");
        await UpdateService.VerifyAsync(handoff.ArchivePath, handoff.Release, _lifetime.Token);
        string? extracted = null;
        if (handoff.Release.IsInstaller) await ExeInstallerService.VerifyAsync(handoff.ArchivePath, handoff.Release, _lifetime.Token);
        else
        {
            string accessProbe = UpdatePackage.Under(handoff.TargetDirectory, ".update-access-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(accessProbe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            extracted = await UpdatePackage.ExtractAsync(handoff.ArchivePath, Path.Combine(directory, "verified"), handoff.Release.Version, _lifetime.Token);
        }
        if (signalParent) await File.WriteAllTextAsync(Path.Combine(directory, "ready"), "ready", _lifetime.Token);
        StatusLabel.Text = Text("Ожидание выхода Win10ray. При ручном обновлении выберите «Выход» в меню трея.", "Waiting for Win10ray to exit. For a manual update, choose Exit in the tray menu.");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        wait.CancelAfter(TimeSpan.FromMinutes(3));
        await UpdatePackage.WaitForClientExitAsync(handoff.TargetDirectory, wait.Token);
        _installing = true;
        try
        {
            StatusLabel.Text = Text("Установка обновления...", "Installing update..."); Progress.IsIndeterminate = handoff.Release.IsInstaller;
            bool restart = false;
            if (handoff.Release.IsInstaller) restart = await ExeInstallerService.InstallAsync(handoff.ArchivePath, handoff.Release, handoff.TargetDirectory, Path.Combine(directory, "setup.log"), _lifetime.Token);
            else await UpdatePackage.InstallAsync(extracted!, handoff.TargetDirectory, new Progress<double>(value => Progress.Value = value), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(directory, "complete"), "complete");
            if (restart) { StatusLabel.Text = Text("Обновление установлено. Для завершения перезагрузите Windows.", "Update installed. Restart Windows to complete the installation."); return; }
            var info = new ProcessStartInfo(Path.Combine(handoff.TargetDirectory, "Win10ray.exe")) { UseShellExecute = true, WorkingDirectory = handoff.TargetDirectory };
            using Process? child = Process.Start(info);
            if (child is null) throw new IOException();
            CloseAfterInstall();
        }
        finally { _installing = false; }
    }
    private void CloseAfterInstall() { _installing = false; Close(); }
    private void ShowInstallerError(InstallerException exception)
    {
        StatusLabel.Text = exception.Code is "elevation_cancelled" or "install_cancelled"
            ? Text("Установка отменена. Текущий клиент можно запустить снова.", "Installation cancelled. You can start the current client again.")
            : Text("Установщик не завершил обновление. Подробности в setup.log. Код: ", "The installer could not complete the update. See setup.log. Code: ") + exception.Code;
    }
    private void ClosingWindow(object? sender, CancelEventArgs e) { if (_installing) e.Cancel = true; }
    private bool _changingChannel;
    private bool ConfirmExperimental() => MessageBox.Show(this, Text("Экспериментальная версия может нарушать подключение и содержать ошибки. Продолжить? Версии старше 1.4 не читают зашифрованный профиль.", "Experimental versions may interrupt your connection and contain bugs. Continue? Versions before 1.4 cannot read encrypted profiles."), "Win10ray", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private void ChannelChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_changingChannel || !IsLoaded) return;
        if (ChannelSelector.SelectedIndex == 1 && !ConfirmExperimental())
        { _changingChannel = true; ChannelSelector.SelectedIndex = 0; _changingChannel = false; return; }
        _release = null; InstallButton.Visibility = Visibility.Collapsed;
        _ = CheckAsync();
    }
}
