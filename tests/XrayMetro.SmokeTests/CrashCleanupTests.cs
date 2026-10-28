using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using XrayMetro.Models;
using XrayMetro.Services;

internal static class CrashCleanupTests
{
    public static async Task RunCoreChildAsync(string[] args)
    {
        int socks = int.Parse(args[2], CultureInfo.InvariantCulture), http = int.Parse(args[3], CultureInfo.InvariantCulture), api = int.Parse(args[4], CultureInfo.InvariantCulture);
        using var core = new XrayProcessService(args[1], socks, http, api);
        const string credential = "feef6b54-1f5a-4e35-bd7a-5b278453f73c";
        var profile = new LinkParser().Parse($"vless://{credential}@127.0.0.1:1?encryption=none&security=none");
        await core.StartAsync(new XrayConfigBuilder().Build(profile, new AppSettings(), api, socks, http), [credential], false);
        Console.WriteLine("READY");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    public static async Task<int> TestJobAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "XrayMetro-JobSmoke-" + Guid.NewGuid().ToString("N"));
        var ports = new HashSet<int>();
        while (ports.Count < 3)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            ports.Add(((IPEndPoint)reservation.LocalEndpoint).Port);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using Process helper = Hidden(Environment.ProcessPath!);
        helper.StartInfo.ArgumentList.Add("--core-child"); helper.StartInfo.ArgumentList.Add(directory);
        foreach (int port in ports) helper.StartInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        try
        {
            helper.Start();
            string? line = await helper.StandardOutput.ReadLineAsync(timeout.Token);
            if (line != "READY") throw new InvalidOperationException("Core job helper did not start: " + await helper.StandardError.ReadToEndAsync(timeout.Token));
            helper.Kill(entireProcessTree: false);
            await helper.WaitForExitAsync(timeout.Token);
            foreach (int port in ports)
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    using var probe = new TcpClient();
                    try { await probe.ConnectAsync(IPAddress.Loopback, port, timeout.Token); }
                    catch (SocketException) { break; }
                    await Task.Delay(50, timeout.Token);
                }
            }
            return 2;
        }
        finally
        {
            try { if (!helper.HasExited) { helper.Kill(true); await helper.WaitForExitAsync(); } } catch (InvalidOperationException) { }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    public static async Task<int> TestWatchdogAsync(string executable)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using Process parent = Hidden(Path.Combine(Environment.SystemDirectory, "ping.exe"));
        parent.StartInfo.ArgumentList.Add("-n"); parent.StartInfo.ArgumentList.Add("15"); parent.StartInfo.ArgumentList.Add("127.0.0.1");
        using Process watchdog = Hidden(Path.GetFullPath(executable));
        try
        {
            parent.Start();
            long ticks = parent.StartTime.ToUniversalTime().Ticks;
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\XrayMetro.Watchdog.{parent.Id}.{ticks}");
            watchdog.StartInfo.ArgumentList.Add("--watchdog"); watchdog.StartInfo.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture)); watchdog.StartInfo.ArgumentList.Add(ticks.ToString(CultureInfo.InvariantCulture));
            watchdog.Start();
            bool signaled = await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(10)), timeout.Token);
            if (!signaled) throw new InvalidOperationException("Watchdog handshake failed.");
            if (watchdog.HasExited) throw new InvalidOperationException("Watchdog did not wait for parent.");
            parent.Kill(); await parent.WaitForExitAsync(timeout.Token);
            await watchdog.WaitForExitAsync(timeout.Token);
            if (watchdog.ExitCode != 0) throw new InvalidOperationException("Watchdog failed on parent termination.");
            // No ownership marker is created, so no registry value is changed by this test.
            return 3;
        }
        finally
        {
            foreach (Process process in new[] { parent, watchdog })
            {
                try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } } catch (InvalidOperationException) { }
            }
        }
    }

    private static Process Hidden(string executable) => new()
    {
        StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        }
    };
}
