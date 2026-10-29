using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using XrayMetro.Services;

namespace Win10ray.TunService;

internal sealed class TunEngine : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SafeFileHandle _job;
    private readonly Queue<string> _logs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Process? _process;
    private int _owner;
    private long _ownerTicks;
    private string? _token;
    private long _lastBeat;
    private Task[] _readers = [];
    private CancellationTokenSource? _starting;
    private readonly ConcurrentDictionary<(int Pid, string Token), long> _cancelledStarts = new();
    public TunEngine()
    {
        _job = Job.Create();
        _ = MonitorAsync();
    }

    public static async Task SelfTestAsync()
    {
        BackendStartup.SelfTest();
        using var engine = new TunEngine();
        await engine._gate.WaitAsync();
        using var starting = new CancellationTokenSource();
        try
        {
            TunResponse ping = await engine.ExecuteAsync(new("ping"), Environment.ProcessId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            if (!ping.Ok || ping.ProtocolVersion != TunProtocol.CurrentVersion) throw new InvalidOperationException();
            engine._owner = Environment.ProcessId; engine._token = new string('A', 64); engine._starting = starting;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await engine.ExecuteAsync(new("stop", new string('B', 64)), Environment.ProcessId, cancelled.Token); } catch (OperationCanceledException) { }
            if (starting.IsCancellationRequested) throw new InvalidOperationException("An invalid session cancelled startup.");
            try { await engine.ExecuteAsync(new("stop", engine._token), Environment.ProcessId, cancelled.Token); } catch (OperationCanceledException) { }
            if (!starting.IsCancellationRequested) throw new InvalidOperationException("The owning client could not cancel pending startup.");
            try { await engine.ExecuteAsync(new("stop", new string('C', 64)), Environment.ProcessId, cancelled.Token); } catch (OperationCanceledException) { }
        }
        finally { engine._owner = 0; engine._token = null; engine._starting = null; engine._gate.Release(); }
        TunResponse cancelledStart = await engine.ExecuteAsync(new("start", new string('C', 64)), Environment.ProcessId, CancellationToken.None);
        if (cancelledStart.Code != "start_cancelled") throw new InvalidOperationException("A cancelled request was started after its stop request.");
    }

    public async Task<TunResponse> ExecuteAsync(TunRequest request, int clientPid, CancellationToken cancellationToken)
    {
        if (request.Command == "ping") return new(true, IsRunning(), ProtocolVersion: TunProtocol.CurrentVersion);
        if (request.Command == "stop" && request.Token is { Length: 64 } stopToken && stopToken.All(Uri.IsHexDigit))
        {
            _cancelledStarts[(clientPid, stopToken)] = Environment.TickCount64;
            foreach (var cancelled in _cancelledStarts)
                if (Environment.TickCount64 - cancelled.Value > 30000 || _cancelledStarts.Count > 512) _cancelledStarts.TryRemove(cancelled.Key, out _);
        }
        if (request.Command == "stop" && Authorized(clientPid, request.Token))
            try { _starting?.Cancel(); } catch (ObjectDisposedException) { }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            bool running = IsRunning();
            if (request.Command == "start")
            {
                if (request.Token is string session && _cancelledStarts.TryRemove((clientPid, session), out long cancelledAt) && Environment.TickCount64 - cancelledAt <= 30000)
                    return new(false, running, "start_cancelled");
                if (request.UseDnsOverHttps && !DnsEndpoint.IsValid(request.DnsOverHttpsUrl)) return new(false, running, "invalid_dns");
                if (running && ValidOwner())
                {
                    if (_owner != clientPid) return new(false, true, "busy");
                }
                await StopCoreAsync();
                using Process client = Process.GetProcessById(clientPid);
                _owner = clientPid; _ownerTicks = client.StartTime.ToUniversalTime().Ticks;
                _token = request.Token is { Length: 64 } && request.Token.All(Uri.IsHexDigit) ? request.Token : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _starting = starting;
                starting.CancelAfter(TimeSpan.FromSeconds(TunProtocol.AdapterStartSeconds));
                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync("127.0.0.1", 10808, starting.Token);
                    await StartCoreAsync(request, starting.Token);
                    _lastBeat = Environment.TickCount64;
                    return new(true, true, Token: _token, Logs: DrainLogs());
                }
                catch (OperationCanceledException)
                {
                    await StopCoreAsync();
                    return new(false, false, "adapter_timeout", Logs: DrainLogs());
                }
                catch (Exception exception)
                {
                    await StopCoreAsync();
                    string code = exception switch { SocketException => "xray_unavailable", UnauthorizedAccessException => "service_access_denied", System.ComponentModel.Win32Exception win32 => "driver_" + win32.NativeErrorCode, InvalidOperationException => "backend_exit", _ => "start_failed" };
                    return new(false, false, code, Logs: DrainLogs());
                }
                finally { _starting = null; }
            }
            if (!Authorized(clientPid, request.Token)) return new(false, running, "denied");
            if (request.Command == "heartbeat")
            {
                _lastBeat = Environment.TickCount64;
                return new(running, running, running ? "" : "stopped", Logs: DrainLogs());
            }
            if (request.Command == "stop") { await StopCoreAsync(); return new(true); }
            return new(false, running, "invalid_command");
        }
        finally { _gate.Release(); }
    }

    private bool Authorized(int clientPid, string? token)
    {
        string? current = _token;
        return _owner == clientPid && current is not null && token is not null && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(current), System.Text.Encoding.UTF8.GetBytes(token));
    }

    private async Task StartCoreAsync(TunRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Installer.DataDirectory);
        string config = Path.Combine(Installer.DataDirectory, "tun.json");
        // Wintun derives its GUID from the name. Avoid a device still being removed by PnP.
        string interfaceName = "Win10ray-" + Guid.NewGuid().ToString("N")[..12];
        bool ipv4Only = request.ProcessRules?.Any(rule => rule.Action == XrayMetro.Models.ProcessRouteAction.Direct) == true && TunNetworkCapabilities.RequiresIpv4Only();
        lock (_logs) _logs.Enqueue(ipv4Only ? "TUN DNS: IPv4 (no IPv6 gateway)." : "TUN DNS: IPv4/IPv6.");
        await File.WriteAllTextAsync(config, TunConfigBuilder.Build(request.UseDnsOverHttps, request.DnsOverHttpsUrl, interfaceName, request.BlockAds, request.ProcessRules, request.DefaultRouteDirect, ipv4Only), cancellationToken);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "sing-box.exe"))
        {
            WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(config);
        var process = new Process { StartInfo = start };
        _process = process;
        process.Start();
        if (!Job.AssignProcessToJobObject(_job, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var startup = new BackendStartup();
        _readers = [ReadAsync(process.StandardOutput, startup), ReadAsync(process.StandardError, startup)];
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            Task exited = process.WaitForExitAsync(waiting.Token);
            await Task.WhenAny(startup.Ready, exited).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited || !startup.Ready.IsCompletedSuccessfully) throw new InvalidOperationException();
        }
        finally { waiting.Cancel(); }
    }

    private async Task ReadAsync(StreamReader reader, BackendStartup startup)
    {
        try
        {
            while (await reader.ReadLineAsync() is string line)
            {
                line = BackendStartup.Clean(line);
                bool ready = startup.Observe(line);
                // INFO supplies the handshake; do not collect per-connection traffic after startup.
                if (startup.Ready.IsCompleted && !ready && !Regex.IsMatch(line, @"\b(WARN|ERROR|FATAL)\b")) continue;
                try { line = Regex.Replace(line, @"https://\S+", "[DoH]", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
                catch (RegexMatchTimeoutException) { line = "DNS request failed."; }
                lock (_logs)
                {
                    _logs.Enqueue(line.Length > 800 ? line[..800] : line);
                    while (_logs.Count > 20) _logs.Dequeue();
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    private string[] DrainLogs() { lock (_logs) { string[] lines = _logs.ToArray(); _logs.Clear(); return lines; } }
    private bool IsRunning()
    {
        try { return _process is { HasExited: false }; } catch (InvalidOperationException) { return false; }
    }
    private bool ValidOwner()
    {
        try { using Process owner = Process.GetProcessById(_owner); return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == _ownerTicks; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    private async Task MonitorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                await _gate.WaitAsync(_shutdown.Token);
                try
                {
                    if (_process is not null && (!IsRunning() || !ValidOwner() || Environment.TickCount64 - _lastBeat > 10000)) await StopCoreAsync();
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch { _job.Dispose(); }
    }
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); } finally { _gate.Release(); }
    }
    private async Task StopCoreAsync()
    {
        Process? process = _process; _process = null;
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
            catch (InvalidOperationException) { }
            await Task.WhenAll(_readers); process.Dispose(); _readers = [];
        }
        _owner = 0; _ownerTicks = 0; _token = null;
        try { File.Delete(Path.Combine(Installer.DataDirectory, "tun.json")); } catch (IOException) { }
    }
    public void Dispose() { _shutdown.Cancel(); _job.Dispose(); }

    private static class Job
    {
        public static SafeFileHandle Create()
        {
            SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) throw new System.ComponentModel.Win32Exception();
            var limits = new Limits { Basic = new BasicLimits { Flags = 0x2000 } };
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<Limits>());
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(job, 9, buffer, (uint)Marshal.SizeOf<Limits>())) throw new System.ComponentModel.Win32Exception();
                return job;
            }
            catch { job.Dispose(); throw; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr Min, Max; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong A, B, C, D, E, F; }
        [StructLayout(LayoutKind.Sequential)] private struct Limits { public BasicLimits Basic; public Io Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int info, IntPtr buffer, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
