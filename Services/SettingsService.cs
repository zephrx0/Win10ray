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
    public static string AppDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Win10ray");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly string _file;
    private readonly bool _migrateLegacy;
    public string? LoadWarning { get; private set; }

    public SettingsService(string? storageDirectory = null)
    {
        _directory = Path.GetFullPath(storageDirectory ?? AppDirectory);
        _file = Path.Combine(_directory, "settings.json");
        _migrateLegacy = storageDirectory is null;
    }

    public async Task<AppState> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            LoadWarning = null;
            await Task.Run(PrepareDirectory).ConfigureAwait(false);
            if (_migrateLegacy && !File.Exists(_file))
            {
                string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XrayMetro", "settings.json");
                if (File.Exists(legacy))
                {
                    await using FileStream input = new(legacy, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
                    await using FileStream output = new(_file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true);
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }
            }
            if (!File.Exists(_file)) return new AppState();
            try
            {
                await using FileStream stream = new(_file, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
                AppState state = await JsonSerializer.DeserializeAsync<AppState>(stream, JsonOptions).ConfigureAwait(false)
                    ?? throw new JsonException();
                state.Settings ??= new AppSettings();
                state.Servers ??= [];
                state.Subscriptions ??= [];
                state.Servers.RemoveAll(server => server is null);
                state.Subscriptions.RemoveAll(subscription => subscription is null);
                ValidateState(state);
                return state;
            }
            catch (JsonException)
            {
                string backup = Path.Combine(_directory, $"settings.damaged-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
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
        string temporary = _file + ".tmp";
        try
        {
            await Task.Run(PrepareDirectory).ConfigureAwait(false);
            await File.WriteAllBytesAsync(temporary, snapshot).ConfigureAwait(false);
            File.Move(temporary, _file, true);
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
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _gate.Release();
        }
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
        state.Settings.UseSystemProxy = !state.Settings.UseTun;
    }
}
