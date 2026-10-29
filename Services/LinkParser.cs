using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XrayMetro.Models;

namespace XrayMetro.Services;

public sealed record ImportResult(IReadOnlyList<ServerProfile> Profiles, IReadOnlyList<string> Errors);

/// <summary>Parses share links without including their contents in exceptions or diagnostics.</summary>
public sealed class LinkParser
{
    private const int MaxInputLength = 2 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ShadowsocksMethods = new(StringComparer.Ordinal)
    {
        "aes-128-gcm", "aes-256-gcm", "chacha20-poly1305", "chacha20-ietf-poly1305",
        "xchacha20-poly1305", "xchacha20-ietf-poly1305", "2022-blake3-aes-128-gcm",
        "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305"
    };

    public ServerProfile Parse(string link)
    {
        if (string.IsNullOrWhiteSpace(link) || link.Length > 65536)
            throw Invalid(LocalizationService.Pick("Пустая или слишком длинная ссылка.", "The link is empty or too long."));
        try
        {
            link = link.Trim().Trim('\uFEFF');
            int schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) throw Invalid(LocalizationService.Pick("Ожидается ссылка vless://, vmess://, trojan:// или ss://.", "Expected a vless://, vmess://, trojan:// or ss:// link."));
            string scheme = link[..schemeEnd].ToLowerInvariant();
            ServerProfile profile = scheme switch
            {
                "vless" or "trojan" => ParseUri(link, scheme),
                "vmess" => ParseVmess(link),
                "ss" => ParseShadowsocks(link),
                _ => throw Invalid(LocalizationService.Pick("Протокол ссылки не поддерживается.", "Unsupported link protocol."))
            };
            Validate(profile);
            profile.SourceKey = CalculateSourceKey(profile);
            profile.HasCustomName = false;
            return profile;
        }
        catch (ShareLinkException) { throw; }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or OverflowException or InvalidOperationException or DecoderFallbackException)
        {
            throw Invalid(LocalizationService.Pick("Некорректная ссылка: проверьте адрес, порт и параметры подключения.", "Invalid link. Check the address, port and connection parameters."));
        }
    }

    public ImportResult ParseMany(string text)
    {
        List<ServerProfile> profiles = [];
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(text)) return new(profiles, [LocalizationService.Pick("Список ссылок пуст.", "The link list is empty.")]);
        if (text.Length > MaxInputLength) return new(profiles, [LocalizationService.Pick("Список превышает ограничение 2 МБ.", "The list exceeds 2 MB.")]);
        text = text.Trim().Trim('\uFEFF');
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            try { text = DecodeBase64(text); }
            catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
            {
                return new(profiles, [LocalizationService.Pick("Ожидается список ссылок или его представление Base64.", "Expected a plain or Base64 encoded link list.")]);
            }
        }
        HashSet<string> seen = new(StringComparer.Ordinal);
        // Also accept links separated by spaces, while retaining spaces in a final display name.
        string[] lines = Regex.Split(text, @"\r?\n|\r|\s+(?=(?:vless|vmess|trojan|ss)://)", RegexOptions.IgnoreCase);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            try
            {
                ServerProfile profile = Parse(line);
                if (seen.Add(profile.SourceKey)) profiles.Add(profile);
            }
            catch (ShareLinkException exception) { errors.Add(LocalizationService.Pick($"Строка {index + 1}: {exception.Message}", $"Line {index + 1}: {exception.Message}")); }
        }
        if (profiles.Count == 0 && errors.Count == 0) errors.Add(LocalizationService.Pick("Список не содержит серверов.", "No servers in the list."));
        return new(profiles, errors);
    }

    private static ServerProfile ParseUri(string link, string protocol)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri)) throw Invalid(LocalizationService.Pick("Некорректный адрес сервера.", "Invalid server address."));
        Dictionary<string, string> query = ParseQuery(uri.Query);
        string credential = Decode(uri.UserInfo);
        if (protocol is "vless" or "vmess" && credential.Contains(':', StringComparison.Ordinal))
        {
            // Some VMess URI exporters use cipher:UUID in the user-info component.
            if (protocol != "vmess") throw Invalid(LocalizationService.Pick("Некорректный идентификатор пользователя.", "Invalid user ID."));
            int colon = credential.IndexOf(':', StringComparison.Ordinal);
            query.TryAdd("encryption", credential[..colon]);
            credential = credential[(colon + 1)..];
        }
        string security = Get(query, "security", protocol == "trojan" ? "tls" : "none").ToLowerInvariant();
        if (Get(query, "tls") == "1") security = "tls";
        ServerProfile profile = new()
        {
            Protocol = protocol,
            Host = uri.IdnHost.Trim('[', ']'),
            Port = uri.Port,
            Credential = credential,
            Name = Decode(uri.Fragment.TrimStart('#')),
            Encryption = Get(query, "encryption", protocol == "vmess" ? "auto" : "none"),
            Flow = Get(query, "flow"),
            Network = NormalizeNetwork(Get(query, "type", Get(query, "network", "tcp"))),
            Security = security,
            ServerName = Get(query, "sni", Get(query, "peer", Get(query, "serverName"))),
            Fingerprint = Get(query, "fp", Get(query, "fingerprint")),
            PublicKey = Get(query, "pbk", Get(query, "publicKey")),
            ShortId = Get(query, "sid", Get(query, "shortId")),
            SpiderX = Get(query, "spx", Get(query, "spiderX")),
            Path = Get(query, "path", "/"),
            HostHeader = Get(query, "host"),
            ServiceName = Get(query, "serviceName"),
            Alpn = Get(query, "alpn"),
            HeaderType = Get(query, "headerType", "none").ToLowerInvariant(),
            TransportMode = Get(query, "mode"),
            Extra = Get(query, "extra"),
            AllowInsecure = IsTrue(Get(query, "allowInsecure", Get(query, "insecure")))
        };
        if (query.TryGetValue("aid", out string? aid)) profile.AlterId = ParseInteger(aid);
        if (!string.IsNullOrEmpty(Get(query, "plugin"))) throw Invalid(LocalizationService.Pick("Внешние плагины транспорта не поддерживаются.", "External transport plugins are unsupported."));
        return profile;
    }

    private static ServerProfile ParseVmess(string link)
    {
        string payload = link[8..];
        if (payload.Contains('@', StringComparison.Ordinal)) return ParseUri(link, "vmess");
        int fragmentIndex = payload.IndexOf('#');
        string fragment = fragmentIndex < 0 ? "" : Decode(payload[(fragmentIndex + 1)..]);
        if (fragmentIndex >= 0) payload = payload[..fragmentIndex];
        using JsonDocument document = JsonDocument.Parse(DecodeBase64(payload));
        JsonElement json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object) throw Invalid(LocalizationService.Pick("VMess должен содержать объект JSON.", "VMess must contain a JSON object."));
        string transport = NormalizeNetwork(Read(json, "net", "tcp"));
        return new ServerProfile
        {
            Protocol = "vmess",
            Name = Read(json, "ps", fragment),
            Host = Read(json, "add").Trim('[', ']'),
            Port = ParseInteger(Read(json, "port")),
            Credential = Read(json, "id"),
            AlterId = ParseInteger(Read(json, "aid", "0")),
            Encryption = Read(json, "scy", "auto"),
            Network = transport,
            Security = Read(json, "tls") is "tls" or "1" ? "tls" : "none",
            ServerName = Read(json, "sni"),
            Fingerprint = Read(json, "fp"),
            Path = Read(json, "path", "/"),
            HostHeader = Read(json, "host"),
            ServiceName = transport == "grpc" ? Read(json, "serviceName", Read(json, "path").TrimStart('/')) : "",
            Alpn = Read(json, "alpn"),
            HeaderType = Read(json, "type", "none").ToLowerInvariant(),
            TransportMode = Read(json, "mode"),
            AllowInsecure = IsTrue(Read(json, "allowInsecure"))
        };
    }

    private static ServerProfile ParseShadowsocks(string link)
    {
        string body = link[5..];
        int fragmentIndex = body.IndexOf('#');
        string name = fragmentIndex < 0 ? "" : Decode(body[(fragmentIndex + 1)..]);
        if (fragmentIndex >= 0) body = body[..fragmentIndex];
        int queryIndex = body.IndexOf('?');
        if (queryIndex >= 0)
        {
            Dictionary<string, string> query = ParseQuery(body[queryIndex..]);
            if (!string.IsNullOrWhiteSpace(Get(query, "plugin")))
                throw Invalid(LocalizationService.Pick("Shadowsocks с внешним SIP003-плагином не поддерживается.", "Shadowsocks SIP003 plugins are unsupported."));
            body = body[..queryIndex];
        }
        int separator = body.LastIndexOf('@');
        bool legacyEncoding = separator < 0;
        if (separator < 0)
        {
            body = DecodeBase64(body.TrimEnd('/'));
            separator = body.LastIndexOf('@');
        }
        if (separator <= 0) throw Invalid(LocalizationService.Pick("Некорректная ссылка Shadowsocks.", "Invalid Shadowsocks link."));
        // A legacy Base64 payload contains the literal password, not URI-escaped text.
        string authentication = legacyEncoding ? body[..separator] : Decode(body[..separator]);
        if (!authentication.Contains(':', StringComparison.Ordinal)) authentication = DecodeBase64(authentication);
        int colon = authentication.IndexOf(':');
        if (colon <= 0 || colon == authentication.Length - 1) throw Invalid(LocalizationService.Pick("В Shadowsocks не указан метод шифрования или пароль.", "Shadowsocks cipher or password is missing."));
        string endpoint = body[(separator + 1)..].TrimEnd('/');
        if (!Uri.TryCreate("ss://" + endpoint, UriKind.Absolute, out Uri? uri)) throw Invalid(LocalizationService.Pick("Некорректный адрес Shadowsocks.", "Invalid Shadowsocks address."));
        return new ServerProfile
        {
            Protocol = "shadowsocks",
            Name = name,
            Host = uri.IdnHost.Trim('[', ']'),
            Port = uri.Port,
            Method = authentication[..colon].ToLowerInvariant(),
            Credential = authentication[(colon + 1)..]
        };
    }

    public static void Validate(ServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Host) || Uri.CheckHostName(profile.Host) == UriHostNameType.Unknown)
            throw Invalid(LocalizationService.Pick("Не указан корректный адрес сервера.", "A valid server address is required."));
        if (profile.Port is < 1 or > 65535) throw Invalid(LocalizationService.Pick("Порт должен быть от 1 до 65535.", "Port must be between 1 and 65535."));
        if (string.IsNullOrEmpty(profile.Credential) || profile.Credential.Any(char.IsControl))
            throw Invalid(LocalizationService.Pick("Не указан корректный ключ или пароль.", "A valid key or password is required."));
        if (profile.Protocol is "vless" or "vmess" && !Guid.TryParse(profile.Credential, out _) && Encoding.UTF8.GetByteCount(profile.Credential) >= 30)
            throw Invalid(LocalizationService.Pick("Некорректный UUID пользователя.", "Invalid user UUID."));
        if (profile.Protocol is not ("vless" or "vmess" or "trojan" or "shadowsocks"))
            throw Invalid(LocalizationService.Pick("Протокол не поддерживается.", "Unsupported protocol."));
        profile.Network = NormalizeNetwork(profile.Network);
        if (profile.Security is not ("none" or "tls" or "reality")) throw Invalid(LocalizationService.Pick("Тип защиты транспорта не поддерживается.", "Unsupported transport security."));
        if (profile.AlterId is < 0 or > 65535) throw Invalid(LocalizationService.Pick("Некорректный VMess alterId.", "Invalid VMess alterId."));
        if (profile.Protocol == "shadowsocks" && !ShadowsocksMethods.Contains(profile.Method))
            throw Invalid(LocalizationService.Pick("Метод шифрования Shadowsocks не поддерживается ядром Xray.", "Shadowsocks cipher is not supported by Xray."));
        if (profile.Security == "reality")
        {
            if (profile.Network is not ("tcp" or "grpc" or "xhttp")) throw Invalid(LocalizationService.Pick("REALITY требует TCP, gRPC или XHTTP.", "REALITY requires TCP, gRPC or XHTTP."));
            if (string.IsNullOrWhiteSpace(profile.PublicKey)) throw Invalid(LocalizationService.Pick("В ссылке REALITY отсутствует публичный ключ.", "REALITY public key is missing."));
            if (!Regex.IsMatch(profile.PublicKey, @"^[A-Za-z0-9_-]{43}$")) throw Invalid(LocalizationService.Pick("Некорректный публичный ключ REALITY.", "Invalid REALITY public key."));
            if (profile.ShortId.Length > 16 || profile.ShortId.Length % 2 != 0 || !profile.ShortId.All(Uri.IsHexDigit))
                throw Invalid(LocalizationService.Pick("Некорректный shortId REALITY.", "Invalid REALITY shortId."));
            if (string.IsNullOrEmpty(profile.Fingerprint)) profile.Fingerprint = "chrome";
        }
        if (profile.Flow.Length != 0 && profile.Flow is not ("xtls-rprx-vision" or "xtls-rprx-vision-udp443"))
            throw Invalid(LocalizationService.Pick("Устаревший или неизвестный режим XTLS flow.", "Unsupported XTLS flow."));
        if (profile.Network == "tcp" && profile.HeaderType is not ("none" or "http" or ""))
            throw Invalid(LocalizationService.Pick("Тип заголовка TCP не поддерживается.", "Unsupported TCP header type."));
        if (profile.Network == "xhttp" && profile.TransportMode is not ("" or "auto" or "packet-up" or "stream-up" or "stream-one"))
            throw Invalid(LocalizationService.Pick("Некорректный режим XHTTP.", "Invalid XHTTP mode."));
        if (profile.Extra.Length != 0)
        {
            using JsonDocument document = JsonDocument.Parse(profile.Extra);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid(LocalizationService.Pick("Параметр extra должен быть объектом JSON.", "The extra parameter must be a JSON object."));
        }
        if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = $"{profile.Protocol.ToUpperInvariant()} · {profile.Host}";
        profile.Name = new string(profile.Name.Where(character => !char.IsControl(character)).Take(200).ToArray());
    }

    public static string CalculateSourceKey(ServerProfile profile)
    {
        string canonical = JsonSerializer.Serialize(new
        {
            profile.Protocol, profile.Host, profile.Port, profile.Credential, profile.Encryption,
            profile.Method, profile.Flow, profile.Network, profile.Security, profile.ServerName,
            profile.Fingerprint, profile.PublicKey, profile.ShortId, profile.SpiderX, profile.Path,
            profile.HostHeader, profile.ServiceName, profile.Alpn, profile.HeaderType,
            profile.TransportMode, profile.Extra, profile.AlterId, profile.AllowInsecure
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string DecodeBase64(string encoded)
    {
        string normalized = string.Concat(encoded.Where(character => !char.IsWhiteSpace(character))).Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return StrictUtf8.GetString(Convert.FromBase64String(normalized));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = segment.IndexOf('=');
            string key = Decode(equals < 0 ? segment : segment[..equals]);
            string value = equals < 0 ? "" : Decode(segment[(equals + 1)..]);
            result[key] = value;
        }
        return result;
    }

    private static string NormalizeNetwork(string value) => value.ToLowerInvariant() switch
    {
        "" or "tcp" or "raw" => "tcp",
        "ws" or "websocket" => "ws",
        "grpc" => "grpc",
        "httpupgrade" => "httpupgrade",
        "xhttp" or "splithttp" => "xhttp",
        _ => throw Invalid(LocalizationService.Pick("Транспорт не поддерживается. Доступны TCP, WebSocket, gRPC, HTTPUpgrade и XHTTP.", "Unsupported transport. Use TCP, WebSocket, gRPC, HTTPUpgrade or XHTTP."))
    };

    private static string Get(Dictionary<string, string> query, string key, string fallback = "") => query.TryGetValue(key, out string? value) && value.Length > 0 ? value : fallback;
    private static string Read(JsonElement json, string key, string fallback = "") => json.TryGetProperty(key, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : fallback;
    private static string Decode(string value) => Uri.UnescapeDataString(value);
    private static int ParseInteger(string value) => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    private static ShareLinkException Invalid(string message) => new(message);
}

public sealed class ShareLinkException(string message) : FormatException(message);
