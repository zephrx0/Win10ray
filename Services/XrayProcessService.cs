using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace XrayMetro.Services;

public sealed record TrafficSnapshot(long UplinkBytes, long DownlinkBytes);

/// <summary>Owns all core processes, their lifetime, and the temporary configuration.</summary>
public sealed class XrayProcessService : IDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _stats = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<Process> _children = [];
    private readonly string _runtimeDirectory;
    private readonly SafeFileHandle _job;
    private Process? _process;
    private CancellationTokenSource? _sessionCancellation;
    private Task[] _readers = [];
    private bool _stopping;
    private bool _usesProcessOverride;
    private volatile bool _disposed;
    private LogSanitizer _sanitizer = new([]);

    public event Action<string>? LogReceived;
    public event Action<int>? UnexpectedExit;
    public int ApiPort { get; }
    public int SocksPort { get; }
    public int HttpPort { get; }
    public string ExecutablePath { get; private set; } = "";
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                try { return _process is { HasExited: false }; }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    public XrayProcessService(string? runtimeDirectory = null, int socksPort = 10808, int httpPort = 10809, int apiPort = 10085)
    {
        if (apiPort is < 1 or > 65535 || socksPort is < 1 or > 65535 || httpPort is < 1 or > 65535 ||
            apiPort == socksPort || apiPort == httpPort || socksPort == httpPort) throw new ArgumentOutOfRangeException(nameof(apiPort));
        SocksPort = socksPort; HttpPort = httpPort; ApiPort = apiPort;
        _runtimeDirectory = runtimeDirectory ?? Path.Combine(SettingsService.AppDirectory, "runtime");
        _job = Native.CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), LocalizationService.Pick("Не удалось создать контейнер процесса xray.", "Could not create the Xray process container."));
        var limits = new Native.JobExtendedLimitInformation
        {
            BasicLimitInformation = new Native.JobBasicLimitInformation { LimitFlags = 0x00002000 }
        };
        int size = Marshal.SizeOf<Native.JobExtendedLimitInformation>();
        IntPtr pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!Native.SetInformationJobObject(_job, 9, pointer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), LocalizationService.Pick("Не удалось настроить завершение дочерних процессов.", "Could not configure child process cleanup."));
        }
        catch { _job.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    public async Task CheckEnvironmentAsync(bool requireGeoData, CancellationToken cancellationToken = default)
    {
        string executable = LocateExecutable();
        string assets = Path.GetDirectoryName(executable)!;
        if (requireGeoData && (!File.Exists(Path.Combine(assets, "geoip.dat")) || !File.Exists(Path.Combine(assets, "geosite.dat"))))
            throw new InvalidOperationException(LocalizationService.Pick("Не найдены geoip.dat и geosite.dat рядом с xray.exe.", "Geo data files are missing next to xray.exe."));
        await CheckPortsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadVersionAsync(CancellationToken cancellationToken = default)
    {
        ExecutablePath = LocateExecutable();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var result = await RunCommandAsync(["version"], timeout.Token).ConfigureAwait(false);
        var match = System.Text.RegularExpressions.Regex.Match(result.Output, @"^Xray\s+(\S+)");
        return result.ExitCode == 0 && match.Success ? match.Groups[1].Value : LocalizationService.Get("Unavailable");
    }

    public async Task StartAsync(string configJson, IEnumerable<string> secrets, bool requireGeoData, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync().ConfigureAwait(false);
            _sanitizer = new LogSanitizer(secrets);
            try
            {
                using JsonDocument document = JsonDocument.Parse(configJson);
                _usesProcessOverride = document.RootElement.GetProperty("inbounds").EnumerateArray().Any(inbound => inbound.TryGetProperty("tag", out JsonElement tag) && tag.GetString() == "process-override" && inbound.GetProperty("port").GetInt32() == 10810);
            }
            catch (JsonException) { throw new InvalidOperationException(LocalizationService.Pick("Некорректная конфигурация Xray.", "Invalid Xray configuration.")); }
            ExecutablePath = LocateExecutable();
            string assets = Path.GetDirectoryName(ExecutablePath)!;
            if (requireGeoData && (!File.Exists(Path.Combine(assets, "geoip.dat")) || !File.Exists(Path.Combine(assets, "geosite.dat"))))
                throw new InvalidOperationException(LocalizationService.Pick("Для маршрутизации России нужны geoip.dat и geosite.dat рядом с xray.exe.", "Russian routing requires geoip.dat and geosite.dat next to xray.exe."));
            await CheckPortsAsync(cancellationToken, _usesProcessOverride).ConfigureAwait(false);
            await Task.Run(PrepareRuntimeDirectory, cancellationToken).ConfigureAwait(false);
            string configPath = Path.Combine(_runtimeDirectory, "config.json");
            await File.WriteAllTextAsync(configPath, configJson, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            using (var validationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                validationTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var validation = await RunCommandAsync(["run", "-test", "-config", configPath], validationTimeout.Token).ConfigureAwait(false);
                if (validation.ExitCode != 0)
                {
                    PublishLines(validation.Output);
                    PublishLines(validation.Error);
                    throw new InvalidOperationException(LocalizationService.Pick("Xray отклонил конфигурацию. Подробности доступны в логах.", "Xray rejected the configuration. Check the log."));
                }
            }

            Process process = await SpawnAsync(["run", "-config", configPath], cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _stopping = false;
                _process = process;
                _sessionCancellation = new CancellationTokenSource();
            }
            _readers = [PumpAsync(process.StandardOutput), PumpAsync(process.StandardError)];
            _ = ObserveExitAsync(process);
            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await WaitForPortsAsync(process, readyTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(LocalizationService.Pick("Xray не открыл локальные порты за 10 секунд. Проверьте логи.", "Xray did not open its ports within 10 seconds. Check the log."));
            }
            Emit(LocalizationService.Pick("Ядро xray запущено. Локальные прокси готовы.", "Xray started."));
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Process? process;
        CancellationTokenSource? sessionCancellation;
        lock (_sync)
        {
            _stopping = true;
            process = _process;
            _process = null;
            sessionCancellation = _sessionCancellation;
            _sessionCancellation = null;
        }
        sessionCancellation?.Cancel();
        if (process is not null)
        {
            Kill(process);
            try { await process.WaitForExitAsync().ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            try { await Task.WhenAll(_readers).ConfigureAwait(false); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            Untrack(process);
            process.Dispose();
            _readers = [];
        }
        // Drain an in-flight stats helper before reporting that all core work has stopped.
        await _stats.WaitAsync().ConfigureAwait(false);
        _stats.Release();
        sessionCancellation?.Dispose();
        await Task.Run(DeleteRuntimeConfiguration).ConfigureAwait(false);
    }

    public async Task<TrafficSnapshot> QueryStatsAsync(CancellationToken cancellationToken = default)
    {
        await _stats.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancellationToken sessionToken;
            lock (_sync)
            {
                if (_process is null || _stopping || _sessionCancellation is null) return new(0, 0);
                sessionToken = _sessionCancellation.Token;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var result = await RunCommandAsync(["api", "statsquery", $"--server=127.0.0.1:{ApiPort}", "-pattern", "inbound>>>", "-timeout=2"], timeout.Token).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(LocalizationService.Pick("Не удалось получить статистику xray.", "Could not read Xray statistics."));
            using JsonDocument document = JsonDocument.Parse(result.Output);
            long uplink = 0;
            long downlink = 0;
            if (document.RootElement.TryGetProperty("stat", out JsonElement stats))
            {
                foreach (JsonElement item in stats.EnumerateArray())
                {
                    string name = item.GetProperty("name").GetString() ?? "";
                    if (!name.StartsWith("inbound>>>socks-in>>>traffic>>>", StringComparison.Ordinal) &&
                        !name.StartsWith("inbound>>>http-in>>>traffic>>>", StringComparison.Ordinal) &&
                        !name.StartsWith("inbound>>>process-override>>>traffic>>>", StringComparison.Ordinal)) continue;
                    if (!item.TryGetProperty("value", out JsonElement valueElement)) continue;
                    long value = valueElement.ValueKind == JsonValueKind.String
                        ? long.Parse(valueElement.GetString()!, CultureInfo.InvariantCulture)
                        : valueElement.GetInt64();
                    value = Math.Max(0, value);
                    if (name.EndsWith(">>>uplink", StringComparison.Ordinal)) uplink += value;
                    if (name.EndsWith(">>>downlink", StringComparison.Ordinal)) downlink += value;
                }
            }
            return new(uplink, downlink);
        }
        finally { _stats.Release(); }
    }

    private static string LocateExecutable()
    {
        string[] locations = [Path.Combine(AppContext.BaseDirectory, "xray", "xray.exe"), Path.Combine(AppContext.BaseDirectory, "xray.exe")];
        return locations.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(LocalizationService.Pick("xray.exe не найден. Поместите ядро в папку xray рядом с Win10ray.exe.", "xray.exe was not found. Place the core in the xray folder next to Win10ray.exe."));
    }

    private Task CheckPortsAsync(CancellationToken cancellationToken, bool processOverride = false) => Task.Run(async () =>
    {
        foreach (int port in new[] { SocksPort, HttpPort, ApiPort }.Concat(processOverride ? [10810] : Array.Empty<int>()))
        {
            var waiting = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var listener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    listener.Server.ExclusiveAddressUse = true;
                    listener.Start();
                    break;
                }
                catch (SocketException)
                {
                    if (waiting.Elapsed >= TimeSpan.FromSeconds(1.5))
                        throw new InvalidOperationException(LocalizationService.Pick($"Порт {port} занят или недоступен. Закройте использующее его приложение.", $"Port {port} is in use or unavailable. Close the application using it."));
                }
                finally { listener.Stop(); }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }, cancellationToken);

    private async Task WaitForPortsAsync(Process process, CancellationToken cancellationToken)
    {
        foreach (int port in new[] { SocksPort, HttpPort, ApiPort }.Concat(_usesProcessOverride ? [10810] : Array.Empty<int>()))
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException(LocalizationService.Pick("Ядро xray завершилось при запуске. Проверьте логи.", "Xray stopped during startup. Check the log."));
                using var client = new TcpClient(AddressFamily.InterNetwork);
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (SocketException) { await Task.Delay(80, cancellationToken).ConfigureAwait(false); }
            }
        }
        if (process.HasExited) throw new InvalidOperationException(LocalizationService.Pick("Ядро xray завершилось при запуске.", "Xray stopped during startup."));
    }

    private async Task<Process> SpawnAsync(string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.Environment["XRAY_LOCATION_ASSET"] = Path.GetDirectoryName(ExecutablePath)!;
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            var process = new Process { StartInfo = startInfo };
            try
            {
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!process.Start()) throw new InvalidOperationException(LocalizationService.Pick("Не удалось запустить xray.exe.", "Could not start xray.exe."));
                    _children.Add(process);
                    if (!Native.AssignProcessToJobObject(_job, process.Handle) && !process.HasExited)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), LocalizationService.Pick("Не удалось связать время жизни xray с приложением.", "Could not bind Xray to the application lifetime."));
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    Kill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return process;
            }
            catch
            {
                Kill(process);
                Untrack(process);
                process.Dispose();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandResult> RunCommandAsync(string[] arguments, CancellationToken cancellationToken)
    {
        Process process = await SpawnAsync(arguments, cancellationToken).ConfigureAwait(false);
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            }
            catch
            {
                Kill(process);
                try { await process.WaitForExitAsync().ConfigureAwait(false); }
                catch (InvalidOperationException) { }
                try { await Task.WhenAll(output, error).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                throw;
            }
        }
        finally
        {
            Untrack(process);
            process.Dispose();
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) Emit(line);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ObserveExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            int exitCode = process.ExitCode;
            bool unexpected;
            lock (_sync) unexpected = !_stopping && ReferenceEquals(_process, process) && !_disposed;
            if (unexpected)
            {
                Emit(LocalizationService.Pick($"Xray завершился, код {exitCode}.", $"Xray stopped with code {exitCode}."));
                try { UnexpectedExit?.Invoke(exitCode); }
                catch { /* A consumer cannot compromise process cleanup. */ }
            }
        }
        catch (InvalidOperationException) { }
    }

    private void PublishLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) Emit(line);
    }

    private void Emit(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        string safeLine = _sanitizer.Sanitize(line);
        try { LogReceived?.Invoke(safeLine); }
        catch { /* Logging must never keep a child process alive. */ }
    }

    private void Untrack(Process process)
    {
        lock (_sync) _children.Remove(process);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private void DeleteRuntimeConfiguration()
    {
        try { File.Delete(Path.Combine(_runtimeDirectory, "config.json")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void PrepareRuntimeDirectory()
    {
        DirectoryInfo directory = Directory.CreateDirectory(_runtimeDirectory);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException(LocalizationService.Pick("Не удалось определить текущего пользователя Windows.", "Could not identify the current Windows user."));
        var permissions = new DirectorySecurity();
        permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        permissions.SetOwner(user);
        permissions.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(permissions);
    }

    /// <summary>Best-effort synchronous fallback for process termination and unhandled exceptions.</summary>
    public void EmergencyStop()
    {
        lock (_sync)
        {
            _disposed = true;
            _stopping = true;
            _sessionCancellation?.Cancel();
            _job.Dispose(); // Windows kills every assigned child even after abrupt app termination.
            foreach (Process child in _children) Kill(child);
            _process = null;
        }
        DeleteRuntimeConfiguration();
    }

    public void Dispose()
    {
        EmergencyStop();
        GC.SuppressFinalize(this);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct JobBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobExtendedLimitInformation
        {
            public JobBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
