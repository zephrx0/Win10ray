using System;
using System.Collections.Generic;
using System.Linq;
using XrayMetro.Models;

namespace XrayMetro.Services;

public static class ServerGroupingService
{
    public static string Key(ServerProfile server) => server.Protocol != "xray-json" && server.SubscriptionId is Guid subscription
        ? subscription.ToString("N") + ":" + (server.ProviderName.Length > 0 ? server.ProviderName : server.Name).Trim().Normalize().ToUpperInvariant()
        : server.Id.ToString("N");

    public static bool SameGroup(ServerProfile first, ServerProfile second) => Key(first) == Key(second);

    public static HashSet<Guid> Representatives(IEnumerable<ServerProfile> servers, ServerProfile? selected)
    {
        HashSet<Guid> representatives = [];
        foreach (var group in servers.GroupBy(Key))
        {
            ServerProfile[] members = group.ToArray();
            foreach (ServerProfile member in members) member.EndpointCount = members.Length;
            representatives.Add(members.FirstOrDefault(member => member.Id == selected?.Id)?.Id ?? members[0].Id);
        }
        return representatives;
    }

    public static ServerProfile[] Candidates(IEnumerable<ServerProfile> servers, ServerProfile current, bool skipCurrent = false)
        => (skipCurrent ? Enumerable.Empty<ServerProfile>() : new[] { current })
            .Concat(servers.Where(server => server.Id != current.Id && SameGroup(server, current))
                .OrderBy(server => server.LatencyMs ?? int.MaxValue))
            .DistinctBy(server => server.Id).ToArray();
}
