using System.Collections.Generic;

namespace XrayMetro.Models;

public sealed class AppState
{
    public AppSettings Settings { get; set; } = new();
    public List<ServerProfile> Servers { get; set; } = [];
    public List<SubscriptionProfile> Subscriptions { get; set; } = [];
}
