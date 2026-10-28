using System;

namespace XrayMetro.Services;

public static class DnsEndpoint
{
    public const string Default = "https://1.1.1.1/dns-query";
    public static bool IsValid(string? value) => value is { Length: > 0 and <= 2048 } &&
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https" &&
        !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && uri.Port is > 0 and <= 65535;
    public static Uri Parse(string? value) => IsValid(value) ? new Uri(value!, UriKind.Absolute) : throw new ArgumentException("Invalid DoH endpoint.");
}
