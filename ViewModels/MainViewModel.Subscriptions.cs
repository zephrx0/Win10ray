using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using XrayMetro.Models;
using XrayMetro.Services;

namespace XrayMetro.ViewModels;

public sealed partial class MainViewModel
{
    private DispatcherTimer? _subscriptionTimer;
    private Task? _subscriptionTask;
    private bool _isRefreshingSubscriptions;
    public AsyncCommand UpdateSubscriptionCommand { get; }
    public bool IsRefreshingSubscriptions { get => _isRefreshingSubscriptions; private set { if (Set(ref _isRefreshingSubscriptions, value)) NotifyCommands(); } }
    public bool AutoUpdateSubscriptions { get => _state.Settings.AutoUpdateSubscriptions; set { _state.Settings.AutoUpdateSubscriptions = value; OnPropertyChanged(); Persist(); } }
    public int[] SubscriptionIntervals { get; } = [1, 3, 6, 12, 24];
    public int SubscriptionUpdateHours { get => _state.Settings.SubscriptionUpdateHours; set { if (SubscriptionIntervals.Contains(value)) { _state.Settings.SubscriptionUpdateHours = value; OnPropertyChanged(); Persist(); } } }
    public bool SubscriptionsThroughConnection { get => _state.Settings.SubscriptionsThroughConnection; set { _state.Settings.SubscriptionsThroughConnection = value; OnPropertyChanged(); Persist(); } }

    private void InitializeSubscriptionScheduler()
    {
        _subscriptionTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => ScheduleSubscriptionUpdate(), Application.Current.Dispatcher);
        _subscriptionTimer.Start();
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailable;
    }
    private void NetworkAvailable(object? sender, NetworkAvailabilityEventArgs args)
    { if (args.IsAvailable && !_closing) Application.Current.Dispatcher.BeginInvoke(new Action(() => ScheduleSubscriptionUpdate(true))); }
    private void ScheduleSubscriptionUpdate(bool networkOrStartup = false)
    { if (AutoUpdateSubscriptions && !_closing && NetworkInterface.GetIsNetworkAvailable()) _ = RefreshSubscriptionsAsync(null, true, networkOrStartup); }
    private void StopSubscriptionScheduler()
    { _subscriptionTimer?.Stop(); NetworkChange.NetworkAvailabilityChanged -= NetworkAvailable; }

    private Task RefreshSubscriptionsAsync(SubscriptionProfile? single, bool automatic, bool networkOrStartup = false)
    {
        if (_closing || IsRefreshingSubscriptions || IsBusy) return Task.CompletedTask;
        _subscriptionTask = RefreshSubscriptionsCoreAsync(single, automatic, networkOrStartup);
        return _subscriptionTask;
    }
    private async Task RefreshSubscriptionsCoreAsync(SubscriptionProfile? single, bool automatic, bool networkOrStartup)
    {
        IsRefreshingSubscriptions = true;
        int updated = 0, failed = 0;
        try
        {
            foreach (SubscriptionProfile subscription in (single is null ? Subscriptions.ToArray() : [single]))
            {
                if (automatic && !SubscriptionService.IsDue(subscription, DateTimeOffset.UtcNow, SubscriptionUpdateHours, networkOrStartup)) continue;
                if (!Subscriptions.Contains(subscription)) continue;
                subscription.IsUpdating = true; subscription.Status = LocalizationService.Get("Refreshing");
                if (automatic) subscription.LastAutoAttempt = DateTimeOffset.UtcNow;
                try
                {
                    ImportResult result = await _subscriptions.UpdateAsync(subscription, _lifetime.Token, SendHwid, SubscriptionsThroughConnection && IsConnected);
                    if (!Subscriptions.Contains(subscription)) continue;
                    if (result.Profiles.Count == 0) throw new InvalidOperationException();
                    ServerProfile? active = IsConnected ? SelectedServer : null;
                    ApplySubscription(subscription, result.Profiles);
                    subscription.FailedAttempts = 0; subscription.RetryAfter = null;
                    subscription.Status = LocalizationService.Get("Refreshed"); updated++;
                    if (active?.SubscriptionId == subscription.Id && !Servers.Contains(active)) await SwitchRemovedServerAsync(active, subscription);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch
                {
                    subscription.FailedAttempts = Math.Min(20, subscription.FailedAttempts + 1);
                    subscription.RetryAfter = DateTimeOffset.UtcNow + SubscriptionService.RetryDelay(subscription.FailedAttempts);
                    subscription.Status = LocalizationService.Get("Error"); failed++;
                }
                finally { subscription.IsUpdating = false; }
            }
            RefreshServers();
            if (!_closing) await SaveAsync();
            if (!automatic) Message = LocalizationService.Pick($"Обновлено: {updated}. Ошибок: {failed}.", $"Updated: {updated}. Failed: {failed}.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { if (!automatic) Message = LocalizationService.Get("OperationFailed"); }
        finally { IsRefreshingSubscriptions = false; }
    }

    private async Task SwitchRemovedServerAsync(ServerProfile active, SubscriptionProfile subscription)
    {
        await _connectionGate.WaitAsync(_lifetime.Token);
        try
        {
            if (!IsConnected || SelectedServer?.Id != active.Id) return;
            IsBusy = true;
            await DisconnectCoreAsync();
            SelectedServer = Servers.FirstOrDefault(s => s.SubscriptionId == subscription.Id) ?? Servers.FirstOrDefault();
            if (AutoSwitchServers && SelectedServer is not null)
                await ConnectAvailableAsync(SelectedServer, false, _lifetime.Token, interactive: false);
            else { _state.Settings.ResumeConnection = false; Message = LocalizationService.Get("ActiveServerRemoved"); }
        }
        catch (Exception exception) { await DisconnectAfterFailureAsync(); StatusText = LocalizationService.Get("Error"); EnqueueLog(LocalizationService.Get("ActiveServerRemoved")); if (exception is OperationCanceledException) throw; }
        finally { IsBusy = false; _connectionGate.Release(); }
    }
}
