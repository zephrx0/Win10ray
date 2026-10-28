using System.Diagnostics;
using System.IO;
using System.Windows;
using XrayMetro.Services;

namespace Win10ray.Updater;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args.Length == 1 && e.Args[0] == "--self-test")
            {
                if (UpdateService.ParseVersion("v1.3.0").ToString(3) != "1.3.0") throw new InvalidDataException();
                Shutdown(0); return;
            }
            if (e.Args.Length == 0)
            {
                UpdatePackage.ValidateTarget(AppContext.BaseDirectory);
                string directory = UpdatePackage.Under(UpdateService.CacheDirectory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string executable = Path.Combine(directory, "updater.exe");
                File.Copy(Environment.ProcessPath!, executable);
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory };
                info.ArgumentList.Add("--standalone"); info.ArgumentList.Add(AppContext.BaseDirectory);
                using Process? child = Process.Start(info);
                if (child is null) throw new IOException();
                Shutdown(); return;
            }
            if (e.Args.Length != 2 || e.Args[0] is not ("--standalone" or "--apply")) throw new ArgumentException();
            var window = new UpdateWindow(e.Args[0], e.Args[1]);
            MainWindow = window; window.Show();
        }
        catch
        {
            MessageBox.Show(UpdateWindow.Text("Не удалось запустить обновление. Запустите updater.exe рядом с Win10ray.exe.", "Could not start the updater. Place updater.exe next to Win10ray.exe."), "Win10ray", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
