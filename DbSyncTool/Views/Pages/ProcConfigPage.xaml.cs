using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DbSyncTool.Helpers;
using DbSyncTool.Models;
using DbSyncTool.Views.Dialogs;
using DbSyncTool.Views.Pages;

namespace DbSyncTool.Views.Pages
{
    public partial class ProcConfigPage : Page
    {
        private List<ProcView> _allItems = new();

        public ProcConfigPage()
        {
            InitializeComponent();
            LoadData();
        }

        private void LoadData()
        {
            var configs = ConfigStore.LoadProcConfigs();
            _allItems = configs.OrderByDescending(x => x.UpdateTime).Select(c => new ProcView
            {
                Id = c.Id,
                ConfigName = c.ConfigName,
                SourceDbName = c.SourceDbConfig?.DisplayName ?? $"DB#{c.SourceDbConfigId}",
                TargetDbName = c.OutputMode == "Api"? (c.ApiConfig?.ApiName ?? $"API#{c.ApiConfigId}")    : (c.TargetDbConfig?.DisplayName ?? $"DB#{c.TargetDbConfigId}"),
                TargetTable = c.TargetTable,
                SyncModeDisplay = c.SyncModeDisplay,
                MappingCount = c.FieldMappings.Count,
                UpdateTime = c.UpdateTime
            }).ToList();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var keyword = SearchName.Text.Trim();
            var filtered = string.IsNullOrEmpty(keyword)
                ? _allItems
                : _allItems.Where(x => x.ConfigName.Contains(keyword,
                    System.StringComparison.OrdinalIgnoreCase)).ToList();
            ProcGrid.ItemsSource = filtered;
            TotalCountText.Text = $"共 {filtered.Count} 条";
        }

        private void Search_Click(object sender, RoutedEventArgs e) => ApplyFilter();
        private void Reset_Click(object sender, RoutedEventArgs e) { SearchName.Text = ""; ApplyFilter(); }

        private void AddNew_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ProcConfigDialog(null) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true) LoadData();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var config = ConfigStore.LoadProcConfigs().FirstOrDefault(c => c.Id == id);
                if (config == null) return;
                var dlg = new ProcConfigDialog(config) { Owner = Window.GetWindow(this) };
                if (dlg.ShowDialog() == true) LoadData();
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                // 检查是否被任务引用
                var tasks = ConfigStore.LoadTaskConfigs();
                if (tasks.Any(t => t.ProcConfigId == id))
                {
                    MessageBox.Show("该存储过程配置已被任务引用，不可删除！", "删除失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (MessageBox.Show("确认删除该存储过程配置？", "确认删除",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                {
                    var list = ConfigStore.LoadProcConfigs();
                    list.RemoveAll(c => c.Id == id);
                    ConfigStore.SaveProcConfigs(list);
                    LoadData();
                }
            }
        }
    }

    public class ProcView
    {
        public int Id { get; set; }
        public string ConfigName { get; set; } = "";
        public string SourceDbName { get; set; } = "";
        public string TargetDbName { get; set; } = "";
        public string TargetTable { get; set; } = "";
        public string SyncModeDisplay { get; set; } = "";
        public int MappingCount { get; set; }
        public System.DateTime UpdateTime { get; set; }
        public ApiConfig? ApiConfig { get; set; }
    }
}
