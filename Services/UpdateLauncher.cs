using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XrayMetro.Services;

public static class UpdateLauncher
{
    public static async Task PrepareAsync(ClientRelease release, IProgress<double>? progress, CancellationToken token)
    {
        string updater = Path.Combine(AppContext.BaseDirectory, "updater.exe");
        if (!File.Exists(updater)) throw new FileNotFoundException("updater.exe is missing.");
        string directory = UpdatePackage.Under(UpdateService.CacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? child = null;
        try
        {
            using var service = new UpdateService();
            string archive = await service.DownloadAsync(release, directory, progress, token).ConfigureAwait(false);
            if (release.IsInstaller) await ExeInstallerService.VerifyAsync(archive, release, token).ConfigureAwait(false);
            else await UpdatePackage.ExtractAsync(archive, Path.Combine(directory, "preflight"), release.Version, token).ConfigureAwait(false);
            string executable = Path.Combine(directory, "updater.exe");
            await Task.Run(() => File.Copy(updater, executable), token).ConfigureAwait(false);
            using Process parent = Process.GetCurrentProcess();
            var handoff = new UpdateHandoff(AppContext.BaseDirectory, archive, release, parent.Id, parent.StartTime.ToUniversalTime().Ticks, release.IsExperimental);
            string request = Path.Combine(directory, "handoff.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(handoff), token).ConfigureAwait(false);
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory };
            info.ArgumentList.Add("--apply"); info.ArgumentList.Add(request);
            child = Process.Start(info) ?? throw new IOException("Could not start updater.exe.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            while (!File.Exists(Path.Combine(directory, "ready")))
            {
                if (child.HasExited) throw new IOException("Updater exited before the handoff.");
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            if (child is not null) { try { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().ConfigureAwait(false); } } catch (InvalidOperationException) { } }
            // Directory is a fresh GUID child of our fixed update cache.
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        finally { child?.Dispose(); }
    }
}
