using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using XrayMetro.Services;

internal static class TunTests
{
    public static async Task<int> RunAsync(string executable, string? helper)
    {
        int passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            passed++;
        }
        await using (var memory = new MemoryStream())
        {
            await TunProtocol.WriteAsync(memory, new TunRequest("heartbeat", "synthetic-session"), CancellationToken.None);
            memory.Position = 0;
            TunRequest restored = await TunProtocol.ReadAsync<TunRequest>(memory, CancellationToken.None);
            Check(restored.Command == "heartbeat" && restored.Token == "synthetic-session", "IPC frame round-trip");
        }
        Check(JsonSerializer.Deserialize<TunResponse>("{\"Ok\":true}")!.ProtocolVersion == 0 && TunProtocol.CurrentVersion == 6, "legacy service detected for one-time upgrade");
        Check(Path.GetDirectoryName(TunServicePaths.InstallDirectory) == Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "privileged service is independent of the application install folder");
        string legacyImage = Path.Combine(TunServicePaths.LegacyDirectory, "Win10ray.TunService.exe");
        Check(TunServicePaths.IsAllowedImage(legacyImage, true) && !TunServicePaths.IsAllowedImage(legacyImage, false), "legacy helper can only receive a public version probe, never session tokens");
        Check(!TunServicePaths.IsAllowedImage(Path.Combine(Path.GetTempPath(), "Win10ray.TunService.exe"), true), "user-writable helpers cannot impersonate the service");
        Check(TunProtocol.AdapterStartSeconds > 15 && TunProtocol.ServiceRequestSeconds > TunProtocol.AdapterStartSeconds && TunProtocol.ClientRequestSeconds > TunProtocol.ServiceRequestSeconds, "IPC cannot terminate valid Wintun startup before its PnP deadline");
        using (JsonDocument fresh = JsonDocument.Parse(TunConfigBuilder.Build(interfaceName: "Win10ray-aabbccddeeff")))
            Check(fresh.RootElement.GetProperty("inbounds")[0].GetProperty("interface_name").GetString() == "Win10ray-aabbccddeeff", "TUN session accepts a fresh PnP identity");
        Check(TunServiceClient.Failure("adapter_timeout").Message.Contains("[adapter_timeout]") && TunServiceClient.Failure("ipc_closed").Code == "ipc_closed", "TUN failure reports a specific safe code");
        Check(!TunServiceClient.Failure("https://secret.example/token").Message.Contains("secret.example"), "unexpected service error codes do not disclose arbitrary strings");
        foreach (TunInstallStage stage in Enum.GetValues<TunInstallStage>())
        {
            int exit = TunInstallDiagnostics.Encode(stage, 5);
            Check(TunInstallDiagnostics.TryDecode(exit, out TunInstallStage decoded, out int native) && decoded == stage && native == 5, "installer stage and Windows error survive UAC process exit");
            string code = TunInstallDiagnostics.ErrorCode(exit);
            Check(TunServiceClient.Failure(code).Code == code && code.StartsWith("install_", StringComparison.Ordinal), "installation errors preserve a safe actionable code");
        }
        Check(!TunInstallDiagnostics.TryDecode(1, out _, out _), "legacy generic installer exits are not mistaken for structured errors");
        Check(TunInstallDiagnostics.ErrorCode(unchecked((int)0xc0000428)) == "install_exit_c0000428", "native image-loader failure codes are retained");
        using (var memory = new MemoryStream())
        {
            await TunProtocol.WriteAsync(memory, new TunRequest("start", UseDnsOverHttps: true, DnsOverHttpsUrl: DnsEndpoint.Default), CancellationToken.None);
            memory.Position = 0;
            TunRequest restored = await TunProtocol.ReadAsync<TunRequest>(memory, CancellationToken.None);
            Check(restored.UseDnsOverHttps && restored.DnsOverHttpsUrl == DnsEndpoint.Default, "DNS options pass through authenticated IPC");
        }
        foreach (int size in new[] { -1, 0, TunProtocol.MaximumFrame + 1, int.MaxValue })
        {
            byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, size);
            using var memory = new MemoryStream(header);
            try { await TunProtocol.ReadAsync<TunRequest>(memory, CancellationToken.None); throw new InvalidOperationException("FAIL: invalid frame accepted"); }
            catch (InvalidDataException) { passed++; }
        }
        using (var truncated = new MemoryStream([10, 0, 0, 0, 123]))
        {
            try { await TunProtocol.ReadAsync<TunRequest>(truncated, CancellationToken.None); throw new InvalidOperationException("FAIL: truncated frame accepted"); }
            catch (EndOfStreamException) { passed++; }
        }
        using (JsonDocument doc = JsonDocument.Parse(TunConfigBuilder.Build()))
        {
            JsonElement root = doc.RootElement;
            Check(root.GetProperty("inbounds")[0].GetProperty("type").GetString() == "tun", "real TUN inbound");
            Check(root.GetProperty("inbounds")[0].GetProperty("address").GetArrayLength() == 2, "IPv4 and IPv6 interface addresses");
            Check(root.GetProperty("inbounds")[0].GetProperty("auto_route").GetBoolean(), "automatic routing");
            Check(root.GetProperty("inbounds")[0].GetProperty("strict_route").GetBoolean(), "strict Windows DNS routing");
            Check(root.GetProperty("outbounds")[0].GetProperty("server").GetString() == "127.0.0.1" && root.GetProperty("outbounds")[0].GetProperty("server_port").GetInt32() == 10808, "TUN forwards to Xray SOCKS");
            Check(root.GetProperty("route").GetProperty("auto_detect_interface").GetBoolean(), "physical interface selection prevents egress loops");
            Check(root.GetProperty("route").GetProperty("rules")[0].GetProperty("process_name").EnumerateArray().Any(item => item.GetString() == "xray.exe"), "Xray egress excluded from recapture");
            Check(root.GetProperty("route").GetProperty("final").GetString() == "xray", "default traffic sent to Xray");
        }
        foreach (string endpoint in new[] { DnsEndpoint.Default, "https://dns.google:8443/custom-query", "https://[2606:4700:4700::1111]/dns-query" })
        {
            Check(DnsEndpoint.IsValid(endpoint), "valid HTTPS DNS endpoint accepted");
            using JsonDocument doc = JsonDocument.Parse(TunConfigBuilder.Build(true, endpoint));
            JsonElement server = doc.RootElement.GetProperty("dns").GetProperty("servers")[0];
            Check(server.GetProperty("type").GetString() == "https" && server.GetProperty("detour").GetString() == "xray", "TUN DNS uses HTTPS through Xray");
            Check(server.GetProperty("server_port").GetInt32() == new Uri(endpoint).Port && server.GetProperty("path").GetString() == new Uri(endpoint).PathAndQuery, "custom DoH path and port preserved");
            if (new Uri(endpoint).HostNameType == UriHostNameType.Dns) Check(server.GetProperty("domain_resolver").GetString() == "bootstrap", "hostname DoH resolver has bootstrap");
        }
        foreach (string endpoint in new[] { "http://dns.example/dns-query", "file:///secret", "https://user:password@example.com/dns-query", "https://example.com/dns-query#fragment", "not a URL" })
        {
            Check(!DnsEndpoint.IsValid(endpoint), "invalid DoH endpoint rejected");
            try { TunConfigBuilder.Build(true, endpoint); throw new InvalidOperationException("FAIL: invalid DoH settings accepted"); }
            catch (ArgumentException) { passed++; }
        }
        string directory = Path.Combine(Path.GetTempPath(), "Win10ray-TunTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await TunComponentIntegrity.VerifyAsync(Path.GetDirectoryName(Path.GetFullPath(executable))!); passed++;
            foreach (string component in TunComponentIntegrity.Hashes.Keys) await File.WriteAllTextAsync(Path.Combine(directory, component), "corrupted test component");
            try { await TunComponentIntegrity.VerifyAsync(directory); throw new InvalidOperationException("FAIL: corrupted TUN component accepted"); }
            catch (InvalidDataException) { passed++; }
            foreach (string configuration in new[] { TunConfigBuilder.Build(), TunConfigBuilder.Build(true, DnsEndpoint.Default), TunConfigBuilder.Build(true, "https://dns.google/dns-query") })
            {
                string config = Path.Combine(directory, "tun.json"); await File.WriteAllTextAsync(config, configuration);
                using var process = new Process { StartInfo = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true } };
                process.StartInfo.ArgumentList.Add("check"); process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add(config);
                process.Start();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                string output = await stdout + await stderr;
                Check(process.ExitCode == 0, "bundled sing-box validates TUN DNS config: " + output);
            }
        }
        finally { Directory.Delete(directory, true); }

        // A user process impersonating the service must be rejected before receiving a token.
        if (!File.Exists(Path.Combine(TunServiceClient.InstallDirectory, "Win10ray.TunService.exe")) && !File.Exists(legacyImage))
        {
            using var fake = new NamedPipeServerStream(TunProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Task connected = fake.WaitForConnectionAsync();
            MethodInfo send = typeof(TunServiceClient).GetMethod("SendAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var response = (Task<TunResponse>)send.Invoke(null, [new TunRequest("ping"), TimeSpan.FromSeconds(3), CancellationToken.None])!;
            await connected.WaitAsync(TimeSpan.FromSeconds(3));
            try { await response; throw new InvalidOperationException("FAIL: untrusted pipe server accepted"); }
            catch (UnauthorizedAccessException) { passed++; }
            byte[] probe = new byte[1];
            Check(await fake.ReadAsync(probe).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == 0, "no request or token disclosed to an untrusted pipe server");
        }
        if (helper is not null)
        {
            foreach (var test in new[] { (Argument: "--self-test", Expected: 0), (Argument: "--service", Expected: 1) })
            {
                using var process = new Process { StartInfo = new ProcessStartInfo(Path.GetFullPath(helper)) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden } };
                process.StartInfo.ArgumentList.Add(test.Argument); process.Start();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                Check(process.ExitCode == test.Expected, "helper process security and SCM-only service entry: " + test.Argument);
            }
        }
        return passed;
    }
}
