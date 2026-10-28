using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace XrayMetro.Services;

public sealed class AutoStartService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Win10ray";

    public void MigrateRegistration()
    {
        bool legacy;
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, false)) legacy = key?.GetValue("XrayMetro") is string;
        if (legacy || IsEnabled()) SetEnabled(true);
    }

    public bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
            return (key?.GetValue(ValueName) ?? key?.GetValue("XrayMetro")) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Нет доступа к настройкам автозапуска Windows.", "Access to Windows startup settings was denied."));
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, true)
                ?? throw new UnauthorizedAccessException();
            if (enabled)
            {
                string executable = Environment.ProcessPath ?? throw new InvalidOperationException(LocalizationService.Pick("Не удалось определить путь приложения для автозапуска.", "Could not locate the application for Windows startup."));
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(LocalizationService.Pick("Для автозапуска запустите Win10ray.exe напрямую.", "Open Win10ray.exe directly to configure Windows startup."));
                key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
            }
            else key.DeleteValue(ValueName, false);
            key.DeleteValue("XrayMetro", false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            throw new InvalidOperationException(LocalizationService.Pick("Windows запретила изменение ключа автозапуска. Проверьте права доступа.", "Windows denied access to the startup registry key."));
        }
    }
}
