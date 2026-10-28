using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace XrayMetro.Services;

public interface ISystemProxyService
{
    void Enable();
    void Disable();
    void EmergencyDisable();
}

/// <summary>Owns only the proxy installed by this application and records ownership before changing Windows.</summary>
public sealed class SystemProxyService : ISystemProxyService
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string LocalProxy = "http=127.0.0.1:10809;https=127.0.0.1:10809;socks=127.0.0.1:10808";
    private static readonly int OwnerPid = Environment.ProcessId;
    private static readonly long OwnerStarted = GetCurrentStartTicks();
    private static readonly string OwnerToken = Guid.NewGuid().ToString("N");
    private static readonly string MarkerPath = Path.Combine(SettingsService.AppDirectory, "proxy-owner.json");
    private static readonly string GateName = @"Local\XrayMetro.Proxy." + WindowsIdentity.GetCurrent().User!.Value;
    private static int _watchdogPid;
    private static long _watchdogStarted;

    public void Enable()
    {
        InGate(() =>
        {
            ProxyOwnership? previous = ReadMarker();
            if (previous is not null && !IsThisProcess(previous) && IsAlive(previous.Pid, previous.StartTicks))
                throw new InvalidOperationException(LocalizationService.Pick("Системный прокси уже используется другим экземпляром Win10ray.", "The proxy is in use by another Win10ray instance."));
            if (previous is not null && !IsThisProcess(previous)) DisableOwned(previous);
            Directory.CreateDirectory(SettingsService.AppDirectory);
            ProxyOwnership marker = new() { Pid = OwnerPid, StartTicks = OwnerStarted, Token = OwnerToken };
            WriteMarker(marker);
            try
            {
                EnsureWatchdog();
                using RegistryKey key = OpenSettings();
                key.SetValue("ProxyServer", LocalProxy, RegistryValueKind.String);
                key.SetValue("ProxyOverride", "<local>;localhost;127.*;[::1]", RegistryValueKind.String);
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                Refresh();
            }
            catch
            {
                try { DisableOwned(marker); } catch (Exception ex) when (IsRecoverable(ex)) { }
                throw;
            }
        });
    }

    public void Disable()
    {
        InGate(() =>
        {
            ProxyOwnership? marker = ReadMarker();
            if (marker is not null && IsThisProcess(marker)) DisableOwned(marker);
        });
    }

    public void EmergencyDisable()
    {
        try { Disable(); }
        catch (Exception ex) when (IsRecoverable(ex)) { }
    }

    public void RecoverIfNeeded()
    {
        InGate(() =>
        {
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XrayMetro", "proxy-owner.json");
            foreach (string path in new[] { MarkerPath, legacy })
            {
                ProxyOwnership? marker = ReadMarker(path);
                if (marker is not null && !IsAlive(marker.Pid, marker.StartTicks)) DisableOwned(marker, path);
            }
        });
    }

    /// <summary>Called by App before acquiring the single-instance mutex or creating any window.</summary>
    public static async Task RunWatchdogAsync(int parentId, long startTicks)
    {
        try
        {
            using EventWaitHandle? ready = EventWaitHandle.TryOpenExisting(ReadyName(parentId, startTicks), out EventWaitHandle? opened) ? opened : null;
            if (ready is null) return;
            using Process parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToUniversalTime().Ticks != startTicks) return;
            ready.Set();
            await parent.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                InGate(() =>
                {
                    ProxyOwnership? marker = ReadMarker();
                    if (marker is not null && marker.Pid == parentId && marker.StartTicks == startTicks)
                        DisableOwned(marker);
                });
                return;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
    }

    private static void EnsureWatchdog()
    {
        if (_watchdogPid != 0 && IsAlive(_watchdogPid, _watchdogStarted)) return;
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException(LocalizationService.Pick("Не удалось запустить защиту системного прокси.", "Could not start proxy recovery."));
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalizationService.Pick("Для системного прокси запустите Win10ray.exe напрямую.", "Open Win10ray.exe directly to enable the system proxy."));
        using EventWaitHandle ready = new(false, EventResetMode.ManualReset, ReadyName(OwnerPid, OwnerStarted));
        ProcessStartInfo info = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        info.ArgumentList.Add("--watchdog");
        info.ArgumentList.Add(OwnerPid.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(OwnerStarted.ToString(CultureInfo.InvariantCulture));
        using Process child = Process.Start(info) ?? throw new InvalidOperationException(LocalizationService.Pick("Не удалось запустить защиту системного прокси.", "Could not start proxy recovery."));
        if (!ready.WaitOne(TimeSpan.FromSeconds(10)))
        {
            try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { }
            throw new InvalidOperationException(LocalizationService.Pick("Защита системного прокси не запустилась. Повторите подключение.", "Proxy recovery did not start. Try connecting again."));
        }
        _watchdogPid = child.Id;
        _watchdogStarted = child.StartTime.ToUniversalTime().Ticks;
    }

    private static void DisableOwned(ProxyOwnership marker, string? markerPath = null)
    {
        markerPath ??= MarkerPath;
        using RegistryKey key = OpenSettings();
        string? configured = key.GetValue("ProxyServer") as string;
        // A user or another client may have replaced our proxy while we were connected.
        if (string.Equals(configured, LocalProxy, StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            Refresh();
        }
        ProxyOwnership? current = ReadMarker(markerPath);
        if (current?.Token == marker.Token) File.Delete(markerPath);
    }

    private static RegistryKey OpenSettings() => Registry.CurrentUser.CreateSubKey(InternetSettings, true)
        ?? throw new UnauthorizedAccessException();

    private static void Refresh()
    {
        bool changed = InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        bool refreshed = InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        if (!changed || !refreshed)
            throw new InvalidOperationException(LocalizationService.Pick("Windows не удалось применить изменение системного прокси.", "Windows could not apply the proxy settings."));
    }

    private static void InGate(Action action)
    {
        using Mutex mutex = new(false, GateName);
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(12)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException(LocalizationService.Pick("Настройки системного прокси заняты. Повторите действие.", "Proxy settings are busy. Try again."));
            action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Нет доступа к системному прокси Windows. Проверьте права на реестр и AppData.", "Access to Windows proxy settings was denied. Check registry and AppData permissions."));
        }
        catch (IOException)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Не удалось сохранить состояние системного прокси в AppData.", "Could not save proxy state to AppData."));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Не удалось запустить процесс защиты системного прокси.", "Could not start proxy recovery."));
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static ProxyOwnership? ReadMarker(string? markerPath = null)
    {
        markerPath ??= MarkerPath;
        if (!File.Exists(markerPath)) return null;
        try { return JsonSerializer.Deserialize<ProxyOwnership>(File.ReadAllText(markerPath)); }
        catch (JsonException)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Файл состояния системного прокси повреждён. Проверьте настройки прокси Windows.", "Proxy recovery data is damaged. Check Windows proxy settings."));
        }
    }

    private static void WriteMarker(ProxyOwnership marker)
    {
        string temporary = MarkerPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(marker));
        File.Move(temporary, MarkerPath, true);
    }

    private static bool IsThisProcess(ProxyOwnership marker) => marker.Pid == OwnerPid && marker.StartTicks == OwnerStarted && marker.Token == OwnerToken;
    private static bool IsAlive(int processId, long ticks)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private static long GetCurrentStartTicks()
    {
        using Process process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static string ReadyName(int processId, long ticks) => $@"Local\XrayMetro.Watchdog.{processId}.{ticks}";
    private static bool IsRecoverable(Exception exception) => exception is InvalidOperationException or IOException or UnauthorizedAccessException or SecurityException or System.ComponentModel.Win32Exception;

    private sealed class ProxyOwnership
    {
        public ProxyOwnership() { }
        public int Pid { get; set; }
        public long StartTicks { get; set; }
        public string Token { get; set; } = "";
    }

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int bufferLength);
}
