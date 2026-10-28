using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using XrayMetro.Models;
using XrayMetro.Services;

internal static class ServicesSupportTests
{
    public static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            passed++;
        }

        string directory = Path.Combine(Path.GetTempPath(), "XrayMetro-SettingsTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            SettingsService storage = new(directory);
            AppState initial = await storage.LoadAsync();
            Check(initial.Servers.Count == 0 && initial.Settings.UseSystemProxy, "new settings defaults");
            Check(initial.Settings.SendHwid && initial.Settings.AutoSwitchServers, "HWID and failover enabled by default");
            Check(!initial.Settings.ResumeConnection, "new installation does not reconnect without saved intent");
            await storage.SaveAsync(new AppState { Settings = new AppSettings { UseTun = true, ResumeConnection = true } });
            AppState resumed = await storage.LoadAsync();
            Check(resumed.Settings.UseTun && resumed.Settings.ResumeConnection && !resumed.Settings.UseSystemProxy, "saved TUN mode and resume intent survive reload");
            await storage.SaveAsync(new AppState { Settings = new AppSettings { UseSystemProxy = false, UseTun = false, SendHwid = false, AutoSwitchServers = false } });
            AppState migrated = await storage.LoadAsync();
            Check(migrated.Settings.UseSystemProxy && !migrated.Settings.UseTun, "legacy local mode migrates to system proxy");
            Check(!migrated.Settings.SendHwid && !migrated.Settings.AutoSwitchServers, "disabled HWID and failover persist");
            ServerProfile server = new LinkParser().Parse("trojan://SYNTHETIC_PASSWORD@example.com:443#Synthetic");
            server.LatencyMs = 42;
            AppState saved = new() { Servers = [server], Settings = new AppSettings { LastServerId = server.Id, AccentColor = "#FF7700" } };
            await storage.SaveAsync(saved);
            AppState loaded = await storage.LoadAsync();
            Check(loaded.Servers.Count == 1 && loaded.Servers[0].Credential == "SYNTHETIC_PASSWORD" && loaded.Settings.LastServerId == server.Id,
                "settings preserve profile and selection");
            Check(loaded.Servers[0].LatencyMs is null && loaded.Settings.AccentColor == "#FF7700", "transient ping omitted and accent preserved");
            Check(!File.Exists(Path.Combine(directory, "settings.json.tmp")), "atomic save removes temporary file");
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            DirectorySecurity acl = new DirectoryInfo(directory).GetAccessControl();
            Check(acl.AreAccessRulesProtected && acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .All(rule => rule.IdentityReference.Equals(identity.User)), "settings folder restricted to current user");

            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{ broken");
            loaded = await storage.LoadAsync();
            Check(loaded.Servers.Count == 0 && storage.LoadWarning is not null && Directory.GetFiles(directory, "settings.damaged-*.json").Length == 1,
                "damaged JSON is preserved and recovered");
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                "{\"Servers\":[{\"Host\":\"example.com\",\"Port\":443,\"Protocol\":\"trojan\",\"Credential\":\"SYNTHETIC_PASSWORD\",\"Flow\":null}]}");
            loaded = await storage.LoadAsync();
            Check(loaded.Servers.Count == 0 && Directory.GetFiles(directory, "settings.damaged-*.json").Length == 2,
                "null required profile properties are quarantined");
            await storage.SaveAsync(new AppState { Settings = new AppSettings { AccentColor = "invalid", LastServerId = Guid.NewGuid() } });
            loaded = await storage.LoadAsync();
            Check(loaded.Settings.AccentColor == "#0078D7" && loaded.Settings.LastServerId is null && storage.LoadWarning is null,
                "invalid accent and stale selection safely normalized");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        using SubscriptionService subscriptions = new();
        Check(CountryFlagService.CountryCode("🇫🇷 ⚡France") == "fr" && CountryFlagService.DisplayName("🇫🇷 ⚡France") == "⚡France", "country flag is separated from the label");
        Check(CountryFlagService.CountryCode("[DE] Germany") == "de" && CountryFlagService.CountryCode("Server") == "", "only explicit country markers produce flags");
        Check(CountryFlagService.CountryCode("🇫") == "" && CountryFlagService.DisplayName("🇫") == "🇫", "an incomplete flag sequence is safe");
        string link = "trojan://SYNTHETIC_PASSWORD@example.com:443#Synthetic";
        string hwid = await HardwareIdService.GetIdAsync();
        Check(hwid == await HardwareIdService.GetIdAsync() && System.Text.RegularExpressions.Regex.IsMatch(hwid, "^[a-zA-Z0-9=-]{10,64}$"), "stable provider-compatible HWID");
        var captured = new List<string>();
        await ServeAsync(_ => Response(link), async url =>
        {
            await subscriptions.FetchAsync(url);
            await subscriptions.FetchAsync(url, sendHwid: false);
            await subscriptions.FetchAsync(url);
        }, 3, captured.Add);
        Check(captured[0].Contains("x-hwid: " + hwid, StringComparison.OrdinalIgnoreCase) && captured[2].Contains("x-hwid: " + hwid, StringComparison.OrdinalIgnoreCase), "HWID present by default and stable between refreshes");
        Check(!captured[1].Contains("x-hwid:", StringComparison.OrdinalIgnoreCase) && !captured[1].Contains("x-device-", StringComparison.OrdinalIgnoreCase), "disabled HWID leaves no per-device headers");
        Check(captured[0].Contains("User-Agent: " + HardwareIdService.UserAgent, StringComparison.OrdinalIgnoreCase) && captured[0].Contains("x-device-os: Windows", StringComparison.OrdinalIgnoreCase), "correct client and platform headers");
        Check(captured[0].Contains("x-app-name: Win10ray\r\n", StringComparison.OrdinalIgnoreCase) && captured[0].Contains("x-app-version: " + HardwareIdService.ClientVersion, StringComparison.OrdinalIgnoreCase), "explicit application name and version accompany HWID");
        Check(captured[0].Contains("x-device-model: Win10ray (Windows PC)\r\n", StringComparison.OrdinalIgnoreCase), "device card contains the application name");
        Check(!captured[1].Contains("x-app-", StringComparison.OrdinalIgnoreCase), "disabled HWID omits supplementary app metadata");
        string userAgent = captured[0].Split("\r\n").Single(line => line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        Check(userAgent.Split('/')[0] == "Win10ray", "Remnawave application-name extraction resolves Win10ray");
        int redirectCount = 0; captured.Clear();
        await ServeAsync(_ => ++redirectCount == 1 ? "HTTP/1.1 302 Found\r\nLocation: /next\r\nContent-Length: 0\r\nConnection: close\r\n\r\n" : Response(link),
            async url => { await subscriptions.FetchAsync(url); }, 2, captured.Add);
        Check(captured.Count == 2 && captured.All(headers => headers.Contains("x-hwid: " + hwid, StringComparison.OrdinalIgnoreCase) && headers.Contains("x-app-name: Win10ray\r\n", StringComparison.OrdinalIgnoreCase)), "same-origin redirect preserves HWID and application name");
        string redirectedHeaders = "";
        await ServeAsync(_ => Response(link), target => ServeAsync(_ => $"HTTP/1.1 302 Found\r\nLocation: {target}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            async source => { await subscriptions.FetchAsync(source); }), inspectRequest: headers => redirectedHeaders = headers);
        Check(!redirectedHeaders.Contains("x-hwid:", StringComparison.OrdinalIgnoreCase), "cross-origin redirect does not disclose HWID");
        Check(!redirectedHeaders.Contains("x-app-name:", StringComparison.OrdinalIgnoreCase) && !redirectedHeaders.Contains("x-device-model:", StringComparison.OrdinalIgnoreCase), "cross-origin redirect omits HWID metadata");
        await ServeAsync(_ => "HTTP/1.1 200 OK\r\nx-hwid-max-devices-reached: true\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", async url =>
        {
            try { await subscriptions.FetchAsync(url); throw new Exception("Device limit ignored"); }
            catch (InvalidOperationException exception) { Check(exception.Message == LocalizationService.Get("HwidLimit"), "subscription device limit has a clear error"); }
        });
        await ServeAsync(_ => Response(link), async url =>
        {
            ImportResult result = await subscriptions.FetchAsync(url);
            Check(result.Profiles.Count == 1 && result.Profiles[0].Name == "Synthetic", "HTTP plain-text subscription");
        });
        await ServeAsync(_ => Response(Convert.ToBase64String(Encoding.UTF8.GetBytes(link))), async url =>
        {
            ImportResult result = await subscriptions.FetchAsync(url);
            Check(result.Profiles.Count == 1 && result.Errors.Count == 0, "HTTP base64 subscription");
        });
        await ServeAsync(_ => "HTTP/1.1 200 OK\r\nContent-Length: 5000000\r\nConnection: close\r\n\r\n", async url =>
        {
            try { await subscriptions.FetchAsync(url); throw new Exception("Oversized subscription accepted"); }
            catch (InvalidOperationException exception) { Check(!exception.Message.Contains(url), "oversized subscription rejected without URL disclosure"); }
        });
        await ServeAsync(_ => "HTTP/1.1 302 Found\r\nLocation: /loop\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", async url =>
        {
            try { await subscriptions.FetchAsync(url); throw new Exception("Infinite redirects accepted"); }
            catch (InvalidOperationException exception) { Check(!exception.Message.Contains(url), "redirect loop bounded without URL disclosure"); }
        }, 6);
        foreach (string invalid in new[] { "file:///C:/secret", "https://user:SYNTHETIC_PASSWORD@example.com/" })
        {
            try { await subscriptions.FetchAsync(invalid); throw new Exception("Invalid subscription URL accepted"); }
            catch (InvalidOperationException exception)
            {
                Check(!exception.Message.Contains("SYNTHETIC_PASSWORD") && !exception.Message.Contains(invalid), "invalid subscription scheme or userinfo rejected");
            }
        }
        await CheckParallelProbesAsync(); passed++;
        return passed;
    }

    private static async Task CheckParallelProbesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
        int port = ((IPEndPoint)proxy.LocalEndpoint).Port;
        async Task RespondAsync()
        {
            using TcpClient socket = await proxy.AcceptTcpClientAsync(deadline.Token);
            await using NetworkStream stream = socket.GetStream();
            string headers = ""; byte[] buffer = new byte[4096];
            while (!headers.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int count = await stream.ReadAsync(buffer, deadline.Token); if (count == 0) return;
                headers += Encoding.ASCII.GetString(buffer, 0, count);
            }
            if (headers.Contains("/slow", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, deadline.Token);
            else await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"), deadline.Token);
        }
        Task[] responses = [RespondAsync(), RespondAsync()];
        try
        {
            var health = new ServerHealthService(new Uri("http://probe.invalid/slow"), new Uri("http://probe.invalid/fast"));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (!await health.CheckTunnelAsync(port, deadline.Token) || watch.Elapsed > TimeSpan.FromSeconds(2))
                throw new InvalidOperationException("FAIL: a blocked probe delays the successful probe");
        }
        finally
        {
            await deadline.CancelAsync(); proxy.Stop();
            try { await Task.WhenAll(responses); } catch (OperationCanceledException) { } catch (SocketException) { }
        }
    }

    private static string Response(string body) => $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    private static async Task ServeAsync(Func<string, string> response, Func<string, Task> client, int requests = 1, Action<string>? inspectRequest = null)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string url = $"http://127.0.0.1:{port}/subscription";
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task server = Task.Run(async () =>
        {
            for (int count = 0; count < requests; count++)
            {
                using TcpClient socket = await listener.AcceptTcpClientAsync(timeout.Token);
                await using NetworkStream stream = socket.GetStream();
                byte[] buffer = new byte[2048];
                StringBuilder headers = new();
                while (!headers.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await stream.ReadAsync(buffer, timeout.Token);
                    if (read == 0 || headers.Length > 32768) throw new IOException("Invalid synthetic HTTP request");
                    headers.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                inspectRequest?.Invoke(headers.ToString());
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response(url)), timeout.Token);
            }
        }, timeout.Token);
        try { await client(url); await server; }
        finally
        {
            await timeout.CancelAsync();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }
    }
}
