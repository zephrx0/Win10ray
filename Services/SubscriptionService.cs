using System;
using System.IO;
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

    public SubscriptionService()
    {
        _client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = false
        }) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(HardwareIdService.UserAgent);
    }

    public async Task<ImportResult> FetchAsync(string url, CancellationToken cancellationToken = default, bool sendHwid = true)
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
                using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
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
    public void Dispose() => _client.Dispose();
}
