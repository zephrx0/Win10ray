using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using XrayMetro.Models;

namespace XrayMetro.Services;

public static class ShareLinkExporter
{
    public static string Export(ServerProfile server)
    {
        LinkParser.Validate(server);
        string authority = server.Address;
        string name = "#" + Uri.EscapeDataString(server.Name);
        if (server.Protocol == "openvpn") return OpenVpnImportService.TryGetUrl(server.OpenVpnSourceUrl, out _) ? "openvpn://import-profile/" + server.OpenVpnSourceUrl : "ovpn://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(server.OpenVpnConfig)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + name;
        if (server.Protocol == "shadowsocks")
            return "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(server.Method + ":" + server.Credential)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "@" + authority + name;
        Dictionary<string, string> query = new()
        {
            ["encryption"] = server.Encryption, ["type"] = server.Network, ["security"] = server.Security,
            ["sni"] = server.ServerName, ["fp"] = server.Fingerprint, ["pbk"] = server.PublicKey,
            ["sid"] = server.ShortId, ["spx"] = server.SpiderX, ["flow"] = server.Flow,
            ["path"] = server.Path, ["host"] = server.HostHeader, ["serviceName"] = server.ServiceName,
            ["alpn"] = server.Alpn, ["headerType"] = server.HeaderType, ["mode"] = server.TransportMode,
            ["extra"] = server.Extra, ["aid"] = server.AlterId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["allowInsecure"] = server.AllowInsecure ? "1" : "0"
        };
        return server.Protocol + "://" + Uri.EscapeDataString(server.Credential) + "@" + authority + "?" +
            string.Join("&", query.Where(pair => pair.Value.Length > 0).Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value))) + name;
    }
}
