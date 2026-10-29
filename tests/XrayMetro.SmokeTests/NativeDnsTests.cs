using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using XrayMetro.Models;
using XrayMetro.Services;

internal static class NativeDnsTests
{
    public static async Task<int> RunAsync(string executable)
    {
        using CancellationTokenSource deadline=new(TimeSpan.FromSeconds(40));
        using TcpListener doh=new(IPAddress.Loopback,0),proxy=new(IPAddress.Loopback,0),origin=new(IPAddress.Loopback,0);
        doh.Start();proxy.Start();origin.Start();
        int dohPort=Port(doh),proxyPort=Port(proxy),originPort=Port(origin);
        int proxyConnections=0;
        System.Collections.Concurrent.ConcurrentBag<Task> clients=[];
        using RSA key=RSA.Create(2048);
        var request=new CertificateRequest("CN=localhost",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment|X509KeyUsageFlags.KeyCertSign,true));
        SubjectAlternativeNameBuilder san=new();san.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(san.Build());
        using X509Certificate2 generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
        // SChannel needs an imported key container rather than CreateSelfSigned's ephemeral key.
        using X509Certificate2 certificate=new(generated.Export(X509ContentType.Pfx));
        Task dohServer=AcceptAsync(doh,async client=>
        {
            using(client)
            using(var tls=new SslStream(client.GetStream(),false))
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate,ApplicationProtocols=[SslApplicationProtocol.Http11]},deadline.Token);
                string headers=await HeadersAsync(tls,deadline.Token);
                int size=int.Parse(headers.Split("\r\n").Single(line=>line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                byte[] body=new byte[size];await tls.ReadExactlyAsync(body,deadline.Token);
                byte[] answer=Answer(body);
                await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/dns-message\r\nContent-Length: "+answer.Length+"\r\nConnection: close\r\n\r\n"),deadline.Token);
                await tls.WriteAsync(answer,deadline.Token);
            }
        });
        Task proxyServer=AcceptAsync(proxy,async client=>
        {
            using(client)
            {
                Interlocked.Increment(ref proxyConnections);
                NetworkStream stream=client.GetStream();byte[] greeting=new byte[2];await stream.ReadExactlyAsync(greeting,deadline.Token);
                byte[] methods=new byte[greeting[1]];await stream.ReadExactlyAsync(methods,deadline.Token);await stream.WriteAsync(new byte[]{5,0},deadline.Token);
                byte[] command=new byte[10];await stream.ReadExactlyAsync(command,deadline.Token);
                if(command[0]!=5||command[1]!=1||command[3]!=1||!new IPAddress(command.AsSpan(4,4)).Equals(IPAddress.Loopback)||BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(8))!=dohPort)throw new Exception("DNS fixture attempted an unexpected external connection");
                using TcpClient upstream=new();await upstream.ConnectAsync(IPAddress.Loopback,dohPort,deadline.Token);
                await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},deadline.Token);
                Task up=stream.CopyToAsync(upstream.GetStream(),deadline.Token),down=upstream.GetStream().CopyToAsync(stream,deadline.Token);
                await Task.WhenAny(up,down);client.Close();upstream.Close();
                try{await Task.WhenAll(up,down);}catch(IOException){}catch(ObjectDisposedException){}
            }
        });
        Task originServer=AcceptAsync(origin,async client=>
        {
            using(client){await HeadersAsync(client.GetStream(),deadline.Token);await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\nDIRECT"),deadline.Token);}
        });
        string directory=Path.Combine(Path.GetTempPath(),"Win10ray-Dns-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        int checks=0;
        try
        {
            foreach(bool ipv4Only in new[]{false,true})
            {
                int dnsPort=FreePort(),socksPort=FreePort();
                JsonNode config=JsonNode.Parse(TunConfigBuilder.Build(true,"https://127.0.0.1:"+dohPort+"/dns-query",blockAds:true,processRules:[new("CURL.EXE",ProcessRouteAction.Direct)],ipv4Only:ipv4Only))!;
                // Exercise actual DNS hijacking, TLS DoH, SOCKS egress and Windows process lookup
                // without installing a driver or changing any system routes or resolver settings.
                config["inbounds"]=new JsonArray(
                    new JsonObject{["type"]="direct",["tag"]="dns-test",["listen"]="127.0.0.1",["listen_port"]=dnsPort,["network"]="tcp"},
                    new JsonObject{["type"]="socks",["tag"]="app-test",["listen"]="127.0.0.1",["listen_port"]=socksPort});
                config["route"]!["auto_detect_interface"]=false;
                config["outbounds"]![0]!["server_port"]=proxyPort;
                foreach(JsonNode? server in config["dns"]!["servers"]!.AsArray())server!["tls"]!["certificate"]=new JsonArray(certificate.ExportCertificatePem());
                string file=Path.Combine(directory,"dns.json");await File.WriteAllTextAsync(file,config.ToJsonString(),deadline.Token);
                ProcessStartInfo start=new(Path.GetFullPath(executable)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(string arg in new[]{"--disable-color","run","-c",file})start.ArgumentList.Add(arg);
                using Process router=Process.Start(start)??throw new Exception("DNS router did not start");
                Task<string> stdout=router.StandardOutput.ReadToEndAsync(),stderr=router.StandardError.ReadToEndAsync();
                try
                {
                    while(true)
                    {
                        if(router.HasExited)throw new Exception("Native DNS configuration failed: "+await stderr);
                        try{using TcpClient ready=new();await ready.ConnectAsync(IPAddress.Loopback,dnsPort,deadline.Token);break;}catch(SocketException){await Task.Delay(30,deadline.Token);}
                    }
                    byte[] aaaa=await QueryAsync(dnsPort,"www.google.com",28,deadline.Token);
                    if((aaaa[3]&15)!=0||BinaryPrimitives.ReadUInt16BigEndian(aaaa.AsSpan(6))!=(ipv4Only?0:1))throw new Exception("AAAA query must be empty NOERROR only on IPv4-only networks");
                    byte[] a=await QueryAsync(dnsPort,"www.google.com",1,deadline.Token);
                    if((a[3]&15)!=0||BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(6))!=1||!a.AsSpan(a.Length-4).SequenceEqual(new byte[]{127,0,0,1}))throw new Exception("An empty AAAA response poisoned the following A lookup");
                    byte[] blocked=await QueryAsync(dnsPort,"doubleclick.net",1,deadline.Token);
                    if((blocked[3]&15)!=3)throw new Exception("DNS ad blocking stopped working");
                    int before=Volatile.Read(ref proxyConnections);
                    if(before==0)throw new Exception("Shared Windows-style DNS did not use the VPN resolver");
                    ProcessStartInfo curl=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"curl.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                    foreach(string arg in new[]{"--silent","--show-error","--max-time","5","--proxy","socks5h://127.0.0.1:"+socksPort,"http://selected.example:"+originPort+"/"})curl.ArgumentList.Add(arg);
                    curl.Environment["NO_PROXY"]="";curl.Environment["no_proxy"]="";
                    using Process selected=Process.Start(curl)??throw new Exception("DNS selected process did not start");
                    Task<string> output=selected.StandardOutput.ReadToEndAsync(),error=selected.StandardError.ReadToEndAsync();await selected.WaitForExitAsync(deadline.Token);
                    if(selected.ExitCode!=0||await output!="DIRECT"||Volatile.Read(ref proxyConnections)!=before)throw new Exception("Selected application's DNS or HTTP escaped to the VPN resolver: "+await error);
                    checks+=4;
                }
                finally{if(!router.HasExited)router.Kill(true);await router.WaitForExitAsync();await stdout;await stderr;}
            }
        }
        finally
        {
            deadline.Cancel();doh.Stop();proxy.Stop();origin.Stop();
            try{await Task.WhenAll(dohServer,proxyServer,originServer);}catch(OperationCanceledException){}catch(SocketException){}
            foreach(Task task in clients){try{await task;}catch(OperationCanceledException){}catch(IOException){}catch(ObjectDisposedException){}}
            Directory.Delete(directory,true);
        }
        return checks;

        async Task AcceptAsync(TcpListener listener,Func<TcpClient,Task> handle)
        {while(!deadline.IsCancellationRequested){TcpClient client=await listener.AcceptTcpClientAsync(deadline.Token);clients.Add(handle(client));}}
    }

    private static int Port(TcpListener listener)=>((IPEndPoint)listener.LocalEndpoint).Port;
    private static int FreePort(){using TcpListener listener=new(IPAddress.Loopback,0);listener.Start();return Port(listener);}
    private static async Task<string> HeadersAsync(Stream stream,CancellationToken token)
    {
        StringBuilder result=new();byte[] one=new byte[1];
        while(result.Length<8192){await stream.ReadExactlyAsync(one,token);result.Append((char)one[0]);if(result.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal))return result.ToString();}
        throw new Exception("Invalid local DNS HTTP headers");
    }
    private static async Task<byte[]> QueryAsync(int port,string name,ushort type,CancellationToken token)
    {
        using MemoryStream query=new();query.Write(new byte[]{0x12,0x34,1,0,0,1,0,0,0,0,0,0});
        foreach(string label in name.Split('.')){query.WriteByte((byte)label.Length);query.Write(Encoding.ASCII.GetBytes(label));}
        query.Write(new byte[]{0,0,(byte)type,0,1});byte[] body=query.ToArray(),length=new byte[2];BinaryPrimitives.WriteUInt16BigEndian(length,(ushort)body.Length);
        using TcpClient client=new();await client.ConnectAsync(IPAddress.Loopback,port,token);NetworkStream stream=client.GetStream();await stream.WriteAsync(length,token);await stream.WriteAsync(body,token);
        await stream.ReadExactlyAsync(length,token);byte[] answer=new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];await stream.ReadExactlyAsync(answer,token);return answer;
    }
    private static byte[] Answer(byte[] query)
    {
        int end=12;while(query[end]!=0)end+=query[end]+1;end+=5;
        ushort type=BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(end-4));byte[] address=type==28?IPAddress.Parse("2001:db8::1234").GetAddressBytes():new byte[]{127,0,0,1};
        using MemoryStream answer=new();answer.Write(query,0,end);byte[] data=answer.GetBuffer();data[2]=0x81;data[3]=0x80;data[6]=0;data[7]=1;data[8]=data[9]=data[10]=data[11]=0;
        answer.Write(new byte[]{0xc0,0x0c,0,(byte)type,0,1,0,0,0,30,0,(byte)address.Length});answer.Write(address);return answer.ToArray();
    }
}
