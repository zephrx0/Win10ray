using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XrayMetro.Services;

public sealed class SubscriptionService : IDisposable
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly HttpClient _proxied;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public SubscriptionService(int httpProxyPort = XrayConfigBuilder.HttpPort)
    {
        _client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = false
        }) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(HardwareIdService.UserAgent);
        _proxied = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = true, Proxy = new WebProxy("http://127.0.0.1:" + httpProxyPort) }) { Timeout = TimeSpan.FromSeconds(30) };
        _proxied.DefaultRequestHeaders.UserAgent.ParseAdd(HardwareIdService.UserAgent);
    }

    public async Task<ImportResult> UpdateAsync(Models.SubscriptionProfile subscription, CancellationToken token, bool sendHwid, bool throughConnection)
    {
        SemaphoreSlim gate = _locks.GetOrAdd(subscription.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await FetchAsync(subscription.Url, token, sendHwid, throughConnection, subscription.PreferJson).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public static bool IsDue(Models.SubscriptionProfile subscription, DateTimeOffset now, int hours, bool networkOrStartup = false)
        => (subscription.LastAutoAttempt is null || now - subscription.LastAutoAttempt >= TimeSpan.FromMinutes(15)) &&
           (subscription.RetryAfter is not null ? now >= subscription.RetryAfter : networkOrStartup || subscription.UpdatedAt is null || now - subscription.UpdatedAt >= TimeSpan.FromHours(hours));

    public static TimeSpan RetryDelay(int failures) => TimeSpan.FromMinutes(Math.Min(720, 15 * Math.Pow(2, Math.Clamp(failures - 1, 0, 6))));

    public async Task<ImportResult> FetchAsync(string url, CancellationToken cancellationToken = default, bool sendHwid = true, bool throughConnection = false, bool preferJson = true)
    {
        ImportResult original = await FetchCoreAsync(url, cancellationToken, sendHwid, throughConnection).ConfigureAwait(false);
        // Accept alone does not select JSON on panels that dispatch by User-Agent.
        // Keep our own identity and try the documented JSON route without changing the stored URL.
        if (!preferJson || original.Profiles.Count == 0 || original.Profiles.Any(profile => profile.Protocol is not ("vless" or "vmess" or "trojan" or "shadowsocks")) || !TryJsonVariant(url, out string variant)) return original;
        using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            ImportResult json = await FetchCoreAsync(variant, probe.Token, sendHwid, throughConnection).ConfigureAwait(false);
            if (json.Profiles.Count > 0 && json.Errors.Count == 0 && json.Profiles.All(profile => profile.Protocol == "xray-json")) return json;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is (InvalidOperationException or OperationCanceledException))
        {
            // Unsupported routes, incompatible JSON and timeouts retain the working response.
        }
        cancellationToken.ThrowIfCancellationRequested();
        return original;
    }

    private static bool TryJsonVariant(string url, out string variant)
    {
        Uri uri = new(url.Trim(), UriKind.Absolute);
        string last = uri.Segments.LastOrDefault()?.Trim('/').ToLowerInvariant() ?? "";
        variant = "";
        if (last.Length == 0 || last.Contains('.') || last is "json" or "v2ray-json" or "clash" or "singbox" or "mihomo" or "stash" ||
            uri.Query.TrimStart('?').Split('&').Any(parameter => parameter.Split('=')[0].ToLowerInvariant() is "type" or "format" or "target")) return false;
        variant = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/json", Fragment = "" }.Uri.AbsoluteUri;
        return true;
    }

    private async Task<ImportResult> FetchCoreAsync(string url, CancellationToken cancellationToken, bool sendHwid, bool throughConnection)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || !IsHttp(uri) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException(LocalizationService.Pick("Введите корректный URL подписки с http:// или https://.", "Enter a valid HTTP or HTTPS subscription URL."));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            string? hwid = sendHwid ? await HardwareIdService.GetIdAsync().WaitAsync(timeout.Token).ConfigureAwait(false) : null;
            Uri origin = uri;
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("application/json, text/plain;q=0.8, */*;q=0.5");
                // Do not disclose a device identifier to a different redirect origin.
                if (hwid is not null && SameOrigin(origin, uri))
                {
                    request.Headers.Add("x-hwid", hwid);
                    request.Headers.Add("x-device-os", "Windows");
                    request.Headers.Add("x-ver-os", Environment.OSVersion.Version.ToString());
                    request.Headers.Add("x-device-model", HardwareIdService.DeviceDescription);
                    request.Headers.Add("x-app-name", HardwareIdService.ClientName);
                    request.Headers.Add("x-app-version", HardwareIdService.ClientVersion);
                }
                using HttpResponseMessage response = await (throughConnection ? _proxied : _client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (HeaderIsTrue(response, "x-hwid-max-devices-reached") || HeaderIsTrue(response, "x-hwid-limit"))
                    throw new InvalidOperationException(LocalizationService.Get("HwidLimit"));
                if (HeaderIsTrue(response, "x-hwid-not-supported"))
                    throw new InvalidOperationException(LocalizationService.Get("HwidRequired"));
                if ((int)response.StatusCode is >= 300 and <= 399)
                {
                    Uri? location = response.Headers.Location;
                    if (location is null || redirect == 5)
                        throw new InvalidOperationException(LocalizationService.Pick("Подписка содержит слишком много перенаправлений или некорректный ответ.", "Too many redirects or an invalid subscription response."));
                    Uri next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (!IsHttp(next) || !string.IsNullOrEmpty(next.UserInfo) || (uri.Scheme == "https" && next.Scheme != "https"))
                        throw new InvalidOperationException(LocalizationService.Pick("Подписка перенаправляет на недопустимый адрес.", "Subscription redirects to an unsupported address."));
                    uri = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(LocalizationService.Pick($"Сервер подписки вернул HTTP {(int)response.StatusCode}.", $"Subscription server returned HTTP {(int)response.StatusCode}."));
                if (response.Content.Headers.ContentLength > MaximumBytes)
                    throw new InvalidOperationException(LocalizationService.Pick("Размер подписки превышает 4 МБ.", "Subscription exceeds 4 MB."));
                await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using MemoryStream data = new();
                byte[] buffer = new byte[8192];
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    if (data.Length + read > MaximumBytes)
                        throw new InvalidOperationException(LocalizationService.Pick("Размер подписки превышает 4 МБ.", "Subscription exceeds 4 MB."));
                    await data.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
                string text = Encoding.UTF8.GetString(data.GetBuffer(), 0, checked((int)data.Length)).TrimStart('\uFEFF');
                return new LinkParser().ParseMany(text);
            }
            throw new InvalidOperationException(LocalizationService.Pick("Не удалось загрузить подписку.", "Could not load the subscription."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Сервер подписки не ответил за 30 секунд.", "Subscription request timed out after 30 seconds."));
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Не удалось загрузить подписку. Проверьте подключение, адрес и сертификат сервера.", "Could not load the subscription. Check the connection, URL and certificate."));
        }
        catch (IOException)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Загрузка подписки была прервана.", "Subscription download was interrupted."));
        }
    }

    private static bool IsHttp(Uri uri) => uri.Scheme is "http" or "https";
    private static bool SameOrigin(Uri first, Uri second) => first.Scheme == second.Scheme && first.IdnHost.Equals(second.IdnHost, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port;
    private static bool HeaderIsTrue(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) && System.Linq.Enumerable.Any(values, value => value.Equals("true", StringComparison.OrdinalIgnoreCase));
    public void Dispose() { _client.Dispose(); _proxied.Dispose(); }
}
