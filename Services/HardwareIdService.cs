using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace XrayMetro.Services;

public static class HardwareIdService
{
    public const string ClientName = "Win10ray";
    private static readonly Lazy<Task<string>> Identifier = new(() => Task.Run(CreateIdentifier));
    public static Task<string> GetIdAsync() => Identifier.Value;
    public static string ClientVersion => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(HardwareIdService).Assembly)?.InformationalVersion.Split('+')[0] ?? typeof(HardwareIdService).Assembly.GetName().Version!.ToString(3);
    public static string UserAgent => ClientName + "/" + ClientVersion;
    public static string DeviceDescription => ClientName + " (Windows PC)";

    private static string CreateIdentifier()
    {
        string? machineGuid = null;
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? cryptography = machine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machineGuid = cryptography?.GetValue("MachineGuid") as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        string source = string.IsNullOrWhiteSpace(machineGuid)
            ? $"{Environment.MachineName}|{Environment.UserDomainName}|{Environment.UserName}"
            : machineGuid.Trim().ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Win10ray:HWID:v1|" + source))).ToLowerInvariant();
    }
}
