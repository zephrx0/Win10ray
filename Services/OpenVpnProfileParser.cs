using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XrayMetro.Models;

namespace XrayMetro.Services;

public static class OpenVpnProfileParser
{
    public const int MaximumBytes = 1024 * 1024;
    private static readonly HashSet<string> Materials = new(StringComparer.Ordinal) { "ca", "cert", "key", "tls-auth", "tls-crypt", "tls-crypt-v2", "auth-user-pass" };
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "client", "tls-client", "dev", "dev-type", "proto", "port", "rport", "remote", "remote-random", "remote-random-hostname",
        "auth-user-pass", "auth-nocache", "ca", "cert", "key", "tls-auth", "tls-crypt", "tls-crypt-v2", "key-direction",
        "cipher", "data-ciphers", "data-ciphers-fallback", "ncp-ciphers", "auth", "tls-version-min", "tls-version-max", "tls-cipher", "tls-groups",
        "verify-x509-name", "remote-cert-tls", "remote-cert-ku", "remote-cert-eku", "peer-fingerprint", "ns-cert-type",
        "nobind", "persist-key", "persist-tun", "resolv-retry", "connect-retry", "connect-retry-max", "connect-timeout", "server-poll-timeout",
        "verb", "mute", "mute-replay-warnings", "explicit-exit-notify", "pull", "pull-filter", "redirect-gateway", "route", "route-ipv6",
        "route-nopull", "route-delay", "route-method", "route-metric", "route-gateway", "dhcp-option", "block-outside-dns", "block-ipv6",
        "tun-mtu", "mssfix", "fragment", "sndbuf", "rcvbuf", "socket-flags", "fast-io", "keepalive", "ping", "ping-restart", "reneg-sec",
        "reneg-bytes", "reneg-pkts", "tls-timeout", "hand-window", "replay-window", "compress", "comp-lzo", "allow-compression", "setenv", "setenv-safe"
    };

    public static bool LooksLikeProfile(string text) => Regex.IsMatch(text, @"(?im)^\s*(?:client|tls-client|remote\s|<ca>)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static ServerProfile Parse(string text, string name = "OpenVPN")
    {
        OpenVpnOptions options = Read(text);
        OpenVpnRemote first = options.Remotes[0];
        ServerProfile profile = new() { Protocol = "openvpn", Host = first.Host, Port = first.Port, Network = first.Network,
            Name = name, OpenVpnConfig = text.Trim().TrimStart('\uFEFF'), HasCustomName = false };
        profile.SourceKey = LinkParser.CalculateSourceKey(profile);
        return profile;
    }

    public static OpenVpnOptions Read(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw Invalid("Недопустимый размер профиля OpenVPN.", "Invalid OpenVPN profile size.");
        OpenVpnOptions result = new();
        List<string[]> directives = [];
        string[] lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line.StartsWith('<'))
            {
                string block = line.Trim('<', '>').ToLowerInvariant();
                if (!Materials.Contains(block) || line != "<" + block + ">" || result.Inline.ContainsKey(block)) throw Invalid("Неподдерживаемый блок в профиле OpenVPN.", "Unsupported OpenVPN inline block.");
                StringBuilder content = new(); bool closed = false;
                while (++index < lines.Length)
                {
                    if (lines[index].Trim() == "</" + block + ">") { closed = true; break; }
                    content.AppendLine(lines[index]);
                }
                if (!closed || content.Length == 0) throw Invalid("Незакрытый или пустой блок OpenVPN.", "Unclosed or empty OpenVPN inline block.");
                result.Inline.Add(block, block == "auth-user-pass" ? content.ToString().TrimEnd('\r', '\n') : content.ToString().Trim()); continue;
            }
            string[] parts = Tokenize(line);
            if (parts.Length == 0) continue;
            parts[0] = parts[0].TrimStart('-').ToLowerInvariant();
            if (!Supported.Contains(parts[0])) throw new ShareLinkException(LocalizationService.Pick("Неподдерживаемая директива OpenVPN: ", "Unsupported OpenVPN directive: ") + SafeOption(parts[0]) + ".");
            directives.Add(parts);
        }
        string protocol = directives.LastOrDefault(p => p[0] == "proto")?.ElementAtOrDefault(1) ?? "udp";
        int defaultPort = Number(directives.LastOrDefault(p => p[0] is "port" or "rport")?.ElementAtOrDefault(1) ?? "1194", 1, 65535);
        foreach (string[] p in directives)
        {
            string option = p[0];
            if (option == "remote")
            {
                if (p.Length is < 2 or > 4 || Uri.CheckHostName(p[1].Trim('[', ']')) == UriHostNameType.Unknown) throw Invalid("Некорректный адрес OpenVPN.", "Invalid OpenVPN remote.");
                result.Remotes.Add(new(p[1].Trim('[', ']'), p.Length > 2 ? Number(p[2], 1, 65535) : defaultPort, Network(p.Length > 3 ? p[3] : protocol)));
                continue;
            }
            if (Materials.Contains(option))
            {
                if (option == "auth-user-pass") result.RequiresCredentials = true;
                if (p.Length > 1 && p[1] != "[inline]") throw Invalid("Внешние файлы профиля нужно импортировать вместе с файлом .ovpn.", "External profile files must be imported with the .ovpn file.");
                if (option == "tls-auth" && p.Length > 2) result.Values["key-direction"] = p[2];
                continue;
            }
            if (option is "dev" or "dev-type" && (p.Length != 2 || !p[1].StartsWith("tun", StringComparison.Ordinal))) throw Invalid("Поддерживается OpenVPN с интерфейсом TUN. TAP не поддерживается.", "OpenVPN TUN profiles are supported; TAP is not supported.");
            if (option is "setenv" or "setenv-safe")
            {
                if (p.Length < 2 || p[1] is not ("CLIENT_CERT" or "UV_IPV6" or "IV_GUI_VER" or "IV_SSO")) throw Invalid("Профиль содержит неподдерживаемую переменную OpenVPN.", "The profile contains an unsupported OpenVPN environment variable.");
                continue;
            }
            // Only inert client/runtime hints are ignored. Executable directives never reach a core.
            if (option is "client" or "tls-client" or "dev" or "dev-type" or "proto" or "port" or "rport" or "nobind" or "persist-key" or "persist-tun" or "resolv-retry" or "verb" or "mute" or "mute-replay-warnings" or "auth-nocache" or "pull" or "connect-retry" or "connect-retry-max" or "connect-timeout" or "server-poll-timeout" or "sndbuf" or "rcvbuf" or "socket-flags" or "fast-io" or "redirect-gateway" or "route" or "route-ipv6" or "route-delay" or "route-method" or "route-metric" or "route-gateway" or "dhcp-option" or "block-outside-dns") continue;
            if (result.Values.ContainsKey(option)) throw Invalid("Повторяющаяся директива OpenVPN.", "Repeated OpenVPN directive.");
            result.Values[option] = string.Join("\t", p.Skip(1));
        }
        if (result.Remotes.Count is < 1 or > 32) throw Invalid("В профиле нужен хотя бы один сервер OpenVPN (не более 32).", "An OpenVPN profile requires 1 to 32 remotes.");
        if (result.Inline.TryGetValue("auth-user-pass", out string? auth))
        {
            string[] credentials = auth.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            if (credentials.Length != 2) throw Invalid("Некорректные учётные данные OpenVPN.", "Invalid OpenVPN credentials.");
            result.Username = credentials[0]; result.Password = credentials[1]; result.RequiresCredentials = true;
        }
        if (!result.Inline.ContainsKey("ca") && !result.Values.ContainsKey("peer-fingerprint")) throw Invalid("В профиле OpenVPN нет CA-сертификата или SHA-256 отпечатка сервера.", "OpenVPN requires a CA certificate or server SHA-256 fingerprint.");
        if (result.Inline.ContainsKey("cert") != result.Inline.ContainsKey("key")) throw Invalid("Нужны оба блока: cert и key.", "Both cert and key blocks are required.");
        if (result.Inline.TryGetValue("key", out string? key) && (key.Contains("ENCRYPTED", StringComparison.Ordinal) || key.Contains("Proc-Type:", StringComparison.Ordinal))) throw Invalid("Зашифрованный ключ сначала экспортируйте в незашифрованный PEM. Профиль Win10ray хранится с защитой DPAPI.", "Export an encrypted key as unencrypted PEM first. Win10ray stores the profile with DPAPI protection.");
        return result;
    }

    public static async Task<ServerProfile> ImportFileAsync(string path, CancellationToken token)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full) || new FileInfo(full).Length > MaximumBytes) throw Invalid("Файл OpenVPN не найден или превышает 1 МБ.", "OpenVPN file is missing or exceeds 1 MB.");
        string text = await File.ReadAllTextAsync(full, token).ConfigureAwait(false);
        StringBuilder inline = new(); string? block = null;
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = line.Trim();
            if (block is not null) { inline.AppendLine(line); if (trimmed == "</" + block + ">") block = null; continue; }
            if (trimmed.StartsWith('<')) { block = trimmed.Trim('<', '>'); inline.AppendLine(line); continue; }
            string[] parts = Tokenize(trimmed);
            if (parts.Length > 1 && Materials.Contains(parts[0]) && parts[1] != "[inline]")
            {
                // Imported files may reference PEM files only inside the selected profile directory.
                string root = Path.GetDirectoryName(full)!;
                string file = Path.GetFullPath(Path.Combine(root, parts[1]));
                if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(parts[1]) || !File.Exists(file) || new FileInfo(file).Length > MaximumBytes || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw Invalid("Файл ключа или сертификата должен находиться рядом с профилем или в его подпапке.", "Key and certificate files must be inside the profile folder.");
                for (string? parent = Path.GetDirectoryName(file); parent is not null && !parent.Equals(root, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
                    if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw Invalid("Ссылки на внешние каталоги ключей не поддерживаются.", "Linked external key directories are not supported.");
                inline.AppendLine("<" + parts[0] + ">"); inline.AppendLine(await File.ReadAllTextAsync(file, token).ConfigureAwait(false)); inline.AppendLine("</" + parts[0] + ">");
                if (parts[0] == "tls-auth" && parts.Length > 2) inline.AppendLine("key-direction " + parts[2]);
            }
            else inline.AppendLine(line);
            if (inline.Length > MaximumBytes) throw Invalid("Профиль OpenVPN слишком большой.", "OpenVPN profile is too large.");
        }
        return Parse(inline.ToString(), Path.GetFileNameWithoutExtension(full));
    }

    internal static string[] Tokenize(string line)
    {
        List<string> tokens = []; StringBuilder word = new(); char quote = '\0';
        for (int index = 0; index < line.Length; index++)
        {
            char c = line[index];
            if (c == '\\')
            {
                if (index + 1 < line.Length && (line[index + 1] is '\\' or '\'' or '"' || char.IsWhiteSpace(line[index + 1]))) word.Append(line[++index]);
                else word.Append(c);
                continue;
            }
            if (quote != '\0') { if (c == quote) quote = '\0'; else word.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if ((c is '#' or ';') && word.Length == 0) break;
            if (char.IsWhiteSpace(c)) { if (word.Length > 0) { tokens.Add(word.ToString()); word.Clear(); } }
            else word.Append(c);
        }
        if (quote != '\0') throw Invalid("Некорректные кавычки в профиле OpenVPN.", "Invalid quoting in OpenVPN profile.");
        if (word.Length > 0) tokens.Add(word.ToString());
        return tokens.ToArray();
    }
    private static string SafeOption(string text) => Regex.IsMatch(text, "^[a-z][a-z0-9-]{0,40}$") ? text : "?";
    internal static string Network(string value) => value.ToLowerInvariant() switch { "udp" or "udp4" or "udp6" => value.ToLowerInvariant(), "tcp" or "tcp-client" => "tcp", "tcp4" or "tcp4-client" => "tcp4", "tcp6" or "tcp6-client" => "tcp6", _ => throw Invalid("Неподдерживаемый транспорт OpenVPN.", "Unsupported OpenVPN transport.") };
    internal static int Number(string text, int min, int max) => int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) && value >= min && value <= max ? value : throw Invalid("Некорректное число в профиле OpenVPN.", "Invalid numeric OpenVPN option.");
    private static ShareLinkException Invalid(string ru, string en) => new(LocalizationService.Pick(ru, en));
}
