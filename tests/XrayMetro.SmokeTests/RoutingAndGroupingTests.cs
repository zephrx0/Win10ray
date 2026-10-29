using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using XrayMetro.Models;
using XrayMetro.Services;

internal static class RoutingAndGroupingTests
{
    public static async Task<int> RunAsync(string[] args)
    {
        Guid subscription = Guid.NewGuid();
        ServerProfile first = new() { Name = "LTE", Host = "one.example", Port = 443, SubscriptionId = subscription };
        ServerProfile second = new() { Name = "LTE", Host = "two.example", Port = 443, SubscriptionId = subscription };
        ServerProfile other = new() { Name = "LTE", Host = "other.example", Port = 443, SubscriptionId = Guid.NewGuid() };
        ServerProfile manual = new() { Name = "LTE", Host = "manual.example", Port = 443 };
        ServerProfile[] servers = [first, second, other, manual];
        var representatives = ServerGroupingService.Representatives(servers, second);
        if (representatives.Count != 3 || representatives.Contains(first.Id) || !representatives.Contains(second.Id) || first.EndpointCount != 2 || second.EndpointCount != 2)
            throw new Exception("Identical provider names must collapse only inside the same subscription and preserve selection.");
        first.ProviderName = second.ProviderName = "LTE"; first.Name = second.Name = "Custom label";
        if (!ServerGroupingService.SameGroup(first, second) || ServerGroupingService.Candidates(servers, first, true).Single().Id != second.Id)
            throw new Exception("Renaming and grouped failover must preserve all endpoints.");
        second.ProviderName = "Different LTE";
        if (ServerGroupingService.SameGroup(first, second)) throw new Exception("Different provider labels must not merge.");

        string metadata = "[{\"draft\":false,\"prerelease\":true,\"tag_name\":\"v1.4.3-experimental.2\",\"assets\":[{\"name\":\"Win10ray-Setup-1.4.3-experimental.2.exe\",\"state\":\"uploaded\",\"size\":1,\"digest\":\"sha256:" + new string('a',64) + "\",\"browser_download_url\":\"https://github.com/zephrx0/Win10ray/releases/download/v1.4.3-experimental.2/Win10ray-Setup-1.4.3-experimental.2.exe\"}]}]";
        using (UpdateService updates = new(new MetadataHandler(metadata)))
        {
            if ((await updates.CheckAsync("1.4.3", experimental:true))?.Version != "1.4.3-experimental.2") throw new Exception("Explicit stable-to-experimental switch with the same base version failed.");
            if (await updates.CheckAsync("1.4.3-experimental.2", experimental:true) is not null || await updates.CheckAsync("1.4.4", experimental:true) is not null)
                throw new Exception("Current or older experimental releases must not be offered.");
        }
        int passed = 6;
        int native = Array.IndexOf(args, "--sing-box");
        if (native >= 0) { await NativeRoutingAsync(args[native + 1]); passed += 4; }
        return passed;
    }

