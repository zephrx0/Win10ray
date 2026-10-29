using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XrayMetro.Services;

namespace XrayMetro.Views;

public sealed class ProcessPickerWindow : Window
{
    private string? _selection;
    private ProcessPickerWindow(string[] processes)
    {
        Title = LocalizationService.Get("RunningProcesses"); Width = 430; Height = 520; MinWidth = 320; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "ForegroundBrush");
        Grid grid = new() { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TextBox search = new() { Margin = new Thickness(0,0,0,12) }; grid.Children.Add(search);
        ListBox list = new() { ItemsSource = processes }; Grid.SetRow(list,1); grid.Children.Add(list);
        search.TextChanged += (_,_) => list.ItemsSource = processes.Where(name => name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
        Button choose = new() { Content = LocalizationService.Get("Select"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,16,0,0) };
        void Select() { if(list.SelectedItem is string value) { _selection=value; DialogResult=true; } }
        choose.Click += (_,_) => Select(); list.MouseDoubleClick += (_,_) => Select(); Grid.SetRow(choose,2); grid.Children.Add(choose); Content=grid;
        PreviewKeyDown += (_,args) => { if(args.Key==Key.Escape) { args.Handled=true; Close(); } };
    }
    public static async Task<string?> PickAsync()
    {
        string[] names = await Task.Run(() => Process.GetProcesses().Select(process => { using(process) { try { return process.ProcessName + ".exe"; } catch { return ""; } } }).Where(name=>name.Length>4).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name=>name,StringComparer.OrdinalIgnoreCase).ToArray());
        ProcessPickerWindow window = new(names) { Owner = Application.Current.MainWindow };
        window.ShowDialog(); return window._selection;
    }
}
