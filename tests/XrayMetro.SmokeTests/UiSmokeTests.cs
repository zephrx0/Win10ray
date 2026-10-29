using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using XrayMetro.Models;
using XrayMetro.Services;
using XrayMetro.ViewModels;
using XrayMetro.Views;

internal static class UiSmokeTests
{
    public static Task<int> RunAsync()
    {
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // Host the real resources without invoking production startup, its mutex or AppData.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string dictionary in new[] { "Dark", "Controls", "Brand" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/Win10ray;component/Themes/{dictionary}.xaml", UriKind.Relative) });
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "XrayMetro-UiSmoke-" + Guid.NewGuid().ToString("N"));
                var diagnostics = new StringWriter();
                var listener = new TextWriterTraceListener(diagnostics);
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                MainWindow? window = null;
                MainViewModel? vm = null;
                try
                {
                    using var core = new XrayProcessService(Path.Combine(directory, "runtime"));
                    var state = new AppState { Settings = new AppSettings() };
                    var parser = new LinkParser();
                    const string id = "6f5e4438-c1a9-4d4b-8f27-371c1c4ee3ba";
                    state.Servers.Add(parser.Parse($"vless://{id}@example.com:443?security=tls#🇫🇷%20Amsterdam"));
                    state.Servers.Add(parser.Parse($"vless://{id}@example.net:443?security=tls&type=ws#Франкфурт"));
                    state.Servers.Add(parser.Parse("trojan://synthetic-password@example.org:443#Helsinki"));
                    vm = new MainViewModel(state, new SettingsService(directory), core, new SystemProxyService());
                    if (vm.ConnectionButtonText != LocalizationService.Get("Connect")) throw new InvalidOperationException("Connection button localization failed.");
                    vm.ConnectionModeIndex = 1;
                    if (!vm.UseTun || vm.UseSystemProxy) throw new InvalidOperationException("TUN mode is not exclusive.");
                    vm.ConnectionModeIndex = 2;
                    if (!vm.UseTun || vm.UseSystemProxy || vm.ConnectionModes.Length != 2) throw new InvalidOperationException("The removed local mode was selectable.");
                    vm.ConnectionModeIndex = 0;
                    window = new MainWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
                    window.Show();
                    using var tray = new TrayService(window, vm, () => { });
                    string artifacts = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "previews", System.Globalization.CultureInfo.CurrentUICulture.Name);
                    Directory.CreateDirectory(artifacts);
                    int checks = 3;
                    if (!vm.SendHwid || !vm.AutoSwitchServers) throw new InvalidOperationException("HWID and failover should be enabled by default.");
                    await vm.RefreshVersionAsync();
                    if (vm.ClientVersion != HardwareIdService.ClientVersion || !System.Text.RegularExpressions.Regex.IsMatch(vm.CoreVersion, @"^\d+\.\d+")) throw new InvalidOperationException("Client or core version was not loaded.");
                    vm.SearchText = "Amsterdam";
                    if (vm.ServerView.Cast<ServerProfile>().Count() != 1 || vm.Servers.Count != 3 || CollectionViewSource.GetDefaultView(vm.Servers).Cast<ServerProfile>().Count() != 3) throw new InvalidOperationException("Server search leaked into the global server selection.");
                    vm.SearchText = ""; checks += 3;
                    if (!vm.Servers[0].HasFlag || vm.Servers[0].DisplayName != "Amsterdam" || CountryFlagService.CountryCode(vm.Servers[0].Name) != "fr") throw new InvalidOperationException("The configured flag is not rendered independently of emoji fonts.");
                    checks++;
                    foreach (bool dark in new[] { true, false })
                    {
                        vm.DarkTheme = dark;
                        window.Width = dark ? 900 : 700; window.Height = dark ? 600 : 500;
                        for (int page = 0; page < 5; page++)
                        {
                            vm.SelectedPage = page;
                            vm.IsRenaming = page == 1;
                            await Task.Delay(240);
                            window.UpdateLayout();
                            foreach (ComboBox selector in Descendants<ComboBox>(window).Where(element => element.IsVisible))
                            {
                                Rect bounds = selector.TransformToAncestor(window).TransformBounds(new Rect(selector.RenderSize));
                                if (bounds.Right > window.ActualWidth - 20 || bounds.Left < 225) throw new InvalidOperationException("A selector extends beyond the content boundaries.");
                            }
                            if (((TextBlock)window.FindName("BrandText")).Foreground is not SolidColorBrush brand || dark && brand.Color.R < 240 || !dark && brand.Color.R > 80) throw new InvalidOperationException("Brand text is not readable in the selected theme.");
                            FrameworkElement content = (FrameworkElement)window.Content;
                            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(content);
                            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                            using var file = File.Create(Path.Combine(artifacts, $"{(dark ? "dark" : "light")}-{page}.png")); png.Save(file);
                            checks++;
                            if (page == 1)
                            {
                                vm.IsRenaming = false; vm.ServersTab = 1;
                                await Task.Delay(100); window.UpdateLayout();
                                ValidateHorizontalBounds((FrameworkElement)window.FindName("PageHost"), 1, page);
                                vm.ServersTab = 0;
                                checks++;
                            }
                        }
                    }
                    ComboBox combo = Find<ComboBox>(window)!;
                    if (combo is null) throw new InvalidOperationException("Theme ComboBox was not created.");
                    combo.IsDropDownOpen = true; await Task.Delay(100); combo.IsDropDownOpen = false;
                    // Different logical viewports cover the space left by Windows display scaling.
                    // Render at the corresponding pixel DPI as well, without changing the user's display.
                    window.MinWidth = 0; window.MinHeight = 0;
                    foreach (bool dark in new[] { true, false })
                    foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5 })
                    {
                        vm.DarkTheme = dark;
                        window.Width = 1920 / scale; window.Height = 1080 / scale;
                        for (int page = 0; page < 5; page++)
                        {
                            vm.SelectedPage = page;
                            vm.IsRenaming = page == 1;
                            await Task.Delay(30);
                            window.UpdateLayout();
                            ValidateHorizontalBounds((FrameworkElement)window.FindName("PageHost"), scale, page);
                            foreach (GlyphIcon icon in Descendants<GlyphIcon>((FrameworkElement)window.FindName("Sidebar")))
                            {
                                DependencyObject? ancestor = VisualTreeHelper.GetParent(icon);
                                while (ancestor is not null && ancestor is not ContentPresenter) ancestor = VisualTreeHelper.GetParent(ancestor);
                                if (ancestor is ContentPresenter presenter && presenter.ActualWidth < 32) throw new InvalidOperationException($"A navigation icon is clipped at {scale * 100:0}%.");
                            }
                            if (scale is 2.5 or 4 && page is 0 or 4)
                            {
                                await Task.Delay(240);
                                window.UpdateLayout();
                                FrameworkElement content = (FrameworkElement)window.Content;
                                var scaled = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale), (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                                scaled.Render(content);
                                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(scaled));
                                using var file = File.Create(Path.Combine(artifacts, $"{(dark ? "dark" : "light")}-{scale * 100:0}pct-{page}.png")); encoder.Save(file);
                            }
                            checks++;
                            if (page == 1)
                            {
                                vm.IsRenaming = false; vm.ServersTab = 1;
                                await Task.Delay(50); window.UpdateLayout();
                                ValidateHorizontalBounds((FrameworkElement)window.FindName("PageHost"), scale, page);
                                vm.ServersTab = 0; checks++;
                            }
                        }
                    }
                    window.Width = 900; window.Height = 600;
                    vm.SelectedPage = 4;
                    foreach (bool dark in new[] { true, false })
                    {
                        vm.DarkTheme = dark;
                        if (((SolidColorBrush)window.FindResource("NavBrush")).Color.A != 255) throw new InvalidOperationException("The sidebar must be opaque.");
                        double textContrast = Contrast(((SolidColorBrush)window.FindResource("ForegroundBrush")).Color, ((SolidColorBrush)window.FindResource("ContentBackgroundBrush")).Color);
                        double secondaryContrast = Contrast(((SolidColorBrush)window.FindResource("SecondaryTextBrush")).Color, ((SolidColorBrush)window.FindResource("ContentBackgroundBrush")).Color);
                        if (textContrast < 7 || secondaryContrast < 4.5) throw new InvalidOperationException("Theme text contrast is insufficient.");
                        checks += 2;
                    }
                    vm.SelectedPage = 1; vm.SearchText = "no-matching-server";
                    await Task.Delay(100);
                    if (vm.SelectedServer is null || vm.ServerView.Cast<ServerProfile>().Any()) throw new InvalidOperationException("Filtering changed the selected connection.");
                    vm.SearchText = ""; vm.ServersTab = 1;
                    await Task.Delay(240); window.UpdateLayout();
                    SavePreview(window, Path.Combine(artifacts, "subscriptions.png"));
                    vm.ServersTab = 0; vm.SelectedPage = 4;
                    await Task.Delay(100); window.UpdateLayout();
                    ((TextBlock)window.FindName("CoreVersionText")).BringIntoView();
                    await Task.Delay(240); window.UpdateLayout();
                    if (((TextBlock)window.FindName("ClientVersionText")).Text != HardwareIdService.ClientVersion || ((TextBlock)window.FindName("CoreVersionText")).Text != vm.CoreVersion) throw new InvalidOperationException("Version information is not bound to settings.");
                    var versionLabel = (TextBlock)window.FindName("CoreVersionText");
                    var settingsViewport = ((Grid)window.FindName("PageHost")).Children.OfType<ScrollViewer>().Single(view => view.IsVisible);
                    Rect versionBounds = versionLabel.TransformToAncestor(settingsViewport).TransformBounds(new Rect(versionLabel.RenderSize));
                    if (versionBounds.Top < 0 || versionBounds.Bottom > settingsViewport.ActualHeight + 1) throw new InvalidOperationException("Core version cannot be scrolled into view.");
                    SavePreview(window, Path.Combine(artifacts, "about.png"));
                    checks += 2;
                    vm.SelectedPage = 2; vm.DarkTheme = true; vm.UseDnsOverHttps = true;
                    vm.DnsOverHttpsUrl = "http://invalid.example/dns-query";
                    if (!vm.HasDnsError) throw new InvalidOperationException("Invalid DNS endpoint must be reported.");
                    vm.DnsOverHttpsUrl = DnsEndpoint.Default;
                    if (vm.HasDnsError) throw new InvalidOperationException("Valid DNS endpoint was rejected.");
                    await Task.Delay(240);
                    if (!Descendants<TextBox>(window).Single(box => box.Name == "DohUrlBox").IsVisible) throw new InvalidOperationException("DoH endpoint field did not appear.");
                    vm.UseDnsOverHttps = false;
                    checks += 3;
                    vm.SelectedPage = 0; vm.DarkTheme = true;
                    window.Width = 900; window.Height = 600;
                    using var busyCancellation = new CancellationTokenSource();
                    typeof(MainViewModel).GetField("_connectCancellation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(vm, busyCancellation);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsBusy))!.SetValue(vm, true);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.StatusText))!.SetValue(vm, LocalizationService.Get("Connecting"));
                    await Task.Delay(100);
                    var spinner = (ConnectionSpinner)window.FindName("ConnectionAnimation");
                    double firstAngle = spinner.Angle;
                    await Task.Delay(150);
                    if (!spinner.IsActive || !spinner.IsVisible || SystemParameters.ClientAreaAnimation && Math.Abs(spinner.Angle - firstAngle) < 1) throw new InvalidOperationException("Connection animation is not running.");
                    window.UpdateLayout();
                    ValidateButtonAlignment(window);
                    var animated = new RenderTargetBitmap((int)((FrameworkElement)window.Content).ActualWidth, (int)((FrameworkElement)window.Content).ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    animated.Render((FrameworkElement)window.Content);
                    var busyPng = new PngBitmapEncoder(); busyPng.Frames.Add(BitmapFrame.Create(animated));
                    using (FileStream shot = File.Create(Path.Combine(artifacts, "dark-connecting.png"))) busyPng.Save(shot);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsBusy))!.SetValue(vm, false);
                    typeof(MainViewModel).GetField("_connectCancellation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(vm, null);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.StatusText))!.SetValue(vm, LocalizationService.Get("Disconnected"));
                    int exits = 0; window.ExitRequested += () => exits++;
                    if (!vm.CloseToTray) throw new InvalidOperationException("Close to tray must be enabled by default.");
                    window.Close();
                    if (window.IsVisible || exits != 0) throw new InvalidOperationException("Close must hide the window without exiting.");
                    window.ShowFromTray();
                    if (!window.IsVisible) throw new InvalidOperationException("Tray restoration failed.");
                    vm.CloseToTray = false; window.Close();
                    if (exits != 1) throw new InvalidOperationException("Closing without close-to-tray must request exit.");
                    vm.CloseToTray = true; checks += 5;
                    vm.UseTun = true;
                    vm.ProcessInput = "browser.exe"; vm.AddProcessRuleCommand.Execute(null);
                    if(vm.ProcessRules.Count != 1 || !vm.CanEditProcessRules) throw new InvalidOperationException("Application rule could not be added in TUN.");
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsConnected))!.SetValue(vm, true);
                    vm.ProcessInput = "another.exe"; vm.AddProcessRuleCommand.Execute(null);
                    if(vm.ProcessRules.Count != 2 || !vm.HasPendingProcessRules || !vm.ApplyProcessRulesCommand.CanExecute(null)) throw new InvalidOperationException("Connected users cannot edit and apply the direct app list.");
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsConnected))!.SetValue(vm, false);
                    vm.UseTun = false;
                    if(vm.CanEditProcessRules || vm.AddProcessRuleCommand.CanExecute(null)) throw new InvalidOperationException("Application routing must be disabled in system proxy mode.");
                    Guid group = Guid.NewGuid();
                    ServerProfile preserved = vm.Servers[0]; preserved.SubscriptionId = group; preserved.HasCustomName = true; preserved.Name = "My custom name";
                    var providerProfile = parser.Parse($"vless://{id}@example.com:443?security=tls#Provider%20rename");
                    var apply = typeof(MainViewModel).GetMethod("ApplySubscription",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                    apply.Invoke(vm,[new SubscriptionProfile{Id=group,Url="https://example.com/test"},new[]{providerProfile}]);
                    if(!vm.Servers.Contains(preserved) || preserved.Name != "My custom name" || vm.SelectedServer != preserved)throw new InvalidOperationException("Subscription refresh lost the selection or custom name.");
                    checks += 3;
                    Guid lte = Guid.NewGuid();
                    ServerProfile lteOne = parser.Parse($"vless://{id}@one.example:443?security=tls#LTE"), lteTwo = parser.Parse($"vless://{id}@two.example:443?security=tls#LTE");
                    lteOne.SubscriptionId = lteTwo.SubscriptionId = lte;
                    vm.Servers.Add(lteOne); vm.Servers.Add(lteTwo);
                    var refresh = typeof(MainViewModel).GetMethod("RefreshServers",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                    refresh.Invoke(vm,null);
                    if(vm.ServerChoices.Cast<ServerProfile>().Count(server=>server.SubscriptionId==lte)!=1 || vm.ServerView.Cast<ServerProfile>().Count(server=>server.SubscriptionId==lte)!=1) throw new InvalidOperationException("LTE endpoints are not grouped in both selectors.");
                    vm.SelectedServer = lteTwo;
                    if(!vm.ServerChoices.Contains(lteTwo) || vm.ServerChoices.Contains(lteOne) || lteTwo.EndpointCount!=2) throw new InvalidOperationException("Grouped endpoint selection was lost.");
                    checks += 3;
                    vm.Servers.Clear(); vm.SelectedServer = null; vm.SelectedPage = 0;
                    string jsonPath=Path.Combine(directory,"ui-profile.json");
                    await File.WriteAllTextAsync(jsonPath,"{\"remarks\":\"LTE JSON\",\"outbounds\":[{\"protocol\":\"freedom\",\"tag\":\"direct\"}]}");
                    vm.ImportJsonFileCommand.Execute(new[]{jsonPath});
                    for(int i=0;i<50&&!vm.Servers.Any(server=>server.Protocol=="xray-json");i++)await Task.Delay(50);
                    var jsonProfile=vm.Servers.Single(server=>server.Protocol=="xray-json");
                    vm.SelectedPage=1;vm.SelectedServer=jsonProfile;await Task.Delay(100);window.UpdateLayout();
                    Grid jsonTile=Descendants<Grid>(window).First(grid=>grid.ContextMenu is not null&&ReferenceEquals(grid.DataContext,jsonProfile));
                    jsonTile.ContextMenu.PlacementTarget=jsonTile;jsonTile.ContextMenu.IsOpen=true;window.UpdateLayout();
                    MenuItem jsonExport=jsonTile.ContextMenu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()==LocalizationService.Get("ExportJson"));
                    if(jsonExport.Visibility!=Visibility.Visible||jsonExport.Command!=vm.ExportJsonCommand||jsonProfile.ProtocolLabel!="JSON")throw new Exception("JSON import/context menu binding failed");
                    jsonTile.ContextMenu.IsOpen=false;checks+=3;
                    vm.Servers.Clear();vm.SelectedServer=null;vm.SelectedPage=0;
                    if (vm.ToggleConnectionCommand.CanExecute(null)) throw new InvalidOperationException("Connecting without a server was allowed.");
                    await Task.Delay(240);
                    listener.Flush();
                    if (diagnostics.ToString().Length > 0) throw new InvalidOperationException("WPF binding errors: " + diagnostics);
                    checks += await ViewModelConnectionTests.RunAsync();

                    await vm.ShutdownAsync(); vm = null;
                    window.AllowClose = true; window.Close(); window = null;
                    completed.SetResult(checks + 1);
                }
                catch (Exception exception)
                {
                    if (vm is not null) { try { await vm.ShutdownAsync(); } catch { } }
                    if (window is not null) { window.AllowClose = true; window.Close(); }
                    completed.TrySetException(exception);
                }
                finally
                {
                    PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); listener.Dispose();
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                    app.Shutdown();
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }

    private static void ValidateHorizontalBounds(FrameworkElement host, double scale, int page)
    {
        foreach (Control control in Descendants<Control>(host).Where(element => element.IsVisible && element.ActualWidth > 0 && element is Button or TextBox or ComboBox or ListBox or SettingsRow))
        {
            Rect bounds = control.TransformToAncestor(host).TransformBounds(new Rect(control.RenderSize));
            if (bounds.Left < -1 || bounds.Right > host.ActualWidth + 1)
                throw new InvalidOperationException($"{control.GetType().Name} extends beyond content at {scale * 100:0}% on page {page}: {bounds}, host {host.ActualWidth}.");
        }
        ValidateButtonAlignment(Window.GetWindow(host));
    }

    private static void ValidateButtonAlignment(Window window)
    {
        foreach (GlyphIcon icon in Descendants<GlyphIcon>(window).Where(element => element.IsVisible))
        {
            DependencyObject? ancestor = VisualTreeHelper.GetParent(icon);
            while (ancestor is not null && ancestor is not Button) ancestor = VisualTreeHelper.GetParent(ancestor);
            if (ancestor is not Button button) continue;
            DrawingGroup? drawing = VisualTreeHelper.GetDrawing(icon);
            if (drawing is null || drawing.Bounds.IsEmpty) continue;
            Rect ink = icon.TransformToAncestor(button).TransformBounds(drawing.Bounds);
            if (button.Content is string && Math.Abs(ink.X + ink.Width / 2 - button.ActualWidth / 2) > 1 || Math.Abs(ink.Y + ink.Height / 2 - button.ActualHeight / 2) > 1)
                throw new InvalidOperationException($"An icon is not centered in its button: {ink}, {button.RenderSize}.");
        }
        foreach (string name in new[] { "ImportButtonContent", "ConnectionButtonContent" })
        {
            var content = (FrameworkElement)window.FindName(name);
            if (!content.IsVisible) continue;
            DependencyObject? ancestor = VisualTreeHelper.GetParent(content);
            while (ancestor is not null && ancestor is not Button) ancestor = VisualTreeHelper.GetParent(ancestor);
            var button = (Button)ancestor!;
            Rect bounds = content.TransformToAncestor(button).TransformBounds(new Rect(content.RenderSize));
            if (Math.Abs(bounds.X + bounds.Width / 2 - button.ActualWidth / 2) > 1 || Math.Abs(bounds.Y + bounds.Height / 2 - button.ActualHeight / 2) > 1)
                throw new InvalidOperationException($"{name} is not centered in its button.");
        }
        var add = (Button)window.FindName("AddSubscriptionButton");
        if (add.IsVisible)
        {
            var field = (TextBox)window.FindName("SubscriptionUrlBox");
            Rect addBounds = add.TransformToAncestor(window).TransformBounds(new Rect(add.RenderSize));
            Rect fieldBounds = field.TransformToAncestor(window).TransformBounds(new Rect(field.RenderSize));
            if (Math.Abs(addBounds.Y + addBounds.Height / 2 - fieldBounds.Y - fieldBounds.Height / 2) > 1)
                throw new InvalidOperationException("The subscription add button is not aligned with the URL field.");
        }
        var connection = (Button)window.FindName("ConnectionButton");
        var header = (FrameworkElement)window.FindName("ConnectionHeader");
        if (connection.IsVisible)
        {
            Rect bounds = connection.TransformToAncestor(header).TransformBounds(new Rect(connection.RenderSize));
            bool compact = header.ActualWidth < 420;
            if (compact ? Math.Abs(bounds.X + bounds.Width / 2 - header.ActualWidth / 2) > 1 : Math.Abs(bounds.Right - header.ActualWidth) > 1)
                throw new InvalidOperationException("The connection button must be right-aligned in a wide section and centered in a compact section.");
        }
    }

    private static void SavePreview(MainWindow window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path); png.Save(file);
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte component) { double value = component / 255d; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T result && (parent is not FrameworkElement element || element.IsVisible)) return result;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            T? child = Find<T>(VisualTreeHelper.GetChild(parent, index));
            if (child is not null) return child;
        }
        return null;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T result) yield return result;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
