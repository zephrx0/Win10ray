using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrayMetro.Services;

public static class TunConfigBuilder
{
    public static string Build(bool useDnsOverHttps = false, string? dnsOverHttpsUrl = null, string interfaceName = "Win10ray")
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(interfaceName, "^Win10ray(?:-[a-f0-9]{12})?$"))
            throw new ArgumentException("Invalid TUN interface name.", nameof(interfaceName));
        JsonNode config = JsonNode.Parse(Template)!;
        config["inbounds"]![0]!["interface_name"] = interfaceName;
        config["route"]!["default_domain_resolver"] = "remote-dns";
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
          { "process_name": ["xray.exe", "Win10ray.exe", "Win10ray.TunService.exe"], "action": "route", "outbound": "direct" },
          { "action": "sniff" },
          { "protocol": "dns", "action": "hijack-dns" },
          { "ip_is_private": true, "action": "route", "outbound": "direct" }
        ],
        "final": "xray"
      }
    }
    """;
}
