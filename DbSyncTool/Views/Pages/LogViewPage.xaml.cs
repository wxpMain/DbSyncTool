using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DbSyncTool.Helpers;
using DbSyncTool.Models;
using Microsoft.Win32;

namespace DbSyncTool.Views.Pages
{
    public partial class LogViewPage : Page
    {
        private bool _showErrorLog = false;     // false = 运行日志, true = 错误日志
        private ObservableCollection<LogEntryViewModel> _logItems = new();
        private readonly DispatcherTimer _debounce;

        public LogViewPage()
        {
            InitializeComponent();
            LogGrid.ItemsSource = _logItems;

            // 关键词防抖 300ms
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); LoadLogs(); };

            // 默认日期范围：最近7天
            DpFrom.SelectedDate = DateTime.Today.AddDays(-7);
            DpTo.SelectedDate = DateTime.Today.AddDays(1).AddSeconds(-1);

            LoadLogs();
        }

        // ===================== 外部设置过滤关键词 =====================
        private string? _pendingKeyword;

        public void SetKeywordFilter(string keyword)
        {
            // 页面可能还未 Loaded，先存起来，Loaded 事件里再应用
            if (IsLoaded)
            {
                TxtKeyword.Text = keyword;
                LoadLogs();
            }
            else
            {
                _pendingKeyword = keyword;
                Loaded += LogViewPage_Loaded;
            }
        }

        private void LogViewPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= LogViewPage_Loaded;
            if (_pendingKeyword != null)
            {
                TxtKeyword.Text = _pendingKeyword;
                _pendingKeyword = null;
                LoadLogs();
            }
        }

        // ===================== Tab 切换 =====================
        private void TabRun_Click(object sender, RoutedEventArgs e)
        {
            _showErrorLog = false;
            TabRunBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(49, 130, 206));
            TabRunBtn.Foreground = new SolidColorBrush(Color.FromRgb(49, 130, 206));
            TabRunBtn.FontWeight = FontWeights.Medium;
            TabErrBorder.BorderBrush = Brushes.Transparent;
            TabErrBtn.Foreground = new SolidColorBrush(Color.FromRgb(113, 128, 150));
            TabErrBtn.FontWeight = FontWeights.Normal;
            LoadLogs();
        }

        private void TabErr_Click(object sender, RoutedEventArgs e)
        {
            _showErrorLog = true;
            TabErrBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(229, 62, 62));
            TabErrBtn.Foreground = new SolidColorBrush(Color.FromRgb(229, 62, 62));
            TabErrBtn.FontWeight = FontWeights.Medium;
            TabRunBorder.BorderBrush = Brushes.Transparent;
            TabRunBtn.Foreground = new SolidColorBrush(Color.FromRgb(113, 128, 150));
            TabRunBtn.FontWeight = FontWeights.Normal;
            LoadLogs();
        }

        // ===================== 数据加载 =====================
        private void LoadLogs()
        {
            DateTime? from = DpFrom.SelectedDate;
            DateTime? to = DpTo.SelectedDate.HasValue
                ? DpTo.SelectedDate.Value.Date.AddDays(1).AddSeconds(-1)
                : (DateTime?)null;

            string? keyword = string.IsNullOrWhiteSpace(TxtKeyword.Text) ? null : TxtKeyword.Text.Trim();

            LogLevel? minLevel = null;
            if (CbLevel.SelectedItem is ComboBoxItem ci && ci.Tag is string tag && !string.IsNullOrEmpty(tag))
            {
                if (Enum.TryParse<LogLevel>(tag, out var lv))
                    minLevel = lv;
            }

            try
            {
                var logs = LogService.ReadLogs(_showErrorLog, from, to, minLevel, keyword);

                _logItems.Clear();
                foreach (var entry in logs)
                    _logItems.Add(new LogEntryViewModel(entry));

                TxtLogCount.Text = $"共 {_logItems.Count} 条";
            }
            catch (Exception ex)
            {
                TxtLogCount.Text = $"读取失败: {ex.Message}";
            }
        }

        // ===================== 过滤器事件 =====================
        private void Filter_Changed(object sender, SelectionChangedEventArgs e) => LoadLogs();

        private void TxtKeyword_TextChanged(object sender, TextChangedEventArgs e)
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void BtnClearFilter_Click(object sender, RoutedEventArgs e)
        {
            DpFrom.SelectedDate = DateTime.Today.AddDays(-7);
            DpTo.SelectedDate = DateTime.Today.AddDays(1).AddSeconds(-1);
            CbLevel.SelectedIndex = 0;
            TxtKeyword.Text = "";
            LoadLogs();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadLogs();

        // ===================== 选中详情 =====================
        private void LogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LogGrid.SelectedItem is not LogEntryViewModel vm) return;

            var sb = new StringBuilder();
            sb.AppendLine($"[{vm.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{vm.Level}] [{vm.Module}]");
            sb.AppendLine(vm.Message);
            if (!string.IsNullOrEmpty(vm.Entry.ExceptionType))
                sb.AppendLine($"ExceptionType: {vm.Entry.ExceptionType}");
            if (!string.IsNullOrEmpty(vm.Entry.StackTrace))
                sb.AppendLine($"StackTrace: {vm.Entry.StackTrace}");
            if (!string.IsNullOrEmpty(vm.Entry.TaskId))
                sb.AppendLine($"TaskId: {vm.Entry.TaskId}");

            TxtDetail.Text = sb.ToString();
        }

        // ===================== 导出 =====================
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_logItems.Count == 0)
            {
                MessageBox.Show("当前无日志可导出。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "导出日志",
                Filter = "文本文件 (*.txt)|*.txt|CSV文件 (*.csv)|*.csv",
                FileName = $"日志导出_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (dlg.ShowDialog() != true) return;

            bool isCsv = dlg.FilterIndex == 2;

            try
            {
                var sb = new StringBuilder();
                if (isCsv)
                {
                    sb.AppendLine("时间,级别,模块,消息,异常类型,任务ID");
                    foreach (var vm in _logItems)
                    {
                        sb.AppendLine(
                            $"\"{vm.Timestamp:yyyy-MM-dd HH:mm:ss.fff}\"," +
                            $"\"{vm.Level}\"," +
                            $"\"{EscapeCsv(vm.Module)}\"," +
                            $"\"{EscapeCsv(vm.Message)}\"," +
                            $"\"{EscapeCsv(vm.Entry.ExceptionType ?? "")}\"," +
                            $"\"{EscapeCsv(vm.Entry.TaskId ?? "")}\"");
                    }
                }
                else
                {
                    foreach (var vm in _logItems)
                    {
                        sb.AppendLine($"[{vm.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{vm.Level}] [{vm.Module}] {vm.Message}");
                        if (!string.IsNullOrEmpty(vm.Entry.ExceptionType))
                            sb.AppendLine($"  ExceptionType: {vm.Entry.ExceptionType}");
                        if (!string.IsNullOrEmpty(vm.Entry.StackTrace))
                            sb.AppendLine($"  StackTrace: {vm.Entry.StackTrace}");
                    }
                }

                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show($"导出成功！\n{dlg.FileName}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string EscapeCsv(string s) => s.Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", "");

        // ===================== 打开目录 =====================
        private void BtnOpenDir_Click(object sender, RoutedEventArgs e)
        {
            var dir = LogService.LogDirectory;
            if (Directory.Exists(dir))
                Process.Start("explorer.exe", dir);
            else
                MessageBox.Show("日志目录不存在或暂无日志。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ===================== ViewModel =====================
    public class LogEntryViewModel
    {
        public LogEntry Entry { get; }
        public DateTime Timestamp => Entry.Timestamp;
        public string Level => Entry.Level.ToString();
        public string Module => Entry.Module;
        public string Message => Entry.Message;

        public Brush LevelBrush => Entry.Level switch
        {
            LogLevel.DEBUG   => new SolidColorBrush(Color.FromRgb(160, 174, 192)),
            LogLevel.INFO    => new SolidColorBrush(Color.FromRgb(56, 161, 105)),
            LogLevel.WARNING => new SolidColorBrush(Color.FromRgb(214, 158, 46)),
            LogLevel.ERROR   => new SolidColorBrush(Color.FromRgb(229, 62, 62)),
            LogLevel.FATAL   => new SolidColorBrush(Color.FromRgb(113, 0, 0)),
            _ => Brushes.Gray
        };

        public LogEntryViewModel(LogEntry entry) => Entry = entry;
    }
}
