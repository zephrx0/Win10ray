using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XrayMetro.Services;

public static class UpdatePackage
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Win10ray.exe", "updater.exe", "README.md", "xray/xray.exe", "xray/geoip.dat", "xray/geosite.dat", "xray/LICENSE", "xray/VERSION.txt",
        "tun/sing-box.exe", "tun/wintun.dll", "tun/libcronet.dll", "tun/LICENSE", "tun/COPYING", "tun/LICENSE-Wintun", "tun/VERSION.txt",
        "service/Win10ray.TunService.exe", "Assets/Flags/SOURCE.txt", "docs/images/connection.png"
    };
    private static readonly string[] Required = ["Win10ray.exe", "updater.exe", "xray/xray.exe", "xray/geoip.dat", "xray/geosite.dat", "tun/sing-box.exe", "tun/wintun.dll", "tun/libcronet.dll", "service/Win10ray.TunService.exe"];

    public static string Under(string directory, string relative)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path leaves the application directory.");
        return path;
    }

    public static async Task<string> ExtractAsync(string archivePath, string directory, string version, CancellationToken token = default)
    {
        UpdateService.ParseVersion(version);
        if (Directory.Exists(directory)) throw new IOException("Extraction directory already exists.");
        Directory.CreateDirectory(directory);
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            if (archive.Entries.Count > 1024) throw new InvalidDataException("Too many archive entries.");
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Archive links are not allowed.");
                if (name.EndsWith('/')) { Under(directory, name); continue; }
                if (!Allowed.Contains(name) || !entries.TryAdd(name, entry)) throw new InvalidDataException("Unexpected or duplicate archive entry.");
                total += entry.Length;
                if (entry.Length < 0 || total > 1024L * 1024 * 1024) throw new InvalidDataException("Unpacked release is too large.");
            }
            if (Required.Any(file => !entries.TryGetValue(file, out ZipArchiveEntry? entry) || entry.Length == 0)) throw new InvalidDataException("The release is missing required files.");
            foreach (var item in entries)
            {
                token.ThrowIfCancellationRequested();
                string path = Under(directory, item.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using Stream source = item.Value.Open();
                await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                byte[] buffer = new byte[65536]; long written = 0; int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    written += count;
                    if (written > item.Value.Length) throw new InvalidDataException("Unpacked entry exceeds its declared size.");
                    await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (written != item.Value.Length) throw new InvalidDataException("Incomplete archive entry.");
            }
            string? productVersion = FileVersionInfo.GetVersionInfo(Path.Combine(directory, "Win10ray.exe")).ProductVersion?.Split('+')[0];
            if (productVersion != version) throw new InvalidDataException("The application version does not match the release tag.");
            return directory;
        }
        catch { Directory.Delete(directory, true); throw; }
    }

    public static void ValidateTarget(string target)
    {
        string directory = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if (directory.Equals(Path.GetPathRoot(directory)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || !File.Exists(Path.Combine(directory, "Win10ray.exe")))
            throw new InvalidDataException("Not a Win10ray installation directory.");
    }

    private static void CheckDestination(string root, string relative)
    {
        string path = Under(root, relative);
        while (path.Length > Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Links in the installation directory are not allowed.");
            path = Path.GetDirectoryName(path)!;
        }
    }

    public static async Task InstallAsync(string extracted, string target, IProgress<double>? progress = null, CancellationToken token = default)
    {
        ValidateTarget(target);
        string[] files = Directory.GetFiles(extracted, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string source in files)
        {
            string name = Path.GetRelativePath(extracted, source).Replace('\\', '/');
            if (!Allowed.Contains(name)) throw new InvalidDataException("Unexpected installation file.");
            CheckDestination(target, name);
        }
        string backup = Under(target, ".win10ray-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var saved = new List<string>(); var written = new List<string>();
        bool completed = false, rolledBack = false;
        try
        {
            foreach (string source in files)
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetRelativePath(extracted, source);
                CheckDestination(target, name);
                string destination = Under(target, name);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    string original = Under(backup, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    await RetryAsync(() => File.Move(destination, original), token).ConfigureAwait(false);
                    saved.Add(name);
                }
                written.Add(name);
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                progress?.Report(100.0 * written.Count / files.Length);
            }
            completed = true;
        }
        catch
        {
            // Keep the backup if recovery fails; never discard the last usable client.
            foreach (string name in written.AsEnumerable().Reverse()) await RetryAsync(() => File.Delete(Under(target, name)), CancellationToken.None).ConfigureAwait(false);
            foreach (string name in saved.AsEnumerable().Reverse()) await RetryAsync(() => File.Move(Under(backup, name), Under(target, name)), CancellationToken.None).ConfigureAwait(false);
            rolledBack = true;
            throw;
        }
        finally
        {
            if (completed || rolledBack) { try { Directory.Delete(backup, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    private static async Task RetryAsync(Action action, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (IOException) when (attempt < 5) { await Task.Delay(200 * (attempt + 1), token).ConfigureAwait(false); }
        }
    }

    public static async Task WaitForClientExitAsync(string directory, CancellationToken token)
    {
        string executable = Path.GetFullPath(Path.Combine(directory, "Win10ray.exe"));
        while (true)
        {
            bool running = false;
            foreach (Process process in Process.GetProcessesByName("Win10ray"))
                using (process)
                {
                    try { if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) running = true; }
                    catch (InvalidOperationException) { }
                }
            if (!running) return;
            await Task.Delay(200, token).ConfigureAwait(false);
        }
    }
}
