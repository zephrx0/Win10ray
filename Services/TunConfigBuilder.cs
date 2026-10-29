using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using XrayMetro.Models;

namespace XrayMetro.Services;

public static class TunConfigBuilder
{
    public static string Build(bool useDnsOverHttps = false, string? dnsOverHttpsUrl = null, string interfaceName = "Win10ray", bool blockAds = true, ProcessRouteRule[]? processRules = null, bool defaultRouteDirect = false)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(interfaceName, "^Win10ray(?:-[a-f0-9]{12})?$"))
            throw new ArgumentException("Invalid TUN interface name.", nameof(interfaceName));
        JsonNode config = JsonNode.Parse(Template)!;
        config["inbounds"]![0]!["interface_name"] = interfaceName;
        config["route"]!["default_domain_resolver"] = "remote-dns";
        if (blockAds)
        {
            JsonObject block = BlockList.SingBoxMatch(); block["action"] = "reject"; block["no_drop"] = true;
            ((JsonArray)config["route"]!["rules"]!).Insert(3, block);
            JsonObject dnsBlock = BlockList.SingBoxMatch(); dnsBlock["action"] = "predefined"; dnsBlock["rcode"] = "NXDOMAIN";
            config["dns"]!["rules"] = new JsonArray(dnsBlock);
        }
        ProcessRouteRule[] processes = processRules ?? [];
        if (processes.Length > 32) throw new ArgumentException("Too many process rules.");
        JsonArray routeRules = (JsonArray)config["route"]!["rules"]!;
        JsonArray dnsRules = config["dns"]!["rules"] as JsonArray ?? new JsonArray();
        if (config["dns"]!["rules"] is null) config["dns"]!["rules"] = dnsRules;
        int routeIndex = 3, dnsIndex = 0;
        foreach (ProcessRouteRule rule in processes)
        {
            ProcessRouteRule.Validate(rule);
            string match = rule.IsPath ? "process_path" : "process_name";
            string value = rule.IsPath ? System.IO.Path.GetFullPath(rule.Value) : rule.Value;
            JsonObject route = new() { [match] = new JsonArray(value), ["action"] = rule.Action == ProcessRouteAction.Block ? "reject" : "route" };
            if (rule.Action == ProcessRouteAction.Block) route["no_drop"] = true;
            else route["outbound"] = rule.Action == ProcessRouteAction.Direct ? "direct" : "xray-override";
            routeRules.Insert(routeIndex++, route);
            JsonObject dnsRule = new() { [match] = new JsonArray(value), ["action"] = rule.Action == ProcessRouteAction.Block ? "predefined" : "route" };
            if (rule.Action == ProcessRouteAction.Block) dnsRule["rcode"] = "NXDOMAIN";
            else dnsRule["server"] = rule.Action == ProcessRouteAction.Direct ? "direct-dns" : "remote-dns";
            dnsRules.Insert(dnsIndex++, dnsRule);
        }
        if (processes.Any(rule => rule.Action == ProcessRouteAction.Proxy))
            ((JsonArray)config["outbounds"]!).Add(new JsonObject { ["type"] = "socks", ["tag"] = "xray-override", ["server"] = "127.0.0.1", ["server_port"] = 10810, ["version"] = "5" });
        config["route"]!["final"] = defaultRouteDirect ? "direct" : "xray";
        if (useDnsOverHttps)
        {
            var uri = DnsEndpoint.Parse(dnsOverHttpsUrl);
            string host = uri.IdnHost.Trim('[', ']');
            JsonObject server = new()
            {
                ["type"] = "https", ["tag"] = "remote-dns", ["server"] = host, ["server_port"] = uri.Port,
                ["path"] = uri.PathAndQuery, ["detour"] = "xray",
                ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = host }
            };
            JsonArray servers = [server];
            if (!IPAddress.TryParse(host, out _))
            {
                server["domain_resolver"] = "bootstrap";
                servers.Add(new JsonObject { ["type"] = "udp", ["tag"] = "bootstrap", ["server"] = "1.1.1.1", ["detour"] = "xray" });
            }
            config["dns"]!["servers"] = servers;
        }
        if (defaultRouteDirect || processes.Any(rule => rule.Action == ProcessRouteAction.Direct))
        {
            JsonArray servers = (JsonArray)config["dns"]!["servers"]!;
            JsonObject directDns = (JsonObject)servers[0]!.DeepClone(); directDns["tag"] = "direct-dns"; directDns.Remove("detour");
            if (directDns.ContainsKey("domain_resolver"))
            {
                directDns["domain_resolver"] = "direct-bootstrap";
                servers.Add(new JsonObject { ["type"] = "udp", ["tag"] = "direct-bootstrap", ["server"] = "1.1.1.1" });
            }
            servers.Add(directDns);
            if (defaultRouteDirect) { config["dns"]!["final"] = "direct-dns"; config["route"]!["default_domain_resolver"] = "direct-dns"; }
        }
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    private const string Template = """
    {
      "log": { "level": "info", "timestamp": false },
      "dns": {
        "servers": [{ "type": "udp", "tag": "remote-dns", "server": "1.1.1.1", "detour": "xray" }],
        "final": "remote-dns", "strategy": "prefer_ipv4"
      },
      "inbounds": [{
        "type": "tun", "tag": "tun-in", "interface_name": "Win10ray",
        "address": ["172.29.0.1/30", "fdfe:dcba:9876::1/126"],
        "mtu": 1500, "auto_route": true, "strict_route": true, "dns_mode": "hijack"
      }],
      "outbounds": [
        { "type": "socks", "tag": "xray", "server": "127.0.0.1", "server_port": 10808, "version": "5" },
        { "type": "direct", "tag": "direct" }
      ],
      "route": {
        "auto_detect_interface": true,
        "rules": [
          { "process_name": ["xray.exe", "sing-box.exe", "Win10ray.exe", "Win10ray.TunService.exe"], "action": "route", "outbound": "direct" },
          { "action": "sniff" },
          { "protocol": "dns", "action": "hijack-dns" },
          { "ip_is_private": true, "action": "route", "outbound": "direct" }
        ],
        "final": "xray"
      }
    }
    """;
}
