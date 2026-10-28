using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XrayMetro.Models;

namespace XrayMetro.Services;

public static class ServerFailoverService
{
    public static ServerProfile[] Candidates(IReadOnlyList<ServerProfile> servers, ServerProfile current, bool skipCurrent = false)
    {
        if (current.SubscriptionId is null) return skipCurrent ? [] : [current];
        ServerProfile[] group = servers.Where(server => server.SubscriptionId == current.SubscriptionId).DistinctBy(server => server.Id).ToArray();
        int position = Array.FindIndex(group, server => server.Id == current.Id);
        if (position < 0) return skipCurrent ? group : new[] { current }.Concat(group).ToArray();
        return Enumerable.Range(skipCurrent ? 1 : 0, group.Length - (skipCurrent ? 1 : 0))
            .Select(offset => group[(position + offset) % group.Length]).ToArray();
    }

    public static async Task<ServerProfile> ConnectAsync(IEnumerable<ServerProfile> candidates, Func<ServerProfile, CancellationToken, Task<bool>> attempt, CancellationToken cancellationToken)
    {
        foreach (ServerProfile candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await attempt(candidate, cancellationToken)) return candidate;
        }
        throw new InvalidOperationException(LocalizationService.Get("NoAvailableServers"));
    }
}
