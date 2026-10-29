using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using XrayMetro.Models;

namespace XrayMetro.Services;

public static class OpenVpnConfigBuilder
{
    public static JsonObject Endpoint(ServerProfile profile, string? username = null, string? password = null)
    {
        OpenVpnOptions options = OpenVpnProfileParser.Read(profile.OpenVpnConfig);
        JsonObject endpoint = new()
        {
            ["type"] = "openvpn-client", ["tag"] = "openvpn", ["mode"] = "tls", ["system"] = false,
            ["servers"] = new JsonArray(options.Remotes.Select(remote => (JsonNode?)new JsonObject { ["server"] = remote.Host, ["server_port"] = remote.Port, ["network"] = remote.Network }).ToArray()),
            ["route_no_pull"] = true, ["domain_resolver"] = "bootstrap"
        };
        JsonObject tls = new(); endpoint["tls"] = tls;
        AddMaterial("ca", "certificate"); AddMaterial("cert", "client_certificate"); AddMaterial("key", "client_key");
        string[] wrapping = new[] { "tls-auth", "tls-crypt", "tls-crypt-v2" }.Where(options.Inline.ContainsKey).ToArray();
        if (wrapping.Length > 1) throw new ShareLinkException(LocalizationService.Get("OpenVpnInvalid"));
        if (wrapping.Length == 1)
        {
            string type = wrapping[0]; JsonObject wrap = new() { ["type"] = type.Replace('-', '_'), ["key"] = options.Inline[type] };
            if (type == "tls-auth" && options.Values.TryGetValue("key-direction", out string? direction)) wrap["direction"] = direction switch { "0" => "server", "1" => "client", _ => throw new ShareLinkException(LocalizationService.Get("OpenVpnInvalid")) };
            tls["control_wrap"] = wrap;
        }
        foreach (var pair in options.Values)
        {
            string value = pair.Value;
            string[] args = value.Split('\t');
            switch (pair.Key)
            {
                case "remote-random": endpoint["remote_random"] = true; break;
                case "remote-random-hostname": throw new ShareLinkException(LocalizationService.Pick("remote-random-hostname не поддерживается.", "remote-random-hostname is not supported."));
                case "auth": endpoint["auth"] = value; break;
                case "cipher":
                    if (!options.Values.ContainsKey("data-ciphers-fallback")) endpoint["data_ciphers_fallback"] = value;
                    if (!options.Values.ContainsKey("data-ciphers") && !options.Values.ContainsKey("ncp-ciphers")) endpoint["data_ciphers"] = Array(new[] { "AES-256-GCM", "AES-128-GCM", "CHACHA20-POLY1305", value }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                    break;
                case "data-ciphers": case "ncp-ciphers": endpoint["data_ciphers"] = Array(value.Split(':')); break;
                case "data-ciphers-fallback": endpoint["data_ciphers_fallback"] = value; break;
                case "tls-version-min": tls["version_min"] = args[0]; break;
                case "tls-version-max": tls["version_max"] = args[0]; break;
                case "tls-cipher": tls["cipher"] = value; break;
                case "tls-groups": tls["groups"] = value; break;
                case "remote-cert-tls": tls["remote_certificate_tls"] = value; break;
                case "remote-cert-ku": tls["remote_certificate_ku"] = Array(args); break;
                case "remote-cert-eku": tls["remote_certificate_eku"] = string.Join(" ", args); break;
                case "ns-cert-type": tls["ns_certificate_type"] = value; break;
                case "peer-fingerprint": tls["peer_fingerprint"] = value.Replace(":", "", StringComparison.Ordinal).ToLowerInvariant(); break;
                case "verify-x509-name": tls["server_name"] = args[0]; tls["server_name_type"] = args.Length > 1 ? args[1] : "subject"; break;
                case "tun-mtu": endpoint["mtu"] = Number(args[0], 576, 65535); break;
                case "mssfix": if (value.Length == 0) break; int mss = Number(args[0], 0, 65535); if (mss == 0) endpoint["mss_fix_disabled"] = true; else { endpoint["mss_fix"] = mss; if (args.Length > 1) endpoint["mss_fix_mode"] = args[1]; } break;
                case "fragment": endpoint["fragment"] = Number(args[0], 68, 65535); break;
                case "explicit-exit-notify": endpoint["explicit_exit_notify"] = value.Length == 0 ? 1 : Number(value, 0, 10); break;
                case "ping": endpoint["ping_interval"] = Duration(value); break;
                case "ping-restart": if (value == "0") endpoint["ping_restart_disabled"] = true; else endpoint["ping_restart"] = Duration(value); break;
                case "keepalive": if (args.Length != 2) throw new ShareLinkException(LocalizationService.Get("OpenVpnInvalid")); endpoint["ping_interval"] = Duration(args[0]); endpoint["ping_restart"] = Duration(args[1]); break;
                case "reneg-sec": if (args[0] == "0") endpoint["renegotiate_disabled"] = true; else endpoint["renegotiate_interval"] = Duration(args[0]); break;
                case "reneg-bytes": endpoint["renegotiate_bytes"] = Number(value, 0, int.MaxValue); break;
                case "reneg-pkts": endpoint["renegotiate_packets"] = Number(value, 0, int.MaxValue); break;
                case "tls-timeout": endpoint["tls_timeout"] = Duration(value); break;
                case "hand-window": endpoint["handshake_window"] = Duration(value); break;
                case "replay-window": endpoint["replay_window"] = Number(args[0], 1, 65536); if (args.Length > 1) endpoint["replay_window_time"] = Duration(args[1]); break;
                case "compress": endpoint["compression"] = value.Length == 0 ? "stub" : value; break;
                case "comp-lzo": endpoint["compression_lzo"] = value.Length == 0 ? "adaptive" : value; break;
                case "allow-compression": endpoint["allow_compression"] = value; break;
                case "block-ipv6": endpoint["block_ipv6"] = true; break;
                case "route-nopull": case "pull-filter": case "key-direction": break;
                default: throw new ShareLinkException(LocalizationService.Get("OpenVpnInvalid"));
            }
        }
        if (options.RequiresCredentials)
        {
            endpoint["username"] = username ?? (profile.OpenVpnUsername.Length > 0 ? profile.OpenVpnUsername : options.Username);
            endpoint["password"] = password ?? (profile.OpenVpnPassword.Length > 0 ? profile.OpenVpnPassword : options.Password);
            endpoint["auth_retry"] = "none";
        }
        return endpoint;
        void AddMaterial(string block, string field) { if (options.Inline.TryGetValue(block, out string? text)) tls[field] = text; }
    }

    public static string Build(ServerProfile profile, int socksPort, int apiPort, string secret, string? username = null, string? password = null, bool useDnsOverHttps = false, string? dnsOverHttpsUrl = null)
    {
        if (socksPort is < 1 or > 65535 || apiPort is < 1 or > 65535 || socksPort == apiPort || secret.Length < 32) throw new ArgumentException("Invalid OpenVPN bridge listener.");
        JsonObject config = new()
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = false },
            ["dns"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["type"] = "udp", ["tag"] = "bootstrap", ["server"] = "1.1.1.1" }, new JsonObject { ["type"] = "udp", ["tag"] = "vpn-dns", ["server"] = "1.1.1.1", ["detour"] = "openvpn" }), ["final"] = "vpn-dns" },
            ["inbounds"] = new JsonArray(new JsonObject { ["type"] = "socks", ["tag"] = "bridge", ["listen"] = "127.0.0.1", ["listen_port"] = socksPort }),
            ["endpoints"] = new JsonArray(Endpoint(profile, username, password)),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject { ["auto_detect_interface"] = true, ["final"] = "openvpn", ["default_domain_resolver"] = "vpn-dns" },
            ["services"] = new JsonArray(new JsonObject { ["type"] = "api", ["tag"] = "control", ["listen"] = "127.0.0.1", ["listen_port"] = apiPort, ["secret"] = secret, ["dashboard"] = new JsonObject { ["enabled"] = false } })
        };
        if (useDnsOverHttps)
        {
            Uri uri = DnsEndpoint.Parse(dnsOverHttpsUrl); string host = uri.IdnHost.Trim('[', ']');
            config["dns"]!["servers"]![1] = new JsonObject { ["type"] = "https", ["tag"] = "vpn-dns", ["server"] = host, ["server_port"] = uri.Port,
                ["path"] = uri.PathAndQuery, ["detour"] = "openvpn", ["domain_resolver"] = "bootstrap", ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = host } };
        }
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    private static int Number(string text, int min, int max) => OpenVpnProfileParser.Number(text, min, max);
    private static string Duration(string value) => Number(value, 1, 864000) + "s";
    private static JsonArray Array(string[] values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
