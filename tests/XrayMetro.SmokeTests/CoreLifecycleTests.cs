using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using XrayMetro.Models;
using XrayMetro.Services;

internal static class CoreLifecycleTests
{
    public static async Task<int> RunAsync(string executable)
    {
        string root = Path.Combine(Path.GetTempPath(), "XrayMetro-CoreSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string stagedCore = Path.Combine(AppContext.BaseDirectory, "xray");
        Directory.CreateDirectory(stagedCore);
        File.Copy(executable, Path.Combine(stagedCore, "xray.exe"), true);
        int backendPort;
        using (TcpListener reservation = new(IPAddress.Loopback, 0))
        {
            reservation.Start();
            backendPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        const string credential = "feef6b54-1f5a-4e35-bd7a-5b278453f73c";
        string backendConfig = JsonSerializer.Serialize(new
        {
            log = new { access = "none", loglevel = "warning" },
            inbounds = new[] { new { listen = "127.0.0.1", port = backendPort, protocol = "vless", settings = new { decryption = "none", clients = new[] { new { id = credential } } } } },
            // Recent cores block private destinations for server-side inbounds by default.
            // This synthetic server explicitly permits only the loopback test origin.
            outbounds = new[] { new { protocol = "freedom", settings = new { finalRules = new[] { new { action = "allow", network = "tcp", ip = new[] { "127.0.0.1/32" } } } } } }
        });
        string backendConfigPath = Path.Combine(root, "backend.json");
        await File.WriteAllTextAsync(backendConfigPath, backendConfig);
        using Process backend = new()
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        backend.StartInfo.ArgumentList.Add("run");
        backend.StartInfo.ArgumentList.Add("-config");
        backend.StartInfo.ArgumentList.Add(backendConfigPath);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using TcpListener origin = new(IPAddress.Loopback, 0);
        var allocatedPorts = new HashSet<int> { backendPort };
        int socksPort = FreePort(allocatedPorts), httpPort = FreePort(allocatedPorts), apiPort = FreePort(allocatedPorts);
        using XrayProcessService client = new(Path.Combine(root, "runtime"), socksPort, httpPort, apiPort);
        List<string> logs = [];
        client.LogReceived += line => { lock (logs) logs.Add(line); };
        ServerProfile profile = new LinkParser().Parse($"vless://{credential}@127.0.0.1:{backendPort}?encryption=none&security=none");
        string clientConfig = new XrayConfigBuilder().Build(profile, new AppSettings(), apiPort, socksPort, httpPort);
        try
        {
            string stagedExecutable = Path.Combine(stagedCore, "xray.exe");
            string stagedBackup = stagedExecutable + ".test-backup";
            File.Move(stagedExecutable, stagedBackup, true);
            try
            {
                try { await client.StartAsync(clientConfig, [credential], false, timeout.Token); throw new InvalidOperationException("FAIL: missing core was accepted"); }
                catch (FileNotFoundException) { }
            }
            finally { File.Move(stagedBackup, stagedExecutable, true); }
            using (TcpListener occupied = new(IPAddress.Loopback, socksPort))
            {
                occupied.Start();
                try { await client.StartAsync(clientConfig, [credential], false, timeout.Token); throw new InvalidOperationException("FAIL: occupied port was accepted"); }
                catch (InvalidOperationException exception) when (exception.Message.Contains(socksPort.ToString(), StringComparison.Ordinal)) { }
            }
            try { await client.StartAsync("{", [credential], false, timeout.Token); throw new InvalidOperationException("FAIL: invalid config was accepted"); }
            catch (InvalidOperationException exception) when (!exception.Message.StartsWith("FAIL:", StringComparison.Ordinal)) { }
            backend.Start();
            Task<string> backendOutput = backend.StandardOutput.ReadToEndAsync();
            Task<string> backendError = backend.StandardError.ReadToEndAsync();
            await WaitForPortAsync(backendPort, timeout.Token);
            origin.Start();
            int originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
            Task serve = ServeHttpAsync(origin, timeout.Token);
            await client.StartAsync(clientConfig, [credential], false, timeout.Token);
            Assert(client.IsRunning, "core is running");
            using HttpClient http = new(new HttpClientHandler { UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{httpPort}") });
            string response;
            try { response = await http.GetStringAsync($"http://127.0.0.1:{originPort}/integration", timeout.Token); }
            catch
            {
                backend.Kill(true); await backend.WaitForExitAsync();
                var sanitizer = new LogSanitizer([credential]);
                Console.Error.WriteLine(sanitizer.Sanitize(await backendOutput + await backendError));
                lock (logs) foreach (string line in logs) Console.Error.WriteLine(line);
                throw;
            }
            await serve;
            Assert(response == "xray-loopback-ok", "HTTP traffic traversed local Xray client and VLESS backend");
            TrafficSnapshot counters = await client.QueryStatsAsync(timeout.Token);
            Assert(counters.UplinkBytes > 0 && counters.DownlinkBytes > 0, "statsquery reports real bidirectional traffic");
            await client.StopAsync();
            // Route the real HTTP fixture through the process-override tag, without using
            // any system proxy settings or creating a TUN adapter.
            System.Text.Json.Nodes.JsonNode processConfig = System.Text.Json.Nodes.JsonNode.Parse(clientConfig)!;
            processConfig["inbounds"]![1]!["tag"] = "process-override";
            await client.StartAsync(processConfig.ToJsonString(), [credential], false, timeout.Token);
            Task processServe = ServeHttpAsync(origin, timeout.Token);
            Assert(await http.GetStringAsync($"http://127.0.0.1:{originPort}/process-stats", timeout.Token) == "xray-loopback-ok", "process override traffic reaches the test origin");
            await processServe;
            TrafficSnapshot processCounters = await client.QueryStatsAsync(timeout.Token);
            Assert(processCounters.UplinkBytes > 0 && processCounters.DownlinkBytes > 0, "process override is included in session traffic counters");
            await client.StopAsync();
            Assert(!client.IsRunning, "core stopped");
            Assert(!File.Exists(Path.Combine(root, "runtime", "config.json")), "runtime credentials removed on stop");
            lock (logs) Assert(logs.All(line => !line.Contains(credential, StringComparison.Ordinal)), "process logs redact credentials");
            foreach (int port in new[] { socksPort, httpPort, apiPort })
            {
                using TcpListener reusable = new(IPAddress.Loopback, port);
                reusable.Start();
            }
            Assert(System.Text.RegularExpressions.Regex.IsMatch(await client.ReadVersionAsync(timeout.Token), @"^\d+\.\d+"), "version comes from the actual core");
            var subscriptionId = Guid.NewGuid();
            ServerProfile invalidAuth = new LinkParser().Parse($"vless://{Guid.NewGuid()}@127.0.0.1:{backendPort}?encryption=none&security=none");
            ServerProfile unreachable = new LinkParser().Parse($"vless://{credential}@127.0.0.1:{FreePort(allocatedPorts)}?encryption=none&security=none");
            profile.SubscriptionId = invalidAuth.SubscriptionId = unreachable.SubscriptionId = subscriptionId;
            ServerProfile foreign = new LinkParser().Parse($"vless://{credential}@127.0.0.1:{backendPort}?encryption=none&security=none");
            foreign.SubscriptionId = Guid.NewGuid();
            var health = new ServerHealthService(new Uri($"http://127.0.0.1:{originPort}/health"));
            Task serveHealth = ServeHttpAsync(origin, timeout.Token);
            var attempted = new List<Guid>();
            ServerProfile selected = await ServerFailoverService.ConnectAsync(ServerFailoverService.Candidates([unreachable, foreign, invalidAuth, profile], unreachable), async (candidate, token) =>
            {
                attempted.Add(candidate.Id);
                if (await health.PingAsync(candidate, token) is null) return false;
                await client.StartAsync(new XrayConfigBuilder().Build(candidate, new AppSettings(), apiPort, socksPort, httpPort), [candidate.Credential], false, token);
                bool working = await health.CheckTunnelAsync(httpPort, token);
                if (!working) await client.StopAsync();
                return working;
            }, timeout.Token);
            await serveHealth;
            Assert(selected.Id == profile.Id && attempted.SequenceEqual(new[] { unreachable.Id, invalidAuth.Id, profile.Id }), "failover skips a refused port and invalid authentication, excludes a foreign subscription");
            await client.StopAsync();
            Assert(!await health.CheckTunnelAsync(httpPort, timeout.Token), "health detects a stopped proxy");
            backend.Kill(true);
            await backend.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(backendOutput, backendError);
            return 17;
        }
        finally
        {
            await client.StopAsync();
            try { if (!backend.HasExited) { backend.Kill(true); await backend.WaitForExitAsync(); } }
            catch (InvalidOperationException) { }
            Directory.Delete(root, true);
        }
    }

    private static int FreePort(HashSet<int> allocated)
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (allocated.Add(port)) return port;
        }
    }

    private static async Task WaitForPortAsync(int port, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using TcpClient probe = new();
            try { await probe.ConnectAsync(IPAddress.Loopback, port, cancellationToken); return; }
            catch (SocketException) { await Task.Delay(50, cancellationToken); }
        }
    }

    private static async Task ServeHttpAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = connection.GetStream();
        byte[] buffer = new byte[1024];
        string request = "";
        while (!request.Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) throw new IOException("Unexpected loopback origin disconnect.");
            request += Encoding.ASCII.GetString(buffer, 0, count);
            if (request.Length > 16384) throw new IOException("Oversized test request.");
        }
        string body = "xray-loopback-ok";
        byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
        await stream.WriteAsync(response, cancellationToken);
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
    }
}
