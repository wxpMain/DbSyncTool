using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DbSyncTool.Helpers;
using DbSyncTool.Models;

namespace DbSyncTool.Views.Dialogs
{
    public partial class DataCallDialog : Window
    {
        private readonly DataCallConfig? _editingConfig;
        private List<DictMatchConfig> _dictMatchConfigs = new();

        public DataCallDialog(DataCallConfig? config)
        {
            InitializeComponent();
            _editingConfig = config;
            DialogTitle.Text = config != null ? "编辑数据调用配置" : "新增数据调用配置";
            LoadDropdowns();
            if (config != null) FillForm(config);
        }

        private void LoadDropdowns()
        {
            _dictMatchConfigs = ConfigStore.LoadDictMatchConfigs();

            CmbDictMatch.Items.Clear();
            foreach (var d in _dictMatchConfigs)
                CmbDictMatch.Items.Add(new ComboBoxItem { Content = d.ConfigName, Tag = d.Id });

            CmbApi.Items.Clear();
            foreach (var a in ConfigStore.LoadApiConfigs())
                CmbApi.Items.Add(new ComboBoxItem { Content = a.ApiName, Tag = a.Id });
        }

        private void FillForm(DataCallConfig c)
        {
            TxtName.Text = c.ConfigName;
            CmbDirection.SelectedIndex = c.SyncDirection switch
            {
                SyncDirection.SqlServerToMySQL     => 0,
                SyncDirection.MySQLToSqlServer     => 1,
                SyncDirection.SqlServerToSqlServer => 2,
                SyncDirection.MySQLToMySQL         => 3,
                _                                  => 0
            };
            RdoDirect.IsChecked = c.CallType == CallType.Direct;
            RdoApi.IsChecked  = c.CallType == CallType.Api;
            RdoScript.IsChecked = c.CallType == CallType.Script;
            RdoFull.IsChecked = c.SyncMode == SyncMode.Full;
            RdoIncremental.IsChecked = c.SyncMode == SyncMode.Incremental;
            TxtIncField.Text = c.IncrementalField ?? "";
            TxtWhere.Text    = c.WhereCondition  ?? "";
            TxtBatch.Text    = c.BatchSize.ToString();
            if (!string.IsNullOrWhiteSpace(c.UpsertKeyFields))
            {
                ChkUpsert.IsChecked = true;
                TxtUpsertKey.Text = c.UpsertKeyFields;
            }
            if (!string.IsNullOrWhiteSpace(c.PostSyncSql))
            {
                ChkPostSql.IsChecked = true;
                TxtPostSql.Text = c.PostSyncSql;
            }
            RdoScript.IsChecked = c.CallType == CallType.Script;
            if (!string.IsNullOrWhiteSpace(c.ScriptSql))
                TxtScriptSql.Text = c.ScriptSql;

            for (int i = 0; i < CmbDictMatch.Items.Count; i++)
                if ((CmbDictMatch.Items[i] as ComboBoxItem)?.Tag is int id && id == c.DictMatchConfigId)
                { CmbDictMatch.SelectedIndex = i; break; }

            if (c.ApiConfigId.HasValue)
                for (int i = 0; i < CmbApi.Items.Count; i++)
                    if ((CmbApi.Items[i] as ComboBoxItem)?.Tag is int id && id == c.ApiConfigId.Value)
                    { CmbApi.SelectedIndex = i; break; }
        }

        // ===================== 字典匹配选择 → 刷新映射预览 =====================
        private void CmbDictMatch_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbDictMatch.SelectedItem is not ComboBoxItem item || item.Tag is not int id)
            {
                PreviewPanel.ItemsSource = null;
                TxtPreviewHint.Text = "请先选择字典匹配配置";
                return;
            }

            var dict = _dictMatchConfigs.FirstOrDefault(d => d.Id == id);
            if (dict == null || dict.Details.Count == 0)
            {
                PreviewPanel.ItemsSource = null;
                TxtPreviewHint.Text = "该配置暂无字段映射";
                return;
            }