    private sealed class MetadataHandler(string metadata) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadata) });
    }

    private static async Task NativeRoutingAsync(string executable)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Win10ray-ProcessRouting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        using TcpListener direct = new(IPAddress.Loopback, 0), proxy = new(IPAddress.Loopback, 0);
        direct.Start(); proxy.Start();
        Task directServer = ServeAsync(direct, false, deadline.Token), proxyServer = ServeAsync(proxy, true, deadline.Token);
        string curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
        try
        {
            foreach (bool path in new[] { false, true })
            {
                using TcpListener reservation = new(IPAddress.Loopback, 0); reservation.Start();
                int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                ProcessRouteRule rule = new(path ? curl.ToUpperInvariant() : "CURL.EXE", ProcessRouteAction.Direct, path);
                JsonNode config = JsonNode.Parse(TunConfigBuilder.Build(useDnsOverHttps:path, dnsOverHttpsUrl:"https://dns.google/dns-query", blockAds:false, processRules:[rule]))!;
                // Exercise the production router and Windows process lookup through a loopback SOCKS
                // inbound, without creating a TUN interface or changing the tester's system routes.
                config["inbounds"] = new JsonArray(new JsonObject { ["type"]="socks",["listen"]="127.0.0.1",["listen_port"]=port });
                config["outbounds"]![0]!["server_port"] = ((IPEndPoint)proxy.LocalEndpoint).Port;
                config["route"]!["auto_detect_interface"] = false;
                JsonArray rules = (JsonArray)config["route"]!["rules"]!;
                for (int i=rules.Count-1;i>=0;i--) if (rules[i]?["ip_is_private"] is not null) rules.RemoveAt(i);
                string file = Path.Combine(directory,"router.json"); await File.WriteAllTextAsync(file,config.ToJsonString(),deadline.Token);
                reservation.Stop();
                ProcessStartInfo info = new(Path.GetFullPath(executable)) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
                foreach(string arg in new[]{"--disable-color","run","-c",file}) info.ArgumentList.Add(arg);
                using Process router = Process.Start(info) ?? throw new Exception("Could not start process router.");
                Task<string> stdout=router.StandardOutput.ReadToEndAsync(),stderr=router.StandardError.ReadToEndAsync();
                try
                {
                    while(true)
                    {
                        if(router.HasExited) throw new Exception("Native router failed: "+await stderr);
                        try { using TcpClient ready=new(); await ready.ConnectAsync(IPAddress.Loopback,port,deadline.Token);break; }
                        catch(SocketException){await Task.Delay(50,deadline.Token);}
                    }
                    string url="http://127.0.0.1:"+((IPEndPoint)direct.LocalEndpoint).Port+"/";
                    ProcessStartInfo selected = new(curl) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
                    foreach(string arg in new[]{"--silent","--show-error","--max-time","5","--proxy","socks5h://127.0.0.1:"+port,url}) selected.ArgumentList.Add(arg);
                    selected.Environment["NO_PROXY"]="";selected.Environment["no_proxy"]="";
                    using Process chosen=Process.Start(selected) ?? throw new Exception("Could not start selected app.");
                    Task<string> result=chosen.StandardOutput.ReadToEndAsync(),error=chosen.StandardError.ReadToEndAsync();
                    await chosen.WaitForExitAsync(deadline.Token);
                    if(chosen.ExitCode!=0 || await result!="DIRECT") throw new Exception("Selected Windows application did not bypass proxy: "+await error);
                    using HttpClient ordinary=new(new HttpClientHandler { UseProxy=true,Proxy=new WebProxy("socks5://127.0.0.1:"+port) });
                    if(await ordinary.GetStringAsync(url,deadline.Token)!="PROXY") throw new Exception("An unlisted application bypassed the proxy.");
                }
                finally
                {
                    if(!router.HasExited)router.Kill(true);
                    await router.WaitForExitAsync(); await stdout; await stderr;
                }
            }
        }
        finally
        {
            deadline.Cancel(); direct.Stop(); proxy.Stop();
            try { await Task.WhenAll(directServer,proxyServer); } catch(OperationCanceledException) { } catch(SocketException) { }
            Directory.Delete(directory,true);
        }
    }

    private static async Task ServeAsync(TcpListener listener,bool socks,CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            using TcpClient client=await listener.AcceptTcpClientAsync(token);
            await using NetworkStream stream=client.GetStream();
            if(socks)
            {
                byte[] greeting=new byte[2];await stream.ReadExactlyAsync(greeting,token);
                await stream.ReadExactlyAsync(new byte[greeting[1]],token);await stream.WriteAsync(new byte[]{5,0},token);
                byte[] header=new byte[4];await stream.ReadExactlyAsync(header,token);
                int length=header[3] switch {1=>4,4=>16,3=>await ReadByteAsync(stream,token),_=>throw new Exception("Invalid SOCKS destination")};
                await stream.ReadExactlyAsync(new byte[length+2],token);
                await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},token);
            }
            using StreamReader reader=new(stream,Encoding.ASCII,leaveOpen:true);
            while(await reader.ReadLineAsync(token) is {Length:>0}) { }
            string body=socks?"PROXY":"DIRECT";
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n"+body),token);
        }
    }
    private static async Task<int> ReadByteAsync(Stream stream,CancellationToken token){byte[] one=new byte[1];await stream.ReadExactlyAsync(one,token);return one[0];}
}
