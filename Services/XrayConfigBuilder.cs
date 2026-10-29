using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using XrayMetro.Models;

namespace XrayMetro.Services;

public sealed class XrayConfigBuilder
{
    public const int SocksPort = 10808;
    public const int HttpPort = 10809;
    public const int DefaultApiPort = 10085;

    public string Build(ServerProfile server, AppSettings settings, int apiPort = DefaultApiPort, int socksPort = SocksPort, int httpPort = HttpPort, string? resolvedServerAddress = null, int? openVpnBridgePort = null)
    {
        LinkParser.Validate(server);
        if (server.Protocol == "openvpn" && (openVpnBridgePort is null or < 1 or > 65535)) throw new ArgumentException("OpenVPN bridge port required.", nameof(openVpnBridgePort));
        if (resolvedServerAddress is not null && !IPAddress.TryParse(resolvedServerAddress, out _)) throw new ArgumentException("Resolved endpoint must be an IP address.", nameof(resolvedServerAddress));
        if (apiPort is < 1 or > 65535 || socksPort is < 1 or > 65535 || httpPort is < 1 or > 65535 ||
            apiPort == socksPort || apiPort == httpPort || socksPort == httpPort) throw new ArgumentOutOfRangeException(nameof(apiPort));
        JsonArray rules = [];
        bool processProxy = settings.UseTun && settings.ProcessRules.Any(rule => rule.Action == ProcessRouteAction.Proxy);
        if (processProxy) rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = Strings("process-override"), ["outboundTag"] = "proxy" });
        if (settings.BlockAds)
            rules.Add(new JsonObject { ["type"] = "field", ["domain"] = Strings("geosite:category-ads-all", "geosite:category-ads-and-telemetry"), ["outboundTag"] = "block" });
        if (settings.BypassRussia)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["domain"] = Strings("geosite:category-ru"), ["outboundTag"] = "direct" });
            rules.Add(new JsonObject { ["type"] = "field", ["ip"] = Strings("geoip:ru"), ["outboundTag"] = "direct" });
        }
        rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = "proxy" });
        JsonObject config = new()
        {
            ["log"] = new JsonObject { ["access"] = "none", ["loglevel"] = "warning", ["dnsLog"] = false },
            // Xray's dedicated API listener needs no extra routing or traffic-accounted inbound.
            ["api"] = new JsonObject { ["tag"] = "api", ["listen"] = $"127.0.0.1:{apiPort}", ["services"] = Strings("StatsService") },
            ["stats"] = new JsonObject(),
            ["policy"] = new JsonObject
            {
                ["system"] = new JsonObject { ["statsInboundUplink"] = true, ["statsInboundDownlink"] = true }
            },
            ["dns"] = new JsonObject { ["servers"] = Strings(settings.UseDnsOverHttps ? DnsEndpoint.Parse(settings.DnsOverHttpsUrl).AbsoluteUri : "1.1.1.1"), ["queryStrategy"] = "UseIP" },
            ["inbounds"] = new JsonArray
            {
                Inbound("socks-in", "socks", socksPort, new JsonObject { ["auth"] = "noauth", ["udp"] = true, ["ip"] = "127.0.0.1" }),
                Inbound("http-in", "http", httpPort, new JsonObject())
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject { ["tag"] = "proxy", ["protocol"] = server.Protocol, ["settings"] = ProtocolSettings(server, resolvedServerAddress ?? server.Host), ["streamSettings"] = StreamSettings(server, resolvedServerAddress is not null) },
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom", ["settings"] = new JsonObject() },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole", ["settings"] = new JsonObject() }
            },
            // Resolve before the geoip rule; IPIfNonMatch would be bypassed by the final catch-all.
            ["routing"] = new JsonObject { ["domainStrategy"] = settings.BypassRussia ? "IPOnDemand" : "AsIs", ["rules"] = rules }
        };
        if (processProxy)
        {
            if (new[] { apiPort, socksPort, httpPort }.Contains(10810)) throw new ArgumentException("Process override port collision.");
            ((JsonArray)config["inbounds"]!).Add(Inbound("process-override", "socks", 10810, new JsonObject { ["auth"] = "noauth", ["udp"] = true, ["ip"] = "127.0.0.1" }));
        }
        if (server.Protocol == "openvpn")
            config["outbounds"]![0] = new JsonObject { ["tag"] = "proxy", ["protocol"] = "socks", ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = openVpnBridgePort!.Value }) } };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Inbound(string tag, string protocol, int port, JsonObject settings) => new()
    {
        ["tag"] = tag, ["listen"] = "127.0.0.1", ["port"] = port, ["protocol"] = protocol,
        ["settings"] = settings,
        ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = Strings("http", "tls", "quic"), ["routeOnly"] = true }
    };

    private static JsonObject ProtocolSettings(ServerProfile server, string endpoint)
    {
        if (server.Protocol is "vless" or "vmess")
        {
            JsonObject user = new() { ["id"] = server.Credential, ["level"] = 0 };
            if (server.Protocol == "vless")
            {
                user["encryption"] = server.Encryption;
                if (server.Flow.Length > 0) user["flow"] = server.Flow;
            }
            else
            {
                user["alterId"] = server.AlterId;
                user["security"] = server.Encryption;
            }
            return new JsonObject { ["vnext"] = new JsonArray { new JsonObject { ["address"] = endpoint, ["port"] = server.Port, ["users"] = new JsonArray { user } } } };
        }
        JsonObject target = new() { ["address"] = endpoint, ["port"] = server.Port, ["password"] = server.Credential };
        if (server.Protocol == "shadowsocks") target["method"] = server.Method;
        return new JsonObject { ["servers"] = new JsonArray { target } };
    }

    private static JsonObject StreamSettings(ServerProfile server, bool pinnedEndpoint)
    {
        JsonObject stream = new() { ["network"] = server.Network, ["security"] = server.Security };
        if (server.Security == "tls")
        {
            JsonObject tls = new() { ["allowInsecure"] = server.AllowInsecure };
            if (server.ServerName.Length > 0) tls["serverName"] = server.ServerName;
            else if (pinnedEndpoint) tls["serverName"] = server.Host;
            if (server.Fingerprint.Length > 0) tls["fingerprint"] = server.Fingerprint;
            if (server.Alpn.Length > 0) tls["alpn"] = Strings(server.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            stream["tlsSettings"] = tls;
        }
        else if (server.Security == "reality")
        {
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = server.ServerName.Length > 0 ? server.ServerName : server.Host,
                ["fingerprint"] = server.Fingerprint,
                ["publicKey"] = server.PublicKey,
                ["shortId"] = server.ShortId,
                ["spiderX"] = server.SpiderX
            };
        }
        switch (server.Network)
        {
            case "ws":
                JsonObject websocket = new() { ["path"] = server.Path };
                if (server.HostHeader.Length > 0 || pinnedEndpoint) websocket["headers"] = new JsonObject { ["Host"] = server.HostHeader.Length > 0 ? server.HostHeader : server.Host };
                stream["wsSettings"] = websocket;
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = server.ServiceName, ["multiMode"] = server.TransportMode == "multi" };
                if (pinnedEndpoint || server.HostHeader.Length > 0) stream["grpcSettings"]!["authority"] = server.HostHeader.Length > 0 ? server.HostHeader : server.Host;
                break;
            case "httpupgrade":
                stream["httpupgradeSettings"] = new JsonObject { ["path"] = server.Path, ["host"] = server.HostHeader.Length > 0 ? server.HostHeader : pinnedEndpoint ? server.Host : "" };
                break;
            case "xhttp":
                JsonObject xhttp = new() { ["path"] = server.Path, ["host"] = server.HostHeader.Length > 0 ? server.HostHeader : pinnedEndpoint ? server.Host : "", ["mode"] = server.TransportMode.Length == 0 ? "auto" : server.TransportMode };
                if (server.Extra.Length > 0) xhttp["extra"] = JsonNode.Parse(server.Extra);
                stream["xhttpSettings"] = xhttp;
                break;
            case "tcp" when server.HeaderType == "http":
                JsonObject request = new() { ["path"] = Strings(server.Path) };
                if (server.HostHeader.Length > 0) request["headers"] = new JsonObject { ["Host"] = Strings(server.HostHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) };
                stream["tcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = "http", ["request"] = request } };
                break;
        }
        return stream;
    }

    private static JsonArray Strings(params string[] values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
