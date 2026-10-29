using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using XrayMetro.Models;
using XrayMetro.Services;

namespace XrayMetro.ViewModels;

public sealed partial class MainViewModel
{
    public AsyncCommand ImportJsonFileCommand { get; private set; } = null!;
    public AsyncCommand ExportJsonCommand { get; private set; } = null!;
    private void InitializeJsonImport()
    {
        ImportJsonFileCommand = new(p=>ImportJsonFilesAsync(p as string[]),ReportError,_=>CanEditConnection);
        ExportJsonCommand = new(p=>ExportJsonAsync(p as ServerProfile),ReportError,p=>p is ServerProfile{Protocol:"xray-json"});
    }
    private async Task ImportJsonFilesAsync(string[]? files)
    {
        if(files is null)
        {
            OpenFileDialog dialog=new(){Filter="Xray JSON (*.json)|*.json",Multiselect=true,CheckFileExists=true};
            if(dialog.ShowDialog()!=true)return;files=dialog.FileNames;
        }
        if(files.Length>16)throw new InvalidOperationException(LocalizationService.Get("JsonFileLimit"));
        int added=0,rejected=0;
        foreach(string file in files)
        {
            try
            {
                if(new FileInfo(file).Length>XrayJsonProfileService.MaximumCharacters)throw XrayJsonProfileService.Invalid();
                string text=await File.ReadAllTextAsync(file,Encoding.UTF8,_lifetime.Token);
                ImportResult result=await Task.Run(()=>XrayJsonProfileService.ParseMany(text),_lifetime.Token);
                if(!CanEditConnection)return;
                foreach(ServerProfile server in result.Profiles)if(!Servers.Any(existing=>existing.SourceKey==server.SourceKey)){Servers.Add(server);added++;}
                rejected+=result.Errors.Count;
            }
            catch(Exception exception)when(exception is IOException or UnauthorizedAccessException or FormatException){rejected++;}
        }
        RefreshServers();await SaveAsync();Message=LocalizationService.Pick($"Добавлено: {added}. Отклонено: {rejected}.",$"Added: {added}. Rejected: {rejected}.");
    }
    private async Task ExportJsonAsync(ServerProfile? server)
    {
        if(server?.Protocol!="xray-json")return;
        if(System.Windows.MessageBox.Show(LocalizationService.Get("JsonExportWarning"),"Win10ray",System.Windows.MessageBoxButton.OKCancel,System.Windows.MessageBoxImage.Warning,System.Windows.MessageBoxResult.Cancel)!=System.Windows.MessageBoxResult.OK)return;
        SaveFileDialog dialog=new(){Filter="Xray JSON (*.json)|*.json",FileName="Win10ray-profile.json",DefaultExt=".json"};
        if(dialog.ShowDialog()==true)await File.WriteAllTextAsync(dialog.FileName,ShareLinkExporter.Export(server),new UTF8Encoding(false),_lifetime.Token);
    }
}
