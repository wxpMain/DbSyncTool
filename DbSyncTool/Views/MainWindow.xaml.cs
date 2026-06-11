using System.Windows;
using System.Windows.Controls;
using DbSyncTool.Views.Pages;
namespace DbSyncTool.Views
{
    public partial class MainWindow : Window
    {
        private Helpers.TrayHelper? _tray;

        // 缓存页面实例，避免重复创建导致调度器被重置
        private readonly Dictionary<string, Page> _pageCache = new();

        public MainWindow()
        {
            InitializeComponent();
            MenuListBox.SelectedIndex = 0;
            NavigateTo("DbConfig");
            ChkAutoStart.IsChecked = Helpers.StartupHelper.IsStartupEnabled();
            _tray = new Helpers.TrayHelper(this);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        private void MenuListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MenuListBox.SelectedItem is ListBoxItem item && item.Tag is string tag)
            {
                NavigateTo(tag);
                UpdateBreadcrumb(tag);
            }
        }

        private void NavigateTo(string page)
        {
            // ScheduledSyncPage 必须缓存，防止重复创建导致调度器状态丢失
            // LogView 不缓存，每次进入刷新内容
            if (page == "LogView")
            {
                MainFrame.Navigate(new LogViewPage());
                return;
            }

            if (!_pageCache.TryGetValue(page, out var cached))
            {
                cached = page switch
                {
                    "DbConfig" => new DbConfigPage(),
                    "DictMatch" => new DictMatchPage(),
                    "Proc" => new ProcConfigPage(),
                    "ApiConfig" => new ApiConfigPage(),
                    "TaskConfig" => new TaskConfigPage(),
                    "ScheduledSync" => new ScheduledSyncPage(),
                    _ => new DbConfigPage()
                };
                _pageCache[page] = cached;
            }

            MainFrame.Navigate(cached);
        }

        private void UpdateBreadcrumb(string tag)
        {
            var name = tag switch
            {
                "DbConfig" => "数据库配置",
                "DictMatch" => "字典匹配",
                "Proc" => "存储过程",
                "ApiConfig" => "API调用",
                "DataCall" => "数据调用配置",
                "TaskConfig" => "任务配置",
                "ScheduledSync" => "定时任务同步",
                "LogView" => "日志查看",
                _ => ""
            };
            BreadcrumbText.Text = $"主页 > {name}";
        }

        public void SetStatus(string text) => StatusText.Text = text;

        private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
        {
            bool enable = ChkAutoStart.IsChecked == true;
            bool ok = Helpers.StartupHelper.SetStartup(enable);
            if (ok)
                StatusText.Text = enable ? "已设置开机自启" : "已取消开机自启";
            else
                StatusText.Text = "设置开机自启失败，请检查权限";
        }

        public void NavigateToLogView(string? taskNameFilter = null)
        {
            MenuListBox.SelectedIndex = 5;
            var page = new LogViewPage();
            if (!string.IsNullOrEmpty(taskNameFilter))
                page.SetKeywordFilter(taskNameFilter);
            MainFrame.Navigate(page);
            UpdateBreadcrumb("LogView");
        }
    }
}