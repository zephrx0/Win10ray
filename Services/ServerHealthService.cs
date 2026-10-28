using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XrayMetro.Models;

namespace XrayMetro.Services;

public sealed class ServerHealthService
{
    private readonly Uri[] _probeUris;
    public ServerHealthService(params Uri[] probeUris) => _probeUris = probeUris.Length > 0 ? probeUris :
        [new("https://www.gstatic.com/generate_204"), new("https://cp.cloudflare.com/generate_204")];

    public async Task<int?> PingAsync(ServerProfile server, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var socket = new TcpClient();
            var watch = Stopwatch.StartNew();
            await socket.ConnectAsync(server.Host, server.Port, timeout.Token).ConfigureAwait(false);
            return Math.Max(1, (int)watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }

    public async Task<bool> CheckTunnelAsync(int httpProxyPort, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(IPAddress.Loopback.ToString(), httpProxyPort) { BypassProxyOnLocal = false },
            UseProxy = true, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(4)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        Task<bool>[] probes = _probeUris.Select(ProbeAsync).ToArray();
        var pending = new List<Task<bool>>(probes);
        bool success = false;
        while (pending.Count > 0)
        {
            Task<bool> completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);
            if (await completed.ConfigureAwait(false)) { success = true; timeout.Cancel(); break; }
        }
        await Task.WhenAll(probes).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return success;

        async Task<bool> ProbeAsync(Uri uri)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
            return false;
        }
    }
}
