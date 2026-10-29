using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Globalization;
using XrayMetro.Models;
using XrayMetro.Services;

if (args.Length > 0 && args[0] == "--core-child") { await CrashCleanupTests.RunCoreChildAsync(args); return; }
if (args.Length > 0 && args[0] == "--routing-only") { Console.WriteLine("PASS: " + await RoutingAndGroupingTests.RunAsync(args) + " routing/grouping checks."); return; }
int cultureArgument = Array.IndexOf(args, "--culture");
if (cultureArgument >= 0 && cultureArgument + 1 < args.Length)
{
    CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(args[cultureArgument + 1]);
    CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture;
}

const string TestId = "6f5e4438-c1a9-4d4b-8f27-371c1c4ee3ba";
LinkParser parser = new();
XrayConfigBuilder builder = new();
int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    passed++;
}
void Reject(string link, string description)
{
    try { parser.Parse(link); throw new InvalidOperationException("FAIL: accepted " + description); }
    catch (ShareLinkException exception)
    {
        Check(!exception.Message.Contains(TestId, StringComparison.Ordinal) && !exception.Message.Contains("TOP_SECRET", StringComparison.Ordinal), description + " and secret-free exception");
    }
}
string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

ServerProfile vless = parser.Parse($"vless://{TestId}@example.com:443?encryption=none&security=tls&type=ws&host=cdn.example.com&path=%2Fvpn&sni=cdn.example.com&alpn=http%2F1.1#Example%20WS");
Check(vless.Protocol == "vless" && vless.Name == "Example WS", "VLESS identity/name");
Check(vless.Network == "ws" && vless.Path == "/vpn" && vless.HostHeader == "cdn.example.com", "VLESS WebSocket transport");
Check(vless.ServerName == "cdn.example.com" && vless.Alpn == "http/1.1", "TLS query decode");

ServerProfile reality = parser.Parse($"vless://{TestId}@[2001:db8::1]:8443?security=reality&type=tcp&sni=example.com&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=aabb&flow=xtls-rprx-vision#IPv6");
Check(reality.Host == "2001:db8::1" && reality.Address == "[2001:db8::1]:8443", "IPv6 parsing/display");
Check(reality.Fingerprint == "chrome" && reality.Security == "reality", "REALITY defaults");

string vmessJson = JsonSerializer.Serialize(new { v = "2", ps = "VMess test", add = "example.com", port = "443", id = TestId, aid = "0", scy = "auto", net = "grpc", path = "test-service", tls = "tls", sni = "example.com" });
ServerProfile vmess = parser.Parse("vmess://" + Base64(vmessJson));
Check(vmess.Protocol == "vmess" && vmess.Port == 443 && vmess.Encryption == "auto", "VMess Base64 JSON");
Check(vmess.ServiceName == "test-service" && vmess.Network == "grpc", "VMess gRPC service name");
ServerProfile vmessUri = parser.Parse($"vmess://auto:{TestId}@example.com:443?security=tls&type=tcp#URI");
Check(vmessUri.Credential == TestId && vmessUri.Encryption == "auto", "VMess URI form");

ServerProfile trojan = parser.Parse("trojan://TOP_SECRET%3Awith%40punctuation@example.com:443?sni=example.com#Trojan");
Check(trojan.Credential == "TOP_SECRET:with@punctuation" && trojan.Security == "tls", "Trojan escaped password");
ServerProfile shadowsocks = parser.Parse("ss://" + Base64("aes-128-gcm:TOP_SECRET:colon") + "@example.com:8388#SIP002");
Check(shadowsocks.Method == "aes-128-gcm" && shadowsocks.Credential == "TOP_SECRET:colon", "SS SIP002 password colon");
ServerProfile legacySs = parser.Parse("ss://" + Base64("aes-256-gcm:TOP_SECRET@[2001:db8::2]:8388") + "#Legacy");
Check(legacySs.Host == "2001:db8::2" && legacySs.Port == 8388, "SS legacy Base64 IPv6");
Check(parser.Parse("ss://" + Base64("aes-256-gcm:TOP_SECRET%3Aliteral@example.com:8388")).Credential == "TOP_SECRET%3Aliteral", "legacy SS preserves literal percent sequences");
ServerProfile plainSs = parser.Parse("ss://aes-128-gcm:TOP_SECRET%3Atest@example.com:8388");
Check(plainSs.Credential == "TOP_SECRET:test", "SS plain escaped credentials");

