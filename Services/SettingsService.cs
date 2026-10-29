using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XrayMetro.Models;

namespace XrayMetro.Services;

public sealed class SettingsService
{
    public static string AppDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Win10ray", "Experimental");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly string _file;
    private readonly bool _migrateLegacy;
    private readonly string? _importDirectory;
    public string? LoadWarning { get; private set; }

    public SettingsService(string? storageDirectory = null, string? importDirectory = null)
    {
        _directory = Path.GetFullPath(storageDirectory ?? AppDirectory);
        _file = Path.Combine(_directory, "settings.json");
        _migrateLegacy = storageDirectory is null || importDirectory is not null;
        _importDirectory = importDirectory is null ? null : Path.GetFullPath(importDirectory);
    }

    public async Task<AppState> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            LoadWarning = null;
            await Task.Run(PrepareDirectory).ConfigureAwait(false);
            bool importedStable = false;
            if (_migrateLegacy && !File.Exists(_file))
            {
                string legacy = Path.Combine(_importDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Win10ray"), "settings.json");
                if (_importDirectory is null && !File.Exists(legacy)) legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XrayMetro", "settings.json");
                if (File.Exists(legacy))
                {
                    await WriteAtomicAsync(_file, await File.ReadAllBytesAsync(legacy).ConfigureAwait(false)).ConfigureAwait(false);
                    importedStable = true;
                }
            }
            if (!File.Exists(_file)) return new AppState();
            try
            {
                byte[] stored = await File.ReadAllBytesAsync(_file).ConfigureAwait(false);
                bool encrypted = ProfileProtection.IsProtected(stored);
                byte[] json = encrypted ? await Task.Run(() => ProfileProtection.Unprotect(stored)).ConfigureAwait(false) : stored;
                AppState state;
                try { state = JsonSerializer.Deserialize<AppState>(json, JsonOptions) ?? throw new JsonException(); }
                finally { if (encrypted) System.Security.Cryptography.CryptographicOperations.ZeroMemory(json); }
                state.Settings ??= new AppSettings();
                state.Servers ??= [];
                state.Subscriptions ??= [];
                state.Servers.RemoveAll(server => server is null);
                state.Subscriptions.RemoveAll(subscription => subscription is null);
                ValidateState(state);
                if (importedStable)
                {
                    state.Settings.ExperimentalUpdates = true;
                }
                string backup = _file + ".bak";
                string marker = backup + ".version";
                if (!encrypted)
                {
                    // Keep a DPAPI-protected copy of the exact legacy bytes for one client version.
                    byte[] protectedData = await Task.Run(() => ProfileProtection.Protect(stored)).ConfigureAwait(false);
                    await WriteAtomicAsync(backup, protectedData).ConfigureAwait(false);
                    await File.WriteAllTextAsync(marker, HardwareIdService.ClientVersion).ConfigureAwait(false);
                    await WriteAtomicAsync(_file, protectedData).ConfigureAwait(false);
                }
                else if (File.Exists(marker) && await File.ReadAllTextAsync(marker).ConfigureAwait(false) != HardwareIdService.ClientVersion)
                {
                    ProfileProtection.RemoveBackup(backup); File.Delete(marker);
                }
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(stored);
                if (importedStable)
                {
                    byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
                    try { await WriteAtomicAsync(_file, await Task.Run(() => ProfileProtection.Protect(snapshot)).ConfigureAwait(false)).ConfigureAwait(false); }
                    finally { Array.Clear(snapshot); }
                }
                return state;
            }
            catch (JsonException)
            {
                string backup = Path.Combine(_directory, $"settings.damaged-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
                byte[] damaged = await File.ReadAllBytesAsync(_file).ConfigureAwait(false);
                if (!ProfileProtection.IsProtected(damaged))
                    await WriteAtomicAsync(_file, await Task.Run(() => ProfileProtection.Protect(damaged)).ConfigureAwait(false)).ConfigureAwait(false);
                File.Move(_file, backup);
                LoadWarning = LocalizationService.Pick("Файл настроек повреждён. Его копия сохранена в папке приложения; загружены настройки по умолчанию.", "Settings were damaged. A backup was saved and defaults have been loaded.");
                return new AppState();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Нет доступа к папке настроек в AppData.", "Access to the settings folder was denied."));
        }
        catch (IOException)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Не удалось прочитать настройки в AppData.", "Could not read the settings."));
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppState state)
    {
        byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(PrepareDirectory).ConfigureAwait(false);
            byte[] encrypted = await Task.Run(() => ProfileProtection.Protect(snapshot)).ConfigureAwait(false);
            await WriteAtomicAsync(_file, encrypted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Нет доступа для сохранения настроек в AppData.", "Access to save settings was denied."));
        }
        catch (IOException)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Не удалось сохранить настройки. Проверьте свободное место и доступ к AppData.", "Could not save settings. Check free space and AppData permissions."));
        }
        finally
        {
            Array.Clear(snapshot);
            _gate.Release();
        }
    }

