using System;
using XrayMetro.Services;
namespace XrayMetro.Models;
public sealed class SubscriptionProfile : XrayMetro.ViewModels.ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = LocalizationService.Pick("Подписка", "Subscription");
    public string Url { get; set; } = "";
    private DateTimeOffset? _updatedAt;
    public DateTimeOffset? UpdatedAt { get => _updatedAt; set { if (Set(ref _updatedAt, value)) OnPropertyChanged(nameof(UpdatedText)); } }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UpdatedText => UpdatedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "";
}
