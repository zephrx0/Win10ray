using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using XrayMetro.Services;

namespace Win10ray.TunService;

internal static class Installer
{
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Win10ray", "TunService");
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Win10ray", "TunService");
    public static async Task InstallAsync(SecurityIdentifier user)
    {
        if (!user.IsAccountSid()) throw new InvalidDataException();
        string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tun"));
        await TunComponentIntegrity.VerifyAsync(source);
        bool existing = await ScAsync("query", TunProtocol.ServiceName) == 0;
        if (existing) await StopAsync();
        SecureDirectory(Path.GetDirectoryName(InstallDirectory)!, true);
        SecureDirectory(InstallDirectory, true);
        File.Delete(Path.Combine(InstallDirectory, "installed.v1"));
        SecureDirectory(Path.GetDirectoryName(DataDirectory)!, false);
        SecureDirectory(DataDirectory, false);
        foreach (string file in TunComponentIntegrity.Hashes.Keys.Append("LICENSE").Append("LICENSE-Wintun").Append("COPYING"))
            await ProtectedStorage.CopyAsync(Path.Combine(source, file), Path.Combine(InstallDirectory, file));
        await ProtectedStorage.CopyAsync(Environment.ProcessPath!, Path.Combine(InstallDirectory, "Win10ray.TunService.exe"));
        await ProtectedStorage.WriteTextAsync(Path.Combine(InstallDirectory, "client.sid"), user.Value);
        string executable = $"\"{Path.Combine(InstallDirectory, "Win10ray.TunService.exe")}\" --service";
        if (await ScAsync(existing ? "config" : "create", TunProtocol.ServiceName, "binPath=", executable, "start=", "auto", "depend=", "Tcpip/BFE", "DisplayName=", "Win10ray TUN") != 0)
            throw new InvalidOperationException();
        string descriptor = $"D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCRPLO;;;{user.Value})";
        if (await ScAsync("sdset", TunProtocol.ServiceName, descriptor) != 0) throw new InvalidOperationException();
        _ = await ScAsync("description", TunProtocol.ServiceName, "Win10ray TUN transport");
        _ = await ScAsync("failure", TunProtocol.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000");
        if (await ScAsync("start", TunProtocol.ServiceName) != 0) throw new InvalidOperationException();
        await ProtectedStorage.WriteTextAsync(Path.Combine(InstallDirectory, "installed.v1"), "1");
    }

    public static async Task UninstallAsync()
    {
        if (await ScAsync("query", TunProtocol.ServiceName) == 0)
        {
            await StopAsync();
            if (await ScAsync("delete", TunProtocol.ServiceName) != 0) throw new InvalidOperationException();
        }
        File.Delete(Path.Combine(InstallDirectory, "installed.v1"));
        // Delete only our fixed data directory. The installed running EXE can be removed after it exits.
        if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, true);
    }

    private static async Task StopAsync()
    {
        _ = await ScAsync("stop", TunProtocol.ServiceName);
        for (int attempt = 0; attempt < 50; attempt++)
        {
            Process[] processes = Process.GetProcessesByName("Win10ray.TunService");
            bool running = false;
            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != Environment.ProcessId && process.MainModule?.FileName == Path.Combine(InstallDirectory, "Win10ray.TunService.exe")) running = true;
                    }
                    catch (InvalidOperationException) { }
                }
            }
            if (!running) return;
            await Task.Delay(200);
        }
        throw new TimeoutException();
    }

    private static void SecureDirectory(string path, bool allowRead)
    {
        ProtectedStorage.CreateDirectory(path, allowRead);
    }

    private static async Task<int> ScAsync(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true }
        };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }
}
