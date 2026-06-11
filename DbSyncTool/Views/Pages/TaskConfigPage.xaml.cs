using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DbSyncTool.Helpers;
using DbSyncTool.Models;
using DbSyncTool.Views.Dialogs;
using TaskStatus = DbSyncTool.Models.TaskStatus;

namespace DbSyncTool.Views.Pages
{
    public partial class TaskConfigPage : Page
    {
        private List<TaskView> _allItems = new();

        public TaskConfigPage()
        {
            InitializeComponent();
            LoadData();
        }

        private void LoadData()
        {
            var tasks = ConfigStore.LoadTaskConfigs();
            var procs = ConfigStore.LoadProcConfigs();

            _allItems = tasks.OrderByDescending(x => x.CreateTime).Select(t => new TaskView
            {
                Id = t.Id,
                TaskName = t.TaskName,
                DataCallName = t.ProcConfigId.HasValue
                    ? procs.FirstOrDefault(p => p.Id == t.ProcConfigId.Value)?.ConfigName ?? "—"
                    : "—",
                TriggerType   = t.TriggerType,
                ScheduledTime = t.ScheduledTime,
                Status        = t.Status,
                CreateTime    = t.CreateTime
            }).ToList();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var keyword = SearchName.Text.Trim();
            var filtered = string.IsNullOrEmpty(keyword)
                ? _allItems
                : _allItems.Where(x => x.TaskName.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            TaskGrid.ItemsSource = filtered;
            TotalCountText.Text = $"共 {filtered.Count} 条";
        }

        private void Search_Click(object sender, RoutedEventArgs e) => ApplyFilter();
        private void Reset_Click(object sender, RoutedEventArgs e) { SearchName.Text = ""; ApplyFilter(); }

        private void AddNew_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new TaskConfigDialog(null);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true) LoadData();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var tasks = ConfigStore.LoadTaskConfigs();
                var task = tasks.FirstOrDefault(t => t.Id == id);
                if (task == null) return;
                if (task.Status == TaskStatus.Running)
                {
                    MessageBox.Show("任务正在执行中，无法编辑！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var dlg = new TaskConfigDialog(task);
                dlg.Owner = Window.GetWindow(this);
                if (dlg.ShowDialog() == true) LoadData();
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                if (MessageBox.Show("确认删除该任务配置？\n删除后不可恢复，历史执行记录也将一并清除。",
                    "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                {
                    var tasks = ConfigStore.LoadTaskConfigs();
                    tasks.RemoveAll(t => t.Id == id);
                    ConfigStore.SaveTaskConfigs(tasks);
                    LoadData();
                }
            }
        }

        private async void RunNow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                if (MessageBox.Show("确认立即执行该任务？", "确认执行",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

                var tasks = ConfigStore.LoadTaskConfigs();
                var task = tasks.FirstOrDefault(t => t.Id == id);
                if (task == null) return;

                MessageBox.Show("任务已提交执行，请在【定时任务同步模块】查看执行进度。","已提交", MessageBoxButton.OK, MessageBoxImage.Information);
                LogService.WriteInfo($"手动触发任务: {task.TaskName}", "TaskConfig");
            }
        }
    }

    public class TaskView
    {
        public int Id { get; set; }
        public string TaskName { get; set; } = "";
        public string DataCallName { get; set; } = "";
        public TriggerType TriggerType { get; set; }
        public DateTime? ScheduledTime { get; set; }
        public TaskStatus Status { get; set; }
        public DateTime CreateTime { get; set; }

        public string TriggerTypeDisplay => TriggerType switch
        {
            TriggerType.Once => "一次性",
            TriggerType.Interval => "循环",
            TriggerType.Cron => "定时",
            _ => ""
        };

        public string NextRunDisplay => ScheduledTime.HasValue
            ? ScheduledTime.Value.ToString("yyyy-MM-dd HH:mm")
            : "—";

        public string StatusDisplay => Status switch
        {
            TaskStatus.Pending => "待执行",
            TaskStatus.Running => "执行中",
            TaskStatus.Success => "已成功",
            TaskStatus.Failed => "失败",
            TaskStatus.Disabled => "已禁用",
            _ => ""
        };

        public Brush StatusBg => Status switch
        {
            TaskStatus.Running => new SolidColorBrush(Color.FromRgb(0xBE, 0xE3, 0xF8)),
            TaskStatus.Success => new SolidColorBrush(Color.FromRgb(0xC6, 0xF6, 0xD5)),
            TaskStatus.Failed => new SolidColorBrush(Color.FromRgb(0xFE, 0xD7, 0xD7)),
            _ => new SolidColorBrush(Color.FromRgb(0xEE, 0xE8, 0xFF))
        };

        public Brush StatusFg => Status switch
        {
            TaskStatus.Running => new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0)),
            TaskStatus.Success => new SolidColorBrush(Color.FromRgb(0x27, 0x6D, 0x49)),
            TaskStatus.Failed => new SolidColorBrush(Color.FromRgb(0x9B, 0x25, 0x25)),
            _ => new SolidColorBrush(Color.FromRgb(0x55, 0x3C, 0x9A))
        };
    }
}
