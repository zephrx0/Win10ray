using System.Collections.Generic;

namespace XrayMetro.Models;

public sealed record OpenVpnRemote(string Host, int Port, string Network);

public sealed class OpenVpnOptions
{
    public List<OpenVpnRemote> Remotes { get; } = [];
    public Dictionary<string, string> Values { get; } = new(System.StringComparer.Ordinal);
    public Dictionary<string, string> Inline { get; } = new(System.StringComparer.Ordinal);
    public bool RequiresCredentials { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
