using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XrayMetro.Models;
using XrayMetro.Services;

internal static class FeatureUpdateTests
{
    public static async Task<int> RunAsync(string[] args)
    {
        int passed = 0;
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException("FAIL: " + description); passed++; }
        string directory = Path.Combine(Path.GetTempPath(), "Win10ray-Features-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            ServerProfile profile = new LinkParser().Parse("trojan://SYNTHETIC_ONLY_SECRET@example.com:443#test");
            AppState state = new() { Servers = [profile], Subscriptions = [new SubscriptionProfile { Url = "https://example.com/SYNTHETIC_ONLY_TOKEN" }] };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state));
            SettingsService storage = new(directory);
            AppState restored = await storage.LoadAsync();
            byte[] bytes = await File.ReadAllBytesAsync(path);
            Check(ProfileProtection.IsProtected(bytes) && !Encoding.UTF8.GetString(bytes).Contains("SYNTHETIC_ONLY"), "legacy profile migrated to DPAPI without plaintext credentials");
            Check(restored.Servers.Single().Credential == profile.Credential && restored.Settings.BlockAds, "migration preserves credentials and enables blocking for legacy profiles");
            Check(ProfileProtection.IsProtected(await File.ReadAllBytesAsync(path + ".bak")), "one-version migration backup is also encrypted");
            await storage.LoadAsync();
            Check(bytes.SequenceEqual(await File.ReadAllBytesAsync(path)), "migration is idempotent");
            await File.WriteAllTextAsync(path + ".bak.version", "0.0.0");
            await storage.LoadAsync();
            Check(!File.Exists(path + ".bak"), "previous-version backup removed");
            byte[] tampered = (byte[])bytes.Clone(); tampered[^1] ^= 1;
            await File.WriteAllBytesAsync(path, tampered);
            try { await storage.LoadAsync(); throw new Exception("Damaged DPAPI accepted."); }
            catch (ProfileLockedException) { Check(tampered.SequenceEqual(await File.ReadAllBytesAsync(path)), "decryption failure leaves original profile untouched"); }
            await storage.ResetLockedProfileAsync(); await storage.SaveAsync(new AppState());
            Check(Directory.GetFiles(directory, "*.locked-*").Length == 1 && (await storage.LoadAsync()).Servers.Count == 0, "explicit reset preserves locked profile and creates usable empty state");

            foreach (string link in new[] { "trojan://pass%3Aword@example.com:443?type=ws&path=%2Ftest#Name", "vless://6f5e4438-c1a9-4d4b-8f27-371c1c4ee3ba@example.com:443?security=reality&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=aabb&flow=xtls-rprx-vision", "ss://YWVzLTEyOC1nY206cGFzc3dvcmQ@example.com:8388#test", "vmess://auto:6f5e4438-c1a9-4d4b-8f27-371c1c4ee3ba@example.com:443?security=tls&type=grpc&serviceName=demo&aid=0" })
            {
                ServerProfile original = new LinkParser().Parse(link);
                string exported = ShareLinkExporter.Export(original);
                Check(new LinkParser().Parse(exported).SourceKey == original.SourceKey, "QR share-link round-trip preserves protocol and transport");
                byte[] png = QrCodeService.CreatePng(exported);
                Check(png.AsSpan(0,8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}), "QR is generated locally as PNG");
            }
            Check(Directory.GetFiles(directory, "*.png").Length == 0, "QR generation creates no cache files");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            SubscriptionProfile subscription = new() { UpdatedAt = now.AddHours(-13), LastAutoAttempt = now.AddMinutes(-10) };
            Check(!SubscriptionService.IsDue(subscription, now, 12), "automatic subscription attempts limited to every 15 minutes");
            subscription.LastAutoAttempt = now.AddMinutes(-16);
            Check(SubscriptionService.IsDue(subscription, now, 12), "overdue subscription refreshes after throttle");
            subscription.RetryAfter = now.AddMinutes(10);
            Check(!SubscriptionService.IsDue(subscription, now, 12) && SubscriptionService.RetryDelay(2) > SubscriptionService.RetryDelay(1), "exponential retry respects next scheduled attempt");
            await VerifySubscriptionSerializationAsync(); passed++;

            foreach (bool block in new[] { false, true })
            foreach (bool bypass in new[] { false, true })
            {
                AppSettings settings = new() { UseTun = true, BlockAds = block, BypassRussia = bypass };
                using JsonDocument xray = JsonDocument.Parse(new XrayConfigBuilder().Build(profile, settings));
                var rules = xray.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray().ToArray();
                Check((rules[0].GetProperty("outboundTag").GetString() == "block") == block, "Xray block toggle and priority precede Russian bypass");
                using JsonDocument tun = JsonDocument.Parse(TunConfigBuilder.Build(blockAds: block));
                var tunRules = tun.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
                JsonElement[] blocking = tunRules.Where(r => r.GetProperty("action").GetString() == "reject").ToArray();
                foreach (string domain in new[] { "doubleclick.net", "sentry.io", "example.com" })
                    Check(blocking.Any(rule => Matches(rule, domain)) == (block && domain != "example.com"), "TUN block matching: " + domain);
                if (block) Check(tun.RootElement.GetProperty("dns").GetProperty("rules")[0].GetProperty("rcode").GetString() == "NXDOMAIN", "blocked DNS gets immediate negative reply");
            }
            ProcessRouteRule[] processes = [new("browser.exe",ProcessRouteAction.Proxy),new(@"C:\Apps\local.exe",ProcessRouteAction.Direct,true),new("blocked.exe",ProcessRouteAction.Block)];
            foreach (bool direct in new[] { false, true })
            foreach (bool ads in new[] { false, true })
            {
                string configuration = TunConfigBuilder.Build(useDnsOverHttps:direct, dnsOverHttpsUrl:"https://dns.google/dns-query",blockAds:ads,processRules:processes,defaultRouteDirect:direct);
                using JsonDocument tun = JsonDocument.Parse(configuration);
                JsonElement route = tun.RootElement.GetProperty("route");
                Check(route.GetProperty("rules")[3].GetProperty("outbound").GetString() == "xray-override" && System.Text.RegularExpressions.Regex.IsMatch(processes[1].Value.ToUpperInvariant(), route.GetProperty("rules")[4].GetProperty("process_path_regex")[0].GetString()!) && route.GetProperty("rules")[5].GetProperty("action").GetString() == "reject", "case-insensitive Windows process routing precedes blocking and default route");
                Check(route.GetProperty("final").GetString() == "xray", "unlisted applications always use Xray, including legacy all-direct settings");
                if(direct) Check(tun.RootElement.GetProperty("dns").GetProperty("servers").EnumerateArray().Single(server=>server.GetProperty("tag").GetString()=="direct-dns").GetProperty("type").GetString()=="https","direct routing preserves DNS over HTTPS");
                int index = Array.IndexOf(args, "--sing-box");
                if(index >= 0) { await ValidateCoreAsync(args[index+1], configuration, directory, false); passed++; }
            }
            AppSettings overrides = new() { UseTun=true,BlockAds=true,BypassRussia=true,ProcessRules=processes.ToList() };
            string overrideConfig = new XrayConfigBuilder().Build(profile,overrides);
            using(JsonDocument xray=JsonDocument.Parse(overrideConfig))
                Check(xray.RootElement.GetProperty("routing").GetProperty("rules")[0].GetProperty("inboundTag")[0].GetString()=="process-override" && xray.RootElement.GetProperty("inbounds")[2].GetProperty("port").GetInt32()==10810,"explicit process proxy bypasses both ad block and Russian direct rules");
            int coreIndex = Array.IndexOf(args,"--xray");
            if(coreIndex>=0){await ValidateCoreAsync(args[coreIndex+1],overrideConfig,directory,true);passed++;}

            string metadata = JsonSerializer.Serialize(new { draft=false,prerelease=true,tag_name="v1.4.1-experimental.2",assets=new[]{new{name="Win10ray-Setup-1.4.1-experimental.2.exe",state="uploaded",size=1,digest="sha256:"+new string('a',64),browser_download_url=UpdateService.ReleasesUrl+"/download/v1.4.1-experimental.2/Win10ray-Setup-1.4.1-experimental.2.exe"}} });
            Check(UpdateService.ParseRelease(metadata,"1.4.0") is null && UpdateService.ParseRelease(metadata,"1.4.0",true)?.IsExperimental==true,"experimental releases are strictly opt in");
            Check(UpdateService.CompareVersions("1.4.1-experimental.10","1.4.1-experimental.2")>0 && UpdateService.CompareVersions("1.4.1","1.4.1-experimental.10")>0,"numeric experimental ordering and stable promotion");
            var stable=JsonNode.Parse(metadata)!;stable["prerelease"]=false;stable["tag_name"]="v1.4.0";
            stable["assets"]![0]!["name"]="Win10ray-Setup-1.4.0.exe";stable["assets"]![0]!["browser_download_url"]=UpdateService.ReleasesUrl+"/download/v1.4.0/Win10ray-Setup-1.4.0.exe";
            Check(UpdateService.ParseRelease(stable.ToJsonString(),"1.4.1-experimental.1",allowStableRollback:true)?.Version=="1.4.0","explicit stable channel rollback accepts the supported encrypted-profile format");
            using(var service = new UpdateService(new FakeHttp(metadata)))
                Check((await service.CheckAsync("1.4.0",experimental:true))?.Version=="1.4.1-experimental.2","experimental channel reads release list");
        }
        finally { Directory.Delete(directory,true); }
        return passed;
    }
    private sealed class FakeHttp(string metadata) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent("["+metadata+"]")}); }
    private static async Task VerifySubscriptionSerializationAsync()
    {
        using var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var service=new SubscriptionService();
        var subscription=new SubscriptionProfile{Url=$"http://127.0.0.1:{port}/synthetic"};
        Task first=service.UpdateAsync(subscription,timeout.Token,false,false);
        Task second=service.UpdateAsync(subscription,timeout.Token,false,false);
        using(var socket=await listener.AcceptTcpClientAsync(timeout.Token))
        {
            byte[] buffer=new byte[4096];await socket.GetStream().ReadAsync(buffer,timeout.Token);
            await Task.Delay(100,timeout.Token);
            if(listener.Pending())throw new InvalidOperationException("Parallel update of one subscription was allowed.");
            await Respond(socket);
        }
        using(var socket=await listener.AcceptTcpClientAsync(timeout.Token))
        {byte[] buffer=new byte[4096];await socket.GetStream().ReadAsync(buffer,timeout.Token);await Respond(socket);}
        await Task.WhenAll(first,second);
        async Task Respond(System.Net.Sockets.TcpClient socket)
        {
            const string body="trojan://SYNTHETIC@example.com:443#Test";
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n{body}"),timeout.Token);
        }
    }
    private static bool Matches(JsonElement rule,string domain)
    {
        foreach(var property in rule.EnumerateObject())
        {
            if(property.Value.ValueKind!=JsonValueKind.Array)continue;
            foreach(JsonElement element in property.Value.EnumerateArray())
            {
                string value=element.GetString()!;
                if(property.Name switch { "domain"=>domain==value,"domain_suffix"=>domain==value||domain.EndsWith("."+value,StringComparison.Ordinal),"domain_keyword"=>domain.Contains(value,StringComparison.Ordinal),"domain_regex"=>Regex.IsMatch(domain,value),_=>false}) return true;
            }
        }
        return false;
    }
    private static async Task ValidateCoreAsync(string executable,string config,string directory,bool xray)
    {
        string path=Path.Combine(directory,"validation.json");await File.WriteAllTextAsync(path,config);
        using Process process=new(){StartInfo=new(Path.GetFullPath(executable)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(executable))!}};
        foreach(string argument in xray?new[]{"run","-test","-config",path}:new[]{"check","-c",path})process.StartInfo.ArgumentList.Add(argument);
        process.Start();Task<string> stdout=process.StandardOutput.ReadToEndAsync(),stderr=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try{await process.WaitForExitAsync(timeout.Token);}catch{process.Kill(true);throw;}
        string output=await stdout+await stderr;
        if(process.ExitCode!=0)throw new InvalidOperationException("Core rejected feature config: "+output);
    }
}
