using System;
using XrayMetro.Services;
namespace XrayMetro.Models;
public sealed class SubscriptionProfile : XrayMetro.ViewModels.ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = LocalizationService.Pick("Подписка", "Subscription");
    public string Url { get; set; } = "";
    private bool _preferJson = true;
    public bool PreferJson { get => _preferJson; set => Set(ref _preferJson, value); }
    public DateTimeOffset? LastAutoAttempt { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }
    public int FailedAttempts { get; set; }
    private bool _isUpdating;
    private string _status = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUpdating { get => _isUpdating; set => Set(ref _isUpdating, value); }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Status { get => _status.Length > 0 ? _status : FailedAttempts > 0 ? LocalizationService.Get("Error") : UpdatedAt is not null ? LocalizationService.Get("Refreshed") : ""; set => Set(ref _status, value); }
    private DateTimeOffset? _updatedAt;
    public DateTimeOffset? UpdatedAt { get => _updatedAt; set { if (Set(ref _updatedAt, value)) OnPropertyChanged(nameof(UpdatedText)); } }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UpdatedText => UpdatedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "";
}
