using System.ComponentModel;
using System.Runtime.InteropServices;
using XrayMetro.Services;

namespace Win10ray.TunService;

internal static class ServiceHost
{
    private static readonly ServiceMain MainCallback = Run;
    private static readonly ServiceControl HandlerCallback = Control;
    private static readonly CancellationTokenSource Stop = new();
    private static IntPtr _handle;
    public static void Dispatch()
    {
        var table = new[] { new ServiceTable { Name = TunProtocol.ServiceName, Callback = MainCallback }, new ServiceTable() };
        if (!StartServiceCtrlDispatcher(table)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static void Run(int count, IntPtr arguments)
    {
        _handle = RegisterServiceCtrlHandler(TunProtocol.ServiceName, HandlerCallback);
        if (_handle == IntPtr.Zero) return;
        Report(2, 0);
        try
        {
            using var engine = new TunEngine();
            new PipeServer(engine).RunAsync(Stop.Token, () => Report(4, 5)).GetAwaiter().GetResult();
            Report(1, 0);
        }
        catch (OperationCanceledException) { Report(1, 0); }
        catch { Report(1, 0, 1); }
    }
    private static void Control(uint control)
    {
        if (control is 1 or 5) { Report(3, 0); Stop.Cancel(); }
        else if (control == 4) Report(4, 5);
    }
    private static void Report(uint state, uint accepted, uint code = 0)
    {
        var status = new ServiceStatus { Type = 0x10, State = state, Accepted = accepted, ExitCode = code, WaitHint = state is 2 or 3 ? 15000u : 0u };
        _ = SetServiceStatus(_handle, ref status);
    }
    private delegate void ServiceMain(int count, IntPtr arguments);
    private delegate void ServiceControl(uint control);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTable { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public ServiceMain? Callback; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, State, Accepted, ExitCode, SpecificCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartServiceCtrlDispatcher([In] ServiceTable[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandler(string name, ServiceControl handler);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
