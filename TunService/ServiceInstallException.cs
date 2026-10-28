using System.ComponentModel;
using XrayMetro.Services;

namespace Win10ray.TunService;

internal sealed class ServiceInstallException : Exception
{
    public int ExitCode { get; }
    public ServiceInstallException(TunInstallStage stage, Exception inner) : base("TUN installation failed at " + stage, inner)
    {
        int code = inner switch
        {
            Win32Exception win32 => win32.NativeErrorCode, UnauthorizedAccessException => 5,
            FileNotFoundException => 2, DirectoryNotFoundException => 3, InvalidDataException => 13,
            TimeoutException => 1460, IOException io => io.HResult & 0xffff, _ => 0
        };
        ExitCode = TunInstallDiagnostics.Encode(stage, code);
    }
}
