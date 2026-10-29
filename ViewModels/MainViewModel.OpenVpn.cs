using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using XrayMetro.Models;
using XrayMetro.Services;

namespace XrayMetro.ViewModels;

public sealed partial class MainViewModel
{
    public AsyncCommand EditOpenVpnCredentialsCommand { get; private set; } = null!;
    public AsyncCommand ExportOpenVpnCommand { get; private set; } = null!;
    private async Task ExportOpenVpnAsync(ServerProfile? profile)
    {
        if (profile is null || profile.Protocol != "openvpn") return;
        if (System.Windows.MessageBox.Show(LocalizationService.Get("OpenVpnExportWarning"), "Win10ray", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.Cancel) != System.Windows.MessageBoxResult.OK) return;
        SaveFileDialog dialog = new() { Filter = "OpenVPN (*.ovpn)|*.ovpn", FileName = "Win10ray-OpenVPN.ovpn", DefaultExt = ".ovpn" };
        if (dialog.ShowDialog() == true) await System.IO.File.WriteAllTextAsync(dialog.FileName, profile.OpenVpnConfig, new System.Text.UTF8Encoding(false), _lifetime.Token);
    }
    private async Task EditOpenVpnCredentialsAsync(ServerProfile? profile)
    {
        if (profile is null || profile.Protocol != "openvpn") return;
        Views.OpenVpnCredentials credentials = Views.OpenVpnCredentialsWindow.Prompt(profile.OpenVpnUsername, _lifetime.Token, editing: true);
        profile.OpenVpnUsername = credentials.Username;
        profile.OpenVpnPassword = credentials.Remember ? credentials.Password : "";
        _openVpnSessionCredentials[profile.SourceKey] = (credentials.Username, credentials.Password);
        await SaveAsync();
    }
    private async Task ImportOpenVpnFilesAsync(string[]? files)
    {
        if (files is null)
        {
            OpenFileDialog dialog = new() { Filter = "OpenVPN (*.ovpn)|*.ovpn", Multiselect = true, CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            files = dialog.FileNames;
        }
        if (files.Length > 32) throw new InvalidOperationException(LocalizationService.Get("OpenVpnFileLimit"));
        int added = 0, rejected = 0;
        foreach (string file in files)
        {
            try
            {
                ServerProfile profile = await OpenVpnProfileParser.ImportFileAsync(file, _lifetime.Token);
                if (!CanEditConnection) return;
                if (Servers.Any(existing => existing.SourceKey == profile.SourceKey)) continue;
                Servers.Add(profile); added++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or FormatException)
            { rejected++; ReportError(exception); }
        }
        RefreshServers(); await SaveAsync();
        if (rejected == 0) Message = LocalizationService.Pick($"Добавлено: {added}", $"Added: {added}");
    }
}
