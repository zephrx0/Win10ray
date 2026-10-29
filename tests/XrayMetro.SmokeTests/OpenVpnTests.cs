using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XrayMetro.Models;
using XrayMetro.Services;

internal static class OpenVpnTests
{
    public static async Task<int> RunAsync(string? executable)
    {
        int passed=0;
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("FAIL: "+name);passed++;}
        using RSA rootKey=RSA.Create(2048),serverKey=RSA.Create(2048),clientKey=RSA.Create(2048);
        CertificateRequest authorityRequest=new("CN=Win10ray OpenVPN Test CA",rootKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
        using X509Certificate2 authority=authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(7));
        using X509Certificate2 server=Issue("CN=localhost",serverKey,true),client=Issue("CN=win10ray-test",clientKey,false);
        string profile=$"client\ndev tun\nproto tcp-client\nremote 127.0.0.1 1194\nremote-cert-tls server\nverify-x509-name localhost name\ndata-ciphers AES-256-GCM:AES-128-GCM\n<ca>\n{authority.ExportCertificatePem()}\n</ca>\n<cert>\n{client.ExportCertificatePem()}\n</cert>\n<key>\n{clientKey.ExportPkcs8PrivateKeyPem()}\n</key>\n";
        ServerProfile parsed=OpenVpnProfileParser.Parse(profile,"Synthetic");
        Check(parsed.Protocol=="openvpn"&&parsed.Host=="127.0.0.1"&&parsed.Port==1194&&parsed.Network=="tcp","OpenVPN .ovpn profile parsed");
        Check(new LinkParser().ParseMany(profile).Profiles.Count==1,"plain OpenVPN profile accepted from clipboard/subscription");
        Check(OpenVpnProfileParser.Parse("# Provider generated on another day\n"+profile).SourceKey==parsed.SourceKey,"provider comments cannot reset the selected OpenVPN server identity");
        ServerProfile withAuth=OpenVpnProfileParser.Parse(profile+"auth-user-pass\n");string identity=withAuth.SourceKey;
        withAuth.OpenVpnUsername="remembered-user";withAuth.OpenVpnPassword="remembered-password";
        Check(LinkParser.CalculateSourceKey(withAuth)==identity,"remembered credentials do not change subscription identity");
        Check(new LinkParser().Parse(ShareLinkExporter.Export(parsed)).SourceKey==parsed.SourceKey,"OpenVPN embedded share link round-trips");
        Check(OpenVpnImportService.TryGetUrl("openvpn://import-profile/https://vpn.example/profile.ovpn",out Uri? url)&&url!.Scheme=="https","official OpenVPN import URL accepted");
        Check(!OpenVpnImportService.TryGetUrl("openvpn://import-profile/http://vpn.example/profile.ovpn",out _)&&!OpenVpnImportService.TryGetUrl("https://u:p@vpn.example/profile.ovpn",out _),"unencrypted imports and embedded URL authentication rejected");
        foreach(string dangerous in new[]{"up C:/run.exe","plugin C:/plugin.dll","config external.ovpn","script-security 2","management 0.0.0.0 1234","dev tap","pkcs12 private.p12"})
        {
            string input=dangerous=="dev tap"?profile.Replace("dev tun","dev tap",StringComparison.Ordinal):profile+dangerous+"\n";
            try{OpenVpnProfileParser.Parse(input);throw new Exception("Unsafe option accepted: "+dangerous);}catch(ShareLinkException e){Check(!e.Message.Contains("private.p12",StringComparison.Ordinal),"unsupported/executable OpenVPN option rejected without leaking arguments");}
        }
        string directory=Path.Combine(Path.GetTempPath(),"Win10ray-OpenVPN-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory,"client.ovpn"),profile);
            string external=profile.Replace($"<ca>\n{authority.ExportCertificatePem()}\n</ca>","ca root.pem",StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(directory,"root.pem"),authority.ExportCertificatePem());
            await File.WriteAllTextAsync(Path.Combine(directory,"external.ovpn"),external);
            Check((await OpenVpnProfileParser.ImportFileAsync(Path.Combine(directory,"external.ovpn"),CancellationToken.None)).OpenVpnConfig.Contains(authority.ExportCertificatePem(),StringComparison.Ordinal),"relative PEM included in imported encrypted profile");
            await File.WriteAllTextAsync(Path.Combine(directory,"external.ovpn"),external.Replace("root.pem","../outside.pem",StringComparison.Ordinal));
            try{await OpenVpnProfileParser.ImportFileAsync(Path.Combine(directory,"external.ovpn"),CancellationToken.None);throw new Exception("Escaping PEM reference accepted");}catch(ShareLinkException){passed++;}
            SettingsService storage=new(Path.Combine(directory,"storage"));await storage.SaveAsync(new AppState{Servers=[parsed]});
            Check((await storage.LoadAsync()).Servers[0].OpenVpnConfig==parsed.OpenVpnConfig&&!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(directory,"storage","settings.json"))).Contains("PRIVATE KEY",StringComparison.Ordinal),"OpenVPN keys stored only inside DPAPI profile");
            string stableDirectory=Path.Combine(directory,"stable"),experimentalDirectory=Path.Combine(directory,"experimental");
            SettingsService stable=new(stableDirectory);
            ServerProfile stableServer=new LinkParser().Parse("trojan://SYNTHETIC_ONLY@example.com:443#Stable");
            await stable.SaveAsync(new AppState{Servers=[stableServer],Settings=new AppSettings{ExperimentalUpdates=false}});
            byte[] stableBytes=await File.ReadAllBytesAsync(Path.Combine(stableDirectory,"settings.json"));
            SettingsService experimental=new(experimentalDirectory,stableDirectory);
            AppState experimentalState=await experimental.LoadAsync();
            Check(experimentalState.Servers.Single().Id==stableServer.Id&&experimentalState.Settings.ExperimentalUpdates,"first experimental launch copies stable profiles and selects experimental updates");
            experimentalState.Servers.Add(parsed);await experimental.SaveAsync(experimentalState);
            Check(stableBytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(stableDirectory,"settings.json")))&&(await stable.LoadAsync()).Servers.Single().Protocol=="trojan","OpenVPN experimental storage cannot modify stable profile");
            using(var download=new OpenVpnImportService(handler:new ProfileResponse(profile)))
            {
                ServerProfile imported=await download.FetchAsync(new Uri("https://vpn.example/client.ovpn"),CancellationToken.None);
                Check(imported.SourceKey==parsed.SourceKey&&imported.OpenVpnSourceUrl=="https://vpn.example/client.ovpn","HTTPS profile import preserves downloadable source for sharing");
                Check(ShareLinkExporter.Export(imported)=="openvpn://import-profile/https://vpn.example/client.ovpn","downloaded OpenVPN profile exports an interoperable import URL");
            }
            string bridge=OpenVpnConfigBuilder.Build(parsed,22330,22331,new string('a',64));
            using(JsonDocument document=JsonDocument.Parse(bridge))
            {
                Check(!document.RootElement.GetProperty("endpoints")[0].GetProperty("system").GetBoolean()&&!document.RootElement.GetProperty("services")[0].GetProperty("dashboard").GetProperty("enabled").GetBoolean(),"OpenVPN bridge uses userspace stack, no system routes or web dashboard");
                Check(document.RootElement.GetProperty("dns").GetProperty("final").GetString()=="vpn-dns"&&document.RootElement.GetProperty("dns").GetProperty("servers")[1].GetProperty("detour").GetString()=="openvpn","traffic DNS resolves through OpenVPN instead of the physical bootstrap resolver");
            }
            string xray=new XrayConfigBuilder().Build(parsed,new AppSettings(),openVpnBridgePort:22330);
            using(JsonDocument document=JsonDocument.Parse(xray))Check(document.RootElement.GetProperty("outbounds")[0].GetProperty("protocol").GetString()=="socks","Xray routing and stats wrap the OpenVPN bridge");
            string sanitized=new LogSanitizer(OpenVpnProcessService.Secrets(parsed)).Sanitize(clientKey.ExportPkcs8PrivateKeyPem());
            Check(!sanitized.Contains("BEGIN PRIVATE KEY",StringComparison.Ordinal)&&!sanitized.Contains(clientKey.ExportPkcs8PrivateKeyPem().Split('\n')[1],StringComparison.Ordinal),"OpenVPN private key redacted from diagnostics");
            if(executable is not null)
            {
                passed+=await NativeAsync(Path.GetFullPath(executable),directory,profile,authority,server,serverKey,false);
                passed+=await NativeAsync(Path.GetFullPath(executable),directory,profile,authority,server,serverKey,true);
            }
        }
        finally{Directory.Delete(directory,true);}
        return passed;
        X509Certificate2 Issue(string name,RSA key,bool isServer)
        {
            CertificateRequest request=new(name,key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new(isServer?"1.3.6.1.5.5.7.3.1":"1.3.6.1.5.5.7.3.2")},true));
            if(isServer){SubjectAlternativeNameBuilder names=new();names.AddDnsName("localhost");names.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(names.Build());}
            using X509Certificate2 certificate=request.Create(authority,DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow.AddDays(1),RandomNumberGenerator.GetBytes(16));return certificate.CopyWithPrivateKey(key);
        }
    }
    private sealed class ProfileResponse(string text):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Headers.Contains("x-hwid")||request.Headers.Contains("Authorization"))throw new Exception("Unexpected identifier on profile download");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(text)});
        }
    }
    private static async Task<int> NativeAsync(string executable,string directory,string profile,X509Certificate2 ca,X509Certificate2 server,RSA key,bool udp)
    {
        int port;using(TcpListener reserve=new(IPAddress.Loopback,0)){reserve.Start();port=((IPEndPoint)reserve.LocalEndpoint).Port;}
        if(udp){using UdpClient reserve=new(new IPEndPoint(IPAddress.Loopback,0));port=((IPEndPoint)reserve.Client.LocalEndPoint!).Port;}
        using TcpListener origin=new(IPAddress.Loopback,0);origin.Start();int originPort=((IPEndPoint)origin.LocalEndpoint).Port;
        using UdpClient dns=new(new IPEndPoint(IPAddress.Loopback,0));int dnsPort=((IPEndPoint)dns.Client.LocalEndPoint!).Port;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));using var dnsLifetime=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        int dnsQueries=0;Task dnsServer=ServeDnsAsync(dns,()=>Interlocked.Increment(ref dnsQueries),dnsLifetime.Token);
        JsonObject configuration=new()
        {
            ["log"]=new JsonObject{["level"]="warn"},
            ["endpoints"]=new JsonArray(new JsonObject{["type"]="openvpn-server",["tag"]="test-server",["listen"]="127.0.0.1",["listen_port"]=port,["network"]="tcp",["system"]=false,["address"]=new JsonArray("10.91.0.1/24"),["tls"]=new JsonObject{["certificate"]=server.ExportCertificatePem(),["key"]=key.ExportPkcs8PrivateKeyPem(),["client_certificate"]=ca.ExportCertificatePem(),["verify_client_certificate"]="require"}}),
            ["outbounds"]=new JsonArray(new JsonObject{["type"]="direct",["tag"]="direct"}),["route"]=new JsonObject{["rules"]=new JsonArray(new JsonObject{["network"]="udp",["port"]=53,["action"]="route-options",["override_address"]="127.0.0.1",["override_port"]=dnsPort},new JsonObject{["network"]="tcp",["action"]="route-options",["override_address"]="127.0.0.1",["override_port"]=originPort}),["final"]="direct"}
        };
        if(udp)
        {
            configuration["endpoints"]![0]!["network"]="udp";
            configuration["endpoints"]![0]!["users"]=new JsonArray(new JsonObject{["username"]="test-user",["password"]="test-password"});
            string wrapping="-----BEGIN OpenVPN Static key V1-----\n"+string.Join("\n",Convert.ToHexString(RandomNumberGenerator.GetBytes(256)).ToLowerInvariant().Chunk(32).Select(chars=>new string(chars)))+"\n-----END OpenVPN Static key V1-----";
            configuration["endpoints"]![0]!["tls"]!["control_wrap"]=new JsonObject{["type"]="tls_crypt",["key"]=wrapping};
            profile=profile.Replace("proto tcp-client","proto udp",StringComparison.Ordinal)+"auth-user-pass\n<tls-crypt>\n"+wrapping+"\n</tls-crypt>\n";
        }
        string path=Path.Combine(directory,"server.json");await File.WriteAllTextAsync(path,configuration.ToJsonString());
        using Process backend=new(){StartInfo=new(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};
        foreach(string arg in new[]{"--disable-color","run","-c",path})backend.StartInfo.ArgumentList.Add(arg);
        backend.Start();Task<string> stdout=backend.StandardOutput.ReadToEndAsync(),stderr=backend.StandardError.ReadToEndAsync();
        using OpenVpnProcessService bridge=new(Path.Combine(directory,"runtime"),executable);
        List<string> logs=[];bridge.LogReceived+=line=>{lock(logs)logs.Add(line);};
        try
        {
            ServerProfile parsed=OpenVpnProfileParser.Parse(profile.Replace("127.0.0.1 1194","127.0.0.1 "+port,StringComparison.Ordinal));
            await bridge.StartAsync(parsed,udp?"test-user":null,udp?"test-password":null,deadline.Token);
            if(File.Exists(Path.Combine(directory,"runtime","config.json")))throw new Exception("OpenVPN wrote its private runtime configuration to disk");
            int socksPort,httpPort,apiPort;
            using(TcpListener a=new(IPAddress.Loopback,0))using(TcpListener b=new(IPAddress.Loopback,0))using(TcpListener c=new(IPAddress.Loopback,0))
            {a.Start();b.Start();c.Start();socksPort=((IPEndPoint)a.LocalEndpoint).Port;httpPort=((IPEndPoint)b.LocalEndpoint).Port;apiPort=((IPEndPoint)c.LocalEndpoint).Port;}
            using XrayProcessService xray=new(Path.Combine(directory,"xray-runtime"),socksPort,httpPort,apiPort);
            string xrayConfig=new XrayConfigBuilder().Build(parsed,new AppSettings{BlockAds=false},apiPort,socksPort,httpPort,openVpnBridgePort:bridge.SocksPort);
            await xray.StartAsync(xrayConfig,OpenVpnProcessService.Secrets(parsed),false,deadline.Token);
            Task serve=ServeAsync(origin,deadline.Token);
            using HttpClient client=new(new SocketsHttpHandler{UseProxy=true,Proxy=new WebProxy("http://127.0.0.1:"+httpPort),ConnectTimeout=TimeSpan.FromSeconds(5)});
            string response=await client.GetStringAsync("http://native-openvpn.test/openvpn",deadline.Token);await serve;
            if(response!="openvpn-loopback-ok")throw new Exception("OpenVPN data channel failed");
            if(dnsQueries==0)throw new Exception("DNS did not traverse the encrypted OpenVPN endpoint");
            TrafficSnapshot traffic=await xray.QueryStatsAsync(deadline.Token);
            if(traffic.UplinkBytes<=0||traffic.DownlinkBytes<=0)throw new Exception("OpenVPN traffic missing from Xray statistics");
            await xray.StopAsync();
            await bridge.StopAsync();if(bridge.IsRunning||File.Exists(Path.Combine(directory,"runtime","config.json")))throw new Exception("OpenVPN cleanup failed");
            if(!udp)
            {
                using TcpListener stalled=new(IPAddress.Loopback,0);stalled.Start();
                int stalledPort=((IPEndPoint)stalled.LocalEndpoint).Port;
                ServerProfile waiting=OpenVpnProfileParser.Parse(profile.Replace("127.0.0.1 1194","127.0.0.1 "+stalledPort,StringComparison.Ordinal));
                using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try{await bridge.StartAsync(waiting,null,null,cancel.Token);throw new Exception("Stalled OpenVPN was accepted");}catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
                if(bridge.IsRunning||File.Exists(Path.Combine(directory,"runtime","config.json")))throw new Exception("Cancelled OpenVPN retained runtime state");
            }
            Console.WriteLine("PASS: real OpenVPN "+(udp?"UDP + tls-crypt + username/password":"TCP + cancellation")+", mutual TLS, HTTP through Xray, stats and cleanup (no system routes changed).");
            return udp?6:7;
        }
        catch
        {
            lock(logs)foreach(string line in logs)Console.Error.WriteLine(line);
            throw;
        }
        finally
        {
            await bridge.StopAsync();if(!backend.HasExited){backend.Kill(true);await backend.WaitForExitAsync();}
            await dnsLifetime.CancelAsync();try{await dnsServer;}catch(OperationCanceledException){}
            string output=await stdout+await stderr;if(backend.ExitCode!=0)Console.Error.WriteLine(output);
        }
    }
    private static async Task ServeDnsAsync(UdpClient dns,Action query,CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            UdpReceiveResult packet=await dns.ReceiveAsync(token);byte[] input=packet.Buffer;
            if(input.Length<17)continue;int position=12;
            while(position<input.Length&&input[position]!=0)position+=input[position]+1;
            if(position+5>input.Length)continue;
            int type=(input[position+1]<<8)|input[position+2];position+=5;query();
            byte[] response=new byte[position+(type==1?16:0)];Array.Copy(input,response,position);
            response[2]=0x81;response[3]=0x80;response[6]=0;response[7]=(byte)(type==1?1:0);response[8]=response[9]=response[10]=response[11]=0;
            if(type==1)new byte[]{0xc0,0x0c,0,1,0,1,0,0,0,60,0,4,198,18,0,22}.CopyTo(response,position);
            await dns.SendAsync(response,packet.RemoteEndPoint,token);
        }
    }
    private static async Task ServeAsync(TcpListener listener,CancellationToken token)
    {
        using TcpClient socket=await listener.AcceptTcpClientAsync(token);await using NetworkStream stream=socket.GetStream();
        byte[] buffer=new byte[4096];StringBuilder input=new();
        while(!input.ToString().Contains("\r\n\r\n",StringComparison.Ordinal)){int count=await stream.ReadAsync(buffer,token);if(count==0)throw new IOException();input.Append(Encoding.ASCII.GetString(buffer,0,count));}
        const string body="openvpn-loopback-ok";await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n{body}"),token);
    }
}