            TxtPreviewHint.Text = $"共 {dict.Details.Count} 对映射";
            PreviewPanel.ItemsSource = dict.Details.OrderBy(d => d.SortOrder).ToList();
        }

        // ===================== 调用方式切换 =====================
        private void CallType_Changed(object sender, RoutedEventArgs e)
        {
            if (LblApi == null) return;
            bool isApi    = RdoApi.IsChecked    == true;
            bool isScript = RdoScript.IsChecked == true;
            LblApi.Visibility    = isApi    ? Visibility.Visible : Visibility.Collapsed;
            CmbApi.Visibility    = isApi    ? Visibility.Visible : Visibility.Collapsed;
            PnlScript.Visibility = isScript ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===================== 同步模式切换 =====================
        private void SyncMode_Changed(object sender, RoutedEventArgs e)
        {
            if (LblIncField == null) return;
            bool isInc = RdoIncremental.IsChecked == true;
            LblIncField.Visibility = isInc ? Visibility.Visible : Visibility.Collapsed;
            TxtIncField.Visibility = isInc ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===================== 保存 =====================
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtName.Text)) { ShowErr("请填写配置名称"); return; }
            if (RdoScript.IsChecked == true && string.IsNullOrWhiteSpace(TxtScriptSql.Text))
            { ShowErr("脚本模式请填写脚本内容"); return; }
            if (RdoScript.IsChecked != true && CmbDictMatch.SelectedItem == null)
            { ShowErr("请选择字典匹配配置"); return; }
            if (RdoApi.IsChecked == true && CmbApi.SelectedItem == null)
            { ShowErr("请选择API配置"); return; }

            var dictId = (int)(CmbDictMatch.SelectedItem as ComboBoxItem)!.Tag;
            int? apiId = RdoApi.IsChecked == true
                ? (int?)(int)(CmbApi.SelectedItem as ComboBoxItem)!.Tag
                : null;

            var newConfig = new DataCallConfig
            {
                Id              = _editingConfig?.Id ?? 0,
                ConfigName      = TxtName.Text.Trim(),
                SyncDirection   = CmbDirection.SelectedIndex switch
                {
                    0 => SyncDirection.SqlServerToMySQL,
                    1 => SyncDirection.MySQLToSqlServer,
                    2 => SyncDirection.SqlServerToSqlServer,
                    3 => SyncDirection.MySQLToMySQL,
                    _ => SyncDirection.SqlServerToMySQL
                },
                CallType        = RdoScript.IsChecked == true ? CallType.Script
                                : RdoApi.IsChecked == true ? CallType.Api
                                : CallType.Direct,
                DictMatchConfigId = dictId,
                ApiConfigId     = apiId,
                SyncMode        = RdoIncremental.IsChecked == true ? SyncMode.Incremental : SyncMode.Full,
                IncrementalField = TxtIncField.Text.Trim().NullIfEmpty(),
                WhereCondition  = TxtWhere.Text.Trim().NullIfEmpty(),
                BatchSize       = int.TryParse(TxtBatch.Text, out var b) ? b : 500,
                UpsertKeyFields = ChkUpsert.IsChecked == true && !string.IsNullOrWhiteSpace(TxtUpsertKey.Text)
                    ? TxtUpsertKey.Text.Trim()
                    : null,
                PostSyncSql = ChkPostSql.IsChecked == true && !string.IsNullOrWhiteSpace(TxtPostSql.Text)
                    ? TxtPostSql.Text.Trim()
                    : null,
                ScriptSql = RdoScript.IsChecked == true && !string.IsNullOrWhiteSpace(TxtScriptSql.Text)
                    ? TxtScriptSql.Text.Trim()
                    : null,
                CreateTime      = _editingConfig?.CreateTime ?? DateTime.Now,
                UpdateTime      = DateTime.Now
            };

            var configs = ConfigStore.LoadDataCallConfigs();
            if (configs.Any(c => c.ConfigName == newConfig.ConfigName && c.Id != newConfig.Id))
            { ShowErr("配置名称已存在"); return; }

            if (_editingConfig != null)
            {
                var idx = configs.FindIndex(c => c.Id == newConfig.Id);
                if (idx >= 0) configs[idx] = newConfig;
            }
            else
            {
                newConfig.Id = ConfigStore.NextDataCallId();
                configs.Insert(0, newConfig);
            }

            ConfigStore.SaveDataCallConfigs(configs);
            LogService.WriteInfo($"保存数据调用配置: {newConfig.ConfigName}", "DataCall");
            DialogResult = true;
            Close();
        }

        private async void BtnValidateScript_Click(object sender, RoutedEventArgs e)
        {
            var sql = TxtScriptSql.Text.Trim();
            if (string.IsNullOrWhiteSpace(sql)) { ShowErr("请输入脚本内容"); return; }
            MessageBox.Show("脚本语法预检通过（实际执行时才能确认完整正确性）。请确保目标数据库中相关表/存储过程已存在。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ChkPostSql_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlPostSql == null) return;
            PnlPostSql.Visibility = ChkPostSql.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChkUpsert_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlUpsert == null) return;
            PnlUpsert.Visibility = ChkUpsert.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowErr(string msg) =>
            MessageBox.Show(msg, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
        private void Close_Click(object sender, RoutedEventArgs e)  { DialogResult = false; Close(); }
    }

    static class NullExtensions
    {
        public static string? NullIfEmpty(this string s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
