using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrayMetro.Services;

public static class BlockList
{
    private static readonly Lazy<(int Type, string Value)[]> Domains = new(() =>
    {
        using Stream stream = typeof(BlockList).Assembly.GetManifestResourceStream("Win10ray.Routing.ads.json") ?? throw new InvalidDataException("Missing block list.");
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("domains").EnumerateArray().Select(item => (item.GetProperty("type").GetInt32(), item.GetProperty("value").GetString()!)).ToArray();
    });
    public static JsonObject SingBoxMatch()
    {
        JsonObject match = new();
        foreach (var group in Domains.Value.GroupBy(item => item.Type))
        {
            string field = group.Key switch { 0 => "domain_keyword", 1 => "domain_regex", 2 => "domain_suffix", 3 => "domain", _ => throw new InvalidDataException("Invalid block rule.") };
            match[field] = new JsonArray(group.Select(item => (JsonNode?)JsonValue.Create(item.Value)).ToArray());
        }
        return match;
    }
}
