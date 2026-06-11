using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DbSyncTool.Helpers;
using DbSyncTool.Models;
using DbSyncTool.Views.Dialogs;

namespace DbSyncTool.Views.Pages
{
    public partial class DictMatchPage : Page
    {
        private List<DictMatchConfigView> _allItems = new();

        public DictMatchPage()
        {
            InitializeComponent();
            LoadData();
        }

        private void LoadData()
        {
            var configs = ConfigStore.LoadDictMatchConfigs();
            var dbConfigs = ConfigStore.LoadDbConfigs();

            _allItems = configs.OrderByDescending(x => x.CreateTime).Select(c => new DictMatchConfigView
            {
                Id = c.Id,
                ConfigName = c.ConfigName,
                SourceDbName = dbConfigs.FirstOrDefault(d => d.Id == c.SourceDbConfigId)?.DisplayName ?? c.SourceDbName,
                TargetDbName = dbConfigs.FirstOrDefault(d => d.Id == c.TargetDbConfigId)?.DisplayName ?? c.TargetDbName,
                SourceDbType = dbConfigs.FirstOrDefault(d => d.Id == c.SourceDbConfigId)?.DbType ?? DbType.SqlServer,
                TargetDbType = dbConfigs.FirstOrDefault(d => d.Id == c.TargetDbConfigId)?.DbType ?? DbType.MySQL,
                DetailsCount = c.Details.Count,
                CreateTime = c.CreateTime
            }).ToList();

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var keyword = SearchName.Text.Trim();
            var filtered = string.IsNullOrEmpty(keyword)
                ? _allItems
                : _allItems.Where(x => x.ConfigName.Contains(keyword, System.StringComparison.OrdinalIgnoreCase)).ToList();

            MatchGrid.ItemsSource = filtered;
            TotalCountText.Text = $"共 {filtered.Count} 条";
        }

        private void Search_Click(object sender, RoutedEventArgs e) => ApplyFilter();
        private void Reset_Click(object sender, RoutedEventArgs e) { SearchName.Text = ""; ApplyFilter(); }

        private void AddNew_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new DictMatchDialog(null);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true) LoadData();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var configs = ConfigStore.LoadDictMatchConfigs();
                var config = configs.FirstOrDefault(c => c.Id == id);
                if (config == null) return;
                var dlg = new DictMatchDialog(config);
                dlg.Owner = Window.GetWindow(this);
                if (dlg.ShowDialog() == true) LoadData();
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var dataCallConfigs = ConfigStore.LoadDataCallConfigs();
                if (dataCallConfigs.Any(c => c.DictMatchConfigId == id))
                {
                    MessageBox.Show("该映射配置已被数据调用配置使用，不可删除！", "删除失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var result = MessageBox.Show("确认删除该字典匹配配置？\n删除后不可恢复。",
                    "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.OK)
                {
                    var configs = ConfigStore.LoadDictMatchConfigs();
                    configs.RemoveAll(c => c.Id == id);
                    ConfigStore.SaveDictMatchConfigs(configs);
                    LoadData();
                }
            }
        }
    }

    public class DictMatchConfigView
    {
        public int Id { get; set; }
        public string ConfigName { get; set; } = "";
        public string SourceDbName { get; set; } = "";
        public string TargetDbName { get; set; } = "";
        public DbType SourceDbType { get; set; }
        public DbType TargetDbType { get; set; }
        public int DetailsCount { get; set; }
        public System.DateTime CreateTime { get; set; }
        public string SyncDirectionDisplay => $"{(SourceDbType == DbType.SqlServer ? "SQL Server" : "MySQL")} → {(TargetDbType == DbType.SqlServer ? "SQL Server" : "MySQL")}";
    }
}
