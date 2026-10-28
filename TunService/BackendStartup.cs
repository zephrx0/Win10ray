using System.Text.RegularExpressions;

namespace Win10ray.TunService;

internal sealed class BackendStartup
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => _ready.Task;

    public bool Observe(string line)
    {
        // Interface Up precedes installation of routes and WFP rules.
        if (!Regex.IsMatch(line, @"\bINFO\b.*\bsing-box started \(")) return false;
        return _ready.TrySetResult();
    }

    public static string Clean(string line) => Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "");

    public static void SelfTest()
    {
        var startup = new BackendStartup();
        startup.Observe("INFO inbound/tun[tun-in]: started at Win10ray");
        startup.Observe("WARN start inbound/tun[tun-in] take too much time to finish!");
        if (startup.Ready.IsCompleted) throw new InvalidOperationException("Partial adapter startup reported ready.");
        if (!startup.Observe(Clean("\x1b[36mINFO\x1b[0m sing-box started (0.345s)")) || !startup.Ready.IsCompletedSuccessfully)
            throw new InvalidOperationException("Backend startup handshake failed.");
        if (Clean("\x1b[33mWARN\x1b[0m test") != "WARN test") throw new InvalidOperationException("ANSI cleanup failed.");
    }
}
