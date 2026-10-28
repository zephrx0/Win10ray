using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using XrayMetro.Models;
using XrayMetro.Services;
using XrayMetro.ViewModels;

internal static class ViewModelConnectionTests
{
    public static async Task<int> RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Win10ray-Failover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        int originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var allocated = new HashSet<int> { originPort };
        int firstPort = FreePort(allocated), secondPort = FreePort(allocated);
        const string credential = "feef6b54-1f5a-4e35-bd7a-5b278453f73c";
        var backends = new List<Process>();
        var readers = new List<Task<string>>();
        MainViewModel? vm = null;
        Task serve = ServeAsync(origin, deadline.Token);
        using var core = new XrayProcessService(Path.Combine(directory, "runtime"), FreePort(allocated), FreePort(allocated), FreePort(allocated));
        try
        {
            foreach (int port in new[] { firstPort, secondPort })
            {
                string path = Path.Combine(directory, port + ".json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
                {
                    log = new { access = "none", loglevel = "warning" },
                    inbounds = new[] { new { listen = "127.0.0.1", port, protocol = "vless", settings = new { decryption = "none", clients = new[] { new { id = credential } } } } },
                    outbounds = new[] { new { protocol = "freedom", settings = new { finalRules = new[] { new { action = "allow", network = "tcp", ip = new[] { "127.0.0.1/32" } } } } } }
                }));
                var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "xray", "xray.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
                process.StartInfo.ArgumentList.Add("run"); process.StartInfo.ArgumentList.Add("-config"); process.StartInfo.ArgumentList.Add(path);
                process.Start(); backends.Add(process);
                readers.Add(process.StandardOutput.ReadToEndAsync()); readers.Add(process.StandardError.ReadToEndAsync());
                await WaitAsync(async () => { using var socket = new TcpClient(); try { await socket.ConnectAsync(IPAddress.Loopback, port, deadline.Token); return true; } catch (SocketException) { return false; } }, deadline.Token);
            }
            var parser = new LinkParser();
            ServerProfile unavailable = parser.Parse($"vless://{credential}@127.0.0.1:{FreePort(allocated)}?security=none");
            ServerProfile first = parser.Parse($"vless://{credential}@127.0.0.1:{firstPort}?security=none");
            ServerProfile second = parser.Parse($"vless://{credential}@127.0.0.1:{secondPort}?security=none");
            Guid subscription = Guid.NewGuid();
            unavailable.SubscriptionId = first.SubscriptionId = second.SubscriptionId = subscription;
            var state = new AppState { Servers = [unavailable, first, second], Subscriptions = [new SubscriptionProfile { Id = subscription, Name = "Synthetic", Url = "https://example.com/synthetic" }] };
            var proxy = new FakeProxy();
            vm = new MainViewModel(state, new SettingsService(directory), core, proxy, new ServerHealthService(new Uri($"http://127.0.0.1:{originPort}/health")), TimeSpan.FromMilliseconds(150));
            await vm.ToggleConnectionAsync();
            Assert(vm.IsConnected && vm.SelectedServer == first && proxy.Enabled, "initial unavailable server is skipped");
            backends[0].Kill(true); await backends[0].WaitForExitAsync(deadline.Token);
            await WaitAsync(() => Task.FromResult(vm.IsConnected && !vm.IsBusy && vm.SelectedServer == second), deadline.Token);
            Assert(proxy.Enables == 2 && proxy.Enabled && core.IsRunning, "background failover reconnects through the next server");
            vm.SearchText = "no-matching-server";
            Assert(vm.SelectedServer == second, "search preserves the active server");
            vm.SearchText = "";
            await vm.ToggleConnectionAsync();
            Assert(!vm.IsConnected && !proxy.Enabled && !core.IsRunning, "manual disconnect stops monitoring and the core");
            using var stall = new TcpListener(IPAddress.Loopback, 0); stall.Start();
            ServerProfile stalled = parser.Parse($"vless://{credential}@127.0.0.1:{((IPEndPoint)stall.LocalEndpoint).Port}?security=none");
            stalled.SubscriptionId = subscription;
            vm.Servers.Clear(); vm.Servers.Add(stalled); vm.SelectedServer = stalled;
            Task connecting = vm.ToggleConnectionAsync();
            await WaitAsync(() => Task.FromResult(core.IsRunning), deadline.Token);
            Assert(vm.IsBusy && vm.ConnectionActionCommand == vm.CancelConnectionCommand, "connecting exposes cancellation");
            vm.CancelConnectionCommand.Execute(null);
            await connecting.WaitAsync(deadline.Token);
            Assert(!vm.IsConnected && !vm.IsBusy && !core.IsRunning && !proxy.Enabled, "cancelled failover cleans up processes and proxy");
            await Task.Delay(350, deadline.Token);
            Assert(!vm.IsConnected && !core.IsRunning, "a disconnected monitor does not reconnect later");
            await vm.ShutdownAsync(); vm = null;
            var bootState = new AppState { Servers = [second], Settings = new AppSettings { UseTun = true, UseSystemProxy = false, ResumeConnection = true, AutoConnect = false } };
            var tun = new FakeTun { Failures = 1 };
            vm = new MainViewModel(bootState, new SettingsService(directory), core, proxy, new ServerHealthService(new Uri($"http://127.0.0.1:{originPort}/health")), TimeSpan.FromMilliseconds(150), tun);
            await vm.RestoreConnectionAsync();
            Assert(vm.IsConnected && vm.UseTun && tun.Starts == 2 && !proxy.Enabled, "resume restores TUN and retries a transient service startup failure");
            await vm.ShutdownAsync(); vm = null;
            Assert(bootState.Settings.ResumeConnection && !core.IsRunning && !tun.Running, "application shutdown preserves resume intent while stopping TUN");
            vm = new MainViewModel(bootState, new SettingsService(directory), core, proxy, new ServerHealthService(new Uri($"http://127.0.0.1:{originPort}/health")), TimeSpan.FromMilliseconds(150), tun);
            await vm.RestoreConnectionAsync();
            Assert(vm.IsConnected && tun.Starts == 3, "reopening reconnects the saved TUN mode");
            await vm.ToggleConnectionAsync();
            await vm.RestoreConnectionAsync();
            Assert(!vm.IsConnected && !bootState.Settings.ResumeConnection && tun.Starts == 3, "manual disconnect prevents automatic reconnection");
            tun.Stall = true; bootState.Settings.ResumeConnection = true;
            Task restoring = vm.RestoreConnectionAsync();
            await WaitAsync(() => Task.FromResult(tun.Starts == 4), deadline.Token);
            vm.CancelConnectionCommand.Execute(null);
            await restoring.WaitAsync(deadline.Token);
            Assert(!vm.IsConnected && !vm.IsBusy && !bootState.Settings.ResumeConnection && !core.IsRunning, "startup cancellation stops retries, the core and the pending TUN session");
            await vm.ShutdownAsync(); vm = null;
            return 12;
        }
        finally
        {
            if (vm is not null) await vm.ShutdownAsync();
            await deadline.CancelAsync(); origin.Stop();
            try { await serve; } catch (OperationCanceledException) { } catch (SocketException) { }
            foreach (Process process in backends) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } process.Dispose(); }
            await Task.WhenAll(readers);
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeProxy : ISystemProxyService
    {
        public bool Enabled { get; private set; }
        public int Enables { get; private set; }
        public void Enable() { Enabled = true; Enables++; }
        public void Disable() => Enabled = false;
        public void EmergencyDisable() => Enabled = false;
    }
    private sealed class FakeTun : ITunSessionService
    {
        public event Action<string>? LogReceived { add { } remove { } }
        public event Action? ConnectionLost { add { } remove { } }
        public int Failures { get; set; }
        public int Starts { get; private set; }
        public bool Running { get; private set; }
        public bool Stall { get; set; }
        public Task PrepareAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public async Task StartAsync(CancellationToken token, bool useDnsOverHttps = false, string? dnsOverHttpsUrl = null)
        {
            Starts++;
            if (Failures-- > 0) throw TunServiceClient.Failure("adapter_timeout");
            if (Stall) await Task.Delay(Timeout.Infinite, token);
            Running = true;
        }
        public Task StopAsync() { Running = false; return Task.CompletedTask; }
        public void EmergencyStop() => Running = false;
    }
    private static int FreePort(HashSet<int> used)
    {
        while (true) { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; if (used.Add(port)) return port; }
    }
    private static async Task WaitAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition()) await Task.Delay(50, token);
    }
    private static async Task ServeAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using TcpClient socket = await listener.AcceptTcpClientAsync(token);
            await using NetworkStream stream = socket.GetStream();
            byte[] buffer = new byte[8192]; string request = "";
            while (!request.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int count = await stream.ReadAsync(buffer, token); if (count == 0) break;
                request += Encoding.ASCII.GetString(buffer, 0, count);
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"), token);
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); }
}
