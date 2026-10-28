using System;
namespace XrayMetro.Models;
public sealed class AppSettings
{
    public bool UseSystemProxy { get; set; } = true;
    public bool UseTun { get; set; }
    public bool SendHwid { get; set; } = true;
    public bool AutoSwitchServers { get; set; } = true;
    public bool BypassRussia { get; set; }
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
