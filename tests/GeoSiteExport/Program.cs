using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
byte[] bytes = File.ReadAllBytes("xray/geosite.dat");
var selected = new List<(int type,string value)>();
foreach (var (_, entry) in Fields(bytes))
{
    string name=""; var domains = new List<(int type,string value)>();
    foreach(var (key,data) in Fields(entry))
    {
        if(key==1)name=Encoding.UTF8.GetString(data).ToLowerInvariant();
        if(key==2){int type=0;string value="";foreach(var(k,d) in Fields(data)){if(k==1)type=d[0];if(k==2)value=Encoding.UTF8.GetString(d);}domains.Add((type,value));}
    }
    if(name.Contains("ads")||name.Contains("track")||name.Contains("sentry")||name.Contains("segment")) Console.WriteLine(name+": "+domains.Count);
    if(name is "category-ads-all" or "category-ads-and-telemetry") selected.AddRange(domains);
}
Directory.CreateDirectory("Assets/Routing");
File.WriteAllText("Assets/Routing/ads.json",JsonSerializer.Serialize(new{source="v2fly/domain-list-community",geositeSha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),tags=new[]{"category-ads-all","category-ads-and-telemetry"},domains=selected.Distinct().OrderBy(x=>x.type).ThenBy(x=>x.value,StringComparer.Ordinal).Select(x=>new{x.type,x.value})},new JsonSerializerOptions{WriteIndented=true})+"\n");
static List<(int,byte[])> Fields(byte[] input)
{
    var result=new List<(int,byte[])>();int i=0;
    int Read(){int n=0,s=0;while(true){int b=input[i++];n|=(b&127)<<s;if(b<128)return n;s+=7;}}
    while(i<input.Length){int key=Read();if((key&7)==0)result.Add((key>>3,[(byte)Read()]));else if((key&7)==2){int n=Read();result.Add((key>>3,input.AsSpan(i,n).ToArray()));i+=n;}else throw new Exception();}
    return result;
}
