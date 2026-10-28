using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using XrayMetro.ViewModels;
using XrayMetro.Views;

namespace XrayMetro.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _connect;
    private readonly MainViewModel _viewModel;
    private readonly MainWindow _window;
    private readonly Icon _icon;
    private bool _disposed;

    public TrayService(MainWindow window, MainViewModel viewModel, Action exit)
    {
        _window = window; _viewModel = viewModel;
        _icon = CreateIcon();
        _menu = new ContextMenuStrip { ShowImageMargin = false, DropShadowEnabled = false, Font = new Font("Segoe UI", 9), Renderer = new FlatMenuRenderer(viewModel) };
        _menu.Items.Add(LocalizationService.Get("Open"), null, (_, _) => OnUi(window.ShowFromTray));
        _connect = new ToolStripMenuItem(LocalizationService.Get("Connect"), null, (_, _) => OnUi(() =>
        {
            if (viewModel.ToggleConnectionCommand.CanExecute(null)) viewModel.ToggleConnectionCommand.Execute(null);
        }));
        _menu.Items.Add(_connect);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(LocalizationService.Get("Exit"), null, (_, _) => OnUi(exit));
        _notify = new NotifyIcon { Icon = _icon, Text = "Win10ray", ContextMenuStrip = _menu, Visible = true };
        _notify.MouseClick += (_, args) => { if (args.Button == MouseButtons.Left) OnUi(window.ShowFromTray); };
        viewModel.PropertyChanged += OnPropertyChanged;
        UpdateState();
    }
    private void OnUi(Action action) => _window.Dispatcher.BeginInvoke(action);
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.IsConnected) or nameof(MainViewModel.IsBusy) or nameof(MainViewModel.SelectedServer) or nameof(MainViewModel.StatusText)) UpdateState();
    }
    private void UpdateState()
    {
        _connect.Text = LocalizationService.Get(_viewModel.IsConnected ? "Disconnect" : "Connect");
        _connect.Enabled = !_viewModel.IsBusy && (_viewModel.IsConnected || _viewModel.SelectedServer is not null);
        _notify.Text = "Win10ray | " + _viewModel.StatusText;
    }
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        graphics.TranslateTransform(1, 4); graphics.ScaleTransform(0.12f, 0.12f);
        using var blue = new SolidBrush(Color.FromArgb(0, 132, 226));
        using var cyan = new SolidBrush(Color.FromArgb(32, 185, 244));
        using var pale = new SolidBrush(Color.FromArgb(114, 228, 250));
        graphics.FillPolygon(blue, [new PointF(0, 30), new(88, 8), new(88, 82), new(0, 88)]);
        graphics.FillPolygon(blue, [new PointF(0, 97), new(88, 92), new(88, 174), new(0, 152)]);
        graphics.FillPolygon(cyan, [new PointF(97, 6), new(160, -5), new(160, 49), new(97, 82)]);
        graphics.FillPolygon(cyan, [new PointF(97, 94), new(160, 126), new(160, 188), new(97, 176)]);
        graphics.FillPolygon(pale, [new PointF(104, 86), new(250, 10), new(250, 50)]);
        graphics.FillPolygon(pale, [new PointF(106, 90), new(250, 67), new(250, 105)]);
        graphics.FillPolygon(pale, [new PointF(104, 95), new(250, 121), new(250, 161)]);
        IntPtr handle = bitmap.GetHicon();
        try { using Icon borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private sealed class FlatMenuRenderer(MainViewModel viewModel) : ToolStripSystemRenderer
    {
        private Color Surface => viewModel.DarkTheme ? Color.FromArgb(43, 43, 43) : Color.FromArgb(242, 242, 242);
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Surface);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.Item.Selected && e.Item.Enabled ? ColorTranslator.FromHtml(viewModel.AccentColor) : Surface);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color accent = ColorTranslator.FromHtml(viewModel.AccentColor);
            e.TextColor = !e.Item.Enabled ? Color.Gray : e.Item.Selected
                ? (accent.R * 299 + accent.G * 587 + accent.B * 114 > 165000 ? Color.Black : Color.White)
                : viewModel.DarkTheme ? Color.White : Color.Black;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(viewModel.DarkTheme ? Color.FromArgb(92, 92, 92) : Color.FromArgb(150, 150, 150));
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(viewModel.DarkTheme ? Color.FromArgb(92, 92, 92) : Color.FromArgb(150, 150, 150));
            e.Graphics.DrawLine(pen, 8, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2);
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= OnPropertyChanged;
        _notify.Visible = false; _notify.Dispose(); _menu.Dispose(); _icon.Dispose();
    }
}
