using System;
using System.Text.Json.Serialization;
using XrayMetro.ViewModels;
using XrayMetro.Services;

namespace XrayMetro.Models;

public sealed class ServerProfile : ObservableObject
{
    private string _name = LocalizationService.Pick("Сервер", "Server");
    private int? _latencyMs;
    private bool _isPinging;
    private bool _pingFailed;
    private string _groupName = "";
    [JsonIgnore] public string GroupName { get => _groupName; set => Set(ref _groupName, value); }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) { OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(FlagImage)); OnPropertyChanged(nameof(HasFlag)); } }
    }
    private string _flagCode = "";
    public string FlagCode { get => _flagCode; set { if (Set(ref _flagCode, value)) { OnPropertyChanged(nameof(FlagImage)); OnPropertyChanged(nameof(HasFlag)); } } }
    public bool HasCustomName { get; set; } = true;
    public string ProviderName { get; set; } = "";
    private int _endpointCount = 1;
    [JsonIgnore] public int EndpointCount { get => _endpointCount; set { if (Set(ref _endpointCount, value)) OnPropertyChanged(nameof(AddressLabel)); } }
    [JsonIgnore] public string AddressLabel => EndpointCount > 1 ? LocalizationService.Pick($"Узлов: {EndpointCount}", $"Nodes: {EndpointCount}") : Address;
    [JsonIgnore] public string DisplayName => CountryFlagService.DisplayName(Name);
    [JsonIgnore] public System.Windows.Media.ImageSource? FlagImage => CountryFlagService.Image(string.IsNullOrEmpty(FlagCode) ? CountryFlagService.CountryCode(Name) : FlagCode);
    [JsonIgnore] public bool HasFlag => FlagImage is not null;
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Protocol { get; set; } = "vless";
    public string Credential { get; set; } = "";
    public string OpenVpnConfig { get; set; } = "";
    public string OpenVpnUsername { get; set; } = "";
    public string OpenVpnPassword { get; set; } = "";
    public string OpenVpnSourceUrl { get; set; } = "";
    public string Encryption { get; set; } = "none";
    public string Method { get; set; } = "";
    public string Flow { get; set; } = "";
    public string Network { get; set; } = "tcp";
    public string Security { get; set; } = "none";
    public string ServerName { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string SpiderX { get; set; } = "";
    public string Path { get; set; } = "/";
    public string HostHeader { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Alpn { get; set; } = "";
    public string HeaderType { get; set; } = "none";
    public string TransportMode { get; set; } = "";
    public string Extra { get; set; } = "";
    public int AlterId { get; set; }
    public bool AllowInsecure { get; set; }
    public Guid? SubscriptionId { get; set; }
    public string SourceKey { get; set; } = "";

    [JsonIgnore]
    public int? LatencyMs
    {
        get => _latencyMs;
        set { if (Set(ref _latencyMs, value)) OnPropertyChanged(nameof(LatencyText)); }
    }

    [JsonIgnore]
    public bool IsPinging
    {
        get => _isPinging;
        set { if (Set(ref _isPinging, value)) OnPropertyChanged(nameof(LatencyText)); }
    }

    [JsonIgnore]
    public bool PingFailed
    {
        get => _pingFailed;
        set { if (Set(ref _pingFailed, value)) OnPropertyChanged(nameof(LatencyText)); }
    }

    [JsonIgnore]
    public string LatencyText => IsPinging ? LocalizationService.Get("Checking") : LatencyMs is int value ? $"{value} {LocalizationService.Get("Ms")}" : PingFailed ? LocalizationService.Get("Unavailable") : Protocol == "openvpn" && Network.StartsWith("udp", StringComparison.Ordinal) ? "UDP" : "";

    [JsonIgnore]
    public string Address => Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    [JsonIgnore]
    public string ProtocolLabel => Protocol == "shadowsocks" ? "SS" : Protocol.ToUpperInvariant();
}
