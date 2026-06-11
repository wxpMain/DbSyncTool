#pragma warning disable CA1416 // 仅在 Windows 上支持
using System.Windows;

namespace DbSyncTool.Helpers
{
    public class TrayHelper : IDisposable
    {
        private readonly Window _mainWindow;
        private System.Drawing.Icon? _icon;
        private readonly System.Threading.Thread _thread;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _disposed;

        // 在独立的STA线程上运行NotifyIcon，避免WPF命名空间冲突
        public TrayHelper(Window mainWindow)
        {
            _mainWindow = mainWindow;

            _thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    _icon = string.IsNullOrEmpty(exePath)
                        ? null
                        : System.Drawing.Icon.ExtractAssociatedIcon(exePath);

                    _notifyIcon = new System.Windows.Forms.NotifyIcon
                    {
                        Text    = "MES 中间库同步工具",
                        Icon    = _icon ?? System.Drawing.SystemIcons.Application,
                        Visible = true
                    };

                    _notifyIcon.DoubleClick += (s, e) =>
                        mainWindow.Dispatcher.Invoke(() =>
                        {
                            mainWindow.Show();
                            mainWindow.WindowState = WindowState.Maximized;
                            mainWindow.Activate();
                        });

                    var menu = new System.Windows.Forms.ContextMenuStrip();
                    menu.Items.Add("显示主窗口", null, (s, e) =>
                        mainWindow.Dispatcher.Invoke(() =>
                        {
                            mainWindow.Show();
                            mainWindow.WindowState = WindowState.Maximized;
                            mainWindow.Activate();
                        }));
                    menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                    menu.Items.Add("退出程序", null, (s, e) =>
                        mainWindow.Dispatcher.Invoke(() =>
                        {
                            _notifyIcon!.Visible = false;
                            System.Windows.Application.Current.Shutdown();
                        }));
                    _notifyIcon.ContextMenuStrip = menu;

                    System.Windows.Forms.Application.Run();
                }
                catch (Exception ex)
                {
                    LogService.WriteError("托盘初始化失败", ex, null, "TrayHelper");
                }
            });
            _thread.SetApartmentState(System.Threading.ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            _icon?.Dispose();
        }
    }
}
