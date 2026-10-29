using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XrayMetro.Models;

namespace XrayMetro.Services;

public sealed class OpenVpnProcessService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OwnedProcessJob _job = new();
    private readonly string _directory;
    private readonly string _executable;
    private Process? _process;
    private CancellationTokenSource? _monitor;
    private Task[] _readers = [];
    private volatile bool _stopping;
    private bool _disposed;
    private int _apiPort;
    private string _secret = "";
    private LogSanitizer _sanitizer = new([]);
    public int SocksPort { get; private set; }
    public event Action<string>? LogReceived;
    public event Action<int>? UnexpectedExit;
    public bool IsRunning { get { try { return _process is { HasExited: false }; } catch (InvalidOperationException) { return false; } } }

    public OpenVpnProcessService(string? directory = null, string? executable = null)
    {
        _directory = directory ?? Path.Combine(SettingsService.AppDirectory, "runtime", "openvpn");
        _executable = executable ?? Path.Combine(AppContext.BaseDirectory, "tun", "sing-box.exe");
    }
    public async Task<string> ReadVersionAsync(CancellationToken token)
    {
        if (!File.Exists(_executable)) return LocalizationService.Get("CoreMissing");
        var version = await CommandAsync(["version"], token).ConfigureAwait(false);
        Match match = Regex.Match(version.Text, @"sing-box version (\S+)");
        return match.Success ? "sing-box " + match.Groups[1].Value : LocalizationService.Get("Unavailable");
    }

    public async Task StartAsync(ServerProfile profile, string? username, string? password, CancellationToken token, bool useDnsOverHttps = false, string? dnsOverHttpsUrl = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync().ConfigureAwait(false);
            if (!File.Exists(_executable)) throw new FileNotFoundException(LocalizationService.Get("OpenVpnCoreMissing"));
            await TunComponentIntegrity.VerifyAsync(Path.GetDirectoryName(_executable)!, token).ConfigureAwait(false);
            _stopping = false;
            _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _sanitizer = new LogSanitizer(Secrets(profile).Concat(new[] { username ?? "", password ?? "", _secret }));
            // Keep both sockets reserved while allocating their distinct ports.
            using (TcpListener socks = new(IPAddress.Loopback, 0))
            using (TcpListener api = new(IPAddress.Loopback, 0))
            {
                socks.Server.ExclusiveAddressUse = api.Server.ExclusiveAddressUse = true;
                socks.Start(); api.Start(); SocksPort = ((IPEndPoint)socks.LocalEndpoint).Port; _apiPort = ((IPEndPoint)api.LocalEndpoint).Port;
            }
            string config = OpenVpnConfigBuilder.Build(profile, SocksPort, _apiPort, _secret, username, password, useDnsOverHttps, dnsOverHttpsUrl);
            // Resolve VPN endpoints with Windows before TUN starts, including private DNS names.
            System.Text.Json.Nodes.JsonNode document = System.Text.Json.Nodes.JsonNode.Parse(config)!;
            foreach (System.Text.Json.Nodes.JsonNode? remote in document["endpoints"]![0]!["servers"]!.AsArray())
            {
                string host = remote!["server"]!.GetValue<string>();
                if (IPAddress.TryParse(host, out _)) continue;
                using var resolve = CancellationTokenSource.CreateLinkedTokenSource(token); resolve.CancelAfter(TimeSpan.FromSeconds(5));
                IPAddress[] addresses;
                try { addresses = await Dns.GetHostAddressesAsync(host, resolve.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is SocketException || exception is OperationCanceledException && !token.IsCancellationRequested)
                { throw new InvalidOperationException(LocalizationService.Get("OpenVpnResolveFailed")); }
                string network = remote["network"]!.GetValue<string>();
                IPAddress? address = addresses.FirstOrDefault(item => network.EndsWith('6') ? item.AddressFamily == AddressFamily.InterNetworkV6 : item.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (address is null) throw new InvalidOperationException(LocalizationService.Get("OpenVpnStartFailed"));
                remote["server"] = address.ToString();
            }
            config = document.ToJsonString();
            using (var validation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                validation.CancelAfter(TimeSpan.FromSeconds(15));
                var check = await CommandAsync(["--disable-color", "check", "-c", "stdin"], validation.Token, input: config).ConfigureAwait(false);
                if (check.Code != 0) { Publish(check.Text); throw new InvalidOperationException(LocalizationService.Get("OpenVpnConfigRejected")); }
            }
            Process process = Start(["--disable-color", "run", "-c", "stdin"], redirectInput: true); _process = process;
            _readers = [ReadAsync(process.StandardOutput), ReadAsync(process.StandardError)];
            await process.StandardInput.WriteAsync(config.AsMemory(), token).ConfigureAwait(false);
            process.StandardInput.Close();
            _monitor = new CancellationTokenSource();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(65));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException(LocalizationService.Get("OpenVpnStartFailed"));
                string state = await StateAsync(deadline.Token).ConfigureAwait(false);
                if (state == "connected") break;
                if (state is "error" or "auth-pending") throw new InvalidOperationException(LocalizationService.Get(state == "auth-pending" ? "OpenVpnAuthFailed" : "OpenVpnStartFailed"));
                await Task.Delay(500, deadline.Token).ConfigureAwait(false);
            }
            Publish(LocalizationService.Get("OpenVpnConnected"));
            _ = MonitorAsync(process, _monitor.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { await StopCoreAsync().ConfigureAwait(false); throw new InvalidOperationException(LocalizationService.Get("OpenVpnTimeout")); }
        catch { await StopCoreAsync().ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }
    private async Task<string> StateAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var response = await CommandAsync(["api", "--url", "http://127.0.0.1:" + _apiPort, "openvpn", "status"], timeout.Token, _secret).ConfigureAwait(false);
            if (response.Code != 0) return "connecting";
            Match match = Regex.Match(response.Text, @"(?im)^State\s*:?\s+(\S+)");
            return match.Success ? match.Groups[1].Value.ToLowerInvariant().Replace('_', '-') : "connecting";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return "connecting"; }
    }
    private async Task MonitorAsync(Process process, CancellationToken token)
    {
        try
        {
            int reconnects = 0;
            while (!token.IsCancellationRequested && !_stopping)
            {
                await Task.Delay(5000, token).ConfigureAwait(false);
                if (process.HasExited) break;
                string state = await StateAsync(token).ConfigureAwait(false);
                if (state == "connected") reconnects = 0;
                else if (state is "error" or "auth-pending" || ++reconnects >= 6) break;
            }
            if (!token.IsCancellationRequested && !_stopping) UnexpectedExit?.Invoke(1);
        }
        catch (OperationCanceledException) { }
        catch { if (!token.IsCancellationRequested && !_stopping) UnexpectedExit?.Invoke(1); }
    }
    public async Task StopAsync() { await _gate.WaitAsync().ConfigureAwait(false); try { await StopCoreAsync().ConfigureAwait(false); } finally { _gate.Release(); } }
    private async Task StopCoreAsync()
    {
        _stopping = true; _monitor?.Cancel(); _monitor?.Dispose(); _monitor = null;
        Process? process = _process; _process = null;
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
            await Task.WhenAll(_readers).ConfigureAwait(false); _readers = []; process.Dispose();
        }
        try { File.Delete(Path.Combine(_directory, "config.json")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _secret = ""; _sanitizer = new([]);
    }
    private Process Start(string[] arguments, string? apiSecret = null, bool redirectInput = false)
    {
        ProcessStartInfo info = new(_executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(_executable)!, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = redirectInput, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (redirectInput) info.StandardInputEncoding = new UTF8Encoding(false);
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        if (apiSecret is not null) info.Environment["BOX_API_SECRET"] = apiSecret;
        Process process = new() { StartInfo = info };
        try { process.Start(); _job.Assign(process); return process; } catch { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } process.Dispose(); throw; }
    }
    private async Task<(int Code, string Text)> CommandAsync(string[] arguments, CancellationToken token, string? apiSecret = null, string? input = null)
    {
        using Process process = Start(arguments, apiSecret, input is not null);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null) { await process.StandardInput.WriteAsync(input.AsMemory(), token).ConfigureAwait(false); process.StandardInput.Close(); }
            await process.WaitForExitAsync(token).ConfigureAwait(false); return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
        }
        finally { try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } } catch (InvalidOperationException) { } }
    }
    private async Task ReadAsync(StreamReader reader) { try { while (await reader.ReadLineAsync().ConfigureAwait(false) is string line) Publish(line); } catch (IOException) { } }
    private void Publish(string line)
    {
        string safe = _sanitizer.Sanitize(line);
        safe = Regex.Replace(safe, @"https?://\S+", "[url]", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        try { LogReceived?.Invoke("[OpenVPN] " + safe); } catch { }
    }
    public static IEnumerable<string> Secrets(ServerProfile profile) => new[] { profile.OpenVpnConfig, profile.OpenVpnUsername, profile.OpenVpnPassword, profile.OpenVpnSourceUrl }
        .Concat(profile.OpenVpnConfig.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(line => line.Trim().Length > 10));
    public void EmergencyStop() { _stopping = true; _monitor?.Cancel(); _job.Dispose(); try { File.Delete(Path.Combine(_directory, "config.json")); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    public void Dispose() { if (_disposed) return; _disposed = true; EmergencyStop(); }
}
