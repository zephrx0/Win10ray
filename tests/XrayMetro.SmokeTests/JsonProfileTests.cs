using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XrayMetro.Models;
using XrayMetro.Services;

internal static class JsonProfileTests
{
    private const string Id="a42b9cc5-1130-4997-bca3-809a4a2d86ef";
    public static async Task<int> RunAsync(string[] args)
    {
        int passed=0;
        void Check(bool condition,string reason){if(!condition)throw new Exception(reason);passed++;}
        JsonObject source=Profile(443,8443);
        ImportResult imported=new LinkParser().ParseMany(source.ToJsonString());
        Check(imported.Profiles.Count==1&&imported.Errors.Count==0,"One provider JSON must remain one profile.");
        ServerProfile profile=imported.Profiles.Single();
        Check(profile.Protocol=="xray-json"&&profile.Name=="LTE"&&XrayJsonProfileService.NodeCount(profile)==2,"JSON metadata and endpoints");
        Check(new LinkParser().ParseMany(Convert.ToBase64String(Encoding.UTF8.GetBytes(source.ToJsonString()))).Profiles.Count==1,"Base64 JSON subscriptions");
        JsonObject second=(JsonObject)source.DeepClone();second["remarks"]="NL";second["outbounds"]![0]!["settings"]!["vnext"]![0]!["port"]=444;
        Check(new LinkParser().ParseMany(new JsonArray(source.DeepClone(),second).ToJsonString()).Profiles.Count==2,"JSON array subscription");
        Check(new LinkParser().ParseMany(ShareLinkExporter.Export(profile)).Profiles.Single().SourceKey==profile.SourceKey,"JSON export/import identity");
        JsonObject renamed=(JsonObject)source.DeepClone();renamed["remarks"]="Renamed";
        Check(new LinkParser().ParseMany(renamed.ToJsonString()).Profiles.Single().SourceKey==profile.SourceKey,"Display metadata does not change the provider connection identity");
        JsonObject generated=JsonNode.Parse(new XrayConfigBuilder().Build(profile,new AppSettings{BlockAds=true,BypassRussia=true}))!.AsObject();
        Check(generated["inbounds"]!.AsArray().All(node=>node!["listen"]!.GetValue<string>()=="127.0.0.1")&&generated["api"]!["listen"]!.GetValue<string>()=="127.0.0.1:10085","Provider cannot expose local listeners or replace statistics API");
        Check(generated["log"]!["access"]!.GetValue<string>()=="none"&&generated["outbounds"]!.AsArray().Count==4,"Safe runtime log and preserved provider outbounds");
        Check(generated["routing"]!["rules"]![0]!["outboundTag"]!.GetValue<string>()!= "block"&&generated["routing"]!["balancers"]![0]!["strategy"]!["type"]!.GetValue<string>()=="roundRobin","Application policy and provider balancing both survive");
        var wildcard=(JsonObject)source.DeepClone();wildcard["routing"]!["balancers"]![0]!["selector"]=new JsonArray("");
        ServerProfile pool=new LinkParser().ParseMany(wildcard.ToJsonString()).Profiles.Single();
        JsonObject poolConfig=JsonNode.Parse(new XrayConfigBuilder().Build(pool,new AppSettings{BlockAds=false}))!.AsObject();
        string[] prefixes=poolConfig["routing"]!["balancers"]![0]!["selector"]!.AsArray().Select(item=>item!.GetValue<string>()).ToArray();
        Check(poolConfig["outbounds"]!.AsArray().Count(item=>prefixes.Any(prefix=>item!["tag"]!.GetValue<string>().StartsWith(prefix,StringComparison.Ordinal)))==2,"A wildcard provider pool cannot select application direct/block handlers");
        var invalid=(JsonObject)source.DeepClone();invalid["outbounds"]![0]!["streamSettings"]=new JsonObject{["tlsSettings"]=new JsonObject{["certificates"]=new JsonArray(new JsonObject{["keyFile"]="PRIVATE_TEST_KEY"})}};
        Check(new LinkParser().ParseMany(invalid.ToJsonString()).Profiles.Count==0,"External file references rejected");
        invalid=(JsonObject)source.DeepClone();invalid["routing"]!["rules"]![0]!["webhook"]=new JsonObject{["url"]="https://example.com/private"};
        Check(new LinkParser().ParseMany(invalid.ToJsonString()).Profiles.Count==0,"Provider routing webhooks rejected");
        invalid=(JsonObject)source.DeepClone();invalid["routing"]!["balancers"]![0]!["selector"]=new JsonArray("missing");
        Check(new LinkParser().ParseMany(invalid.ToJsonString()).Profiles.Count==0,"Empty balancer matches rejected");
        Check(new LinkParser().ParseMany("{\"password\":\"DO_NOT_LEAK\", invalid}").Errors.All(error=>!error.Contains("DO_NOT_LEAK")),"Malformed JSON errors do not disclose data");
        Check(!new LogSanitizer(XrayJsonProfileService.Secrets(profile)).Sanitize("User "+Id).Contains(Id),"JSON credentials redacted");
        JsonObject pinned=(JsonObject)source.DeepClone();pinned["outbounds"]![0]!["settings"]!["vnext"]![0]!["address"]="proxy.invalid";
        pinned["outbounds"]![0]!["streamSettings"]=new JsonObject{["network"]="ws",["security"]="tls",["tlsSettings"]=new JsonObject(),["wsSettings"]=new JsonObject{["path"]="/vpn"}};
        pinned["dns"]=new JsonObject{["hosts"]=new JsonObject{["proxy.invalid"]="127.0.0.1"},["servers"]=new JsonArray("1.1.1.1")};
        var pinnedProfile=new LinkParser().ParseMany(pinned.ToJsonString()).Profiles.Single();
        var resolved=await XrayJsonProfileService.ResolveEndpointsAsync(pinnedProfile,CancellationToken.None);
        var pinnedConfig=JsonNode.Parse(new XrayConfigBuilder().Build(pinnedProfile,new AppSettings{BlockAds=false},jsonAddresses:resolved))!;
        Check(pinnedConfig["outbounds"]![0]!["settings"]!["vnext"]![0]!["address"]!.GetValue<string>()=="127.0.0.1"&&pinnedConfig["outbounds"]![0]!["streamSettings"]!["tlsSettings"]!["serverName"]!.GetValue<string>()=="proxy.invalid"&&pinnedConfig["outbounds"]![0]!["streamSettings"]!["wsSettings"]!["headers"]!["Host"]!.GetValue<string>()=="proxy.invalid","Pre-TUN DNS pinning preserves TLS SNI and HTTP host");
        var emptyInbound=(JsonObject)source.DeepClone();emptyInbound["routing"]!["rules"]![0]!["inboundTag"]=new JsonArray();
        Check(XrayJsonProfileService.Read(new LinkParser().ParseMany(emptyInbound.ToJsonString()).Profiles.Single().XrayJson)["routing"]!["rules"]!.AsArray().Count==1,"An empty inbound filter must not drop provider balancing");
        await SubscriptionAsync(source.ToJsonString());passed++;
        passed += await SubscriptionNegotiationTests.RunAsync(source);
        string root=Path.Combine(Path.GetTempPath(),"Win10ray-Json-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var store=new SettingsService(Path.Combine(root,"storage"));await store.SaveAsync(new AppState{Servers=[profile],Subscriptions=[new SubscriptionProfile{Url="https://example.invalid/SYNTHETIC",PreferJson=false}]});
            string encrypted=await File.ReadAllTextAsync(Path.Combine(root,"storage","settings.json"));
            Check(!encrypted.Contains(Id)&&!encrypted.Contains("outbounds"),("JSON profile encrypted at rest"));
            Check((await store.LoadAsync()).Servers.Single().XrayJson==profile.XrayJson,"Encrypted JSON roundtrip");
            Check(!(await store.LoadAsync()).Subscriptions.Single().PreferJson,"Subscription format preference encrypted roundtrip");
            int index=Array.IndexOf(args,"--xray");if(index>=0){await NativeAsync(args[index+1],root);passed+=3;}
        }
        finally{Directory.Delete(root,true);}
        return passed;
    }
    private static async Task SubscriptionAsync(string body)
    {
        using TcpListener listener=new(IPAddress.Loopback,0);listener.Start();using CancellationTokenSource deadline=new(TimeSpan.FromSeconds(10));
        Task serve=Task.Run(async()=>
        {
            using TcpClient client=await listener.AcceptTcpClientAsync(deadline.Token);await using NetworkStream stream=client.GetStream();
            using StreamReader reader=new(stream,Encoding.ASCII,leaveOpen:true);List<string> headers=[];string? line;
            while(!string.IsNullOrEmpty(line=await reader.ReadLineAsync(deadline.Token)))headers.Add(line);
            if(!headers.Any(item=>item.StartsWith("Accept:",StringComparison.OrdinalIgnoreCase)&&item.Contains("application/json",StringComparison.Ordinal))||headers.Any(item=>item.StartsWith("x-hwid:",StringComparison.OrdinalIgnoreCase)))throw new Exception("JSON negotiation or disabled HWID failed");
            byte[] data=Encoding.UTF8.GetBytes(body);await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+data.Length+"\r\nConnection: close\r\n\r\n"),deadline.Token);await stream.WriteAsync(data,deadline.Token);
        });
        using SubscriptionService service=new();ImportResult result=await service.FetchAsync("http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port,deadline.Token,false,false);
        await serve;if(result.Profiles.Count!=1||result.Profiles[0].Protocol!="xray-json")throw new Exception("JSON HTTP subscription was split or rejected");
    }
    private static JsonObject Profile(int first,int second)=>new()
    {
        ["remarks"]="LTE",
        ["log"]=new JsonObject{["access"]="NEVER_CREATE_THIS.log"},
        ["api"]=new JsonObject{["tag"]="api",["listen"]="0.0.0.0:9999"},
        ["inbounds"]=new JsonArray(new JsonObject{["tag"]="provider-socks",["listen"]="0.0.0.0",["port"]=9000,["protocol"]="socks"}),
        ["outbounds"]=new JsonArray(Outbound("lte-a",first),Outbound("lte-b",second)),
        ["routing"]=new JsonObject{["rules"]=new JsonArray(new JsonObject{["type"]="field",["inboundTag"]=new JsonArray("provider-socks"),["network"]="tcp,udp",["balancerTag"]="lte"}),
            ["balancers"]=new JsonArray(new JsonObject{["tag"]="lte",["selector"]=new JsonArray("lte-"),["strategy"]=new JsonObject{["type"]="roundRobin"}})}
    };
    private static JsonObject Outbound(string tag,int port)=>new(){["tag"]=tag,["protocol"]="vless",["settings"]=new JsonObject{["vnext"]=new JsonArray(new JsonObject{["address"]="127.0.0.1",["port"]=port,["users"]=new JsonArray(new JsonObject{["id"]=Id,["encryption"]="none"})})}};
    private static async Task NativeAsync(string executable,string root)
    {
        using CancellationTokenSource deadline=new(TimeSpan.FromSeconds(30));
        List<Process> backends=[];List<Task<string>> readers=[];List<TcpListener> origins=[];List<Task> servers=[];HashSet<int> ports=[];
        using XrayProcessService core=new(Path.Combine(root,"runtime"),Free(ports),Free(ports),Free(ports));
        int[] remote=[Free(ports),Free(ports)];
        try
        {
            for(int i=0;i<2;i++)
            {
                TcpListener origin=new(IPAddress.Loopback,0);origin.Start();origins.Add(origin);servers.Add(ServeAsync(origin,i==0?"A":"B",deadline.Token));
                string file=Path.Combine(root,"backend-"+i+".json");
                var config=new{log=new{access="none",loglevel="warning"},inbounds=new[]{new{listen="127.0.0.1",port=remote[i],protocol="vless",settings=new{decryption="none",clients=new[]{new{id=Id}}}}},
                    outbounds=new[]{new{protocol="freedom",settings=new{redirect="127.0.0.1:"+((IPEndPoint)origin.LocalEndpoint).Port,finalRules=new[]{new{action="allow",network="tcp",ip=new[]{"127.0.0.1/32"}}}}}}};
                await File.WriteAllTextAsync(file,JsonSerializer.Serialize(config),deadline.Token);
                var info=new ProcessStartInfo(Path.GetFullPath(executable)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(string arg in new[]{"run","-config",file})info.ArgumentList.Add(arg);
                Process process=Process.Start(info)??throw new Exception("Native backend start failed");backends.Add(process);readers.Add(process.StandardOutput.ReadToEndAsync());readers.Add(process.StandardError.ReadToEndAsync());
                while(true){try{using TcpClient check=new();await check.ConnectAsync(IPAddress.Loopback,remote[i],deadline.Token);break;}catch(SocketException){await Task.Delay(50,deadline.Token);}}
            }
            var raw=Profile(remote[0],remote[1]);ServerProfile profile=new LinkParser().ParseMany(raw.ToJsonString()).Profiles.Single();
            string configJson=new XrayConfigBuilder().Build(profile,new AppSettings{BlockAds=false},core.ApiPort,core.SocksPort,core.HttpPort);
            await core.StartAsync(configJson,XrayJsonProfileService.Secrets(profile),false,deadline.Token);
            using HttpClient http=new(new HttpClientHandler{UseProxy=true,Proxy=new WebProxy("http://127.0.0.1:"+core.HttpPort)});
            http.DefaultRequestHeaders.ConnectionClose=true;
            HashSet<string> seen=[];
            for(int i=0;i<4;i++)seen.Add(await http.GetStringAsync("http://127.0.0.1:18080/data",deadline.Token));
            if(!seen.SetEquals(new[]{"A","B"}))throw new Exception("JSON round-robin did not use both VLESS nodes");
            TrafficSnapshot traffic=await core.QueryStatsAsync(deadline.Token);if(traffic.UplinkBytes<=0||traffic.DownlinkBytes<=0)throw new Exception("JSON traffic statistics missing");
            await core.StopAsync();
            backends[0].Kill(true);await backends[0].WaitForExitAsync(deadline.Token);
            raw["routing"]!["balancers"]![0]!["strategy"]!["type"]="leastPing";
            raw["routing"]!["balancers"]![0]!["fallbackTag"]="lte-b";
            raw["observatory"]=new JsonObject{["subjectSelector"]=new JsonArray("lte-"),["probeURL"]="http://127.0.0.1:18080/health",["probeInterval"]="1s",["enableConcurrency"]=true};
            profile=new LinkParser().ParseMany(raw.ToJsonString()).Profiles.Single();
            await core.StartAsync(new XrayConfigBuilder().Build(profile,new AppSettings{BlockAds=false},core.ApiPort,core.SocksPort,core.HttpPort),XrayJsonProfileService.Secrets(profile),false,deadline.Token);
            if(await http.GetStringAsync("http://127.0.0.1:18080/data",deadline.Token)!="B")throw new Exception("JSON balancer failed with its first endpoint offline");
            await core.StopAsync();if(core.IsRunning||File.Exists(Path.Combine(root,"runtime","config.json")))throw new Exception("JSON runtime cleanup failed");
        }
        finally
        {
            await core.StopAsync();foreach(Process backend in backends){if(!backend.HasExited)backend.Kill(true);await backend.WaitForExitAsync();backend.Dispose();}
            await Task.WhenAll(readers);deadline.Cancel();foreach(TcpListener listener in origins)listener.Stop();
            try{await Task.WhenAll(servers);}catch(OperationCanceledException){}catch(SocketException){}
        }
    }
    private static async Task ServeAsync(TcpListener listener,string body,CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            using TcpClient client=await listener.AcceptTcpClientAsync(token);await using NetworkStream stream=client.GetStream();
            using StreamReader reader=new(stream,Encoding.ASCII,leaveOpen:true);string? first=await reader.ReadLineAsync(token);while(await reader.ReadLineAsync(token) is {Length:>0}){}
            string response=first?.Contains("/health",StringComparison.Ordinal)==true?"HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n":"HTTP/1.1 200 OK\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n"+body;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response),token);
        }
    }
    private static int Free(HashSet<int> used){while(true){using TcpListener listener=new(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;if(used.Add(port))return port;}}
}
