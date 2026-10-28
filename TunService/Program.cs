using System.Security.Principal;
namespace Win10ray.TunService;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--self-test")
            {
                using var identity = WindowsIdentity.GetCurrent();
                ProcessSecurity.AllowQuery(identity.User!); await TunEngine.SelfTestAsync(); return 0;
            }
            if (args.Length == 1 && args[0] == "--service") { ServiceHost.Dispatch(); return 0; }
            if (args.Length == 2 && args[0] == "--install")
            {
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
                await Installer.InstallAsync(new SecurityIdentifier(args[1])); return 0;
            }
            if (args.Length == 1 && args[0] == "--uninstall")
            {
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
                await Installer.UninstallAsync(); return 0;
            }
            return 87;
        }
        catch { return 1; }
    }
}
