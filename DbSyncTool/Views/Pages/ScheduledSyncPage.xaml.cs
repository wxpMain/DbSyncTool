using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DbSyncTool.Helpers;
using DbSyncTool.Models;
using Quartz;
using Quartz.Impl;
using TaskStatus = DbSyncTool.Models.TaskStatus;

namespace DbSyncTool.Views.Pages
{
    /// <summary>
    /// 定时任务同步页面 — 管理 Quartz.NET 调度器
    /// </summary>
    public partial class ScheduledSyncPage : Page
    {
        // ===================== 静态共享调度器 =====================
        private static IScheduler? _scheduler;
        private static bool _schedulerRunning = false;

        // ===================== 进度更新 =====================
        public static event Action<SyncProgressEvent>? ProgressChanged;

        /// <summary>供类外部（SyncJob/SyncExecutor）触发进度事件的唯一入口</summary>
        public static void RaiseProgressChanged(SyncProgressEvent evt)
            => ProgressChanged?.Invoke(evt);

        // ===================== 实例状态 =====================
        public ObservableCollection<TaskViewModel> TaskItems { get; } = new();
        private List<TaskConfig> _allTasks = new();
        private List<DataCallConfig> _dataCalls = new();
        private int _todayRuns = 0;

        private readonly DispatcherTimer _uiTimer;
        // 正在运行的任务取消令牌：taskId → CancellationTokenSource
        // 静态字典，页面切换后任务仍可继续
        private static readonly Dictionary<int, CancellationTokenSource> _runningCts = new();
        private static readonly Dictionary<int, SyncProgress> _runningProgress = new();

        public ScheduledSyncPage()
        {
            InitializeComponent();
            DataContext = this;

            // 订阅进度事件
            ProgressChanged += OnProgressChanged;

            // UI 刷新定时器 (每2秒)
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _uiTimer.Tick += (s, e) => RefreshSchedulerStatus();
            _uiTimer.Start();

            LoadTasks();
            UpdateSchedulerStatusUI();
        }

        // ===================== 数据加载 =====================
        private void LoadTasks()
        {
            _allTasks = ConfigStore.LoadTaskConfigs();
            _dataCalls = ConfigStore.LoadDataCallConfigs();
            var procs = ConfigStore.LoadProcConfigs();

            bool activeOnly = ChkActiveOnly.IsChecked == true;
            var filtered = activeOnly
                ? _allTasks.Where(t => t.Status != TaskStatus.Disabled).ToList()
                : _allTasks;

            TaskItems.Clear();
            foreach (var t in filtered)
            {
                string procName = t.ProcConfigId.HasValue
                    ? procs.FirstOrDefault(p => p.Id == t.ProcConfigId.Value)?.ConfigName
                        ?? $"存储过程#{t.ProcConfigId}"
                    : _dataCalls.FirstOrDefault(d => d.Id == t.DataCallConfigId)?.ConfigName
                        ?? $"配置#{t.DataCallConfigId}";

                TaskItems.Add(new TaskViewModel(t, procName, GetNextFireTime(t)));
            }

            // 汇总卡片
            TxtTotalTasks.Text = _allTasks.Count.ToString();
            TxtRunningTasks.Text = _allTasks.Count(t => t.Status == TaskStatus.Running).ToString();
            TxtDisabledTasks.Text = _allTasks.Count(t => t.Status == TaskStatus.Disabled).ToString();
            TxtTodayRuns.Text = _todayRuns.ToString();
        }

        private string GetNextFireTime(TaskConfig t)
        {
            if (t.Status == TaskStatus.Disabled) return "已禁用";
            if (!_schedulerRunning) return "调度器未启动";

            // 从 Quartz 查询
            if (_scheduler != null)
            {
                var key = new TriggerKey($"trigger_{t.Id}", "default");
                try
                {
                    var trigger = _scheduler.GetTrigger(key).Result;
                    if (trigger != null)
                    {
                        var next = trigger.GetNextFireTimeUtc();
                        if (next.HasValue)
                            return next.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
                    }
                }
                catch { }
            }
            return t.TriggerType switch
            {
                TriggerType.Once => t.ScheduledTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—",
                TriggerType.Interval => "启动后计算",
                TriggerType.Cron => "启动后计算",
                _ => "—"
            };
        }

        // ===================== 调度器控制 =====================
        private async void BtnStartScheduler_Click(object sender, RoutedEventArgs e)
        {
            if (_schedulerRunning) return;
            try
            {
                BtnStartScheduler.IsEnabled = false;
                await StartSchedulerAsync();
                UpdateSchedulerStatusUI();
                LoadTasks();
                LogService.WriteInfo("调度器已启动", "Scheduler");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动调度器失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                BtnStartScheduler.IsEnabled = true;
            }
        }

        private async void BtnStopScheduler_Click(object sender, RoutedEventArgs e)
        {
            if (!_schedulerRunning) return;
            try
            {
                BtnStopScheduler.IsEnabled = false;
                await StopSchedulerAsync();
                UpdateSchedulerStatusUI();
                LoadTasks();
                LogService.WriteInfo("调度器已停止", "Scheduler");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止调度器失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                BtnStopScheduler.IsEnabled = true;
            }
        }

        private static async Task StartSchedulerAsync()
        {
            if (_scheduler == null)
            {
                var factory = new StdSchedulerFactory();
                _scheduler = await factory.GetScheduler();
            }

            if (!_scheduler.IsStarted)
                await _scheduler.Start();

            _schedulerRunning = true;

            // 注册所有启用的任务
            var tasks = ConfigStore.LoadTaskConfigs()
                .Where(t => t.Status != TaskStatus.Disabled).ToList();

            foreach (var task in tasks)
                await ScheduleTask(task);
        }

        private static async Task StopSchedulerAsync()
        {
            if (_scheduler != null && _scheduler.IsStarted)
                await _scheduler.Standby();
            _schedulerRunning = false;
        }

        private static async Task ScheduleTask(TaskConfig task)
        {
            if (_scheduler == null) return;

            var jobKey = new JobKey($"job_{task.Id}", "default");
            var triggerKey = new TriggerKey($"trigger_{task.Id}", "default");

            // 清除旧的
            await _scheduler.DeleteJob(jobKey);

            // 构建 Job
            var job = JobBuilder.Create<SyncJob>()
                .WithIdentity(jobKey)
                .UsingJobData("taskId", task.Id)
                .StoreDurably()
                .Build();

            // 构建 Trigger
            ITrigger trigger;
            switch (task.TriggerType)
            {
                case TriggerType.Once:
                    var fireAt = task.ScheduledTime ?? DateTime.Now.AddSeconds(5);
                    if (fireAt < DateTime.Now) return; // 已过期
                    trigger = TriggerBuilder.Create()
                        .WithIdentity(triggerKey)
                        .StartAt(new DateTimeOffset(fireAt))
                        .Build();
                    break;

                case TriggerType.Interval:
                    int intervalSeconds = GetIntervalSeconds(task);
                    trigger = TriggerBuilder.Create()
                        .WithIdentity(triggerKey)
                        .StartNow()
                        .WithSimpleSchedule(x => x
                            .WithIntervalInSeconds(intervalSeconds)
                            .RepeatForever())
                        .Build();
                    break;

                case TriggerType.Cron:
                    // 5位Cron（分 时 日 月 周）转Quartz 6位（秒 分 时 日 月 周）
                    // Quartz要求：日和周不能同时为*，其中一个必须是?
                    var rawCron = task.CronExpression ?? task.IntervalUnit ?? "0 0 * * *";
                    string cronExpr;
                    if (rawCron.Split(' ').Length == 5)
                    {
                        var parts = rawCron.Split(' ');
                        // parts: 分 时 日 月 周
                        // 如果日是*且周是*，把周改成?
                        if (parts[2] == "*" && parts[4] == "*")
                            parts[4] = "?";
                        // 如果日是具体值，把周改成?
                        else if (parts[2] != "*" && parts[2] != "?")
                            parts[4] = "?";
                        // 如果周是具体值，把日改成?
                        else if (parts[4] != "*" && parts[4] != "?")
                            parts[2] = "?";
                        cronExpr = "0 " + string.Join(" ", parts);
                    }
                    else
                    {
                        cronExpr = rawCron;
                    }
                    trigger = TriggerBuilder.Create()
                        .WithIdentity(triggerKey)
                        .WithCronSchedule(cronExpr)
                        .Build();
                    break;

                default:
                    return;
            }

            await _scheduler.ScheduleJob(job, trigger);
        }

        private static int GetIntervalSeconds(TaskConfig task)
        {
            int val = task.IntervalValue ?? 1;
            return task.IntervalUnit switch
            {
                "秒" => val,
                "分钟" => val * 60,
                "小时" => val * 3600,
                "天" => val * 86400,
                _ => val * 60
            };
        }

        private static async Task UnscheduleTask(int taskId)
        {
            if (_scheduler == null) return;
            var jobKey = new JobKey($"job_{taskId}", "default");
            await _scheduler.DeleteJob(jobKey);
        }

        // ===================== 任务操作 =====================
        private async void BtnRunNow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int taskId) return;
            var task = _allTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;

            var result = MessageBox.Show($"立即执行任务「{task.TaskName}」?",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            await RunTaskNow(task);
        }

