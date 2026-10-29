using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using XrayMetro.Models;
using XrayMetro.Services;

internal static class SubscriptionNegotiationTests
{
    private const string Links = "vless://a42b9cc5-1130-4997-bca3-809a4a2d86ef@127.0.0.1:443#LTE%20%2345\nvless://a42b9cc5-1130-4997-bca3-809a4a2d86ef@127.0.0.1:8443#LTE%20%2388";
    private const string Path = "/s/key%2Bvalue?token=SYNTHETIC";
    private const string JsonPath = "/s/key%2Bvalue/json?token=SYNTHETIC";

    public static async Task<int> RunAsync(JsonObject profile)
    {
        JsonObject mobile = (JsonObject)profile.DeepClone(); mobile["remarks"] = "Мобильный интернет";
        string json = mobile.ToJsonString();
        await CaseAsync(Path, [Response(Links), Response(json)], [Path, JsonPath], true);
        await CaseAsync(Path, [Response(Base64(Links)), Response(Base64(json))], [Path, JsonPath], true);
        await CaseAsync(Path, [Response(Links), Response(Base64("\uFEFF"+json))], [Path, JsonPath], true);
        await CaseAsync(Path, [Response(Links), "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"], [Path, JsonPath], false);
        await CaseAsync(Path, [Response(Links), Response("<html>Not a subscription</html>")], [Path, JsonPath], false);
        await CaseAsync(Path, [Response(Links), Response("{invalid JSON}")], [Path, JsonPath], false);
        await CaseAsync(Path, [Response(Links), Response(new JsonArray(mobile.DeepClone(),new JsonObject()).ToJsonString())], [Path, JsonPath], false);
        await CaseAsync(Path, [Response(Links), Response(Links)], [Path, JsonPath], false);
        await CaseAsync(Path, [Response(Links)], [Path], false, false);
        foreach (string explicitPath in new[]{"/s/key/json", "/s/key/v2ray-json", "/profile.json", "/s/key?format=base64", "/s/key?type=links", "/s/key?target=v2ray"})
            await CaseAsync(explicitPath, [Response(Links)], [explicitPath], false);
        await CaseAsync(Path, [Response(Links), "HTTP/1.1 302 Found\r\nLocation: /actual-json\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", Response(json)], [Path,JsonPath,"/actual-json"], true);
        await WaitAsync(false);
        await WaitAsync(true);
        return 18;
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static string Response(string body)
        => "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;

    private static async Task<string> HeadersAsync(NetworkStream stream,CancellationToken cancellation)
    {
        using StreamReader reader = new(stream,Encoding.ASCII,leaveOpen:true);
        StringBuilder headers = new(); string? line;
        while (!string.IsNullOrEmpty(line=await reader.ReadLineAsync(cancellation))) headers.AppendLine(line);
        return headers.ToString();
    }

    private static async Task CaseAsync(string path,string[] responses,string[] paths,bool json,bool preferJson=true)
    {
        using TcpListener listener=new(IPAddress.Loopback,0);listener.Start();
        using CancellationTokenSource deadline=new(TimeSpan.FromSeconds(10));
        List<string> received=[];
        Task server=Task.Run(async()=>
        {
            for(int i=0;i<responses.Length;i++)
            {
                using TcpClient client=await listener.AcceptTcpClientAsync(deadline.Token);
                await using NetworkStream stream=client.GetStream();
                string headers=await HeadersAsync(stream,deadline.Token); received.Add(headers);
                if(!headers.StartsWith("GET "+paths[i]+" HTTP/1.1",StringComparison.Ordinal))throw new Exception("Subscription JSON route changed the path or query");
                if(!headers.Contains("User-Agent: Win10ray/",StringComparison.OrdinalIgnoreCase)||!headers.Contains("x-app-name: Win10ray",StringComparison.OrdinalIgnoreCase))throw new Exception("JSON negotiation must retain Win10ray identity");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(responses[i]),deadline.Token);
            }
        });
        using SubscriptionService service=new();
        SubscriptionProfile subscription=new(){Url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+path,PreferJson=preferJson};
        string originalUrl=subscription.Url;
        ImportResult result=await service.UpdateAsync(subscription,deadline.Token,true,false);
        await server;
        if(subscription.Url!=originalUrl)throw new Exception("JSON negotiation rewrote the stored subscription URL");
        string[] ids=received.Select(headers=>headers.Split('\n').Single(line=>line.StartsWith("x-hwid:",StringComparison.OrdinalIgnoreCase)).Trim()).ToArray();
        if(ids.Distinct().Count()!=1)throw new Exception("JSON route changed HWID");
        if(json)
        {
            if(result.Errors.Count!=0||result.Profiles.Count!=1||result.Profiles[0].Protocol!="xray-json"||result.Profiles[0].Name!="Мобильный интернет"||XrayJsonProfileService.NodeCount(result.Profiles[0])!=2)throw new Exception("Numbered LTE links did not upgrade to a single provider JSON pool");
        }
        else if(result.Errors.Count!=0||result.Profiles.Count!=2||result.Profiles[0].Name!="LTE #45"||result.Profiles[1].Name!="LTE #88")throw new Exception("Unsupported or partial JSON replaced working links");
    }

    private static async Task WaitAsync(bool cancel)
    {
        using TcpListener listener=new(IPAddress.Loopback,0);listener.Start();
        using CancellationTokenSource deadline=new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource caller=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Task server=Task.Run(async()=>
        {
            for(int i=0;i<2;i++)
            {
                using TcpClient client=await listener.AcceptTcpClientAsync(deadline.Token);
                await using NetworkStream stream=client.GetStream();await HeadersAsync(stream,deadline.Token);
                if(i==0)await stream.WriteAsync(Encoding.UTF8.GetBytes(Response(Links)),deadline.Token);
                else
                {
                    if(cancel)caller.Cancel();
                    byte[] one=new byte[1];await stream.ReadAsync(one,deadline.Token);
                }
            }
        });
        using SubscriptionService service=new();Stopwatch watch=Stopwatch.StartNew();bool cancelled=false;
        try
        {
            ImportResult result=await service.FetchAsync("http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+Path,caller.Token,false);
            if(result.Profiles.Count!=2)throw new Exception("A JSON timeout lost the working subscription");
        }
        catch(OperationCanceledException)when(cancel&&caller.IsCancellationRequested){cancelled=true;}
        await server;
        if(cancel!=cancelled||watch.Elapsed>TimeSpan.FromSeconds(7))throw new Exception("JSON negotiation did not honor cancellation or the probe timeout");
    }
}
