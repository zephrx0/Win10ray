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
    private ProcessRouteAction _processAction;
    public ObservableCollection<ProcessRouteRule> ProcessRules { get; private set; } = [];
    public bool CanEditProcessRules => UseTun && CanEditConnection;
    public string[] DefaultRouteChoices { get; } = [LocalizationService.Get("AllProxy"), LocalizationService.Get("AllDirect")];
    public int DefaultRouteIndex { get => DefaultRouteDirect ? 1 : 0; set { if (!CanEditProcessRules || value is < 0 or > 1) return; _state.Settings.DefaultRouteDirect = value == 1; OnPropertyChanged(); OnPropertyChanged(nameof(DefaultRouteDirect)); Persist(); } }
    public bool DefaultRouteDirect => _state.Settings.DefaultRouteDirect;
    public ProcessRouteAction[] ProcessActions { get; } = Enum.GetValues<ProcessRouteAction>();
    public string ProcessInput { get => _processInput; set => Set(ref _processInput, value ?? ""); }
    public ProcessRouteAction ProcessAction { get => _processAction; set => Set(ref _processAction, value); }
    public RelayCommand AddProcessRuleCommand { get; private set; } = null!;
    public RelayCommand DeleteProcessRuleCommand { get; private set; } = null!;
    public RelayCommand BrowseProcessCommand { get; private set; } = null!;
    public AsyncCommand SelectRunningProcessCommand { get; private set; } = null!;
    private void InitializeProcessRouting()
    {
        ProcessRules = new(_state.Settings.ProcessRules);
        AddProcessRuleCommand = new(_ => AddProcessRule(), _ => CanEditProcessRules);
        DeleteProcessRuleCommand = new(p => { if (p is ProcessRouteRule rule) { ProcessRules.Remove(rule); SaveProcessRules(); } }, _ => CanEditProcessRules);
        BrowseProcessCommand = new(_ => { OpenFileDialog dialog = new() { Filter = "Windows executable|*.exe", CheckFileExists = true }; if (dialog.ShowDialog() == true) ProcessInput = dialog.FileName; }, _ => CanEditProcessRules);
        SelectRunningProcessCommand = new(async _ => { string? process = await Views.ProcessPickerWindow.PickAsync(); if (process is not null) ProcessInput = process; }, ReportError, _ => CanEditProcessRules);
    }
    private void AddProcessRule()
    {
        try
        {
            string value = ProcessInput.Trim();
            ProcessRouteRule rule = new(value, ProcessAction, Path.IsPathFullyQualified(value));
            ProcessRouteRule.Validate(rule);
            ProcessRouteRule? existing = ProcessRules.FirstOrDefault(r => r.IsPath == rule.IsPath && r.Value.Equals(rule.Value, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) ProcessRules[ProcessRules.IndexOf(existing)] = rule;
            else if (ProcessRules.Count < 32) ProcessRules.Add(rule);
            else { Message = LocalizationService.Get("ProcessLimit"); return; }
            ProcessInput = ""; SaveProcessRules();
        }
        catch (ArgumentException) { Message = LocalizationService.Get("InvalidProcessRule"); }
    }
    private void SaveProcessRules() { _state.Settings.ProcessRules = ProcessRules.ToList(); Persist(); }
}
