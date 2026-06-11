using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DbSyncTool.Helpers;
using DbSyncTool.Models;
using DbSyncTool.Views.Dialogs;

namespace DbSyncTool.Views.Pages
{
    public partial class DbConfigPage : Page
    {
        private List<DbConfig> _allConfigs = new();
        private List<DbConfig> _filteredConfigs = new();
        private int _currentPage = 0;
        private const int PageSize = 20;

        public DbConfigPage()
        {
            InitializeComponent();
            LoadData();
        }

        private void LoadData()
        {
            _allConfigs = ConfigStore.LoadDbConfigs();
            _allConfigs = _allConfigs.OrderByDescending(x => x.CreateTime).ToList();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var dbTypeFilter = (SearchDbType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
            var serverFilter = SearchServer.Text.Trim();
            var dbNameFilter = SearchDbName.Text.Trim();

            _filteredConfigs = _allConfigs.Where(c =>
            {
                if (dbTypeFilter == "SQL Server" && c.DbType != Models.DbType.SqlServer) return false;
                if (dbTypeFilter == "MySQL" && c.DbType != Models.DbType.MySQL) return false;
                if (!string.IsNullOrEmpty(serverFilter) && !c.ServerAddress.Contains(serverFilter, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrEmpty(dbNameFilter) && !c.DatabaseName.Contains(dbNameFilter, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }).ToList();

            _currentPage = 0;
            RefreshGrid();
        }

        private void RefreshGrid()
        {
            var total = _filteredConfigs.Count;
            var totalPages = Math.Max(1, (total + PageSize - 1) / PageSize);
            if (_currentPage >= totalPages) _currentPage = totalPages - 1;

            var page = _filteredConfigs.Skip(_currentPage * PageSize).Take(PageSize).ToList();
            ConfigGrid.ItemsSource = page;
            TotalCountText.Text = $"共 {total} 条";
            PageText.Text = $"第 {_currentPage + 1} 页 / 共 {totalPages} 页";
        }

        private void Search_Click(object sender, RoutedEventArgs e) => ApplyFilter();
        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            SearchDbType.SelectedIndex = 0;
            SearchServer.Text = "";
            SearchDbName.Text = "";
            ApplyFilter();
        }

        private void AddNew_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new DbConfigDialog(null);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true) LoadData();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var config = _allConfigs.FirstOrDefault(c => c.Id == id);
                if (config == null) return;
                var dlg = new DbConfigDialog(config);
                dlg.Owner = Window.GetWindow(this);
                if (dlg.ShowDialog() == true) LoadData();
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var config = _allConfigs.FirstOrDefault(c => c.Id == id);
                if (config == null) return;

                // 检查引用
                var dictConfigs = ConfigStore.LoadDictMatchConfigs();
                var referenced = dictConfigs.Any(d => d.SourceDbConfigId == id || d.TargetDbConfigId == id);
                if (!referenced)
                {
                    var dataCalls = ConfigStore.LoadDataCallConfigs();
                    // DataCallConfig doesn't directly ref DbConfig in this simplified model
                }

                if (referenced)
                {
                    MessageBox.Show("该配置已被字典匹配模块使用，不可删除！",
                        "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = MessageBox.Show($"确认删除数据库配置「{config.ConfigName}」？\n删除后不可恢复。",
                    "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.OK)
                {
                    _allConfigs.RemoveAll(c => c.Id == id);
                    ConfigStore.SaveDbConfigs(_allConfigs);
                    LogService.WriteInfo($"删除数据库配置: {config.ConfigName}", "DbConfig");
                    LoadData();
                }
            }
        }

        private void FirstPage_Click(object sender, RoutedEventArgs e) { _currentPage = 0; RefreshGrid(); }
        private void PrevPage_Click(object sender, RoutedEventArgs e) { if (_currentPage > 0) { _currentPage--; RefreshGrid(); } }
        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            var totalPages = (_filteredConfigs.Count + PageSize - 1) / PageSize;
            if (_currentPage < totalPages - 1) { _currentPage++; RefreshGrid(); }
        }
        private void LastPage_Click(object sender, RoutedEventArgs e)
        {
            _currentPage = Math.Max(0, (_filteredConfigs.Count + PageSize - 1) / PageSize - 1);
            RefreshGrid();
        }
    }
}
