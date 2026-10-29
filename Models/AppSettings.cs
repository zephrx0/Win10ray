using System;
namespace XrayMetro.Models;
public sealed class AppSettings
{
    public bool UseSystemProxy { get; set; } = true;
    public bool UseTun { get; set; }
    public bool SendHwid { get; set; } = true;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool ExperimentalUpdates { get; set; } = true;
    public bool AutoSwitchServers { get; set; } = true;
    public bool BypassRussia { get; set; }
    public bool BlockAds { get; set; } = true;
    public bool DefaultRouteDirect { get; set; }
    public System.Collections.Generic.List<ProcessRouteRule> ProcessRules { get; set; } = [];
    public bool AutoUpdateSubscriptions { get; set; } = true;
    public int SubscriptionUpdateHours { get; set; } = 12;
    public bool SubscriptionsThroughConnection { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool UseDnsOverHttps { get; set; }
    public string DnsOverHttpsUrl { get; set; } = "https://1.1.1.1/dns-query";
    public bool StartMinimized { get; set; }
    public bool AutoConnect { get; set; }
    public bool ResumeConnection { get; set; }
    public bool DarkTheme { get; set; } = true;
    public string AccentColor { get; set; } = "#0078D7";
    public Guid? LastServerId { get; set; }
}
