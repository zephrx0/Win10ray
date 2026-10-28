using System;

namespace XrayMetro.Services;

public enum TunInstallStage
{
    UserIdentity = 1, Components, StopService, AppContainer, AppDirectory,
    DataContainer, DataDirectory, CopyComponents, CopyHelper, ClientIdentity,
    RegisterService, ServicePermissions, StartService, Completion
}

public static class TunInstallDiagnostics
{
    private const int Signature = 0x2A000000;
    public static int Encode(TunInstallStage stage, int nativeCode) => Signature | ((int)stage << 16) | (nativeCode & 0xffff);
    public static bool TryDecode(int exitCode, out TunInstallStage stage, out int nativeCode)
    {
        stage = (TunInstallStage)((exitCode >> 16) & 0xff); nativeCode = exitCode & 0xffff;
        return (exitCode & unchecked((int)0xff000000)) == Signature && Enum.IsDefined(stage);
    }
    public static string ErrorCode(int exitCode)
    {
        if (!TryDecode(exitCode, out TunInstallStage stage, out int code)) return "install_exit_" + unchecked((uint)exitCode).ToString("x");
        string name = stage switch
        {
            TunInstallStage.UserIdentity => "user_identity", TunInstallStage.Components => "components", TunInstallStage.StopService => "stop_service",
            TunInstallStage.AppContainer => "app_container", TunInstallStage.AppDirectory => "app_directory", TunInstallStage.DataContainer => "data_container",
            TunInstallStage.DataDirectory => "data_directory", TunInstallStage.CopyComponents => "copy_components", TunInstallStage.CopyHelper => "copy_helper",
            TunInstallStage.ClientIdentity => "client_identity", TunInstallStage.RegisterService => "register_service", TunInstallStage.ServicePermissions => "service_permissions",
            TunInstallStage.StartService => "start_service", _ => "completion"
        };
        return "install_" + name + "_" + code;
    }
}