Reject("vless://TOP_SECRET@example.com:0", "zero port");
Reject("trojan://TOP_SECRET@example.com:70000", "out of range port");
Reject("trojan://@example.com:443", "empty password");
Reject("vmess://not-valid-base64", "malformed VMess Base64");
Reject("ss://broken@host.example:8388", "malformed Shadowsocks credentials");
Reject("ss://" + Base64("aes-128-gcm:TOP_SECRET") + "@example.com:8388?plugin=obfs-local", "unsupported SIP003 plugin");
Reject($"vless://{TestId}@example.com:443?type=quic", "removed QUIC transport");
Reject($"vless://{TestId}@example.com:443?security=reality&pbk=TOP_SECRET", "invalid REALITY key");
Reject($"vless://{TestId}@example.com:443?security=reality&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=abc", "odd shortId");

string list = $"vless://{TestId}@example.com:443?security=tls#First\ninvalid line\nvless://{TestId}@example.com:443?security=tls#Renamed";
ImportResult imported = parser.ParseMany(list);
Check(imported.Profiles.Count == 1 && imported.Errors.Count == 1, "partial import and credential deduplication");
ImportResult encodedList = parser.ParseMany(Base64(list));
Check(encodedList.Profiles.Count == 1 && encodedList.Errors.Count == 1, "Base64 subscription list");

List<ServerProfile> profiles = [vless, reality, vmess, vmessUri, trojan, shadowsocks, legacySs, plainSs];
profiles.Add(parser.Parse($"vless://{TestId}@example.com:443?security=tls&type=xhttp&mode=stream-up&path=%2Ftest&extra=%7B%22noSSEHeader%22%3Atrue%7D"));
profiles.Add(parser.Parse($"vless://{TestId}@example.com:443?security=tls&type=httpupgrade&path=%2Fupgrade"));
using (JsonDocument pinned = JsonDocument.Parse(builder.Build(vless, new AppSettings { UseTun = true }, resolvedServerAddress: "192.0.2.10")))
{
    JsonElement outbound = pinned.RootElement.GetProperty("outbounds")[0];
    Check(outbound.GetProperty("settings").GetProperty("vnext")[0].GetProperty("address").GetString() == "192.0.2.10", "TUN endpoint pinned before routing changes");
    Check(outbound.GetProperty("streamSettings").GetProperty("tlsSettings").GetProperty("serverName").GetString() == "cdn.example.com", "TLS name preserved for pinned endpoint");
    Check(outbound.GetProperty("streamSettings").GetProperty("wsSettings").GetProperty("headers").GetProperty("Host").GetString() == "cdn.example.com", "WebSocket host preserved for pinned endpoint");
}
ServerProfile defaultNames = parser.Parse($"vless://{TestId}@example.com:443?security=tls&type=ws");
using (JsonDocument pinned = JsonDocument.Parse(builder.Build(defaultNames, new AppSettings { UseTun = true }, resolvedServerAddress: "192.0.2.10")))
{
    JsonElement transport = pinned.RootElement.GetProperty("outbounds")[0].GetProperty("streamSettings");
    Check(transport.GetProperty("tlsSettings").GetProperty("serverName").GetString() == "example.com" && transport.GetProperty("wsSettings").GetProperty("headers").GetProperty("Host").GetString() == "example.com", "implicit TLS and WebSocket names preserved");
}
foreach (ServerProfile profile in profiles)
{
    using JsonDocument configuration = JsonDocument.Parse(builder.Build(profile, new AppSettings()));
    JsonElement root = configuration.RootElement;
    Check(root.GetProperty("outbounds")[0].GetProperty("protocol").GetString() == profile.Protocol, "outbound protocol preserved");
    Check(root.GetProperty("inbounds")[0].GetProperty("listen").GetString() == "127.0.0.1", "loopback-only SOCKS listener");
    Check(root.GetProperty("inbounds")[1].GetProperty("port").GetInt32() == 10809, "HTTP listener port");
    Check(root.GetProperty("api").GetProperty("listen").GetString() == "127.0.0.1:10085", "loopback-only stats API");
    Check(root.GetProperty("policy").GetProperty("system").GetProperty("statsInboundDownlink").GetBoolean(), "inbound counters enabled");
    Check(root.GetProperty("log").GetProperty("access").GetString() == "none", "access logs disabled");
}
using (JsonDocument configuration = JsonDocument.Parse(builder.Build(vless, new AppSettings { BypassRussia = true, BlockAds = false })))
{
    JsonElement routing = configuration.RootElement.GetProperty("routing");
    Check(routing.GetProperty("domainStrategy").GetString() == "IPOnDemand", "IP lookup occurs before catch-all");
    JsonElement rules = routing.GetProperty("rules");
    Check(rules[0].GetProperty("domain")[0].GetString() == "geosite:category-ru" && rules[0].GetProperty("outboundTag").GetString() == "direct", "Russian domains route directly");
    Check(rules[1].GetProperty("ip")[0].GetString() == "geoip:ru" && rules[1].GetProperty("outboundTag").GetString() == "direct", "Russian IPs route directly");
    Check(rules[2].GetProperty("outboundTag").GetString() == "proxy", "other traffic routes through proxy");
}
using (JsonDocument configuration = JsonDocument.Parse(builder.Build(vless, new AppSettings { UseDnsOverHttps = true, DnsOverHttpsUrl = DnsEndpoint.Default })))
    Check(configuration.RootElement.GetProperty("dns").GetProperty("servers")[0].GetString() == DnsEndpoint.Default, "Xray DNS-over-HTTPS configured");

