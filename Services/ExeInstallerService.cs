using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace XrayMetro.Services;

public sealed class InstallerException(string code, int? exitCode = null) : IOException("Installer failed: " + code)
{
    public string Code { get; } = code;
    public int? ExitCode { get; } = exitCode;
}

public static class ExeInstallerService
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{4FBAF886-CFEB-4ADE-8F01-611553E36F5A}_is1";

    public static async Task VerifyAsync(string path, ClientRelease release, CancellationToken token = default)
    {
        if (!release.IsInstaller) throw new InvalidDataException("Not an EXE release.");
        await UpdateService.VerifyAsync(path, release, token).ConfigureAwait(false);
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductVersion?.Trim().Split('+')[0] != release.Version || info.ProductName?.Trim() != "Win10ray" || info.FileDescription?.Trim() != "Win10ray Setup")
            throw new InvalidDataException("Installer identity or version does not match the release.");
    }

    public static bool IsMachineInstallation(string target)
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey? key = machine.OpenSubKey(UninstallKey);
            string? location = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(location) && SameDirectory(location, target)) return true;
        }
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFiles32 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string path = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(programFiles.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(programFiles32.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameDirectory(string first, string second) => string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    public static ProcessStartInfo CreateStartInfo(string installer, string target, string log, bool machineInstallation)
    {
        string directory = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if (directory.IndexOfAny(['"', '\r', '\n']) >= 0) throw new InvalidDataException("Invalid installation directory.");
        var info = new ProcessStartInfo(Path.GetFullPath(installer)) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(installer))! };
        foreach (string argument in new[] { "/SP-", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/RESTARTEXITCODE=3010", "/NOCLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS", machineInstallation ? "/ALLUSERS" : "/CURRENTUSER", "/DIR=" + directory, "/LOG=" + Path.GetFullPath(log) })
            info.ArgumentList.Add(argument);
        return info;
    }

    public static bool EvaluateExitCode(int exitCode)
    {
        if (exitCode == 3010) return true;
        if (exitCode == 0) return false;
        throw new InstallerException(exitCode is 2 or 5 ? "install_cancelled" : "installer_exit_" + exitCode, exitCode);
    }

    // Returns true when Setup completed but deferred replacement requires a Windows restart.
    public static async Task<bool> InstallAsync(string installer, ClientRelease release, string target, string log, CancellationToken token = default)
    {
        UpdatePackage.ValidateTarget(target);
        // Deny changes/deletion from verification through completion, including the UAC handoff.
        await using var packageLock = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        await VerifyAsync(installer, release, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        ProcessStartInfo info = CreateStartInfo(installer, target, log, IsMachineInstallation(target));
        Process process;
        try { process = Process.Start(info) ?? throw new InstallerException("installer_start_failed"); }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) { throw new InstallerException("elevation_cancelled"); }
        using (process)
        {
            // Never terminate an installer in the middle of replacing application files.
            await process.WaitForExitAsync().ConfigureAwait(false);
            bool restart = EvaluateExitCode(process.ExitCode);
            if (!restart && FileVersionInfo.GetVersionInfo(Path.Combine(target, "Win10ray.exe")).ProductVersion?.Trim().Split('+')[0] != release.Version)
                throw new InstallerException("installed_version_mismatch");
            return restart;
        }
    }
}