    public async Task ResetLockedProfileAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(_file)) File.Move(_file, _file + ".locked-" + Guid.NewGuid().ToString("N"));
        }
        finally { _gate.Release(); }
    }

    private static async Task WriteAtomicAsync(string path, byte[] data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(data).ConfigureAwait(false); await stream.FlushAsync().ConfigureAwait(false); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void PrepareDirectory()
    {
        DirectoryInfo directory = Directory.CreateDirectory(_directory);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException(LocalizationService.Pick("Не удалось определить текущего пользователя Windows.", "Could not identify the current Windows user."));
        DirectorySecurity permissions = new();
        permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        permissions.SetOwner(user);
        permissions.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(permissions);
    }

    private static void ValidateState(AppState state)
    {
        HashSet<Guid> serverIds = [];
        foreach (ServerProfile server in state.Servers)
        {
            if (server.Id == Guid.Empty || !serverIds.Add(server.Id)) throw new JsonException();
            // Explicit JSON null values bypass C# non-nullable annotations.
            if (typeof(ServerProfile).GetProperties().Any(property => property.CanWrite && property.PropertyType == typeof(string) && property.GetValue(server) is null))
                throw new JsonException();
            try { LinkParser.Validate(server); }
            catch (FormatException) { throw new JsonException(); }
            server.SourceKey = LinkParser.CalculateSourceKey(server);
        }
        HashSet<Guid> subscriptionIds = [];
        foreach (SubscriptionProfile subscription in state.Subscriptions)
        {
            if (subscription.Id == Guid.Empty || !subscriptionIds.Add(subscription.Id) ||
                !Uri.TryCreate(subscription.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new JsonException();
            if (string.IsNullOrWhiteSpace(subscription.Name)) subscription.Name = LocalizationService.Pick("Подписка", "Subscription");
        }
        if (state.Settings.LastServerId is Guid last && !serverIds.Contains(last)) state.Settings.LastServerId = null;
        if (state.Settings.AccentColor is null || !Regex.IsMatch(state.Settings.AccentColor, "^#[0-9a-fA-F]{6}$"))
            state.Settings.AccentColor = "#0078D7";
        if (!DnsEndpoint.IsValid(state.Settings.DnsOverHttpsUrl)) state.Settings.DnsOverHttpsUrl = DnsEndpoint.Default;
        if (state.Settings.SubscriptionUpdateHours is not (1 or 3 or 6 or 12 or 24)) state.Settings.SubscriptionUpdateHours = 12;
        state.Settings.ProcessRules ??= [];
        if (state.Settings.ProcessRules.Count > 32) throw new JsonException();
        foreach (ProcessRouteRule rule in state.Settings.ProcessRules)
            try { ProcessRouteRule.Validate(rule); } catch (ArgumentException) { throw new JsonException(); }
        state.Settings.DefaultRouteDirect = false;
        state.Settings.ProcessRules.RemoveAll(rule => rule.Action != ProcessRouteAction.Direct);
        state.Settings.UseSystemProxy = !state.Settings.UseTun;
    }
}
