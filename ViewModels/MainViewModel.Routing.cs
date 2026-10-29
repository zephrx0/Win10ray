using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using XrayMetro.Models;
using XrayMetro.Services;

namespace XrayMetro.ViewModels;

public sealed partial class MainViewModel
{
    private string _processInput = "";
    private bool _hasPendingProcessRules;
    public ObservableCollection<ProcessRouteRule> ProcessRules { get; private set; } = [];
    public bool CanEditProcessRules => UseTun && !IsBusy && !IsRefreshingSubscriptions && !_closing;
    public bool HasPendingProcessRules { get => _hasPendingProcessRules; private set { if (Set(ref _hasPendingProcessRules, value)) ApplyProcessRulesCommand?.Notify(); } }
    public string ProcessInput { get => _processInput; set => Set(ref _processInput, value ?? ""); }
    public RelayCommand AddProcessRuleCommand { get; private set; } = null!;
    public RelayCommand DeleteProcessRuleCommand { get; private set; } = null!;
    public RelayCommand BrowseProcessCommand { get; private set; } = null!;
    public AsyncCommand SelectRunningProcessCommand { get; private set; } = null!;
    public AsyncCommand ApplyProcessRulesCommand { get; private set; } = null!;
    private void InitializeProcessRouting()
    {
        _state.Settings.DefaultRouteDirect = false;
        _state.Settings.ProcessRules.RemoveAll(rule => rule.Action != ProcessRouteAction.Direct);
        ProcessRules = new(_state.Settings.ProcessRules);
        AddProcessRuleCommand = new(_ => AddProcessRule(), _ => CanEditProcessRules);
        DeleteProcessRuleCommand = new(p => { if (p is ProcessRouteRule rule) { ProcessRules.Remove(rule); SaveProcessRules(); } }, _ => CanEditProcessRules);
        BrowseProcessCommand = new(_ => { OpenFileDialog dialog = new() { Filter = "Windows executable|*.exe", CheckFileExists = true }; if (dialog.ShowDialog() == true && CanEditProcessRules) { ProcessInput = dialog.FileName; AddProcessRule(); } }, _ => CanEditProcessRules);
        SelectRunningProcessCommand = new(async _ => { string? process = await Views.ProcessPickerWindow.PickAsync(); if (process is not null && CanEditProcessRules) { ProcessInput = process; AddProcessRule(); } }, ReportError, _ => CanEditProcessRules);
        ApplyProcessRulesCommand = new(_ => ApplyProcessRulesAsync(), ReportError, _ => CanEditProcessRules && IsConnected && HasPendingProcessRules);
    }
    private void AddProcessRule()
    {
        try
        {
            string value = ProcessInput.Trim();
            ProcessRouteRule rule = new(value, ProcessRouteAction.Direct, Path.IsPathFullyQualified(value));
            ProcessRouteRule.Validate(rule);
            ProcessRouteRule? existing = ProcessRules.FirstOrDefault(r => r.IsPath == rule.IsPath && r.Value.Equals(rule.Value, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) ProcessRules[ProcessRules.IndexOf(existing)] = rule;
            else if (ProcessRules.Count < 32) ProcessRules.Add(rule);
            else { Message = LocalizationService.Get("ProcessLimit"); return; }
            ProcessInput = ""; SaveProcessRules();
        }
        catch (ArgumentException) { Message = LocalizationService.Get("InvalidProcessRule"); }
    }
    private void SaveProcessRules() { _state.Settings.ProcessRules = ProcessRules.ToList(); HasPendingProcessRules = IsConnected; Persist(); }
    private async Task ApplyProcessRulesAsync()
    {
        await _connectionGate.WaitAsync(_lifetime.Token);
        try
        {
            if (_closing || !IsConnected || SelectedServer is null || !UseTun) return;
            var server = SelectedServer;
            _connectCancellation = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            IsBusy = true;
            _trafficCarry = _sessionBytes;
            await DisconnectCoreAsync();
            await ConnectAvailableAsync(server, false, _connectCancellation.Token);
        }
        catch (System.OperationCanceledException)
        {
            await DisconnectAfterFailureAsync(); throw;
        }
        catch
        {
            await DisconnectAfterFailureAsync(); StatusText = LocalizationService.Get("Error"); throw;
        }
        finally
        {
            _connectCancellation?.Dispose(); _connectCancellation = null;
            IsBusy = false; _connectionGate.Release();
        }
    }
}
