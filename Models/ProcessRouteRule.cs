using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace XrayMetro.Models;

public enum ProcessRouteAction { Direct, Proxy, Block }

public sealed record ProcessRouteRule(string Value, ProcessRouteAction Action, bool IsPath = false)
{
    [JsonIgnore] public string ActionText => Action.ToString();
    public static void Validate(ProcessRouteRule rule)
    {
        if (rule is null || string.IsNullOrWhiteSpace(rule.Value) || rule.Value.Length > 1024 || !Enum.IsDefined(rule.Action) ||
            rule.Value.Any(char.IsControl) || rule.Value.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0 || !rule.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid process rule.");
        if (rule.IsPath ? !Path.IsPathFullyQualified(rule.Value) || rule.Value.StartsWith(@"\\", StringComparison.Ordinal) : Path.GetFileName(rule.Value) != rule.Value || rule.Value.Contains(':'))
            throw new ArgumentException("Invalid process rule path.");
    }
}
