using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XrayMetro.Models;

namespace XrayMetro.Services;

public sealed class OpenVpnImportService : IDisposable
{
    private readonly HttpClient _http;
    public OpenVpnImportService(int? proxyPort = null, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = proxyPort is not null,
            Proxy = proxyPort is int port ? new WebProxy("http://127.0.0.1:" + port) : null, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(HardwareIdService.UserAgent);
    }
    public static bool TryGetUrl(string text, out Uri? uri)
    {
        text = text.Trim();
        if (text.IndexOfAny(['\r', '\n']) >= 0) { uri = null; return false; }
        const string prefix = "openvpn://import-profile/";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];
        return Uri.TryCreate(text, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0;
    }
    public async Task<ServerProfile> FetchAsync(Uri uri, CancellationToken token)
    {
        if (!TryGetUrl(uri.AbsoluteUri, out _)) throw new InvalidOperationException(LocalizationService.Get("OpenVpnImportUrl"));
        Uri source = uri;
        try
        {
            for (int redirects = 0; redirects < 6; redirects++)
            {
                using HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and <= 399)
                {
                    Uri next = new(uri, response.Headers.Location ?? throw new InvalidOperationException(LocalizationService.Get("OpenVpnDownloadFailed")));
                    if (!TryGetUrl(next.AbsoluteUri, out _)) throw new InvalidOperationException(LocalizationService.Get("OpenVpnDownloadFailed"));
                    uri = next; continue;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > OpenVpnProfileParser.MaximumBytes || response.Content.Headers.ContentType?.MediaType == "text/html") throw new InvalidOperationException(LocalizationService.Get("OpenVpnDownloadFailed"));
                await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using MemoryStream output = new(); byte[] buffer = new byte[8192]; int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + count > OpenVpnProfileParser.MaximumBytes) throw new InvalidOperationException(LocalizationService.Get("OpenVpnDownloadFailed"));
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                string text = new UTF8Encoding(false, true).GetString(output.ToArray());
                ServerProfile profile = OpenVpnProfileParser.Parse(text, uri.Host);
                profile.OpenVpnSourceUrl = source.AbsoluteUri;
                if (response.Headers.TryGetValues("VPN-Session-User", out var users)) profile.OpenVpnUsername = System.Linq.Enumerable.First(users);
                if (response.Headers.TryGetValues("VPN-Session-Token", out var tokens)) profile.OpenVpnPassword = System.Linq.Enumerable.First(tokens);
                return profile;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or DecoderFallbackException or OperationCanceledException && !token.IsCancellationRequested)
        { throw new InvalidOperationException(LocalizationService.Get("OpenVpnDownloadFailed")); }
        throw new InvalidOperationException(LocalizationService.Get("OpenVpnDownloadFailed"));
    }
    public void Dispose() => _http.Dispose();
}
