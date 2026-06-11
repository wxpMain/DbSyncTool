using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DbSyncTool.Helpers;
using DbSyncTool.Models;
using TaskStatus = DbSyncTool.Models.TaskStatus;

namespace DbSyncTool.Views.Dialogs
{
    public partial class TaskConfigDialog : Window
    {
        private readonly TaskConfig? _editingConfig;

        public TaskConfigDialog(TaskConfig? config)
        {
            InitializeComponent();
            _editingConfig = config;
            DialogTitle.Text = config != null ? "编辑任务" : "新增任务";
            LoadDropdowns();
            if (config != null) FillForm(config);
        }

        private void LoadDropdowns()
        {
            // 改为加载存储过程配置
            CmbDataCall.Items.Clear();
            foreach (var p in ConfigStore.LoadProcConfigs())
                CmbDataCall.Items.Add(new ComboBoxItem
                {
                    Content = $"{p.ConfigName}  ({p.TargetTable})",
                    Tag = p.Id
                });
        }

        private void FillForm(TaskConfig t)
        {
            TxtName.Text = t.TaskName;
            TxtDesc.Text = t.TaskDesc ?? "";

            // 优先用 ProcConfigId
            var selectedId = t.ProcConfigId ?? t.DataCallConfigId;
            for (int i = 0; i < CmbDataCall.Items.Count; i++)
                if ((CmbDataCall.Items[i] as ComboBoxItem)?.Tag is int id && id == selectedId)
                    CmbDataCall.SelectedIndex = i;

            CmbTrigger.SelectedIndex = t.TriggerType switch
            {
                TriggerType.Once => 0, TriggerType.Interval => 1, TriggerType.Cron => 2, _ => 0
            };
            var scheduled = t.ScheduledTime ?? DateTime.Now.AddDays(1);
            DpSchedule.SelectedDate = scheduled.Date;
            TxtScheduleTime.Text = scheduled.ToString("HH:mm:ss");
            TxtInterval.Text = t.IntervalValue?.ToString() ?? "5";
            TxtMaxRows.Text = t.MaxRows.ToString();
            TxtRetry.Text = t.RetryCount.ToString();
            if (!string.IsNullOrWhiteSpace(t.CronExpression))
                TxtCron.Text = t.CronExpression;
        }

        private void Trigger_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (LblSchedule == null) return;
            var tag = (CmbTrigger.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            bool isOnce     = tag == "Once";
            bool isInterval = tag == "Interval";
            bool isCron     = tag == "Cron";

            LblSchedule.Visibility = isOnce     ? Visibility.Visible : Visibility.Collapsed;
            PnlSchedule.Visibility = isOnce     ? Visibility.Visible : Visibility.Collapsed;
            LblInterval.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
            PnlInterval.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
            LblCron.Visibility     = isCron     ? Visibility.Visible : Visibility.Collapsed;
            PnlCron.Visibility     = isCron     ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CronPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string cron)
                TxtCron.Text = cron;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtName.Text))  { ShowErr("请填写任务名称"); return; }
            if (CmbDataCall.SelectedItem == null)          { ShowErr("请选择存储过程配置"); return; }

            var procId = (int)(CmbDataCall.SelectedItem as ComboBoxItem)!.Tag;
            var triggerTag = (CmbTrigger.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var trigger = triggerTag switch
            {
                "Once"     => TriggerType.Once,
                "Interval" => TriggerType.Interval,
                _          => TriggerType.Cron
            };

            if (trigger == TriggerType.Cron && string.IsNullOrWhiteSpace(TxtCron.Text))
            { ShowErr("请填写Cron表达式"); return; }

            var newTask = new TaskConfig
            {
                Id               = _editingConfig?.Id ?? 0,
                TaskName         = TxtName.Text.Trim(),
                TaskDesc         = TxtDesc.Text.Trim().NullIfEmpty(),
                ProcConfigId     = procId,
                DataCallConfigId = 0,
                TriggerType      = trigger,
                ScheduledTime    = trigger == TriggerType.Interval ? null : CombineDateTime(),
                CronExpression   = trigger == TriggerType.Cron ? TxtCron.Text.Trim() : null,
                IntervalValue    = trigger == TriggerType.Interval
                    ? (int.TryParse(TxtInterval.Text, out var iv) ? iv : 5) : null,
                IntervalUnit     = trigger == TriggerType.Interval
                    ? (CmbIntervalUnit.SelectedItem as ComboBoxItem)?.Content?.ToString() : null,
                MaxRows          = int.TryParse(TxtMaxRows.Text, out var mr) ? mr : 10000,
                RetryCount       = int.TryParse(TxtRetry.Text, out var rc) ? rc : 3,
                Status           = TaskStatus.Pending,
                CreateTime       = _editingConfig?.CreateTime ?? DateTime.Now,
                UpdateTime       = DateTime.Now
            };

            var tasks = ConfigStore.LoadTaskConfigs();
            if (tasks.Any(t => t.TaskName == newTask.TaskName && t.Id != newTask.Id))
            { ShowErr("任务名称已存在"); return; }

            if (_editingConfig != null)
            { var idx = tasks.FindIndex(t => t.Id == newTask.Id); if (idx >= 0) tasks[idx] = newTask; }
            else
            { newTask.Id = ConfigStore.NextTaskId(); tasks.Insert(0, newTask); }

            ConfigStore.SaveTaskConfigs(tasks);
            LogService.WriteInfo($"保存任务配置: {newTask.TaskName}", "TaskConfig");
            DialogResult = true;
            Close();
        }

        private DateTime? CombineDateTime()
        {
            if (DpSchedule.SelectedDate == null) return null;
            var date = DpSchedule.SelectedDate.Value.Date;
            if (TimeSpan.TryParseExact(TxtScheduleTime.Text, @"hh\:mm\:ss", null, out var ts))
                return date + ts;
            return date;
        }

        private void TxtTime_PreviewInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
            => e.Handled = !System.Text.RegularExpressions.Regex.IsMatch(e.Text, @"[\d:]");

        private void TxtTime_LostFocus(object sender, RoutedEventArgs e)
        {
            var txt = TxtScheduleTime.Text.Trim();
            if (!TimeSpan.TryParseExact(txt, @"hh\:mm\:ss", null, out var ts))
            {
                if (txt.Length == 6 && int.TryParse(txt, out _))
                    txt = $"{txt[..2]}:{txt[2..4]}:{txt[4..6]}";
                if (!TimeSpan.TryParseExact(txt, @"hh\:mm\:ss", null, out ts))
                    txt = "00:00:00";
            }
            TxtScheduleTime.Text = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        private void DpSchedule_Changed(object sender, SelectionChangedEventArgs e) { }
        private void ShowErr(string msg) => MessageBox.Show(msg, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
        private void Close_Click(object sender, RoutedEventArgs e)  { DialogResult = false; Close(); }
    }
}
