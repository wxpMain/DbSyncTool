using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DbSyncTool.Helpers;
using DbSyncTool.Models;
using Microsoft.Win32;

namespace DbSyncTool.Views.Pages
{
    public partial class SqlToolPage : Page
    {
        private List<Dictionary<string, object?>> _lastResult = new();

        public SqlToolPage()
        {
            InitializeComponent();
            LoadDatabases();
        }

        private void LoadDatabases()
        {
            var configs = ConfigStore.LoadDbConfigs();
            CmbDatabase.ItemsSource = configs;
            if (configs.Count > 0)
                CmbDatabase.SelectedIndex = 0;
        }

        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            var sql = TxtSql.Text.Trim();
            if (string.IsNullOrWhiteSpace(sql))
            {
                MessageBox.Show("请输入SQL语句", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dbConfig = CmbDatabase.SelectedItem as DbConfig;
            if (dbConfig == null)
            {
                MessageBox.Show("请选择目标数据库", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnExecute.IsEnabled = false;
            TxtStatus.Text = "执行中...";
            ResultGrid.ItemsSource = null;
            _lastResult.Clear();

            try
            {
                var sqlUpper = sql.TrimStart().ToUpper();

                if (sqlUpper.StartsWith("SELECT"))
                {
                    List<Dictionary<string, object?>> rows;
                    if (dbConfig.DbType == DbType.SqlServer)
                        rows = await new DataAccess.SqlServerHelper(dbConfig)
                            .QueryPageByQueryAsync(sql, new List<string>(), "", 0, 10000);
                    else
                        rows = await new DataAccess.MySqlHelper(dbConfig)
                            .QueryPageByQueryAsync(sql, new List<string>(), "", 0, 10000);

                    _lastResult = rows;
                    BindToDataGrid(rows);
                    TxtStatus.Text = "查询完成";
                    TxtRowCount.Text = $"共 {rows.Count} 条";
                }
                else
                {
                    var confirm = MessageBox.Show(
                        $"即将执行以下SQL，是否确认？\n\n{sql}",
                        "确认执行",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (confirm != MessageBoxResult.Yes) return;

                    int affected;
                    if (dbConfig.DbType == DbType.SqlServer)
                        affected = await new DataAccess.SqlServerHelper(dbConfig)
                            .ExecuteNonQueryAsync(sql);
                    else
                        affected = await new DataAccess.MySqlHelper(dbConfig)
                            .ExecuteNonQueryAsync(sql);

                    TxtStatus.Text = $"执行成功，影响 {affected} 行";
                    TxtRowCount.Text = "";
                    ResultGrid.ItemsSource = null;

                    LogService.WriteInfo(
                        $"[SqlTool] 执行成功 影响{affected}行 DB={dbConfig.DisplayName} SQL={sql}",
                        "SqlTool");
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"执行失败：{ex.Message}";
                LogService.WriteError($"[SqlTool] 执行失败 SQL={sql}", ex, null, "SqlTool");
                MessageBox.Show($"执行失败：\n\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnExecute.IsEnabled = true;
            }
        }

        private void BindToDataGrid(List<Dictionary<string, object?>> rows)
        {
            if (rows.Count == 0)
            {
                ResultGrid.ItemsSource = null;
                return;
            }

            var dt = new DataTable();
            foreach (var key in rows[0].Keys)
                dt.Columns.Add(key);

            foreach (var row in rows)
            {
                var dr = dt.NewRow();
                foreach (var kv in row)
                    dr[kv.Key] = kv.Value ?? DBNull.Value;
                dt.Rows.Add(dr);
            }

            ResultGrid.ItemsSource = dt.DefaultView;
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            ResultGrid.ItemsSource = null;
            _lastResult.Clear();
            TxtStatus.Text = "";
            TxtRowCount.Text = "";
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResult.Count == 0)
            {
                MessageBox.Show("没有可导出的数据", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "CSV文件|*.csv",
                FileName = $"导出_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dlg.ShowDialog() != true) return;

            var sb = new StringBuilder();
            var headers = string.Join(",", _lastResult[0].Keys);
            sb.AppendLine(headers);

            foreach (var row in _lastResult)
            {
                var vals = row.Values.Select(v =>
                {
                    var s = v?.ToString() ?? "";
                    if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                        s = "\"" + s.Replace("\"", "\"\"") + "\"";
                    return s;
                });
                sb.AppendLine(string.Join(",", vals));
            }

            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            TxtStatus.Text = $"已导出到 {dlg.FileName}";
        }
    }
}
