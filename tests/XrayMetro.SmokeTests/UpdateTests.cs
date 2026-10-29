using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XrayMetro.Services;

internal static class UpdateTests
{
    public static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); passed++; }
        void Reject(Action action)
        {
            try { action(); throw new InvalidOperationException("FAIL: invalid update accepted"); }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or KeyNotFoundException or FormatException) { passed++; }
        }
        string version = HardwareIdService.ClientVersion;
        bool experimental = UpdateService.IsExperimentalVersion(version);
        ClientRelease Release(byte[] bytes) => new(version, UpdateService.ReleasesUrl + "/download/v" + version + "/Win10ray-" + version + "-win-x64.zip", Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength, experimental);
        string Metadata(ClientRelease release, bool draft = false, bool? prerelease = null) => JsonSerializer.Serialize(new
        {
            tag_name = "v" + release.Version, draft, prerelease = prerelease ?? release.IsExperimental,
            assets = new[] { new { name = Path.GetFileName(new Uri(release.DownloadUrl).LocalPath), state = "uploaded", digest = "sha256:" + release.Sha256, size = release.Size, browser_download_url = release.DownloadUrl } }
        });
        foreach (string invalid in new[] { "latest", "v1.3", "v1.3.0-beta", "1.3.0.0", "v99999999999999.0.0", "https://secret.example" }) Reject(() => UpdateService.ParseVersion(invalid));
        ClientRelease fixture = Release(Encoding.UTF8.GetBytes("Verified release payload"));
        ClientRelease exe = fixture with { DownloadUrl = UpdateService.ReleasesUrl + "/download/v" + version + "/Win10ray-Setup-" + version + ".exe" };
        Check(UpdateService.ParseRelease(Metadata(exe), "1.0.0", experimental) == exe && exe.IsInstaller && exe.CacheFileName == "setup.exe", "EXE-only releases are supported");
        using (JsonDocument zipMetadata = JsonDocument.Parse(Metadata(fixture)))
        using (JsonDocument exeMetadata = JsonDocument.Parse(Metadata(exe)))
        {
            string both = JsonSerializer.Serialize(new { tag_name = "v" + version, draft = false, prerelease = experimental, assets = new[] { zipMetadata.RootElement.GetProperty("assets")[0], exeMetadata.RootElement.GetProperty("assets")[0] } });
            Check(UpdateService.ParseRelease(both, "1.0.0", experimental) == exe, "installer is preferred even when ZIP is listed first");
        }
        Reject(() => UpdateService.Validate(exe with { DownloadUrl = exe.DownloadUrl + "?run=anything" }));
        Check(!ExeInstallerService.EvaluateExitCode(0) && ExeInstallerService.EvaluateExitCode(3010), "restart-required success distinguished without restarting Windows");
        foreach (int exitCode in new[] { 1, 2, 3, 4, 5, 7, 8, 123 })
        {
            try { ExeInstallerService.EvaluateExitCode(exitCode); throw new InvalidOperationException("FAIL: installer failure accepted"); }
            catch (InstallerException exception) { Check(exception.ExitCode == exitCode && (exitCode is not (2 or 5) || exception.Code == "install_cancelled"), "installer cancellation and failure codes preserved"); }
        }
        Check(UpdateService.ParseRelease(Metadata(fixture), "1.0.0", experimental) == fixture, "new release in the selected channel is selected");
        Check(UpdateService.ParseRelease(Metadata(fixture), version, experimental) is null && UpdateService.ParseRelease(Metadata(fixture), "99.0.0", experimental) is null, "equal versions and downgrades ignored");
        Check(UpdateService.ParseRelease(Metadata(fixture, draft: true), "1.0.0") is null && UpdateService.ParseRelease(Metadata(fixture, prerelease: true), "1.0.0") is null, "drafts and prereleases ignored");
        Reject(() => UpdateService.Validate(fixture with { DownloadUrl = "https://example.com/release.zip" }));
        Reject(() => UpdateService.Validate(fixture with { Sha256 = "unverified" }));
        Reject(() => UpdateService.Validate(fixture with { Size = UpdateService.MaximumArchiveBytes + 1 }));
        Reject(() => UpdateService.ParseRelease(Metadata(fixture).Replace("win-x64.zip", "win-arm64.zip"), "1.0.0", experimental));
        Reject(() => UpdatePackage.Under(Path.GetTempPath(), "../escape.exe"));

        string directory = Path.Combine(Path.GetTempPath(), "Win10ray-UpdateTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var service = new UpdateService(new Handler(request =>
            {
                Check(!request.Headers.Contains("x-hwid") && !request.Headers.Contains("Authorization"), "updates never send subscription identifiers or credentials");
                return new(HttpStatusCode.OK) { Content = new StringContent(experimental ? "[" + Metadata(fixture) + "]" : Metadata(fixture)) };
            }))) Check(await service.CheckAsync("1.0.0", experimental: experimental) == fixture, "GitHub release metadata request");
            using (var service = new UpdateService(new Handler(_ => new(HttpStatusCode.NotFound)))) Check(await service.CheckAsync("1.0.0") is null, "repository without published releases is handled");
            byte[] payload = Encoding.UTF8.GetBytes("Verified release payload");
            using (var service = new UpdateService(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })))
                Check(Path.GetFileName(await service.DownloadAsync(exe, Path.Combine(directory, "exe-download"))) == "setup.exe", "EXE downloaded with executable extension");
            string targetWithSpaces = Path.Combine(directory, "client folder Юникод");
            ProcessStartInfo info = ExeInstallerService.CreateStartInfo(Path.Combine(directory, "setup.exe"), targetWithSpaces, Path.Combine(directory, "setup.log"), false);
            Check(info.UseShellExecute && info.ArgumentList.Contains("/CURRENTUSER") && info.ArgumentList.Contains("/DIR=" + targetWithSpaces), "installer gets the exact target directory as one argument");
            Check(info.ArgumentList.Contains("/NORESTART") && info.ArgumentList.Contains("/NOCLOSEAPPLICATIONS") && info.ArgumentList.Contains("/NORESTARTAPPLICATIONS"), "installer cannot restart Windows or close unrelated apps");
            Check(ExeInstallerService.CreateStartInfo(info.FileName, targetWithSpaces, Path.Combine(directory, "setup.log"), true).ArgumentList.Contains("/ALLUSERS"), "machine-wide installations preserve install scope");
            using (var service = new UpdateService(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })))
            {
                string downloaded = await service.DownloadAsync(fixture, Path.Combine(directory, "valid"));
                Check(File.ReadAllBytes(downloaded).SequenceEqual(payload), "archive downloaded and SHA256 verified");
            }
            foreach (var bad in new[] { fixture with { Sha256 = new string('0', 64) }, fixture with { Size = payload.Length + 1 } })
            {
                string target = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                using var service = new UpdateService(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
                try { await service.DownloadAsync(bad, target); throw new InvalidOperationException("FAIL: corrupt download accepted"); }
                catch (InvalidDataException) { Check(!File.Exists(Path.Combine(target, "release.zip")), "corrupt download removed"); }
            }
            using (var service = new UpdateService(new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://untrusted.example/steal") } })))
            {
                try { await service.DownloadAsync(fixture, Path.Combine(directory, "redirect")); throw new InvalidOperationException("FAIL: unsafe redirect accepted"); }
                catch (InvalidDataException) { passed++; }
            }
            string existing = Path.Combine(directory, "existing"); Directory.CreateDirectory(existing); await File.WriteAllTextAsync(Path.Combine(existing, "release.zip"), "keep");
            using (var service = new UpdateService(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })))
            {
                try { await service.DownloadAsync(fixture, existing); throw new InvalidOperationException("FAIL: existing download overwritten"); }
                catch (IOException) { Check(await File.ReadAllTextAsync(Path.Combine(existing, "release.zip")) == "keep", "existing files are preserved when download creation fails"); }
            }
            string main = Path.Combine(Path.GetDirectoryName(typeof(UpdateService).Assembly.Location)!, "Win10ray.exe");
            string mainHash;
            await using (var mainStream = File.OpenRead(main)) mainHash = Convert.ToHexString(await SHA256.HashDataAsync(mainStream));
            ClientRelease wrongExecutable = exe with { Size = new FileInfo(main).Length, Sha256 = mainHash };
            try { await ExeInstallerService.VerifyAsync(main, wrongExecutable); throw new InvalidOperationException("FAIL: non-installer executable accepted"); }
            catch (InvalidDataException) { passed++; }
            string[] required = ["Win10ray.exe", "updater.exe", "xray/xray.exe", "xray/geoip.dat", "xray/geosite.dat", "tun/sing-box.exe", "tun/wintun.dll", "tun/libcronet.dll", "service/Win10ray.TunService.exe"];
            void MakeZip(string path, string? extra = null, bool omitMain = false)
            {
                using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
                foreach (string name in required)
                {
                    if (omitMain && name == "Win10ray.exe") continue;
                    using Stream output = zip.CreateEntry(name).Open();
                    if (name == "Win10ray.exe") { using var input = File.OpenRead(main); input.CopyTo(output); }
                    else output.Write(Encoding.UTF8.GetBytes("Synthetic test component"));
                }
                if (extra is not null) { using Stream output = zip.CreateEntry(extra).Open(); output.WriteByte(1); }
            }
            string valid = Path.Combine(directory, "valid.zip"); MakeZip(valid);
            string extracted = await UpdatePackage.ExtractAsync(valid, Path.Combine(directory, "payload"), version);
            Check(File.Exists(Path.Combine(extracted, "Win10ray.exe")), "valid ZIP layout and product version accepted");
            foreach (string extra in new[] { "../escape.txt", "settings.json", "service/client.sid", "Win10ray.exe", "xray/../../escape.txt" })
            {
                string archive = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip"); MakeZip(archive, extra);
                string target = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                try { await UpdatePackage.ExtractAsync(archive, target, version); throw new InvalidOperationException("FAIL: unsafe ZIP accepted"); }
                catch (InvalidDataException) { Check(!Directory.Exists(target), "unsafe archive rejected and staging removed"); }
            }
            string incomplete = Path.Combine(directory, "incomplete.zip"); MakeZip(incomplete, omitMain: true);
            try { await UpdatePackage.ExtractAsync(incomplete, Path.Combine(directory, "incomplete"), version); throw new InvalidOperationException("FAIL: incomplete update accepted"); }
            catch (InvalidDataException) { passed++; }
            try { await UpdatePackage.ExtractAsync(valid, Path.Combine(directory, "wrong-version"), "9.9.9"); throw new InvalidOperationException("FAIL: mismatched version accepted"); }
            catch (InvalidDataException) { passed++; }

            string targetDirectory = Path.Combine(directory, "installation"); Directory.CreateDirectory(targetDirectory);
            await File.WriteAllTextAsync(Path.Combine(targetDirectory, "Win10ray.exe"), "old exe");
            await File.WriteAllTextAsync(Path.Combine(targetDirectory, "README.md"), "old readme");
            await File.WriteAllTextAsync(Path.Combine(targetDirectory, "settings.json"), "personal settings");
            string replacement = Path.Combine(directory, "replacement"); Directory.CreateDirectory(replacement);
            await File.WriteAllTextAsync(Path.Combine(replacement, "README.md"), "new readme");
            await File.WriteAllTextAsync(Path.Combine(replacement, "Win10ray.exe"), "new exe");
            using (var locked = new FileStream(Path.Combine(targetDirectory, "Win10ray.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { await UpdatePackage.InstallAsync(replacement, targetDirectory); throw new InvalidOperationException("FAIL: locked installation replaced"); }
                catch (IOException) { Check(await File.ReadAllTextAsync(Path.Combine(targetDirectory, "README.md")) == "old readme", "partial update rolled back after a locked executable"); }
            }
            await UpdatePackage.InstallAsync(replacement, targetDirectory);
            Check(await File.ReadAllTextAsync(Path.Combine(targetDirectory, "Win10ray.exe")) == "new exe", "new client files installed");
            Check(await File.ReadAllTextAsync(Path.Combine(targetDirectory, "settings.json")) == "personal settings", "personal files in application directory preserved");
            Check(!Directory.GetDirectories(targetDirectory, ".win10ray-update-*").Any(), "successful update and rollback leave no backup directory");
            using var updater = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(main)!, "updater.exe"), "--self-test") { UseShellExecute = false, CreateNoWindow = true } };
            updater.Start(); await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(updater.ExitCode == 0, "standalone self-contained updater.exe starts successfully");
        }
        finally { Directory.Delete(directory, true); }
        return passed;
    }

    public static async Task<int> RunInstallerAsync(string setup, string failingSetup)
    {
        int passed = 0;
        string directory = Path.Combine(Path.GetTempPath(), "Win10ray-InstallerTest-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(directory, "client folder Юникод");
        Directory.CreateDirectory(target);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(target, "Win10ray.exe"), "previous version");
            await File.WriteAllTextAsync(Path.Combine(target, "settings.json"), "TEST_ONLY_PERSONAL_SETTINGS");
            async Task<ClientRelease> ReleaseAsync(string path)
            {
                string version = FileVersionInfo.GetVersionInfo(path).ProductVersion!.Trim();
                await using var input = File.OpenRead(path);
                return new(version, UpdateService.ReleasesUrl + "/download/v" + version + "/Win10ray-Setup-" + version + ".exe", Convert.ToHexString(await SHA256.HashDataAsync(input)), input.Length, UpdateService.IsExperimentalVersion(version));
            }
            ClientRelease release = await ReleaseAsync(setup);
            try { await ExeInstallerService.VerifyAsync(setup, release with { Sha256 = new string('0', 64) }); throw new InvalidOperationException("Invalid installer checksum accepted."); }
            catch (InvalidDataException) { passed++; }
            var wrongVersion = release with { Version = "9.9.9", DownloadUrl = UpdateService.ReleasesUrl + "/download/v9.9.9/Win10ray-Setup-9.9.9.exe" };
            try { await ExeInstallerService.VerifyAsync(setup, wrongVersion); throw new InvalidOperationException("Invalid installer version accepted."); }
            catch (InvalidDataException) { passed++; }
            if (await ExeInstallerService.InstallAsync(setup, release, target, Path.Combine(directory, "setup.log"))) throw new InvalidOperationException("Unexpected restart requested by test installer.");
            passed++;
            if (FileVersionInfo.GetVersionInfo(Path.Combine(target, "Win10ray.exe")).ProductVersion?.Split('+')[0] != release.Version) throw new InvalidOperationException("Wrong client installed.");
            passed++;
            if (await File.ReadAllTextAsync(Path.Combine(target, "settings.json")) != "TEST_ONLY_PERSONAL_SETTINGS") throw new InvalidOperationException("Personal settings changed.");
            passed++;
            string before;
            await using (var installed = File.OpenRead(Path.Combine(target, "Win10ray.exe"))) before = Convert.ToHexString(await SHA256.HashDataAsync(installed));
            ClientRelease failure = await ReleaseAsync(failingSetup);
            try { await ExeInstallerService.InstallAsync(failingSetup, failure, target, Path.Combine(directory, "failed.log")); throw new InvalidOperationException("Failed Setup reported success."); }
            catch (InstallerException exception) when (exception.ExitCode == 1) { passed++; }
            await using var again = File.OpenRead(Path.Combine(target, "Win10ray.exe"));
            if (before != Convert.ToHexString(await SHA256.HashDataAsync(again))) throw new InvalidOperationException("Failed installation changed the client.");
            passed++;
        }
        finally { Directory.Delete(directory, true); }
        return passed;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
