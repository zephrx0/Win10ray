using XrayMetro.Models;
using XrayMetro.Services;

internal static class FailoverTests
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("FAIL: failover policy"); checks++; }
        Guid subscription = Guid.NewGuid();
        var first = new ServerProfile { SubscriptionId = subscription };
        var second = new ServerProfile { SubscriptionId = subscription };
        var third = new ServerProfile { SubscriptionId = subscription };
        var foreign = new ServerProfile { SubscriptionId = Guid.NewGuid() };
        var manual = new ServerProfile();
        ServerProfile[] servers = [first, foreign, second, manual, third];
        Check(ServerFailoverService.Candidates(servers, second).Select(s => s.Id).SequenceEqual(new[] { second.Id, third.Id, first.Id }));
        Check(ServerFailoverService.Candidates(servers, second, true).Select(s => s.Id).SequenceEqual(new[] { third.Id, first.Id }));
        Check(ServerFailoverService.Candidates(servers, manual).Single() == manual && ServerFailoverService.Candidates(servers, manual, true).Length == 0);
        var attempted = new List<Guid>();
        ServerProfile result = await ServerFailoverService.ConnectAsync(ServerFailoverService.Candidates(servers, first), (server, _) => { attempted.Add(server.Id); return Task.FromResult(server == second); }, CancellationToken.None);
        Check(result == second && attempted.SequenceEqual(new[] { first.Id, second.Id }));
        try { await ServerFailoverService.ConnectAsync([first, second], (_, _) => Task.FromResult(false), CancellationToken.None); throw new Exception("No available servers was accepted"); }
        catch (InvalidOperationException exception) { Check(exception.Message == LocalizationService.Get("NoAvailableServers")); }
        using var cancellation = new CancellationTokenSource();
        attempted.Clear();
        try
        {
            await ServerFailoverService.ConnectAsync([first, second], (server, _) => { attempted.Add(server.Id); cancellation.Cancel(); return Task.FromResult(false); }, cancellation.Token);
            throw new Exception("Cancellation ignored");
        }
        catch (OperationCanceledException) { Check(attempted.Count == 1); }
        return checks;
    }
}
