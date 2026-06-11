using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DbSyncTool.Helpers;
using DbSyncTool.Models;
using DbSyncTool.Views.Dialogs;

namespace DbSyncTool.Views.Pages
{
    public partial class DataCallPage : Page
    {
        private List<DataCallView> _allItems = new();

        public DataCallPage()
        {
            InitializeComponent();
            LoadData();
        }

        private void LoadData()
        {
            var configs = ConfigStore.LoadDataCallConfigs();
            var dicts = ConfigStore.LoadDictMatchConfigs();
            var apis = ConfigStore.LoadApiConfigs();

            _allItems = configs.OrderByDescending(x => x.CreateTime).Select(c => new DataCallView
            {
                Id = c.Id,
                ConfigName = c.ConfigName,
                SyncDirection = c.SyncDirection,
                CallType = c.CallType,
                DictMatchName = dicts.FirstOrDefault(d => d.Id == c.DictMatchConfigId)?.ConfigName ?? "—",
                ApiName = c.ApiConfigId.HasValue
                    ? (apis.FirstOrDefault(a => a.Id == c.ApiConfigId.Value)?.ApiName ?? "—")
                    : "—",
                CreateTime = c.CreateTime
            }).ToList();

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var dirFilter = (SearchDirection.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
            var typeFilter = (SearchCallType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
            var keyword = SearchName.Text.Trim();

            var filtered = _allItems.Where(x =>
            {
                if (dirFilter == "SQL Server → MySQL"         && x.SyncDirection != SyncDirection.SqlServerToMySQL)     return false;
                if (dirFilter == "MySQL → SQL Server"         && x.SyncDirection != SyncDirection.MySQLToSqlServer)     return false;
                if (dirFilter == "SQL Server → SQL Server"    && x.SyncDirection != SyncDirection.SqlServerToSqlServer) return false;
                if (dirFilter == "MySQL → MySQL"              && x.SyncDirection != SyncDirection.MySQLToMySQL)         return false;
                if (typeFilter == "直接插入" && x.CallType != CallType.Direct) return false;
                if (typeFilter == "API处理" && x.CallType != CallType.Api) return false;
                if (!string.IsNullOrEmpty(keyword) && !x.ConfigName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }).ToList();

            DataCallGrid.ItemsSource = filtered;
            TotalCountText.Text = $"共 {filtered.Count} 条";
        }

        private void Search_Click(object sender, RoutedEventArgs e) => ApplyFilter();
        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            SearchDirection.SelectedIndex = 0;
            SearchCallType.SelectedIndex = 0;
            SearchName.Text = "";
            ApplyFilter();
        }

        private void AddNew_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new DataCallDialog(null);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true) LoadData();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var configs = ConfigStore.LoadDataCallConfigs();
                var config = configs.FirstOrDefault(c => c.Id == id);
                if (config == null) return;
                var dlg = new DataCallDialog(config);
                dlg.Owner = Window.GetWindow(this);
                if (dlg.ShowDialog() == true) LoadData();
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var tasks = ConfigStore.LoadTaskConfigs();
                if (tasks.Any(t => t.DataCallConfigId == id))
                {
                    MessageBox.Show("该配置已被任务配置使用，不可删除！", "删除失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (MessageBox.Show("确认删除该数据调用配置？", "确认删除",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                {
                    var configs = ConfigStore.LoadDataCallConfigs();
                    configs.RemoveAll(c => c.Id == id);
                    ConfigStore.SaveDataCallConfigs(configs);
                    LoadData();
                }
            }
        }
    }

    public class DataCallView
    {
        public int Id { get; set; }
        public string ConfigName { get; set; } = "";
        public SyncDirection SyncDirection { get; set; }
        public CallType CallType { get; set; }
        public string DictMatchName { get; set; } = "";
        public string ApiName { get; set; } = "";
        public DateTime CreateTime { get; set; }
        public string SyncDirectionDisplay => SyncDirection switch
        {
            SyncDirection.SqlServerToMySQL     => "SQL Server → MySQL",
            SyncDirection.MySQLToSqlServer     => "MySQL → SQL Server",
            SyncDirection.SqlServerToSqlServer => "SQL Server → SQL Server",
            SyncDirection.MySQLToMySQL         => "MySQL → MySQL",
            _                                  => "未知"
        };
        public string CallTypeDisplay => CallType == CallType.Direct ? "直接插入" : "API处理";
        public Brush CallTypeBg => CallType == CallType.Direct
            ? new SolidColorBrush(Color.FromRgb(0xEB, 0xF8, 0xFF))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xED, 0xD5));
        public Brush CallTypeFg => CallType == CallType.Direct
            ? new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0))
            : new SolidColorBrush(Color.FromRgb(0xC0, 0x53, 0x21));
    }
}
