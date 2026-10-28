using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace XrayMetro.Services;

public static class NetworkReadinessService
{
    public static async Task WaitAsync(CancellationToken token)
    {
        while (!NetworkInterface.GetIsNetworkAvailable()) await Task.Delay(500, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
}
