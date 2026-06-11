using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DbSyncTool.Helpers;
using DbSyncTool.Models;
using DbSyncTool.Views.Dialogs;

namespace DbSyncTool.Views.Pages
{
    public partial class ApiConfigPage : Page
    {
        private List<ApiConfigView> _allItems = new();

        public ApiConfigPage()
        {
            InitializeComponent();
            LoadData();
        }

        private void LoadData()
        {
            var configs = ConfigStore.LoadApiConfigs();
            _allItems = configs.OrderByDescending(x => x.CreateTime)
                .Select(c => new ApiConfigView
                {
                    Id = c.Id,
                    ApiName = c.ApiName,
                    Method = c.Method.ToString(),
                    Url = c.Url,
                    Timeout = c.Timeout,
                    IsVerified = c.IsVerified,
                    CreateTime = c.CreateTime
                }).ToList();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var keyword = SearchName.Text.Trim();
            var filtered = string.IsNullOrEmpty(keyword)
                ? _allItems
                : _allItems.Where(x => x.ApiName.Contains(keyword, System.StringComparison.OrdinalIgnoreCase)).ToList();
            ApiGrid.ItemsSource = filtered;
            TotalCountText.Text = $"共 {filtered.Count} 条";
        }

        private void Search_Click(object sender, RoutedEventArgs e) => ApplyFilter();
        private void Reset_Click(object sender, RoutedEventArgs e) { SearchName.Text = ""; ApplyFilter(); }

        private void AddNew_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ApiConfigDialog(null);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true) LoadData();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var configs = ConfigStore.LoadApiConfigs();
                var config = configs.FirstOrDefault(c => c.Id == id);
                if (config == null) return;
                var dlg = new ApiConfigDialog(config);
                dlg.Owner = Window.GetWindow(this);
                if (dlg.ShowDialog() == true) LoadData();
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var dataCallConfigs = ConfigStore.LoadDataCallConfigs();
                if (dataCallConfigs.Any(c => c.ApiConfigId == id))
                {
                    MessageBox.Show("该API配置已被数据调用配置使用，不可删除！", "删除失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var result = MessageBox.Show("确认删除该API配置？删除后不可恢复。",
                    "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.OK)
                {
                    var configs = ConfigStore.LoadApiConfigs();
                    configs.RemoveAll(c => c.Id == id);
                    ConfigStore.SaveApiConfigs(configs);
                    LoadData();
                }
            }
        }
    }

    public class ApiConfigView
    {
        public int Id { get; set; }
        public string ApiName { get; set; } = "";
        public string Method { get; set; } = "";
        public string Url { get; set; } = "";
        public int Timeout { get; set; }
        public bool IsVerified { get; set; }
        public System.DateTime CreateTime { get; set; }
        public string IsVerifiedDisplay => IsVerified ? "✔ 已验证" : "未验证";
    }
}