        private void BtnEnable_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int taskId) return;
            SetTaskEnabled(taskId, true);
        }

        private void BtnDisable_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int taskId) return;
            SetTaskEnabled(taskId, false);
        }

        private void SetTaskEnabled(int taskId, bool enabled)
        {
            var all = ConfigStore.LoadTaskConfigs();
            var task = all.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;

            task.Status = enabled ? TaskStatus.Pending : TaskStatus.Disabled;
            task.UpdateTime = DateTime.Now;
            ConfigStore.SaveTaskConfigs(all);

            // 更新调度器
            if (_schedulerRunning)
            {
                if (enabled)
                    _ = ScheduleTask(task);
                else
                    _ = UnscheduleTask(taskId);
            }

            LogService.WriteInfo($"任务[{task.TaskName}]已{(enabled ? "启用" : "禁用")}", "Scheduler");
            LoadTasks();
        }

        private void BtnViewLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Page 嵌在 Frame 里，需要向上找到 MainWindow
                var win = Window.GetWindow(this);
                if (win is MainWindow mw)
                {
                    // 若能拿到任务ID，带过去做关键词过滤
                    string? taskName = null;
                    if (sender is Button btn && btn.Tag is int taskId)
                        taskName = _allTasks.FirstOrDefault(t => t.Id == taskId)?.TaskName;

                    mw.NavigateToLogView(taskName);
                }
                else
                {
                    MessageBox.Show(
                        $"无法找到主窗口（当前窗口类型：{win?.GetType().Name ?? "null"}），请直接点击左侧菜单「日志查看」。",
                        "跳转失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"跳转日志页失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===================== 立即执行 (手动触发) =====================
        private async Task RunTaskNow(TaskConfig task)
        {
            var vm = TaskItems.FirstOrDefault(v => v.Task.Id == task.Id);
            if (vm != null)
            {
                vm.Task.Status = TaskStatus.Running;
                vm.IsRunning = true;
                vm.Refresh();
            }

            ResetProgressUI(task.TaskName);
            PbProgress.Foreground = new SolidColorBrush(Color.FromRgb(49, 130, 206));

            // 创建取消令牌
            var cts = new CancellationTokenSource();
            _runningCts[task.Id] = cts;

            try
            {
                await SyncExecutor.RunAsync(task,
                    progress =>
                    {
                        // 保存到全局状态
                        _runningProgress[task.Id] = progress;
                        // 安全更新UI（页面可能已切换）
                        try
                        {
                            Dispatcher.Invoke(() => UpdateProgressUI(progress));
                        }
                        catch { /* 页面已销毁，忽略UI更新，任务继续运行 */ }
                    },
                    cts.Token);

                // 更新状态
                var all = ConfigStore.LoadTaskConfigs();
                var t = all.FirstOrDefault(x => x.Id == task.Id);
                if (t != null)
                {
                    t.Status = TaskStatus.Success;
                    t.LastRunTime = DateTime.Now;
                    t.LastRunStatus = "成功";
                    ConfigStore.SaveTaskConfigs(all);
                }
                _todayRuns++;
            }
            catch (Exception ex)
            {
                LogService.WriteError($"任务[{task.TaskName}]执行失败", ex, task.Id.ToString(), "SyncExecutor");

                // 把错误信息同步显示到进度面板
                Dispatcher.Invoke(() =>
                {
                    var sb = new StringBuilder(TxtProgressLog.Text);
                    sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] ❌ 执行失败: {ex.Message}");
                    if (ex.InnerException != null)
                        sb.AppendLine($"[{DateTime.Now:HH:mm:ss}]    原因: {ex.InnerException.Message}");
                    TxtProgressLog.Text = sb.ToString();
                    SvProgressLog.ScrollToBottom();
                    TxtProgressTaskName.Text = $"执行失败: {task.TaskName}";
                    PbProgress.Foreground = new SolidColorBrush(Color.FromRgb(229, 62, 62));
                });

                var all = ConfigStore.LoadTaskConfigs();
                var t = all.FirstOrDefault(x => x.Id == task.Id);
                if (t != null)
                {
                    t.Status = TaskStatus.Failed;
                    t.LastRunTime = DateTime.Now;
                    t.LastRunStatus = $"失败: {ex.Message}";
                    ConfigStore.SaveTaskConfigs(all);
                }
                MessageBox.Show($"任务执行失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _runningCts.Remove(task.Id);
                if (vm != null) { vm.IsRunning = false; vm.Refresh(); }
                LoadTasks();
            }
        }

        // ===================== 停止任务 =====================
        private void BtnStopTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int id) return;
            if (_runningCts.TryGetValue(id, out var cts))
            {
                cts.Cancel();
                var vm = TaskItems.FirstOrDefault(v => v.Task.Id == id);
                if (vm != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        var sb = new StringBuilder(TxtProgressLog.Text);
                        sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] ⏹ 用户手动停止任务");
                        TxtProgressLog.Text = sb.ToString();
                        SvProgressLog.ScrollToBottom();
                        TxtProgressTaskName.Text = $"已停止: {vm.Task.TaskName}";
                        PbProgress.Foreground = new SolidColorBrush(Color.FromRgb(237, 137, 54));
                    });
                }
            }
        }

        // ===================== 进度 UI =====================
        private void ResetProgressUI(string taskName)
        {
            TxtProgressTaskName.Text = $"正在执行: {taskName}";
            PbProgress.Value = 0;
            TxtProgressPct.Text = "0%";
            TxtProgressLog.Text = "";
            TxtTotal.Text = "—";
            TxtSuccess.Text = "—";
            TxtFailed.Text = "—";
            TxtElapsed.Text = "—";
        }

        private void UpdateProgressUI(SyncProgress p)
        {
            if (p.Total > 0)
            {
                double pct = (double)p.Processed / p.Total * 100;
                PbProgress.Value = pct;
                TxtProgressPct.Text = $"{pct:F1}%";
            }

            TxtTotal.Text = p.Total.ToString();
            TxtSuccess.Text = p.Success.ToString();
            TxtFailed.Text = p.Failed.ToString();
            TxtElapsed.Text = FormatElapsed(p.Elapsed);

            if (!string.IsNullOrEmpty(p.Status))
            {
                var sb = new StringBuilder(TxtProgressLog.Text);
                // 错误/警告行加前缀标记
                bool isError = p.Status.StartsWith("⚠") || p.Status.StartsWith("❌");
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {p.Status}");
                TxtProgressLog.Text = sb.ToString();
                SvProgressLog.ScrollToBottom();

                // 有错误时进度条变红，正常时保持蓝色
                if (isError)
                    PbProgress.Foreground = new SolidColorBrush(Color.FromRgb(229, 62, 62));
            }
        }

        private string FormatElapsed(TimeSpan ts)
        {
            if (ts.TotalHours >= 1) return $"{ts.TotalHours:F1}h";
            if (ts.TotalMinutes >= 1) return $"{ts.TotalMinutes:F1}m";
            return $"{ts.TotalSeconds:F1}s";
        }

        private void OnProgressChanged(SyncProgressEvent evt)
        {
            Dispatcher.InvokeAsync(() =>
            {
                TxtProgressTaskName.Text = $"正在执行: {evt.TaskName}";
                UpdateProgressUI(evt.Progress);
            });
        }

        private void BtnClearProgress_Click(object sender, RoutedEventArgs e)
        {
            TxtProgressTaskName.Text = "无正在运行的任务";
            PbProgress.Value = 0;
            TxtProgressPct.Text = "0%";
            TxtProgressLog.Text = "";
            TxtTotal.Text = "—";
            TxtSuccess.Text = "—";
            TxtFailed.Text = "—";
            TxtElapsed.Text = "—";
        }

        // ===================== 其他 UI 事件 =====================
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadTasks();

        private void ChkActiveOnly_Changed(object sender, RoutedEventArgs e) => LoadTasks();

        private void TaskGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void RefreshSchedulerStatus()
        {
            // 更新运行数量
            TxtRunningTasks.Text = _allTasks.Count(t => t.Status == TaskStatus.Running).ToString();
        }

        private void UpdateSchedulerStatusUI()
        {
            if (_schedulerRunning)
            {
                TxtSchedulerStatus.Text = "调度器状态：运行中 ✓";
                TxtSchedulerStatus.Foreground = new SolidColorBrush(Color.FromRgb(56, 161, 105));
                BtnStartScheduler.IsEnabled = false;
                BtnStopScheduler.IsEnabled = true;
            }
            else
            {
                TxtSchedulerStatus.Text = "调度器状态：已停止";
                TxtSchedulerStatus.Foreground = new SolidColorBrush(Color.FromRgb(113, 128, 150));
                BtnStartScheduler.IsEnabled = true;
                BtnStopScheduler.IsEnabled = false;
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            ProgressChanged -= OnProgressChanged;
            _uiTimer.Stop();
        }
    }

    // ===================== ViewModel =====================
    public class TaskViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public TaskConfig Task { get; }
        public string DataCallName { get; }
        public string NextFireDisplay { get; private set; }

        public string StatusText => Task.Status switch
        {
            TaskStatus.Pending => "待运行",
            TaskStatus.Running => "运行中",
            TaskStatus.Success => "已完成",
            TaskStatus.Failed => "已失败",
            TaskStatus.Disabled => "已禁用",
            _ => "未知"
        };

        public Brush StatusBrush => Task.Status switch
        {
            TaskStatus.Pending => new SolidColorBrush(Color.FromRgb(160, 174, 192)),
            TaskStatus.Running => new SolidColorBrush(Color.FromRgb(56, 161, 105)),
            TaskStatus.Success => new SolidColorBrush(Color.FromRgb(49, 130, 206)),
            TaskStatus.Failed => new SolidColorBrush(Color.FromRgb(229, 62, 62)),
            TaskStatus.Disabled => new SolidColorBrush(Color.FromRgb(113, 128, 150)),
            _ => Brushes.Gray
        };

        public string TriggerParam => Task.TriggerType switch
        {
            TriggerType.Once => Task.ScheduledTime?.ToString("yyyy-MM-dd HH:mm") ?? "—",
            TriggerType.Interval => $"每 {Task.IntervalValue} {Task.IntervalUnit}",
            TriggerType.Cron => Task.CronExpression ?? Task.IntervalUnit ?? "—",
            _ => "—"
        };

        public bool IsRunning { get; set; } = false;

        public Visibility RunButtonVisibility =>
            IsRunning ? Visibility.Collapsed : Visibility.Visible;

        public Visibility StopButtonVisibility =>
            IsRunning ? Visibility.Visible : Visibility.Collapsed;

        public Visibility EnableButtonVisibility =>
            Task.Status == TaskStatus.Disabled ? Visibility.Visible : Visibility.Collapsed;

        public Visibility DisableButtonVisibility =>
            Task.Status != TaskStatus.Disabled ? Visibility.Visible : Visibility.Collapsed;

        public TaskViewModel(TaskConfig task, string dataCallName, string nextFire)
        {
            Task = task;
            DataCallName = dataCallName;
            NextFireDisplay = nextFire;
        }

        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    // ===================== 进度事件 =====================
    public class SyncProgressEvent
    {
        public string TaskName { get; set; } = "";
        public SyncProgress Progress { get; set; } = new();
    }

    // ===================== Quartz Job =====================
    [DisallowConcurrentExecution]
    public class SyncJob : IJob
    {
        public async System.Threading.Tasks.Task Execute(IJobExecutionContext context)
        {
            int taskId = context.JobDetail.JobDataMap.GetInt("taskId");

            var all = ConfigStore.LoadTaskConfigs();
            var task = all.FirstOrDefault(t => t.Id == taskId);
            if (task == null || task.Status == TaskStatus.Disabled) return;

            task.Status = TaskStatus.Running;
            ConfigStore.SaveTaskConfigs(all);

            LogService.WriteInfo($"任务[{task.TaskName}]开始执行", "SyncJob");

            try
            {
                await SyncExecutor.RunAsync(task, progress =>
                {
                    ScheduledSyncPage.RaiseProgressChanged(new SyncProgressEvent
                    {
                        TaskName = task.TaskName,
                        Progress = progress
                    });
                });

                task.Status = TaskStatus.Success;
                task.LastRunTime = DateTime.Now;
                task.LastRunStatus = "成功";
                LogService.WriteInfo($"任务[{task.TaskName}]执行完成", "SyncJob");
            }
            catch (Exception ex)
            {
                task.Status = TaskStatus.Failed;
                task.LastRunTime = DateTime.Now;
                task.LastRunStatus = $"失败: {ex.Message}";
                LogService.WriteError($"任务[{task.TaskName}]执行失败", ex, taskId.ToString(), "SyncJob");
            }
            finally
            {
                // 一次性任务完成后设为禁用防止重复
                if (task.TriggerType == TriggerType.Once)
                    task.Status = TaskStatus.Disabled;
                ConfigStore.SaveTaskConfigs(all);
            }
        }
    }

    // ===================== 同步执行器 =====================
    /// <summary>
    /// 核心同步执行器 - 根据 DataCallConfig 决定 Direct Insert 或 API Processing
    /// </summary>
    public static class SyncExecutor
    {
        // 全局复用 HttpClient，避免每次创建销毁带来的性能开销
        private static readonly System.Net.Http.HttpClient _sharedHttp = new(
            new System.Net.Http.SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 20,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            });
        public static async System.Threading.Tasks.Task RunAsync(
            TaskConfig task, Action<SyncProgress> onProgress,
            CancellationToken cancellationToken = default)
        {
            // 存储过程模式
            if (task.ProcConfigId.HasValue)
            {
                var procs = ConfigStore.LoadProcConfigs();
                var proc = procs.FirstOrDefault(p => p.Id == task.ProcConfigId.Value)
                    ?? throw new InvalidOperationException($"找不到存储过程配置 Id={task.ProcConfigId}");
                var procDbCfgs = ConfigStore.LoadDbConfigs();
                var procSrcDb = procDbCfgs.FirstOrDefault(d => d.Id == proc.SourceDbConfigId)
                    ?? throw new InvalidOperationException("找不到源数据库配置");

                // API模式不需要目标数据库
                if (proc.OutputMode == "Api")
                {
                    await RunProcApiAsync(proc, procSrcDb, task, onProgress, cancellationToken);
                    return;
                }

                var procDstDb = procDbCfgs.FirstOrDefault(d => d.Id == proc.TargetDbConfigId)
                    ?? throw new InvalidOperationException("找不到目标数据库配置");
                await RunProcAsync(proc, procSrcDb, procDstDb, task, onProgress, cancellationToken);
                return;
            }

            var dataCalls = ConfigStore.LoadDataCallConfigs();
            var dc = dataCalls.FirstOrDefault(d => d.Id == task.DataCallConfigId)
                ?? throw new InvalidOperationException($"找不到数据调用配置 Id={task.DataCallConfigId}");

            var dbConfigs = ConfigStore.LoadDbConfigs();

            // 脚本模式：只需目标库，不走字段映射
            if (dc.CallType == CallType.Script)
            {
                // 脚本模式下目标库从 DictMatchConfig 取，也可单独配置
                var dictCfgs = ConfigStore.LoadDictMatchConfigs();
                var dm = dictCfgs.FirstOrDefault(d => d.Id == dc.DictMatchConfigId);
                DbConfig? dstDbForScript = dm != null
                    ? dbConfigs.FirstOrDefault(d => d.Id == dm.TargetDbConfigId)
                    : null;
                if (dstDbForScript == null && dbConfigs.Count > 0)
                    dstDbForScript = dbConfigs.First(); // fallback 取第一个
                if (dstDbForScript == null)
                    throw new InvalidOperationException("找不到目标数据库配置，请在字典匹配配置中选择目标库");
                await RunScriptAsync(dc, dstDbForScript, task, onProgress);
                return;
            }

            var dictConfigs = ConfigStore.LoadDictMatchConfigs();
            var dictMatch = dictConfigs.FirstOrDefault(d => d.Id == dc.DictMatchConfigId)
                ?? throw new InvalidOperationException($"找不到字典匹配配置 Id={dc.DictMatchConfigId}");

            // 确定源/目标
            DbConfig srcDb = dbConfigs.FirstOrDefault(d => d.Id == dictMatch.SourceDbConfigId)
                ?? throw new InvalidOperationException("找不到源数据库配置");
            DbConfig dstDb = dbConfigs.FirstOrDefault(d => d.Id == dictMatch.TargetDbConfigId)
                ?? throw new InvalidOperationException("找不到目标数据库配置");

            if (dc.CallType == CallType.Direct)
                await RunDirectAsync(dc, dictMatch, srcDb, dstDb, task, onProgress);
            else
                await RunApiAsync(dc, dictMatch, srcDb, task, onProgress);
        }

        private static async System.Threading.Tasks.Task RunDirectAsync(
            DataCallConfig dc, DictMatchConfig dictMatch,
            DbConfig srcDb, DbConfig dstDb,
            TaskConfig task, Action<SyncProgress> onProgress,
            CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new SyncProgress();

            var firstDetail = dictMatch.Details.FirstOrDefault();
            if (firstDetail == null)
                throw new InvalidOperationException("字典匹配配置中没有字段映射");

            string dstTable = firstDetail.TargetTableName;
            var srcCols = dictMatch.Details.Select(d => d.SourceFieldName).ToList();
            var dstCols = dictMatch.Details.Select(d => d.TargetFieldName).ToList();

            string whereClause = "";
            if (!string.IsNullOrWhiteSpace(dc.WhereCondition))
                whereClause = dc.WhereCondition;
            else if (dc.SyncMode == SyncMode.Incremental && !string.IsNullOrEmpty(dc.IncrementalField)
                     && task.LastRunTime.HasValue)
                whereClause = $"{dc.IncrementalField} > '{task.LastRunTime.Value:yyyy-MM-dd HH:mm:ss}'";

            bool useCustomSql = !string.IsNullOrWhiteSpace(dictMatch.CustomSql);
            string srcTable = firstDetail.SourceTableName;

            int pageSize = dc.BatchSize > 0 ? dc.BatchSize : 500;
            if (task.MaxRows > 0) pageSize = Math.Min(pageSize, task.MaxRows);

            // 根据源/目标库类型动态选择 Helper
            bool isSqlSrc = srcDb.DbType == DbType.SqlServer;
            bool isSqlDst = dstDb.DbType == DbType.SqlServer;

            async Task<int> GetCount() => isSqlSrc
                ? (useCustomSql
                    ? await new DataAccess.SqlServerHelper(srcDb).GetCountByQueryAsync(dictMatch.CustomSql!, whereClause)
                    : await new DataAccess.SqlServerHelper(srcDb).GetCountByTableAsync(srcTable, whereClause))
                : (useCustomSql
                    ? await new DataAccess.MySqlHelper(srcDb).GetCountByQueryAsync(dictMatch.CustomSql!, whereClause)
                    : await new DataAccess.MySqlHelper(srcDb).GetCountByTableAsync(srcTable, whereClause));

            async Task<List<Dictionary<string, object?>>> GetPage(int page) => isSqlSrc
                ? (useCustomSql
                    ? await new DataAccess.SqlServerHelper(srcDb).QueryPageByQueryAsync(dictMatch.CustomSql!, srcCols, whereClause, page, pageSize)
                    : await new DataAccess.SqlServerHelper(srcDb).QueryPageByTableAsync(srcTable, srcCols, whereClause, page, pageSize))
                : (useCustomSql
                    ? await new DataAccess.MySqlHelper(srcDb).QueryPageByQueryAsync(dictMatch.CustomSql!, srcCols, whereClause, page, pageSize)
                    : await new DataAccess.MySqlHelper(srcDb).QueryPageByTableAsync(srcTable, srcCols, whereClause, page, pageSize));

            // Upsert 模式：有唯一键时逐行查重
            bool useUpsert = !string.IsNullOrWhiteSpace(dc.UpsertKeyFields);
            var upsertKeys = useUpsert
                ? dc.UpsertKeyFields!.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToList()
                : new List<string>();

            int skipCount = 0; // 数据一致跳过数

            progress.Total = await GetCount();
            onProgress(progress);
            if (progress.Total == 0) return;

            {
                int page = 0;
                while (true)
                {
                    var rows = await GetPage(page);
                    if (rows.Count == 0) break;

                    var mapped = MapColumns(rows, srcCols, dstCols);

                    if (!useUpsert)
                    {
                        // 普通模式：直接批量写入
                        try
                        {
                            if (isSqlDst)
                                await new DataAccess.SqlServerHelper(dstDb).BulkInsertAsync(dstTable, dstCols, mapped);
                            else
                                await new DataAccess.MySqlHelper(dstDb).BatchInsertAsync(dstTable, dstCols, mapped);
                            progress.Success += mapped.Count;
                        }
                        catch (Exception ex)
                        {
                            progress.Failed += mapped.Count;
                            progress.Status = $"⚠ 第{page + 1}批写入失败: {ex.Message}";
                            LogService.WriteError($"批次写入失败 page={page}", ex, task.Id.ToString(), "SyncExecutor");
                        }
                    }
                    else
                    {
                        // Upsert 模式：逐行查重决定 INSERT / UPDATE / SKIP
                        var toInsert = new List<Dictionary<string, object?>>();
                        var toUpdate = new List<Dictionary<string, object?>>();

                        foreach (var row in mapped)
                        {
                            try
                            {
                                Dictionary<string, object?>? existing = isSqlDst
                                    ? await new DataAccess.SqlServerHelper(dstDb).FindRowAsync(dstTable, upsertKeys, row)
                                    : await new DataAccess.MySqlHelper(dstDb).FindRowAsync(dstTable, upsertKeys, row);

                                if (existing == null)
                                {
                                    toInsert.Add(row);
                                }
                                else if (RowsAreDifferent(existing, row, dstCols))
                                {
                                    toUpdate.Add(row);
                                }
                                else
                                {
                                    skipCount++;
                                    var keyVal = string.Join(",", upsertKeys.Select(k => row.TryGetValue(k, out var v) ? v : "?"));
                                    LogService.WriteInfo($"[Upsert] 数据一致跳过: {dstTable} 键={keyVal}", "SyncExecutor");
                                }
                            }
                            catch (Exception ex)
                            {
                                progress.Failed++;
                                LogService.WriteError($"[Upsert] 查重失败", ex, task.Id.ToString(), "SyncExecutor");
                            }
                        }

                        // 批量 INSERT
                        if (toInsert.Count > 0)
                        {
                            try
                            {
                                if (isSqlDst)
                                    await new DataAccess.SqlServerHelper(dstDb).BulkInsertAsync(dstTable, dstCols, toInsert);
                                else
                                    await new DataAccess.MySqlHelper(dstDb).BatchInsertAsync(dstTable, dstCols, toInsert);
                                progress.Success += toInsert.Count;
                            }
                            catch (Exception ex)
                            {
                                progress.Failed += toInsert.Count;
                                LogService.WriteError($"[Upsert] 批量INSERT失败", ex, task.Id.ToString(), "SyncExecutor");
                            }
                        }

                        // 逐行 UPDATE
                        foreach (var row in toUpdate)
                        {
                            try
                            {
                                if (isSqlDst)
                                    await new DataAccess.SqlServerHelper(dstDb).UpdateRowAsync(dstTable, upsertKeys, dstCols, row);
                                else
                                    await new DataAccess.MySqlHelper(dstDb).UpdateRowAsync(dstTable, upsertKeys, dstCols, row);
                                progress.Success++;
                            }
                            catch (Exception ex)
                            {
                                progress.Failed++;
                                LogService.WriteError($"[Upsert] UPDATE失败", ex, task.Id.ToString(), "SyncExecutor");
                            }
                        }

                        progress.Status = $"INSERT:{toInsert.Count} UPDATE:{toUpdate.Count} SKIP:{skipCount}";
                    }

                    progress.Processed += mapped.Count;
                    progress.Elapsed = sw.Elapsed;
                    if (!useUpsert)
                        progress.Status = $"已处理 {progress.Processed}/{progress.Total} 行";
                    onProgress(progress);
                    if (rows.Count < pageSize) break;
                    page++;
                    await System.Threading.Tasks.Task.Delay(10);
                }
            }

            progress.Status = useUpsert
                ? $"Upsert完成 成功{progress.Success}行 失败{progress.Failed}行 跳过{skipCount}行"
                : $"同步完成 成功{progress.Success}行 失败{progress.Failed}行";
            progress.Elapsed = sw.Elapsed;
            onProgress(progress);

            // 同步后执行SQL（PostSyncSql）
            if (!string.IsNullOrWhiteSpace(dc.PostSyncSql))
            {
                progress.Status = "执行同步后SQL...";
                onProgress(progress);
                try
                {
                    var statements = dc.PostSyncSql
                        .Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToList();

                    foreach (var stmt in statements)
                    {
                        if (isSqlDst)
                            await new DataAccess.SqlServerHelper(dstDb).ExecuteNonQueryAsync(stmt);
                        else
                            await new DataAccess.MySqlHelper(dstDb).ExecuteNonQueryAsync(stmt);

                        progress.Status = $"已执行: {(stmt.Length > 60 ? stmt[..60] + "..." : stmt)}";
                        onProgress(progress);
                    }
                    progress.Status = "同步后SQL执行完成 ✓";
                    onProgress(progress);
                    LogService.WriteInfo($"同步后SQL执行完成，共{statements.Count}条", "SyncExecutor");
                }
                catch (Exception ex)
                {
                    progress.Status = $"❌ 同步后SQL执行失败: {ex.Message}";
                    onProgress(progress);
                    LogService.WriteError("同步后SQL执行失败", ex, task.Id.ToString(), "SyncExecutor");
                }
            }
        }

        private static List<Dictionary<string, object?>> MapColumns(
            List<Dictionary<string, object?>> rows,
            List<string> srcCols, List<string> dstCols)
        {
            var result = new List<Dictionary<string, object?>>();
            for (int i = 0; i < rows.Count; i++)
            {
                var src = rows[i];
                var dst = new Dictionary<string, object?>();
                for (int c = 0; c < srcCols.Count && c < dstCols.Count; c++)
                {
                    dst[dstCols[c]] = src.TryGetValue(srcCols[c], out var v) ? v : null;
                }
                result.Add(dst);
            }
            return result;
        }

        /// <summary>比较源行与目标库已有行是否有差异（忽略唯一键列）</summary>
        private static bool RowsAreDifferent(
            Dictionary<string, object?> existing,
            Dictionary<string, object?> incoming,
            List<string> cols)
        {
            foreach (var col in cols)
            {
                var a = existing.TryGetValue(col, out var va) ? va : null;
                var b = incoming.TryGetValue(col, out var vb) ? vb : null;
                if (a == null && b == null) continue;
                if (a == null || b == null) return true;
                if (!a.ToString()!.Equals(b.ToString(), StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // ===================== 存储过程执行模式 =====================
        private static async System.Threading.Tasks.Task RunProcAsync(
            ProcConfig proc, DbConfig srcDb, DbConfig dstDb,
            TaskConfig task, Action<SyncProgress> onProgress,
            CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new SyncProgress { Status = "存储过程模式：初始化..." };
            onProgress(progress);

            // API 模式分支
            if (proc.OutputMode == "Api")
            {
                await RunProcApiAsync(proc, srcDb, task, onProgress);
                return;
            }

            bool isSqlSrc = srcDb.DbType == DbType.SqlServer;
            bool isSqlDst = dstDb.DbType == DbType.SqlServer;

            // 构建 WHERE
            string whereClause = "";
            if (!string.IsNullOrWhiteSpace(proc.WhereCondition))
                whereClause = proc.WhereCondition;

            // 从源库按 SQL 查总数
            int total = isSqlSrc
                ? await new DataAccess.SqlServerHelper(srcDb).GetCountByQueryAsync(proc.SourceSql, whereClause)
                : await new DataAccess.MySqlHelper(srcDb).GetCountByQueryAsync(proc.SourceSql, whereClause);

            progress.Total = total;
            progress.Status = $"源库共 {total} 行，开始分批读取...";
            onProgress(progress);
            if (total == 0) { progress.Status = "源库无数据，同步完成"; onProgress(progress); return; }

            // 字段映射
            var fieldMappings = proc.FieldMappings.OrderBy(m => m.SortOrder).ToList();
            var srcCols = fieldMappings
                .Where(m => m.MappingType == "Field" && !string.IsNullOrWhiteSpace(m.SourceAlias))
                .Select(m => m.SourceAlias)
                .ToList();
            var dstCols = fieldMappings.Select(m => m.TargetField).ToList();

            // Upsert
            bool useUpsert = !string.IsNullOrWhiteSpace(proc.UpsertKeyFields);
            var upsertKeys = useUpsert
                ? proc.UpsertKeyFields!.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToList()
                : new List<string>();
            int skipCount = 0;

            int page = 0, pageSize = proc.BatchSize > 0 ? proc.BatchSize : 500;

            while (true)
            {
                // 读源库
                List<Dictionary<string, object?>> rows;
                if (isSqlSrc)
                    rows = await new DataAccess.SqlServerHelper(srcDb)
                        .QueryPageByQueryAsync(proc.SourceSql, srcCols, whereClause, page, pageSize);
                else
                    rows = await new DataAccess.MySqlHelper(srcDb)
                        .QueryPageByQueryAsync(proc.SourceSql, srcCols, whereClause, page, pageSize);

                if (rows.Count == 0) break;

                // 应用字段映射（包含固定值）
                var mapped = ApplyProcMapping(rows, fieldMappings);

                if (!useUpsert)
                {
                    try
                    {
                        if (isSqlDst)
                            await new DataAccess.SqlServerHelper(dstDb).BulkInsertAsync(proc.TargetTable, dstCols, mapped);
                        else
                            await new DataAccess.MySqlHelper(dstDb).BatchInsertAsync(proc.TargetTable, dstCols, mapped);
                        progress.Success += mapped.Count;
                    }
                    catch (Exception ex)
                    {
                        progress.Failed += mapped.Count;
                        progress.Status = $"⚠ 第{page + 1}批写入失败: {ex.Message}";
                        LogService.WriteError($"[Proc] 批次写入失败 page={page}", ex, task.Id.ToString(), "SyncExecutor");
                    }
                }
                else
                {
                    // Upsert 逐行处理
                    var toInsert = new List<Dictionary<string, object?>>();
                    var toUpdate = new List<Dictionary<string, object?>>();
                    foreach (var row in mapped)
                    {
                        try
                        {
                            Dictionary<string, object?>? existing = isSqlDst
                                ? await new DataAccess.SqlServerHelper(dstDb).FindRowAsync(proc.TargetTable, upsertKeys, row)
                                : await new DataAccess.MySqlHelper(dstDb).FindRowAsync(proc.TargetTable, upsertKeys, row);

                            if (existing == null) toInsert.Add(row);
                            else if (RowsAreDifferent(existing, row, dstCols)) toUpdate.Add(row);
                            else
                            {
                                skipCount++;
                                LogService.WriteInfo($"[Proc/Upsert] 数据一致跳过", "SyncExecutor");
                            }
                        }
                        catch (Exception ex)
                        {
                            progress.Failed++;
                            LogService.WriteError("[Proc/Upsert] 查重失败", ex, task.Id.ToString(), "SyncExecutor");
                        }
                    }
                    if (toInsert.Count > 0)
                    {
                        try
                        {
                            if (isSqlDst)
                                await new DataAccess.SqlServerHelper(dstDb).BulkInsertAsync(proc.TargetTable, dstCols, toInsert);
                            else
                                await new DataAccess.MySqlHelper(dstDb).BatchInsertAsync(proc.TargetTable, dstCols, toInsert);
                            progress.Success += toInsert.Count;
                        }
                        catch (Exception ex)
                        {
                            progress.Failed += toInsert.Count;
                            LogService.WriteError("[Proc/Upsert] INSERT失败", ex, task.Id.ToString(), "SyncExecutor");
                        }
                    }
                    foreach (var row in toUpdate)
                    {
                        try
                        {
                            if (isSqlDst)
                                await new DataAccess.SqlServerHelper(dstDb).UpdateRowAsync(proc.TargetTable, upsertKeys, dstCols, row);
                            else
                                await new DataAccess.MySqlHelper(dstDb).UpdateRowAsync(proc.TargetTable, upsertKeys, dstCols, row);
                            progress.Success++;
                        }
                        catch (Exception ex)
                        {
                            progress.Failed++;
                            LogService.WriteError("[Proc/Upsert] UPDATE失败", ex, task.Id.ToString(), "SyncExecutor");
                        }
                    }
                    progress.Status = $"INSERT:{toInsert.Count} UPDATE:{toUpdate.Count} SKIP:{skipCount}";
                }

                progress.Processed += rows.Count;
                progress.Elapsed = sw.Elapsed;
                double pct = (double)progress.Processed / progress.Total * 100;
                if (!useUpsert)
                    progress.Status = $"进度 {pct:F0}% ({progress.Processed}/{progress.Total})";
                onProgress(progress);

                if (rows.Count < pageSize) break;
                page++;
                await System.Threading.Tasks.Task.Delay(10);
            }

            // 同步后执行SQL
            if (!string.IsNullOrWhiteSpace(proc.PostSyncSql))
            {
                progress.Status = "执行同步后SQL...";
                onProgress(progress);
                try
                {
                    var stmts = proc.PostSyncSql.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    foreach (var stmt in stmts)
                    {
                        if (isSqlDst)
                            await new DataAccess.SqlServerHelper(dstDb).ExecuteNonQueryAsync(stmt);
                        else
                            await new DataAccess.MySqlHelper(dstDb).ExecuteNonQueryAsync(stmt);
                    }
                    LogService.WriteInfo($"[Proc] 同步后SQL执行完成", "SyncExecutor");
                }
                catch (Exception ex)
                {
                    progress.Status = $"❌ 同步后SQL失败: {ex.Message}";
                    onProgress(progress);
                    LogService.WriteError("[Proc] 同步后SQL失败", ex, task.Id.ToString(), "SyncExecutor");
                }
            }

            progress.Status = useUpsert
                ? $"完成 成功{progress.Success}行 失败{progress.Failed}行 跳过{skipCount}行"
                : $"完成 成功{progress.Success}行 失败{progress.Failed}行";
            progress.Elapsed = sw.Elapsed;
            onProgress(progress);
        }

        // ===================== 存储过程 API 调用模式 =====================
        private static async System.Threading.Tasks.Task RunProcApiAsync(
            ProcConfig proc, DbConfig srcDb,
            TaskConfig task, Action<SyncProgress> onProgress,
            CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new SyncProgress { Status = "API模式：初始化..." };
            onProgress(progress);

            // 取 API 配置
            var apiConfigs = ConfigStore.LoadApiConfigs();
            var api = apiConfigs.FirstOrDefault(a => a.Id == proc.ApiConfigId)
                ?? throw new InvalidOperationException($"找不到API配置 Id={proc.ApiConfigId}");

            bool isSqlSrc = srcDb.DbType == DbType.SqlServer;
            var fieldMappings = proc.FieldMappings.OrderBy(m => m.SortOrder).ToList();
            var srcCols = fieldMappings
                .Where(m => m.MappingType == "Field" && !string.IsNullOrWhiteSpace(m.SourceAlias))
                .Select(m => m.SourceAlias).ToList();

            string whereClause = proc.WhereCondition ?? "";

            // 统计总数
            int total = isSqlSrc
                ? await new DataAccess.SqlServerHelper(srcDb).GetCountByQueryAsync(proc.SourceSql, whereClause)
                : await new DataAccess.MySqlHelper(srcDb).GetCountByQueryAsync(proc.SourceSql, whereClause);

            progress.Total = total;
            progress.Status = $"源库共 {total} 行，开始调用API...";
            onProgress(progress);
            if (total == 0) { progress.Status = "源库无数据"; onProgress(progress); return; }

            // 使用全局复用的 HttpClient，每次请求单独构建请求头
            var http = _sharedHttp;
            var apiTimeout = TimeSpan.FromSeconds(api.Timeout > 0 ? api.Timeout : 30);

            // 自动登录获取Token，保存到 currentToken
            string? currentToken = null;
            if (api.EnableAutoLogin && !string.IsNullOrWhiteSpace(api.LoginUrl)
                && !string.IsNullOrWhiteSpace(api.LoginBody))
            {
                progress.Status = "自动登录获取Token...";
                onProgress(progress);
                try
                {
                    using var loginReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, api.LoginUrl);
                    loginReq.Content = new System.Net.Http.StringContent(api.LoginBody, System.Text.Encoding.UTF8, "application/json");
                    loginReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                    var loginResp = await http.SendAsync(loginReq);
                    var loginJson = await loginResp.Content.ReadAsStringAsync();
                    LogService.WriteInfo($"[ProcApi] 登录响应: HTTP {(int)loginResp.StatusCode} | {loginJson}", "SyncExecutor");

                    var jobj = Newtonsoft.Json.Linq.JObject.Parse(loginJson);
                    currentToken = jobj.SelectToken(api.TokenJsonPath ?? "response.xtoken")?.ToString();
                    if (!string.IsNullOrWhiteSpace(currentToken))
                    {
                        progress.Status = "登录成功，Token已获取";
                        onProgress(progress);
                        LogService.WriteInfo($"[ProcApi] 自动登录成功，Token已获取", "SyncExecutor");
                    }
                    else
                    {
                        progress.Status = $"⚠ 登录响应中未找到Token路径 '{api.TokenJsonPath}'";
                        onProgress(progress);
                        LogService.WriteError($"[ProcApi] 登录成功但Token路径 '{api.TokenJsonPath}' 找不到，响应：{loginJson}", null, task.Id.ToString(), "SyncExecutor");
                    }
                }
                catch (Exception ex)
                {
                    progress.Status = $"⚠ 自动登录失败: {ex.Message}";
                    onProgress(progress);
                    LogService.WriteError("[ProcApi] 自动登录失败", ex, task.Id.ToString(), "SyncExecutor");
                }
            }

            int page = 0, pageSize = proc.BatchSize > 0 ? proc.BatchSize : 500;
            bool isBatch = proc.ApiBodyMode == "Batch";
            int skipCount = 0;

            // BOM分组模式：先一次性读取所有数据再分组处理
            bool isBomBatchMode = !string.IsNullOrWhiteSpace(proc.BomGroupKey);
            if (isBomBatchMode)
            {
                // ── 目标库配置（查重/写标记复用）──────────────────────────
                var dstDbCfgs2i = ConfigStore.LoadDbConfigs();
                var dstDbId2i = proc.ApiTargetDbConfigId > 0 ? proc.ApiTargetDbConfigId : proc.TargetDbConfigId;
                var dstDbCfg2i = dstDbCfgs2i.FirstOrDefault(d => d.Id == dstDbId2i);
                bool hasDstDbi = dstDbCfg2i != null;
                bool isDstSql2i = dstDbCfg2i?.DbType == DbType.SqlServer;
                var groupKey = proc.BomGroupKey!;
                bool useUpsert = hasDstDbi && !string.IsNullOrWhiteSpace(proc.ApiUpsertTable);

                // ── 流式分页读取 + 即时分组（防百万级OOM）────────────────
                var groupDict = new Dictionary<string, List<Dictionary<string, object?>>>(
                    StringComparer.OrdinalIgnoreCase);
                int fetchPage = 0;
                int totalRows = 0;
                progress.Status = "BOM分组模式：流式读取中...";
                onProgress(progress);

                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    { progress.Status = "⏹ 任务已停止"; onProgress(progress); return; }

                    List<Dictionary<string, object?>> fetchRows;
                    if (isSqlSrc)
                        fetchRows = await new DataAccess.SqlServerHelper(srcDb)
                            .QueryPageByQueryAsync(proc.SourceSql, srcCols, whereClause, fetchPage, pageSize);
                    else
                        fetchRows = await new DataAccess.MySqlHelper(srcDb)
                            .QueryPageByQueryAsync(proc.SourceSql, srcCols, whereClause, fetchPage, pageSize);

                    if (fetchRows.Count == 0) break;
                    var mappedRows = ApplyProcMapping(fetchRows, fieldMappings);
                    fetchRows.Clear(); // 及时释放原始行引用

                    foreach (var row in mappedRows)
                    {
                        var key = row.TryGetValue(groupKey, out var kv) ? kv?.ToString() ?? "" : "";
                        if (key.Length == 0) continue;
                        if (!groupDict.TryGetValue(key, out var lst))
                        { lst = new List<Dictionary<string, object?>>(); groupDict[key] = lst; }
                        lst.Add(row);
                    }
                    totalRows += mappedRows.Count;
                    if (mappedRows.Count < pageSize) break;
                    fetchPage++;
                }

                progress.Total = totalRows;
                progress.Status = $"读取完成 {totalRows} 条 / {groupDict.Count} 组，查重中...";
                onProgress(progress);

                // ── 分批查重（每批500个key，防IN子句超长）────────────────
                var existingSyncedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (useUpsert)
                {
                    try
                    {
                        var allGroupKeys = groupDict.Keys.ToList();
                        const int checkBatch = 500;
                        for (int bi = 0; bi < allGroupKeys.Count; bi += checkBatch)
                        {
                            if (cancellationToken.IsCancellationRequested) break;
                            var bkeys = allGroupKeys.Skip(bi).Take(checkBatch).ToList();
                            var inClause = string.Join(",", bkeys.Select(k => $"'{k.Replace("'", "''")}'"));
                            var checkSql = $"SELECT orderCode FROM synced_records WHERE orderCode IN ({inClause}) AND syncType = '{proc.ApiUpsertTable}'";
                            var existRows2 = isDstSql2i
                                ? await new DataAccess.SqlServerHelper(dstDbCfg2i!).QueryPageByQueryAsync(checkSql, new List<string>(), "", 0, checkBatch + 10)
                                : await new DataAccess.MySqlHelper(dstDbCfg2i!).QueryPageByQueryAsync(checkSql, new List<string>(), "", 0, checkBatch + 10);
                            foreach (var r2 in existRows2)
                                if (r2.TryGetValue("orderCode", out var v2) && v2 != null)
                                    existingSyncedKeys.Add(v2.ToString()!);
                        }
                        if (existingSyncedKeys.Count > 0)
                            LogService.WriteInfo($"[ProcApi/BOM] 查重过滤已同步 {existingSyncedKeys.Count} 组", "SyncExecutor");
                    }
                    catch (Exception ex)
                    {
                        LogService.WriteError("[ProcApi/BOM] 查重失败，将全量同步", ex, task.Id.ToString(), "SyncExecutor");
                    }
                }

                // 过滤已同步组，释放原字典
                var groups = groupDict.Where(kv2 => !existingSyncedKeys.Contains(kv2.Key)).ToList();
                groupDict.Clear();
                progress.Status = $"待同步 {groups.Count} 组，开始并发提交...";
                onProgress(progress);

                // ── Channel 有界并发（替代 Task.WhenAll 全量并发，防内存爆炸）──
                int bomConcurrency = Math.Max(1, Math.Min(proc.ApiConcurrency, 20));
                var channel = System.Threading.Channels.Channel.CreateBounded<KeyValuePair<string, List<Dictionary<string, object?>>>>(
                    new System.Threading.Channels.BoundedChannelOptions(bomConcurrency * 2)
                    { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
                var bomLock = new object();
                string bodyTpl = (api.BodyTemplate ?? "").Trim();
                bool hasDetails = bodyTpl.Contains("[[details]]") && !string.IsNullOrWhiteSpace(proc.DetailsTemplate);
                bool isArrayTpl = bodyTpl.StartsWith("[");
                string singleTpl = isArrayTpl
                    ? bodyTpl.TrimStart().TrimStart('[').TrimEnd().TrimEnd(']').Trim()
                    : bodyTpl;

                // 生产者：顺序写入 Channel
                var producer = System.Threading.Tasks.Task.Run(async () =>
                {
                    foreach (var grp in groups)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        await channel.Writer.WriteAsync(grp, cancellationToken);
                    }
                    channel.Writer.Complete();
                }, cancellationToken);

                // 固定并发数，处理完即释放内存
                var workers = Enumerable.Range(0, bomConcurrency).Select(_ =>
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        await foreach (var grp in channel.Reader.ReadAllAsync(cancellationToken))
                        {
                            if (cancellationToken.IsCancellationRequested) break;
                            var rows = grp.Value;
                            string arrayBody;

                            if (!string.IsNullOrWhiteSpace(bodyTpl))
                            {
                                var sb = new System.Text.StringBuilder();
                                if (hasDetails)
                                {
                                    var mainRow = rows[0];
                                    var dtpl = proc.DetailsTemplate!.Trim();
                                    var dsb = new System.Text.StringBuilder("[");
                                    for (int ri = 0; ri < rows.Count; ri++)
                                    { if (ri > 0) dsb.Append(','); dsb.Append(BuildSingleBody(dtpl, rows[ri])); }
                                    dsb.Append(']');
                                    var mainBody = BuildSingleBody(bodyTpl, mainRow);
                                    arrayBody = mainBody.Replace("[[details]]", dsb.ToString());
                                    if (!arrayBody.TrimStart().StartsWith("["))
                                        arrayBody = "[" + arrayBody + "]";
                                }
                                else
                                {
                                    if (isArrayTpl) sb.Append('[');
                                    for (int ri = 0; ri < rows.Count; ri++)
                                    { if (ri > 0) sb.Append(','); sb.Append(BuildSingleBody(singleTpl, rows[ri])); }
                                    if (isArrayTpl) sb.Append(']');
                                    arrayBody = sb.ToString();
                                }
                            }
                            else
                            {
                                arrayBody = Newtonsoft.Json.JsonConvert.SerializeObject(rows);
                            }

                            string? respBodyC = null;
                            int? respStatusC = null;
                            long elapsedC = 0;
                            try
                            {
                                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                                using var reqMsg = new System.Net.Http.HttpRequestMessage(
                                    System.Net.Http.HttpMethod.Post, api.Url);
                                reqMsg.Content = new System.Net.Http.StringContent(
                                    arrayBody, System.Text.Encoding.UTF8, "application/json");
                                reqMsg.Headers.TryAddWithoutValidation("User-Agent",
                                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                                foreach (var h in api.Headers)
                                    reqMsg.Headers.TryAddWithoutValidation(h.Key, h.Value);
                                if (!string.IsNullOrWhiteSpace(currentToken))
                                    reqMsg.Headers.TryAddWithoutValidation(
                                        api.TokenHeaderKey ?? "X-token", currentToken);
                                using var ctsBom = new System.Threading.CancellationTokenSource(apiTimeout);
                                var resp = await http.SendAsync(reqMsg, ctsBom.Token);
                                sw2.Stop();
                                respBodyC = await resp.Content.ReadAsStringAsync();
                                respStatusC = (int)resp.StatusCode;
                                elapsedC = sw2.ElapsedMilliseconds;
                                resp.EnsureSuccessStatusCode();
                                CheckBizSuccess(respBodyC);
                                lock (bomLock) { progress.Success += rows.Count; progress.Processed += rows.Count; }
                                // 接口成功后回写SQL（用该组第一行数据替换占位符）
                                var wbMsgC = await ExecuteWriteBackSqlAsync(proc, rows[0], task);
                                LogService.WriteApiLog(api.ApiName, api.Method.ToString(), api.Url,
                                    $"[处理{grp.Key} {rows.Count}条] " + arrayBody,
                                    respStatusC, respBodyC, elapsedC, true, writeBackMsg: wbMsgC);

                                // 写同步标记
                                if (useUpsert)
                                {
                                    try
                                    {
                                        var syncSql = $"INSERT IGNORE INTO synced_records (orderCode, syncType) VALUES ('{grp.Key.Replace("'", "''")}', '{proc.ApiUpsertTable}')";
                                        if (isDstSql2i)
                                            await new DataAccess.SqlServerHelper(dstDbCfg2i!).ExecuteNonQueryAsync(syncSql);
                                        else
                                            await new DataAccess.MySqlHelper(dstDbCfg2i!).ExecuteNonQueryAsync(syncSql);
                                    }
                                    catch (Exception exSync)
                                    {
                                        LogService.WriteError("[ProcApi/SyncMark] 写标记失败", exSync, task.Id.ToString(), "SyncExecutor");
                                    }
                                }
                                // 接口成功后回写SQL（用该组第一行数据替换占位符）
                                await ExecuteWriteBackSqlAsync(proc, rows[0], task);
                            }
                            catch (BizSkipException ex)
                            {
                                lock (bomLock) { progress.Processed += rows.Count; }
                                var wbSkipMsgC = GetWriteBackSkipMsg(proc, respStatusC, ex.Message);
                                LogService.WriteApiLog(api.ApiName, api.Method.ToString(), api.Url,
                                    $"[处理{grp.Key} {rows.Count}条] " + arrayBody,
                                    respStatusC, respBodyC, elapsedC, false, ex.Message,
                                    writeBackMsg: wbSkipMsgC);
                            }
                            catch (Exception ex)
                            {
                                lock (bomLock) { progress.Failed += rows.Count; progress.Processed += rows.Count; }
                                var wbErrMsgC = GetWriteBackSkipMsg(proc, respStatusC, ex.Message);
                                LogService.WriteApiLog(api.ApiName, api.Method.ToString(), api.Url,
                                    $"[处理{grp.Key} {rows.Count}条] " + arrayBody,
                                    respStatusC, respBodyC, elapsedC, false, ex.Message,
                                    writeBackMsg: wbErrMsgC);
                            }

                            rows.Clear(); // 处理完立即释放内存
                            lock (bomLock)
                            {
                                if (progress.Processed % 10 == 0 || progress.Processed >= progress.Total)
                                {
                                    double pct = (double)progress.Processed / progress.Total * 100;
                                    progress.Status = $"进度 {pct:F0}% ({progress.Processed}/{progress.Total}) 成功:{progress.Success} 失败:{progress.Failed}";
                                    onProgress(progress);
                                }
                            }
                        }
                    }, cancellationToken)).ToList();

                await System.Threading.Tasks.Task.WhenAll(new[] { producer }.Concat(workers));
                progress.Status = $"BOM同步完成 成功{progress.Success}条 失败{progress.Failed}条";
                progress.Elapsed = sw.Elapsed;
                onProgress(progress);
                return;
            }

            // ── 预加载目标库映射表 ──────────────────────────────────────────
            var dstDbCfgs2 = ConfigStore.LoadDbConfigs();
            // API模式优先用 ApiTargetDbConfigId，否则用 TargetDbConfigId
            var dstDbId2 = proc.ApiTargetDbConfigId > 0 ? proc.ApiTargetDbConfigId : proc.TargetDbConfigId;
            var dstDbCfg2 = dstDbCfgs2.FirstOrDefault(d => d.Id == dstDbId2);
            bool hasDstDb = dstDbCfg2 != null;
            bool isDstSql2 = dstDbCfg2?.DbType == DbType.SqlServer;

            // 1. product_type Code→Id
            var productTypeMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // 2. product_info Code→Id
            var productCodeMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // 3. bom_item 根节点 productCode→bom_item.Id（用于子件 parentId）
            var bomRootMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (hasDstDb)
            {
                try
                {
                    progress.Status = "加载目标库映射表...";
                    onProgress(progress);

                    async Task<List<Dictionary<string, object?>>> QueryDst(string sql) =>
                        isDstSql2
                            ? await new DataAccess.SqlServerHelper(dstDbCfg2!).QueryPageByQueryAsync(sql, new List<string>(), "", 0, 99999)
                            : await new DataAccess.MySqlHelper(dstDbCfg2!).QueryPageByQueryAsync(sql, new List<string>(), "", 0, 99999);

                    // product_type
                    foreach (var r in await QueryDst("SELECT Id, Code FROM product_type"))
                    {
                        var c = r.TryGetValue("Code", out var cv) ? cv?.ToString() : null;
                        var i = r.TryGetValue("Id", out var iv) ? iv?.ToString() : null;
                        if (!string.IsNullOrWhiteSpace(c) && int.TryParse(i, out var id))
                            productTypeMap[c] = id;
                    }

                    // product_info
                    foreach (var r in await QueryDst("SELECT Id, Code FROM product_info"))
                    {
                        var c = r.TryGetValue("Code", out var cv) ? cv?.ToString() : null;
                        var i = r.TryGetValue("Id", out var iv) ? iv?.ToString() : null;
                        if (!string.IsNullOrWhiteSpace(c) && int.TryParse(i, out var id))
                            productCodeMap[c] = id;
                    }

                    // bom_item 根节点（ParentId=0，ProductId=RootProductId）
                    // 需要 JOIN product_info 拿 Code
                    var bomSql = isDstSql2
                        ? "SELECT bi.Id, pi.Code FROM bom_item bi INNER JOIN product_info pi ON pi.Id=bi.ProductId WHERE bi.ParentId=0"
                        : "SELECT bi.Id, pi.Code FROM bom_item bi INNER JOIN product_info pi ON pi.Id=bi.ProductId WHERE bi.ParentId=0";
                    foreach (var r in await QueryDst(bomSql))
                    {
                        var c = r.TryGetValue("Code", out var cv) ? cv?.ToString() : null;
                        var i = r.TryGetValue("Id", out var iv) ? iv?.ToString() : null;
                        if (!string.IsNullOrWhiteSpace(c) && int.TryParse(i, out var id))
                            bomRootMap[c] = id;
                    }

                    LogService.WriteInfo($"[ProcApi] 映射加载完成: productType={productTypeMap.Count} productCode={productCodeMap.Count} bomRoot={bomRootMap.Count}", "SyncExecutor");
                }
                catch (Exception ex)
                {
                    LogService.WriteError("[ProcApi] 加载映射表失败", ex, task.Id.ToString(), "SyncExecutor");
                }
            }

            while (true)
            {
                // 读源库
                List<Dictionary<string, object?>> rows;
                if (isSqlSrc)
                    rows = await new DataAccess.SqlServerHelper(srcDb)
                        .QueryPageByQueryAsync(proc.SourceSql, srcCols, whereClause, page, pageSize);
                else
                    rows = await new DataAccess.MySqlHelper(srcDb)
                        .QueryPageByQueryAsync(proc.SourceSql, srcCols, whereClause, page, pageSize);

                if (rows.Count == 0) break;

                // 应用字段映射
                var mapped = ApplyProcMapping(rows, fieldMappings);

                // ── 跨库字段转换 ──────────────────────────────────────────
                foreach (var row in mapped)
                {
                    // ProductTypeId: Code → product_type.Id
                    if (row.TryGetValue("ProductTypeId", out var ptVal) && ptVal != null
                        && productTypeMap.TryGetValue(ptVal.ToString()!, out var ptId))
                        row["ProductTypeId"] = ptId;

                    // productCode → product_info.Id（用于 productId 字段）
                    if (row.TryGetValue("productCode", out var pcVal) && pcVal != null
                        && productCodeMap.TryGetValue(pcVal.ToString()!, out var pcId))
                    {
                        row["productId"] = pcId;
                        row.Remove("productCode");
                    }

                    // rootProductCode → product_info.Id（用于 rootProductId）
                    if (row.TryGetValue("rootProductCode", out var rcVal) && rcVal != null)
                    {
                        var rcStr = rcVal.ToString()!;
                        if (productCodeMap.TryGetValue(rcStr, out var rcId))
                            row["rootProductId"] = rcId;

                        // 同时用 rootProductCode 查 bom_item 根节点 Id 作为 parentId
                        if (bomRootMap.TryGetValue(rcStr, out var parentIdVal))
                            row["parentId"] = parentIdVal;
                        else
                            row["parentId"] = 0;

                        row.Remove("rootProductCode");
                    }
                }

                if (isBatch)
                {
                    // 整批打包为 JSON 数组，调一次API
                    string body = string.IsNullOrWhiteSpace(api.BodyTemplate)
                        ? Newtonsoft.Json.JsonConvert.SerializeObject(mapped)
                        : BuildBatchBody(api.BodyTemplate, mapped);

                    string? respBodyB = null;
                    int? respStatusB = null;
                    long elapsedB = 0;
                    try
                    {
                        var sw2 = System.Diagnostics.Stopwatch.StartNew();
                        var resp = await http.PostAsync(api.Url,
                            new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                        sw2.Stop();
                        respBodyB = await resp.Content.ReadAsStringAsync();
                        respStatusB = (int)resp.StatusCode;
                        elapsedB = sw2.ElapsedMilliseconds;
                        resp.EnsureSuccessStatusCode();
                        CheckBizSuccess(respBodyB);
                        progress.Success += mapped.Count;
                        LogService.WriteApiLog(
                            api.ApiName, api.Method.ToString(), api.Url,
                            $"[批量第{page + 1}批 {mapped.Count}条] " + body,
                            respStatusB, respBodyB, elapsedB, true);
                    }
                    catch (BizSkipException ex)
                    {
                        progress.Status = $"⚠ 跳过第{page + 1}批: {ex.Message}";
                        LogService.WriteApiLog(
                            api.ApiName, api.Method.ToString(), api.Url,
                            $"[批量第{page + 1}批 {mapped.Count}条] " + body,
                            respStatusB, respBodyB, elapsedB, false, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        progress.Failed += mapped.Count;
                        progress.Status = $"⚠ 第{page + 1}批API调用失败: {ex.Message}";
                        LogService.WriteApiLog(
                            api.ApiName, api.Method.ToString(), api.Url,
                            $"[批量第{page + 1}批 {mapped.Count}条] " + body,
                            respStatusB, respBodyB, elapsedB, false, ex.Message);
                    }
                }
                else
                {
                    // API查重配置
                    bool apiUpsert = proc.ApiEnableUpsert
                        && !string.IsNullOrWhiteSpace(proc.ApiUpsertTable)
                        && !string.IsNullOrWhiteSpace(proc.ApiUpsertKeyFields)
                        && hasDstDb;
                    var apiUpsertKeys = apiUpsert
                        ? proc.ApiUpsertKeyFields!.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToList()
                        : new List<string>();

                    // ── BOM批量模式：按 RootCode 分组，每组一次POST ──────────
                    bool isBomBatch = !string.IsNullOrWhiteSpace(proc.BomGroupKey);
                    if (isBomBatch)
                    {
                        var groupKey = proc.BomGroupKey!;
                        var groups = mapped
                            .GroupBy(r => r.TryGetValue(groupKey, out var v) ? v?.ToString() ?? "" : "")
                            .Where(g => g.Key.Length > 0)
                            .ToList();

                        progress.Status = $"BOM分组模式：共 {groups.Count} 个BOM，开始批量提交...";
                        onProgress(progress);

                        int bomConcurrency = Math.Max(1, Math.Min(proc.ApiConcurrency, 20));
                        var bomSemaphore = new System.Threading.SemaphoreSlim(bomConcurrency);
                        var bomLock = new object();

                        var bomTasks = groups.Select(async grp =>
                        {
                            if (cancellationToken.IsCancellationRequested) return;
                            await bomSemaphore.WaitAsync(cancellationToken);
                            try
                            {
                                // 把这个BOM的所有行组成数组
                                var rows = grp.ToList();
                                var bodyTemplate = api.BodyTemplate ?? "";
                                // 生成数组：每行替换占位符后组合
                                string arrayBody;
                                if (!string.IsNullOrWhiteSpace(bodyTemplate))
                                {
                                    var items = rows.Select(r => BuildSingleBody(bodyTemplate, r));
                                    arrayBody = "[" + string.Join(",", items) + "]";
                                }
                                else
                                {
                                    arrayBody = Newtonsoft.Json.JsonConvert.SerializeObject(rows);
                                }

                                string? respBodyOld = null;
                                int? respStatusOld = null;
                                long elapsedMsOld = 0;
                                try
                                {
                                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                                    using var reqMsg = new System.Net.Http.HttpRequestMessage(
                                        System.Net.Http.HttpMethod.Post, api.Url);
                                    reqMsg.Content = new System.Net.Http.StringContent(
                                        arrayBody, System.Text.Encoding.UTF8, "application/json");
                                    reqMsg.Headers.TryAddWithoutValidation("User-Agent",
                                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                                    foreach (var h in api.Headers)
                                        reqMsg.Headers.TryAddWithoutValidation(h.Key, h.Value);
                                    if (!string.IsNullOrWhiteSpace(currentToken))
                                        reqMsg.Headers.TryAddWithoutValidation(
                                            api.TokenHeaderKey ?? "X-token", currentToken);
                                    using var ctsBom = new System.Threading.CancellationTokenSource(apiTimeout);
                                    var resp = await http.SendAsync(reqMsg, ctsBom.Token);
                                    sw2.Stop();
                                    respBodyOld = await resp.Content.ReadAsStringAsync();
                                    respStatusOld = (int)resp.StatusCode;
                                    elapsedMsOld = sw2.ElapsedMilliseconds;
                                    resp.EnsureSuccessStatusCode();
                                    CheckBizSuccess(respBodyOld);
                                    lock (bomLock) { progress.Success += rows.Count; progress.Processed += rows.Count; }
                                    // 接口成功后回写SQL（用该组第一行数据替换占位符）
                                    var wbMsgOld = await ExecuteWriteBackSqlAsync(proc, rows[0], task);
                                    LogService.WriteApiLog(api.ApiName, api.Method.ToString(), api.Url,
                                        $"[BOM:{grp.Key} {rows.Count}条] " + arrayBody,
                                        respStatusOld, respBodyOld, elapsedMsOld, true,
                                        writeBackMsg: wbMsgOld);
                                }
                                catch (BizSkipException ex)
                                {
                                    lock (bomLock) { progress.Processed += rows.Count; }
                                    var wbSkipOld = GetWriteBackSkipMsg(proc, respStatusOld, ex.Message);
                                    LogService.WriteApiLog(api.ApiName, api.Method.ToString(), api.Url,
                                        $"[BOM:{grp.Key} {rows.Count}条] " + arrayBody,
                                        respStatusOld, respBodyOld, elapsedMsOld, false, ex.Message,
                                        writeBackMsg: wbSkipOld);
                                }
                                catch (Exception ex)
                                {
                                    lock (bomLock) { progress.Failed += rows.Count; progress.Processed += rows.Count; }
                                    var wbErrOld = GetWriteBackSkipMsg(proc, respStatusOld, ex.Message);
                                    LogService.WriteApiLog(api.ApiName, api.Method.ToString(), api.Url,
                                        $"[BOM:{grp.Key} {rows.Count}条] " + arrayBody,
                                        respStatusOld, respBodyOld, elapsedMsOld, false, ex.Message,
                                        writeBackMsg: wbErrOld);
                                }

                                lock (bomLock)
                                {
                                    if (progress.Processed % 10 == 0 || progress.Processed >= progress.Total)
                                    {
                                        double pct = (double)progress.Processed / progress.Total * 100;
                                        progress.Status = $"进度 {pct:F0}% ({progress.Processed}/{progress.Total}) 成功:{progress.Success} 失败:{progress.Failed}";
                                        onProgress(progress);
                                    }
                                }
                            }
                            finally { bomSemaphore.Release(); }
                        }).ToList();

                        await System.Threading.Tasks.Task.WhenAll(bomTasks);
                        goto BatchDone;
                    }

                    // ── 批量查重：一次SQL查出本批所有已存在的记录 ──────────
                    var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (apiUpsert && apiUpsertKeys.Count > 0)
                    {
                        if (!hasDstDb || dstDbCfg2 == null)
                        {
                            progress.Status = "⚠ 启用了查重但未配置目标数据库，跳过查重";
                            onProgress(progress);
                        }
                        try
                        {
                            // 提取本批所有唯一键值
                            var keyField = apiUpsertKeys[0]; // 取第一个唯一键
                            var keyValues = mapped
                                .Where(r => r.TryGetValue(keyField, out var v) && v != null)
                                .Select(r => r[keyField]!.ToString()!)
                                .Distinct()
                                .ToList();

                            if (keyValues.Count > 0)
                            {
                                // 拼 IN 条件查目标表
                                var inClause = string.Join(",", keyValues.Select(v => $"'{v.Replace("'", "''")}'"));
                                var checkSql = $"SELECT `{keyField}` FROM `{proc.ApiUpsertTable}` WHERE `{keyField}` IN ({inClause})";

                                var existRows = isDstSql2
                                    ? await new DataAccess.SqlServerHelper(dstDbCfg2!)
                                        .QueryPageByQueryAsync(checkSql, new List<string>(), "", 0, keyValues.Count + 10)
                                    : await new DataAccess.MySqlHelper(dstDbCfg2!)
                                        .QueryPageByQueryAsync(checkSql, new List<string>(), "", 0, keyValues.Count + 10);

                                foreach (var r in existRows)
                                    if (r.TryGetValue(keyField, out var v) && v != null)
                                        existingKeys.Add(v.ToString()!);

                                LogService.WriteInfo(
                                    $"[ProcApi/BatchUpsert] 本批{mapped.Count}条，已存在{existingKeys.Count}条，新增{mapped.Count - existingKeys.Count}条",
                                    "SyncExecutor");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.WriteError("[ProcApi/BatchUpsert] 批量查重失败，将全量调API", ex, task.Id.ToString(), "SyncExecutor");
                        }
                    }

                    // 过滤掉已存在的记录
                    var toInsert = apiUpsert && apiUpsertKeys.Count > 0
                        ? mapped.Where(r =>
                        {
                            var keyField = apiUpsertKeys[0];
                            if (!r.TryGetValue(keyField, out var v) || v == null) return true;
                            return !existingKeys.Contains(v.ToString()!);
                        }).ToList()
                        : mapped;

                    skipCount += mapped.Count - toInsert.Count;
                    progress.Processed += mapped.Count - toInsert.Count;

                    // 并发控制，只对新记录调API
                    int concurrency = Math.Max(1, Math.Min(proc.ApiConcurrency, 20));
                    var semaphore = new System.Threading.SemaphoreSlim(concurrency);
                    var lockObj = new object();

                    var rowTasks = toInsert.Select(async row =>
                    {
                        if (cancellationToken.IsCancellationRequested) return;

                        await semaphore.WaitAsync(cancellationToken);
                        try
                        {
                            string body = string.IsNullOrWhiteSpace(api.BodyTemplate)
                                ? Newtonsoft.Json.JsonConvert.SerializeObject(row)
                                : BuildSingleBody(api.BodyTemplate, row);

                            string? respBody3 = null;
                            int? respStatus3 = null;
                            long elapsed3 = 0;
                            try
                            {
                                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                                using var reqMsg = new System.Net.Http.HttpRequestMessage(
                                    System.Net.Http.HttpMethod.Post, api.Url);
                                reqMsg.Content = new System.Net.Http.StringContent(
                                    body, System.Text.Encoding.UTF8, "application/json");
                                reqMsg.Headers.TryAddWithoutValidation("User-Agent",
                                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                                foreach (var h in api.Headers)
                                    reqMsg.Headers.TryAddWithoutValidation(h.Key, h.Value);
                                if (!string.IsNullOrWhiteSpace(currentToken))
                                    reqMsg.Headers.TryAddWithoutValidation(
                                        api.TokenHeaderKey ?? "X-token", currentToken);
                                using var cts2 = new System.Threading.CancellationTokenSource(apiTimeout);
                                var resp = await http.SendAsync(reqMsg, cts2.Token);
                                sw2.Stop();
                                respBody3 = await resp.Content.ReadAsStringAsync();
                                respStatus3 = (int)resp.StatusCode;
                                elapsed3 = sw2.ElapsedMilliseconds;
                                resp.EnsureSuccessStatusCode();
                                CheckBizSuccess(respBody3);
                                lock (lockObj) { progress.Success++; }
                                // 接口成功后回写SQL
                                var wbMsg3 = await ExecuteWriteBackSqlAsync(proc, row, task);
                                LogService.WriteApiLog(
                                    api.ApiName, api.Method.ToString(), api.Url,
                                    body, respStatus3, respBody3,
                                    elapsed3, true, writeBackMsg: wbMsg3);
                            }
                            catch (BizSkipException ex)
                            {
                                var wbSkipMsg3 = GetWriteBackSkipMsg(proc, respStatus3, ex.Message);
                                LogService.WriteApiLog(api.ApiName, api.Method.ToString(), api.Url,
                                    body, respStatus3, respBody3, elapsed3, false, ex.Message,
                                    writeBackMsg: wbSkipMsg3);
                            }
                            catch (Exception ex)
                            {
                                lock (lockObj) { progress.Failed++; }
                                var wbErrMsg3 = GetWriteBackSkipMsg(proc, respStatus3, ex.Message);
                                LogService.WriteApiLog(api.ApiName, api.Method.ToString(), api.Url,
                                    body, respStatus3, respBody3, elapsed3, false, ex.Message,
                                    writeBackMsg: wbErrMsg3);
                            }

                            lock (lockObj)
                            {
                                progress.Processed++;
                                progress.Elapsed = sw.Elapsed;
                                // 每10条更新一次UI，减少刷新开销
                                if (progress.Processed % 10 == 0 || progress.Processed >= progress.Total)
                                {
                                    double pct = (double)progress.Processed / progress.Total * 100;
                                    progress.Status = $"进度 {pct:F0}% ({progress.Processed}/{progress.Total}) 成功:{progress.Success} 跳过:{skipCount} 失败:{progress.Failed} 并发:{concurrency}";
                                    onProgress(progress);
                                }
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }).ToList();

                    await System.Threading.Tasks.Task.WhenAll(rowTasks);
                BatchDone:;
                }

                if (!isBatch)
                {
                    // 逐条模式进度已在循环内更新
                }
                else
                {
                    progress.Processed += mapped.Count;
                    progress.Elapsed = sw.Elapsed;
                    double pct = (double)progress.Processed / progress.Total * 100;
                    progress.Status = $"进度 {pct:F0}% ({progress.Processed}/{progress.Total}) 成功:{progress.Success} 失败:{progress.Failed}";
                    onProgress(progress);
                }

                if (rows.Count < pageSize) break;
                if (cancellationToken.IsCancellationRequested)
                {
                    progress.Status = "⏹ 任务已被手动停止";
                    onProgress(progress);
                    return;
                }
                page++;
                await System.Threading.Tasks.Task.Delay(50);
            }

            // 同步后执行SQL（可选，在源库或指定库执行）
            if (!string.IsNullOrWhiteSpace(proc.PostSyncSql))
            {
                progress.Status = "执行同步后SQL...";
                onProgress(progress);
                try
                {
                    var stmts = proc.PostSyncSql.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    foreach (var stmt in stmts)
                    {
                        if (isSqlSrc)
                            await new DataAccess.SqlServerHelper(srcDb).ExecuteNonQueryAsync(stmt);
                        else
                            await new DataAccess.MySqlHelper(srcDb).ExecuteNonQueryAsync(stmt);
                    }
                }
                catch (Exception ex)
                {
                    progress.Status = $"❌ 同步后SQL失败: {ex.Message}";
                    LogService.WriteError("[ProcApi] 同步后SQL失败", ex, task.Id.ToString(), "SyncExecutor");
                }
            }

            progress.Status = $"API同步完成 成功{progress.Success}条 失败{progress.Failed}条 跳过{skipCount}条";
            progress.Elapsed = sw.Elapsed;
            onProgress(progress);
        }

        /// <summary>单条记录替换 BodyTemplate 中的 {{字段名}} 占位符</summary>
        private static string BuildSingleBody(string template, Dictionary<string, object?> row)
        {
            var result = template;
            foreach (var kv in row)
            {
                var val = kv.Value == null ? "null" : kv.Value.ToString()!;
                result = result.Replace($"{{{{{kv.Key}}}}}", val);
            }
            return result;
        }

        /// <summary>批量模式：将数组嵌入 BodyTemplate 中的 {{items}} 占位符，或直接序列化为数组</summary>
        private static string BuildBatchBody(string template, List<Dictionary<string, object?>> rows)
        {
            var arr = Newtonsoft.Json.JsonConvert.SerializeObject(rows);
            if (template.Contains("{{items}}"))
                return template.Replace("{{items}}", arr);
            return arr;
        }

        private static List<Dictionary<string, object?>> ApplyProcMapping(
            List<Dictionary<string, object?>> rows, List<ProcFieldMapping> mappings)
        {
            return rows.Select(src =>
            {
                var dst = new Dictionary<string, object?>();
                foreach (var m in mappings)
                {
                    if (m.MappingType == "Const" || string.IsNullOrWhiteSpace(m.SourceAlias))
                        dst[m.TargetField] = m.DefaultValue;
                    else
                        dst[m.TargetField] = src.TryGetValue(m.SourceAlias, out var v) ? v : m.DefaultValue;
                }
                return dst;
            }).ToList();
        }

        // ===================== 脚本执行模式 =====================
        private static async System.Threading.Tasks.Task RunScriptAsync(
            DataCallConfig dc, DbConfig dstDb,
            TaskConfig task, Action<SyncProgress> onProgress,
            CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new SyncProgress { Status = "脚本模式：准备执行..." };
            onProgress(progress);

            if (string.IsNullOrWhiteSpace(dc.ScriptSql))
                throw new InvalidOperationException("脚本内容为空，请在数据调用配置中填写脚本SQL");

            // 按分号拆分语句，过滤空行和注释行
            var statements = dc.ScriptSql
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0 && !s.StartsWith("--"))
                .ToList();

            progress.Total = statements.Count;
            progress.Status = $"共 {statements.Count} 条语句，开始执行...";
            onProgress(progress);

            bool isSqlDst = dstDb.DbType == DbType.SqlServer;

            for (int i = 0; i < statements.Count; i++)
            {
                var stmt = statements[i];
                var preview = stmt.Length > 80 ? stmt[..80] + "..." : stmt;
                progress.Status = $"[{i + 1}/{statements.Count}] 执行: {preview}";
                onProgress(progress);

                try
                {
                    if (isSqlDst)
                        await new DataAccess.SqlServerHelper(dstDb).ExecuteNonQueryAsync(stmt);
                    else
                        await new DataAccess.MySqlHelper(dstDb).ExecuteNonQueryAsync(stmt);

                    progress.Success++;
                    LogService.WriteInfo($"[脚本] 语句{i + 1}执行成功: {preview}", "SyncExecutor");
                }
                catch (Exception ex)
                {
                    progress.Failed++;
                    progress.Status = $"❌ [{i + 1}/{statements.Count}] 执行失败: {ex.Message}";
                    onProgress(progress);
                    LogService.WriteError($"[脚本] 语句{i + 1}执行失败: {preview}", ex, task.Id.ToString(), "SyncExecutor");
                    // 继续执行剩余语句，不中断
                }

                progress.Processed++;
                progress.Elapsed = sw.Elapsed;
                double pct = (double)progress.Processed / progress.Total * 100;
                progress.Status = $"进度 {pct:F0}% ({progress.Processed}/{progress.Total}) 成功:{progress.Success} 失败:{progress.Failed}";
                onProgress(progress);
            }

            progress.Status = progress.Failed == 0
                ? $"✓ 脚本执行完成，共 {progress.Success} 条语句全部成功"
                : $"⚠ 脚本执行完成，成功 {progress.Success} 条，失败 {progress.Failed} 条";
            progress.Elapsed = sw.Elapsed;
            onProgress(progress);
        }

        private static async System.Threading.Tasks.Task RunApiAsync(
            DataCallConfig dc, DictMatchConfig dictMatch,
            DbConfig srcDb, TaskConfig task, Action<SyncProgress> onProgress,
            CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new SyncProgress { Status = "API模式：从源读取数据后转发至API..." };
            onProgress(progress);

            var apiConfigs = ConfigStore.LoadApiConfigs();
            var api = apiConfigs.FirstOrDefault(a => a.Id == dc.ApiConfigId)
                ?? throw new InvalidOperationException("找不到API配置");

            var firstDetail = dictMatch.Details.FirstOrDefault()
                ?? throw new InvalidOperationException("字典匹配配置中没有字段映射");

            string srcTable = firstDetail.SourceTableName;
            var srcCols = dictMatch.Details.Select(d => d.SourceFieldName).ToList();

            string whereClause = "";
            if (!string.IsNullOrWhiteSpace(dc.WhereCondition))
                whereClause = dc.WhereCondition;

            int batchSize = dc.BatchSize > 0 ? dc.BatchSize : 500;

            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(60);
            foreach (var h in api.Headers)
                http.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);

            int total, page = 0;
            if (srcDb.DbType == DbType.SqlServer)
            {
                var srcHelper = new DataAccess.SqlServerHelper(srcDb);
                total = await srcHelper.GetCountByTableAsync(srcTable, whereClause);
                progress.Total = total; onProgress(progress);

                while (true)
                {
                    var rows = await srcHelper.QueryPageByTableAsync(srcTable, srcCols, whereClause, page, batchSize);
                    if (rows.Count == 0) break;
                    await PostToApiAsync(http, api.Url, rows, progress, page, task, onProgress);
                    if (rows.Count < batchSize) break;
                    page++;
                }
            }
            else
            {
                var srcHelper = new DataAccess.MySqlHelper(srcDb);
                total = await srcHelper.GetCountByTableAsync(srcTable, whereClause);
                progress.Total = total; onProgress(progress);

                while (true)
                {
                    var rows = await srcHelper.QueryPageByTableAsync(srcTable, srcCols, whereClause, page, batchSize);
                    if (rows.Count == 0) break;
                    await PostToApiAsync(http, api.Url, rows, progress, page, task, onProgress);
                    if (rows.Count < batchSize) break;
                    page++;
                }
            }

            progress.Status = $"API同步完成，成功{progress.Success}行，失败{progress.Failed}行";
            progress.Elapsed = sw.Elapsed;
            onProgress(progress);
        }

        private static async System.Threading.Tasks.Task PostToApiAsync(
            System.Net.Http.HttpClient http, string url,
            List<Dictionary<string, object?>> rows,
            SyncProgress progress, int page, TaskConfig task,
            Action<SyncProgress> onProgress)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(rows);
            var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            try
            {
                var resp = await http.PostAsync(url, content);
                var respBody = await resp.Content.ReadAsStringAsync();
                resp.EnsureSuccessStatusCode();
                CheckBizSuccess(respBody);
                progress.Success += rows.Count;
            }
            catch (BizSkipException ex)
            {
                progress.Status = $"⚠ 跳过第{page + 1}批: {ex.Message}";
                LogService.WriteError($"业务跳过 page={page}", ex, task.Id.ToString(), "SyncExecutor");
            }
            catch (Exception ex)
            {
                progress.Failed += rows.Count;
                progress.Status = $"⚠ 第{page + 1}批API调用失败: {ex.Message}";
                LogService.WriteError($"API调用失败 page={page}", ex, task.Id.ToString(), "SyncExecutor");
            }
            progress.Processed += rows.Count;
            progress.Status = $"已提交 {progress.Processed}/{progress.Total} 行至API";
            onProgress(progress);
        }

        /// <summary>接口成功后执行回写SQL，返回回写结果描述（用于写入API日志）</summary>
        private static async Task<string> ExecuteWriteBackSqlAsync(
            ProcConfig proc, Dictionary<string, object?> row, TaskConfig task)
        {
            if (string.IsNullOrWhiteSpace(proc.WriteBackSql) || proc.WriteBackDbConfigId <= 0)
                return "";
            var wbSqlFinal = "";
            try
            {
                var wbDbCfg = ConfigStore.LoadDbConfigs()
                    .FirstOrDefault(d => d.Id == proc.WriteBackDbConfigId);
                if (wbDbCfg == null)
                {
                    var warnMsg = $"回写跳过，找不到目标数据库配置 ID={proc.WriteBackDbConfigId}";
                    LogService.WriteWarning($"[回写SQL跳过] {warnMsg}", "WriteBack");
                    return warnMsg;
                }

                wbSqlFinal = proc.WriteBackSql
                    .Replace("{{now}}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                foreach (var kv in row)
                    wbSqlFinal = wbSqlFinal.Replace($"{{{{{kv.Key}}}}}", kv.Value?.ToString() ?? "");

                if (wbDbCfg.DbType == DbType.SqlServer)
                    await new DataAccess.SqlServerHelper(wbDbCfg).ExecuteNonQueryAsync(wbSqlFinal);
                else
                    await new DataAccess.MySqlHelper(wbDbCfg).ExecuteNonQueryAsync(wbSqlFinal);

                var successMsg = $"回写成功，SQL：{wbSqlFinal}";
                LogService.WriteInfo($"[回写SQL成功] {wbSqlFinal}", "WriteBack");
                return successMsg;
            }
            catch (Exception exWb)
            {
                var failMsg = $"回写失败，原因：{exWb.Message}，SQL：{wbSqlFinal}";
                LogService.WriteError(
                    $"[回写SQL失败] SQL={wbSqlFinal} | 原因={exWb.Message}",
                    exWb, task.Id.ToString(), "WriteBack");
                return failMsg;
            }
        }

        /// <summary>接口跳过时生成回写失败说明（用于写入API日志）</summary>
        private static string GetWriteBackSkipMsg(ProcConfig proc, int? statusCode, string? bizMsg)
        {
            if (string.IsNullOrWhiteSpace(proc.WriteBackSql) || proc.WriteBackDbConfigId <= 0)
                return "";
            return $"回写失败，原因：【返回状态码：{statusCode}，{bizMsg}】";
        }

        /// <summary>检查业务层是否成功（success=false 时抛异常）</summary>
        private static void CheckBizSuccess(string? respBody)
        {
            if (string.IsNullOrWhiteSpace(respBody)) return;
            try
            {
                var jobj = Newtonsoft.Json.Linq.JObject.Parse(respBody);
                // success 字段不存在或为 true，视为成功直接返回
                if (jobj["success"] == null) return;
                var bizSuccess = (bool)jobj["success"];
                if (bizSuccess) return;
                // success=false 才处理
                var status = jobj["status"] != null ? (int)jobj["status"] : 0;
                var msg = jobj["msg"]?.ToString() ?? "业务失败";
                // 404/401/403 跳过不重试
                if (status == 404 || status == 401 || status == 403)
                    throw new BizSkipException($"[跳过] {msg}");
                // 其他（含500）抛异常记为失败
                throw new Exception($"业务失败: {msg}");
            }
            catch (Newtonsoft.Json.JsonException)
            {

            }
        }     /// <summary>业务层跳过异常（不重试，直接跳过）</summary>
        internal class BizSkipException : Exception
        {
            public BizSkipException(string message) : base(message) { }

        }
    }
}