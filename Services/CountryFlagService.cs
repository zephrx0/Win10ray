using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XrayMetro.Services;

public static class CountryFlagService
{
    private static readonly ConcurrentDictionary<string, Lazy<ImageSource?>> Images = new();
    private static readonly Regex CodePrefix = new(@"^\s*\[([A-Za-z]{2})\]\s*", RegexOptions.CultureInvariant);
    public static string CountryCode(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        for (int i = 0; i < name.Length;)
        {
            if (!Rune.TryGetRuneAt(name, i, out Rune first)) { i++; continue; }
            int next = i + first.Utf16SequenceLength;
            if (first.Value is >= 0x1F1E6 and <= 0x1F1FF && next < name.Length && Rune.TryGetRuneAt(name, next, out Rune second) && second.Value is >= 0x1F1E6 and <= 0x1F1FF)
                return Normalize(new string([(char)('a' + first.Value - 0x1F1E6), (char)('a' + second.Value - 0x1F1E6)]));
            i = next;
        }
        Match prefix = CodePrefix.Match(name);
        return prefix.Success ? Normalize(prefix.Groups[1].Value) : "";
    }

    public static string DisplayName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var result = new StringBuilder();
        for (int i = 0; i < name.Length;)
        {
            if (!Rune.TryGetRuneAt(name, i, out Rune first)) { i++; continue; }
            int next = i + first.Utf16SequenceLength;
            if (first.Value is >= 0x1F1E6 and <= 0x1F1FF && next < name.Length && Rune.TryGetRuneAt(name, next, out Rune second) && second.Value is >= 0x1F1E6 and <= 0x1F1FF)
            { i = next + second.Utf16SequenceLength; continue; }
            result.Append(first); i = next;
        }
        return CodePrefix.Replace(result.ToString(), "").Trim();
    }

    private static string Normalize(string code) => code.Equals("uk", StringComparison.OrdinalIgnoreCase) ? "gb" : code.ToLowerInvariant();
    public static ImageSource? Image(string code)
    {
        code = Normalize(code);
        if (!Regex.IsMatch(code, "^[a-z]{2}$")) return null;
        return Images.GetOrAdd(code, key => new Lazy<ImageSource?>(() => Load(key))).Value;
    }
    private static ImageSource? Load(string code)
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri($"/Win10ray;component/Assets/Flags/{code}.png", UriKind.Relative));
            if (resource is null) return null;
            using var stream = resource.Stream;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            return bitmap;
        }
        catch (System.IO.IOException) { return null; }
    }
}