// Optional integration: use the supplied real core to validate every protocol/transport.
int xrayArgument = Array.IndexOf(args, "--xray");
if (xrayArgument >= 0 && xrayArgument + 1 < args.Length)
{
    string executable = Path.GetFullPath(args[xrayArgument + 1]);
    string directory = Path.Combine(Path.GetTempPath(), "XrayMetro-Smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        for (int index = 0; index <= profiles.Count + 1; index++)
        {
            string configPath = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(configPath, builder.Build(profiles[index % profiles.Count], new AppSettings { BypassRussia = index == profiles.Count, UseDnsOverHttps = index == profiles.Count + 1 }));
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("run");
            process.StartInfo.ArgumentList.Add("-test");
            process.StartInfo.ArgumentList.Add("-config");
            process.StartInfo.ArgumentList.Add(configPath);
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw; }
            string output = await stdout + await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Core rejected synthetic fixture {index}: " + output);
            passed++;
        }
    }
    finally { Directory.Delete(directory, true); }
    passed += await CoreLifecycleTests.RunAsync(executable);
    passed += await CrashCleanupTests.TestJobAsync();
}
passed += await ServicesSupportTests.RunAsync();
passed += await FeatureUpdateTests.RunAsync(args);
passed += await RoutingAndGroupingTests.RunAsync(args);
passed += await FailoverTests.RunAsync();
passed += await UpdateTests.RunAsync();
int installerArgument = Array.IndexOf(args, "--installer-test");
if (installerArgument >= 0 && installerArgument + 2 < args.Length) passed += await UpdateTests.RunInstallerAsync(args[installerArgument + 1], args[installerArgument + 2]);
if (args.Contains("--ui", StringComparer.Ordinal)) passed += await UiSmokeTests.RunAsync();
int watchdogArgument = Array.IndexOf(args, "--watchdog-exe");
if (watchdogArgument >= 0 && watchdogArgument + 1 < args.Length) passed += await CrashCleanupTests.TestWatchdogAsync(args[watchdogArgument + 1]);
int tunArgument = Array.IndexOf(args, "--sing-box");
int helperArgument = Array.IndexOf(args, "--tun-helper");
if (tunArgument >= 0 && tunArgument + 1 < args.Length) passed += await TunTests.RunAsync(args[tunArgument + 1], helperArgument >= 0 && helperArgument + 1 < args.Length ? args[helperArgument + 1] : null);
Console.WriteLine($"PASS: {passed} smoke/integration checks.");
