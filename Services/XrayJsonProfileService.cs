using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using XrayMetro.Models;

namespace XrayMetro.Services;

public static class XrayJsonProfileService
{
    public const int MaximumCharacters = 2 * 1024 * 1024;
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };
    public static bool LooksLike(string text) => text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[');
    public static ShareLinkException Invalid() => new(LocalizationService.Pick("Некорректный или неподдерживаемый JSON-профиль Xray.", "Invalid or unsupported Xray JSON profile."));

    public static ImportResult ParseMany(string text)
    {
        try
        {
            if (text.Length > MaximumCharacters) throw Invalid();
            JsonNode root = JsonNode.Parse(text, documentOptions: JsonOptions) ?? throw Invalid();
            JsonNode?[] configurations = root is JsonArray array ? array.ToArray() : [root];
            if (configurations.Length is < 1 or > 256) throw Invalid();
            List<ServerProfile> profiles = []; List<string> errors = []; HashSet<string> seen = [];
            foreach (JsonNode? item in configurations)
            {
                try
                {
                    if (item is not JsonObject source) throw Invalid();
                    string name = Text(source["remarks"] ?? source["ps"] ?? source["name"]);
                    JsonObject bundle = Normalize(source);
                    var endpoints = Endpoints(bundle);
                    string data = bundle.ToJsonString();
                    var profile = new ServerProfile { Protocol = "xray-json", XrayJson = data, Host = endpoints.FirstOrDefault().Host ?? "127.0.0.1", Port = endpoints.FirstOrDefault().Port,
                        Name = string.IsNullOrWhiteSpace(name) ? "Xray JSON" : new string(name.Where(c => !char.IsControl(c)).Take(200).ToArray()), HasCustomName = false };
                    profile.SourceKey = SourceKey(data);
                    if (seen.Add(profile.SourceKey)) profiles.Add(profile);
                }
                catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException)
                { errors.Add(Invalid().Message); }
            }
            return new(profiles, errors);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException)
        { return new([], [Invalid().Message]); }
    }

    public static JsonObject Read(string text)
    {
        try
        {
            if (text.Length > MaximumCharacters || JsonNode.Parse(text, documentOptions: JsonOptions) is not JsonObject source) throw Invalid();
            return Normalize(source);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException) { throw Invalid(); }
    }

    private static JsonObject Normalize(JsonObject source)
    {
        if (source["outbounds"] is not JsonArray original || original.Count is < 1 or > 128) throw Invalid();
        JsonObject bundle = new();
        foreach (string key in new[] { "outbounds", "routing", "dns", "observatory", "burstObservatory", "transport" })
            if (source[key] is JsonNode value) bundle[key] = value.DeepClone();
        if (source["policy"]?["levels"] is JsonObject levels) bundle["policy"] = new JsonObject { ["levels"] = levels.DeepClone() };
        RejectExternalFeatures(bundle);
        JsonArray outbounds = (JsonArray)bundle["outbounds"]!;
        HashSet<string> tags = new(StringComparer.Ordinal);
        for (int index = 0; index < outbounds.Count; index++)
        {
            if (outbounds[index] is not JsonObject outbound) throw Invalid();
            string protocol = Text(outbound["protocol"]);
            if (protocol is not ("vless" or "vmess" or "trojan" or "shadowsocks" or "socks" or "http" or "freedom" or "blackhole" or "dns" or "loopback")) throw Invalid();
            string tag = Text(outbound["tag"]);
            if (tag.Length == 0) tag = "json-out-" + index;
            if (tag.Length > 200 || tag.Any(char.IsControl) || !tags.Add(tag)) throw Invalid();
            outbound["tag"] = tag;
            if (protocol is "vless" or "vmess" or "trojan" or "shadowsocks" or "socks" or "http")
            {
                JsonArray servers = outbound["settings"]?[protocol is "vless" or "vmess" ? "vnext" : "servers"] as JsonArray ?? throw Invalid();
                if (servers.Count is < 1 or > 128) throw Invalid();
                foreach (JsonNode? server in servers) ValidateEndpoint(server);
            }
        }
        if (Endpoints(bundle).Length > 128) throw Invalid();
        if (bundle["routing"] is not null && bundle["routing"] is not JsonObject) throw Invalid();
        JsonObject routing = bundle["routing"] as JsonObject ?? new JsonObject(); bundle["routing"] = routing;
        if (routing["rules"] is not null && routing["rules"] is not JsonArray || routing["balancers"] is not null && routing["balancers"] is not JsonArray) throw Invalid();
        JsonArray rules = routing["rules"] as JsonArray ?? new JsonArray(); routing["rules"] = rules;
        if (rules.Count > 1024) throw Invalid();
        // The application owns listeners. Apply rules for provider SOCKS/HTTP listeners to both
        // application listeners; ignore the provider's API-only rules instead of exposing its API.
        HashSet<string> localTags = source["inbounds"] is JsonArray inbounds
            ? inbounds.OfType<JsonObject>().Where(item => Text(item["protocol"]) is "socks" or "http").Select(item => Text(item["tag"])).ToHashSet(StringComparer.Ordinal)
            : [];
        HashSet<string> virtualTags = outbounds.OfType<JsonObject>().Where(outbound=>Text(outbound["protocol"])=="loopback").Select(outbound=>Text(outbound["settings"]?["inboundTag"])).ToHashSet(StringComparer.Ordinal);
        if (virtualTags.Contains("")) throw Invalid();
        foreach (JsonObject rule in rules.OfType<JsonObject>().ToArray())
        {
            if (rule["inboundTag"] is JsonArray inboundTags)
            {
                string[] inbound = inboundTags.Select(Text).ToArray();
                if(inbound.Length==0){rule.Remove("inboundTag");continue;}
                if(inbound.Any(string.IsNullOrEmpty))throw Invalid();
                if (inbound.All(tag => tag == "api" || Text(source["api"]?["tag"]) == tag)) { rules.Remove(rule); continue; }
                if (inbound.Any(tag => localTags.Contains(tag) || tag is "socks-in" or "http-in")) rule.Remove("inboundTag");
                else if (inbound.Any(tag => tag == Text(source["dns"]?["tag"]) || virtualTags.Contains(tag))) { }
                else throw Invalid();
            }
        }
        HashSet<string> balancerTags = [];
        if (routing["balancers"] is JsonArray balancers)
        {
            if (balancers.Count > 32) throw Invalid();
            foreach (JsonObject balancer in balancers.OfType<JsonObject>())
            {
                string tag = Text(balancer["tag"]);
                if (tag.Length is < 1 or > 200 || !balancerTags.Add(tag) || balancer["selector"] is not JsonArray selector || selector.Count == 0 ||
                    !selector.Any(prefix => tags.Any(candidate => candidate.StartsWith(Text(prefix), StringComparison.Ordinal)))) throw Invalid();
                balancer["selector"] = ExpandSelector(selector,tags);
                string strategy = Text(balancer["strategy"]?["type"]);
                if (strategy is not ("" or "random" or "roundRobin" or "leastPing" or "leastLoad")) throw Invalid();
                if (strategy is "leastPing" or "leastLoad" && bundle["observatory"] is null && bundle["burstObservatory"] is null) throw Invalid();
                string fallback = Text(balancer["fallbackTag"]); if (fallback.Length > 0 && !tags.Contains(fallback)) throw Invalid();
            }
            if (balancers.Any(node=>node is not JsonObject)) throw Invalid();
        }
        foreach(string key in new[]{"observatory","burstObservatory"})
            if(bundle[key]?["subjectSelector"] is JsonArray subjects) bundle[key]!["subjectSelector"]=ExpandSelector(subjects,tags);
        foreach (JsonNode? node in rules)
        {
            if (node is not JsonObject rule) throw Invalid();
            if(rule["type"] is null)rule["type"]="field";
            string outbound = Text(rule["outboundTag"]), balancer = Text(rule["balancerTag"]);
            if (outbound.Length > 0 ? !tags.Contains(outbound) : balancer.Length == 0 || !balancerTags.Contains(balancer)) throw Invalid();
        }
        foreach (JsonObject outbound in outbounds.OfType<JsonObject>())
        {
            foreach (string reference in new[] { Text(outbound["proxySettings"]?["tag"]), Text(outbound["streamSettings"]?["sockopt"]?["dialerProxy"]) })
                if (reference.Length > 0 && !tags.Contains(reference)) throw Invalid();
        }
        var edges=outbounds.OfType<JsonObject>().ToDictionary(outbound=>Text(outbound["tag"]),outbound=>new[]{Text(outbound["proxySettings"]?["tag"]),Text(outbound["streamSettings"]?["sockopt"]?["dialerProxy"])}.Where(value=>value.Length>0).ToArray(),StringComparer.Ordinal);
        HashSet<string> complete=[];HashSet<string> active=[];
        foreach(string tag in tags)Visit(tag);
        return bundle;
        void Visit(string tag){if(complete.Contains(tag))return;if(!active.Add(tag))throw Invalid();foreach(string next in edges[tag])Visit(next);active.Remove(tag);complete.Add(tag);}
    }

    private static void ValidateEndpoint(JsonNode? node)
    {
        string address = Text(node?["address"]);
        if (address.Length == 0 || address.Length > 253 || Uri.CheckHostName(address.Trim('[', ']')) == UriHostNameType.Unknown ||
            !int.TryParse(Text(node?["port"]), out int port) || port is < 1 or > 65535) throw Invalid();
    }
    private static void RejectExternalFeatures(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj)
            {
                if (pair.Key.Equals("webhook",StringComparison.OrdinalIgnoreCase) || pair.Key.Contains("lua",StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.EndsWith("File",StringComparison.OrdinalIgnoreCase) || pair.Key.EndsWith("FilePath",StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("useBrowserForwarding",StringComparison.OrdinalIgnoreCase) && Text(pair.Value) == "true") throw Invalid();
                RejectExternalFeatures(pair.Value);
            }
        else if (node is JsonArray array) foreach (JsonNode? child in array) RejectExternalFeatures(child);
        else if (node is JsonValue value && value.TryGetValue<string>(out string? text) && text.StartsWith("ext:",StringComparison.OrdinalIgnoreCase)) throw Invalid();
    }
    private static string Text(JsonNode? node) => node is null ? "" : node is JsonValue value ? value.ToString() : throw Invalid();
    private static JsonArray ExpandSelector(JsonArray selector,HashSet<string> tags)
    {
        if(selector.Any(item=>item is not JsonValue value || !value.TryGetValue<string>(out _)))throw Invalid();
        return new(tags.Where(tag=>selector.Any(prefix=>tag.StartsWith(Text(prefix),StringComparison.Ordinal))).Order(StringComparer.Ordinal).Select(tag=>(JsonNode?)JsonValue.Create(tag)).ToArray());
    }

    public static (string Host,int Port)[] Endpoints(JsonObject bundle) => ((JsonArray)bundle["outbounds"]!).OfType<JsonObject>()
        .SelectMany(outbound => (outbound["settings"]?[Text(outbound["protocol"]) is "vless" or "vmess" ? "vnext" : "servers"] as JsonArray)?.ToArray() ?? [])
        .Select(server => (Text(server?["address"]).Trim('[', ']'), int.Parse(Text(server?["port"]),System.Globalization.CultureInfo.InvariantCulture))).Distinct().ToArray();
    public static int NodeCount(ServerProfile profile) => Endpoints(Read(profile.XrayJson)).Length;
    public static bool NeedsGeoData(ServerProfile profile) => Strings(Read(profile.XrayJson)).Any(value=>value.StartsWith("geosite:",StringComparison.OrdinalIgnoreCase)||value.StartsWith("geoip:",StringComparison.OrdinalIgnoreCase));
    private static IEnumerable<string> Strings(JsonNode? node)
    {
        if(node is JsonObject obj){foreach(var pair in obj)foreach(string value in Strings(pair.Value))yield return value;}
        else if(node is JsonArray array){foreach(JsonNode? child in array)foreach(string value in Strings(child))yield return value;}
        else if(node is JsonValue scalar&&scalar.TryGetValue<string>(out string? text)&&text is not null)yield return text;
    }
    public static string SourceKey(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(Read(text)).ToJsonString())));
    private static JsonNode Canonical(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair=>pair.Key,StringComparer.Ordinal).Select(pair=>new KeyValuePair<string,JsonNode?>(pair.Key,pair.Value is null?null:Canonical(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(value=>value is null?null:Canonical(value)).ToArray()), _ => node.DeepClone()
    };

    public static void Apply(JsonObject config,ServerProfile profile,AppSettings settings,IReadOnlyDictionary<string,string>? addresses)
    {
        JsonObject provider = Read(profile.XrayJson);
        JsonArray outbounds = (JsonArray)provider["outbounds"]!;
        foreach (JsonObject outbound in outbounds.OfType<JsonObject>())
        {
            JsonArray? servers = outbound["settings"]?[Text(outbound["protocol"]) is "vless" or "vmess" ? "vnext" : "servers"] as JsonArray;
            if (servers is null || addresses is null) continue;
            foreach (JsonObject server in servers.OfType<JsonObject>())
            {
                string host = Text(server["address"]).Trim('[', ']');
                if (!addresses.TryGetValue(host,out string? address)) continue;
                if(servers.Count>1 && outbound["streamSettings"]?["tlsSettings"] is JsonObject dynamicTls && Text(dynamicTls["serverName"]).Length==0)continue;
                server["address"] = address;
                if (servers.Count == 1 && outbound["streamSettings"] is JsonObject stream)
                {
                    if (stream["tlsSettings"] is JsonObject tls && Text(tls["serverName"]).Length == 0) tls["serverName"] = host;
                    if (stream["wsSettings"] is JsonObject ws)
                    {
                        JsonObject headers = ws["headers"] as JsonObject ?? new JsonObject(); ws["headers"] = headers;
                        if (!headers.Any(pair=>pair.Key.Equals("Host",StringComparison.OrdinalIgnoreCase))) headers["Host"] = host;
                    }
                    foreach (string transport in new[]{"httpupgradeSettings","xhttpSettings"})
                        if (stream[transport] is JsonObject http && Text(http["host"]).Length == 0) http["host"] = host;
                    if (stream["grpcSettings"] is JsonObject grpc && Text(grpc["authority"]).Length == 0) grpc["authority"] = host;
                }
            }
        }
        HashSet<string> tags = outbounds.Select(node=>Text(node?["tag"])).ToHashSet(StringComparer.Ordinal);
        // Select a first character unused by provider tags. Expanded selectors can then never
        // include the application's direct/block/API handlers, even for a wildcard pool.
        char scope=Enumerable.Range(0xe000,129).Select(value=>(char)value).First(character=>tags.All(tag=>tag[0]!=character));
        string direct = InternalTag(scope+"win10ray-direct"), block = InternalTag(scope+"win10ray-block"), first = Text(outbounds[0]!["tag"]);
        config["api"]!["tag"] = InternalTag(scope+"win10ray-api");
        JsonArray appRules = (JsonArray)config["routing"]!["rules"]!;
        appRules.RemoveAt(appRules.Count-1);
        foreach (JsonObject rule in appRules.OfType<JsonObject>()) rule["outboundTag"] = Text(rule["outboundTag"]) switch { "direct"=>direct,"block"=>block,_=>first };
        JsonObject routing = (JsonObject)provider["routing"]!.DeepClone();
        JsonArray rules = (JsonArray)routing["rules"]!;
        for (int i=appRules.Count-1;i>=0;i--) rules.Insert(0,appRules[i]!.DeepClone());
        rules.Add(new JsonObject { ["type"]="field",["network"]="tcp,udp",["outboundTag"]=first });
        if(settings.BypassRussia) routing["domainStrategy"]="IPOnDemand";
        config["routing"]=routing;
        config["outbounds"]=outbounds.DeepClone();
        ((JsonArray)config["outbounds"]!).Add(new JsonObject{["tag"]=direct,["protocol"]="freedom",["settings"]=new JsonObject()});
        ((JsonArray)config["outbounds"]!).Add(new JsonObject{["tag"]=block,["protocol"]="blackhole",["settings"]=new JsonObject()});
        foreach(string key in new[]{"observatory","burstObservatory","transport"}) if(provider[key] is JsonNode value)config[key]=value.DeepClone();
        if(provider["policy"]?["levels"] is JsonObject levels) config["policy"]!["levels"]=levels.DeepClone();
        if(provider["dns"] is JsonObject dns)
        {
            JsonObject effective=(JsonObject)dns.DeepClone();
            if(settings.UseDnsOverHttps){effective["servers"]=config["dns"]!["servers"]!.DeepClone();effective["queryStrategy"]="UseIP";}
            config["dns"]=effective;
        }
        string InternalTag(string name){while(!tags.Add(name))name+="_";return name;}
    }

    public static async Task<Dictionary<string,string>> ResolveEndpointsAsync(ServerProfile profile,CancellationToken token)
    {
        JsonObject bundle=Read(profile.XrayJson);
        Dictionary<string,string> resolved=new(StringComparer.OrdinalIgnoreCase);
        using var overall=CancellationTokenSource.CreateLinkedTokenSource(token);overall.CancelAfter(TimeSpan.FromSeconds(10));
        using SemaphoreSlim limit=new(8);
        await Task.WhenAll(Endpoints(bundle).Select(item=>item.Host).Distinct(StringComparer.OrdinalIgnoreCase).Select(async host=>
        {
            if(IPAddress.TryParse(host,out _))return;
            bool entered=false;
            try
            {
                await limit.WaitAsync(overall.Token).ConfigureAwait(false);entered=true;
                JsonNode? mapped=bundle["dns"]?["hosts"]?[host] ?? bundle["dns"]?["hosts"]?["domain:"+host];
                string fixedIp=mapped is JsonArray list ? list.Select(Text).FirstOrDefault(value=>IPAddress.TryParse(value,out _))??"" : Text(mapped);
                if(IPAddress.TryParse(fixedIp,out _)){lock(resolved)resolved[host]=fixedIp;return;}
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(overall.Token);deadline.CancelAfter(TimeSpan.FromSeconds(3));
                IPAddress[] addresses=await Dns.GetHostAddressesAsync(host,deadline.Token).ConfigureAwait(false);
                IPAddress? address=addresses.FirstOrDefault(item=>item.AddressFamily==AddressFamily.InterNetwork)??addresses.FirstOrDefault();
                if(address is not null){lock(resolved)resolved[host]=address.ToString();}
            }
            catch(Exception exception)when(exception is SocketException || exception is OperationCanceledException && !token.IsCancellationRequested){ }
            finally{if(entered)limit.Release();}
        })).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();return resolved;
    }

    public static IEnumerable<string> Secrets(ServerProfile profile) => SecretStrings(Read(profile.XrayJson));
    private static IEnumerable<string> SecretStrings(JsonNode? node,string key="",bool headers=false)
    {
        if(node is JsonObject obj) foreach(var pair in obj)foreach(string value in SecretStrings(pair.Value,pair.Key,headers||pair.Key.Equals("headers",StringComparison.OrdinalIgnoreCase)))yield return value;
        else if(node is JsonArray array)foreach(JsonNode? child in array)foreach(string value in SecretStrings(child,key,headers))yield return value;
        else if(node is JsonValue scalar)
        {
            string text=scalar.ToString();
            if(text.Length>0 && (headers || new[]{"id","password","pass","token","authorization","key","secret","privateKey","publicKey","shortId","username","user"}.Contains(key,StringComparer.OrdinalIgnoreCase)||text.Contains("://",StringComparison.Ordinal)||text.Contains("-----BEGIN",StringComparison.Ordinal)))yield return text;
            if(text.Contains("-----BEGIN",StringComparison.Ordinal))foreach(string line in text.Split('\n').Select(value=>value.Trim()).Where(value=>value.Length>20))yield return line;
        }
    }
}
