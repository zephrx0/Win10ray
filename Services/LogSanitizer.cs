using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XrayMetro.Services;

/// <summary>Removes credentials before process output can reach UI or a log buffer.</summary>
public sealed class LogSanitizer
{
    private static readonly Regex ShareLink = new(@"\b(?:vless|vmess|trojan|ss)://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Uuid = new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex JsonSecret = new(@"(""(?:password|id|privateKey|publicKey|shortId|token|authorization)""\s*:\s*"")(?:\\.|[^""\\])*("")", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private readonly string[] _secrets;

    public LogSanitizer(IEnumerable<string> secrets)
    {
        _secrets = secrets.Where(s => !string.IsNullOrEmpty(s))
            .SelectMany(s => new[] { s, Uri.EscapeDataString(s), JsonSerializer.Serialize(s)[1..^1] })
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
    }

    public string Sanitize(string text)
    {
        // Sanitize before truncating: a secret spanning the truncation boundary must not leak.
        try
        {
            foreach (string secret in _secrets)
                text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
            text = ShareLink.Replace(text, "[redacted]");
            text = Uuid.Replace(text, "[redacted]");
            text = JsonSecret.Replace(text, "$1[redacted]$2");
            text = text.Replace('\0', ' ');
            return text.Length <= 2048 ? text : text[..2048] + "…";
        }
        catch (RegexMatchTimeoutException)
        {
            return LocalizationService.Pick("Строка скрыта.", "Line redacted.");
        }
    }
}
