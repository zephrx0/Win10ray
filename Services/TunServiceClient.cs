using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace XrayMetro.Services;

public sealed class TunServiceException(string message, string code) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public bool IsTransient => Code is "ipc_closed" or "adapter_timeout" or "backend_exit" or "start_timeout" or "service_timeout";
}

public interface ITunSessionService
{
    event Action<string>? LogReceived;
    event Action? ConnectionLost;
    Task PrepareAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken, bool useDnsOverHttps = false, string? dnsOverHttpsUrl = null, bool blockAds = true, Models.ProcessRouteRule[]? processRules = null, bool defaultRouteDirect = false);
    Task StopAsync();
    void EmergencyStop();
}

public sealed class TunServiceClient : ITunSessionService
{
    private bool _prepared;
    private string? _token;
    private CancellationTokenSource? _heartbeat;
    private Task? _heartbeatTask;
    public event Action<string>? LogReceived;
    public event Action? ConnectionLost;
    public static string InstallDirectory => TunServicePaths.InstallDirectory;

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (_prepared) return;
        try { await EnsureInstalledAsync(cancellationToken).ConfigureAwait(false); _prepared = true; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Failure("service_timeout"); }
        catch (IOException) { throw Failure("ipc_closed"); }
        catch (UnauthorizedAccessException) { throw Failure("service_access_denied"); }
        catch (Win32Exception exception) { throw Failure("service_win32_" + exception.NativeErrorCode); }
    }

    public async Task StartAsync(CancellationToken cancellationToken, bool useDnsOverHttps = false, string? dnsOverHttpsUrl = null, bool blockAds = true, Models.ProcessRouteRule[]? processRules = null, bool defaultRouteDirect = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { await StartOnceAsync(cancellationToken, useDnsOverHttps, dnsOverHttpsUrl, blockAds, processRules, defaultRouteDirect).ConfigureAwait(false); return; }
            catch (TunServiceException exception) when (attempt == 0 && !cancellationToken.IsCancellationRequested && exception.IsTransient)
            {
                _prepared = false;
                try { LogReceived?.Invoke("[TUN] Startup retry: " + exception.Code); } catch { }
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task StartOnceAsync(CancellationToken cancellationToken, bool useDnsOverHttps, string? dnsOverHttpsUrl, bool blockAds, Models.ProcessRouteRule[]? processRules, bool defaultRouteDirect)
    {
        try
        {
            await PrepareAsync(cancellationToken).ConfigureAwait(false);
            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            TunResponse result = await SendAsync(new("start", _token, useDnsOverHttps, dnsOverHttpsUrl, blockAds, processRules, defaultRouteDirect), TimeSpan.FromSeconds(TunProtocol.ClientRequestSeconds), cancellationToken).ConfigureAwait(false);
            Publish(result);
            if (!result.Ok || !result.Running || string.IsNullOrEmpty(result.Token)) throw Failure(result.Code);
            _token = result.Token;
            _heartbeat = new CancellationTokenSource();
            _heartbeatTask = HeartbeatAsync(_heartbeat.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync().ConfigureAwait(false); _prepared = false; throw Failure("start_timeout");
        }
        catch (IOException) { await StopAsync().ConfigureAwait(false); _prepared = false; throw Failure("ipc_closed"); }
        catch (UnauthorizedAccessException) { await StopAsync().ConfigureAwait(false); _prepared = false; throw Failure("service_access_denied"); }
        catch (Win32Exception exception) { await StopAsync().ConfigureAwait(false); _prepared = false; throw Failure("service_win32_" + exception.NativeErrorCode); }
        catch { await StopAsync().ConfigureAwait(false); throw; }
    }

    public static TunServiceException Failure(string code)
    {
        string key = code switch
        {
            _ when code is "service_win32_577" or "service_win32_225" or "service_win32_226" or "service_win32_1260" or "install_exit_c0000428" => "TunWindowsBlocked",
            _ when code.StartsWith("install_", StringComparison.Ordinal) && (code.EndsWith("_577", StringComparison.Ordinal) || code.EndsWith("_225", StringComparison.Ordinal) || code.EndsWith("_226", StringComparison.Ordinal) || code.EndsWith("_1260", StringComparison.Ordinal)) => "TunWindowsBlocked",
            "install_start_service_1068" or "service_win32_1068" => "TunDependencyFailed",
            _ when code.StartsWith("install_app_", StringComparison.Ordinal) || code.StartsWith("install_data_", StringComparison.Ordinal) => "TunInstallStorage",
            _ when code.StartsWith("install_", StringComparison.Ordinal) => "TunInstallFailed",
            "busy" => "TunBusy", "adapter_timeout" => "TunAdapterTimeout", "backend_exit" => "TunBackendExit",
            "ipc_closed" => "TunPipeClosed", "service_access_denied" => "TunAccessDenied",
            "xray_unavailable" => "TunXrayUnavailable", "service_timeout" => "TunUnavailable",
            "start_timeout" => "TunAdapterTimeout", "invalid_dns" => "InvalidDoh", _ => "TunStartFailed"
        };
        string safeCode = System.Text.RegularExpressions.Regex.IsMatch(code, "^[a-z0-9_]{1,48}$") ? code : "start_failed";
        return new TunServiceException(LocalizationService.Get(key) + " [" + safeCode + "]", safeCode);
    }

    private static async Task EnsureInstalledAsync(CancellationToken token)
    {
        int? version = await ProbeAsync(token).ConfigureAwait(false);
        if (version == TunProtocol.CurrentVersion) return;
        bool update = version.HasValue;
        string installed = Path.Combine(InstallDirectory, "Win10ray.TunService.exe");
        if (!update && File.Exists(installed) && File.Exists(Path.Combine(InstallDirectory, "installed.v1")))
        {
            await StartInstalledServiceAsync(token).ConfigureAwait(false);
            var readiness = Stopwatch.StartNew();
            while (readiness.Elapsed < TimeSpan.FromSeconds(12))
            {
                version = await ProbeAsync(token).ConfigureAwait(false);
                if (version == TunProtocol.CurrentVersion) return;
                if (version.HasValue) { update = true; break; }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            if (!update) throw Failure("service_timeout");
        }
        string helper = Path.Combine(AppContext.BaseDirectory, "service", "Win10ray.TunService.exe");
        if (!File.Exists(helper) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "tun", "sing-box.exe")) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "tun", "wintun.dll")))
            throw new InvalidOperationException(LocalizationService.Get("TunMissing"));
        try { await TunComponentIntegrity.VerifyAsync(Path.Combine(AppContext.BaseDirectory, "tun"), token).ConfigureAwait(false); }
        catch (IOException) { throw new InvalidOperationException(LocalizationService.Get("TunMissing")); }
        string sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) sid = identity.User!.Value;
        var info = new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--install"); info.ArgumentList.Add(sid);
        try
        {
            // Elevation is used only by this installer. All later TUN requests use the protected pipe.
            using Process installer = Process.Start(info) ?? throw new InvalidOperationException(LocalizationService.Get("TunInstallFailed"));
            await installer.WaitForExitAsync().ConfigureAwait(false);
            if (installer.ExitCode != 0) throw Failure(TunInstallDiagnostics.ErrorCode(installer.ExitCode));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(LocalizationService.Get("TunUacCancelled"));
        }
        token.ThrowIfCancellationRequested();
        for (int attempt = 0; attempt < 25; attempt++)
        {
            if (await ProbeAsync(token).ConfigureAwait(false) == TunProtocol.CurrentVersion) return;
            await Task.Delay(200, token).ConfigureAwait(false);
        }
        throw Failure("service_timeout");
    }

    private static async Task StartInstalledServiceAsync(CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true }
        };
        process.StartInfo.ArgumentList.Add("start"); process.StartInfo.ArgumentList.Add(TunProtocol.ServiceName);
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(token), error = process.StandardError.ReadToEndAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode is not (0 or 1056)) throw Failure("service_win32_" + process.ExitCode);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static async Task<int?> ProbeAsync(CancellationToken token)
    {
        try { TunResponse response = await SendAsync(new("ping"), TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false); return response.Ok ? response.ProtocolVersion : null; }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            token.ThrowIfCancellationRequested(); return null;
        }
    }
    private async Task HeartbeatAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                TunResponse response = await SendAsync(new("heartbeat", _token), TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                Publish(response);
                if (!response.Ok || !response.Running) throw new IOException();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { _prepared = false; try { ConnectionLost?.Invoke(); } catch { } }
    }
    private void Publish(TunResponse response)
    {
        foreach (string line in response.Logs ?? [])
            try { LogReceived?.Invoke("[TUN] " + line); } catch { }
    }
    public async Task StopAsync()
    {
        _heartbeat?.Cancel();
        if (_heartbeatTask is not null) await _heartbeatTask.ConfigureAwait(false);
        _heartbeat?.Dispose(); _heartbeat = null; _heartbeatTask = null;
        string? session = _token; _token = null;
        if (session is null) return;
        try { _ = await SendAsync(new("stop", session), TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
        catch { /* The service independently removes the adapter after heartbeat timeout or owner exit. */ }
    }

    public void EmergencyStop()
    {
        _heartbeat?.Cancel();
        string? session = _token; _token = null;
        if (session is not null) _ = Task.Run(async () =>
        {
            try { _ = await SendAsync(new("stop", session), TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
            catch { }
        });
    }

    private static async Task<TunResponse> SendAsync(TunRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(timeout);
        using var pipe = new NamedPipeClientStream(".", TunProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        ValidateServer(pipe, request.Command == "ping");
        await TunProtocol.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
        return await TunProtocol.ReadAsync<TunResponse>(pipe, deadline.Token).ConfigureAwait(false);
    }
    private static void ValidateServer(NamedPipeClientStream pipe, bool probeOnly)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) throw new UnauthorizedAccessException();
        using SafeProcessHandle process = OpenProcess(0x1000, false, pid);
        var path = new StringBuilder(32768); int size = path.Capacity;
        if (process.IsInvalid || !QueryFullProcessImageName(process, 0, path, ref size) ||
            !TunServicePaths.IsAllowedImage(path.ToString(), probeOnly))
            throw new UnauthorizedAccessException();
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
}
