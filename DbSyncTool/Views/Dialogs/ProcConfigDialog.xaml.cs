using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using DbSyncTool.DataAccess;
using DbSyncTool.Helpers;
using DbSyncTool.Models;

namespace DbSyncTool.Views.Dialogs
{
    public partial class ProcConfigDialog : Window
    {
        private readonly ProcConfig? _editingConfig;
        private List<DbConfig> _dbConfigs = new();
        private List<TableInfo> _dstTables = new();
        private ObservableCollection<ProcFieldMapping> _mappings = new();
        private List<string> _dstTableNames = new();

        public ProcConfigDialog(ProcConfig? config)
        {
            InitializeComponent();
            _editingConfig = config;
            DialogTitle.Text = config != null ? "编辑存储过程配置" : "新增存储过程配置";
            MappingPanel.ItemsSource = _mappings;
            _mappings.CollectionChanged += (s, e) =>
                TxtMappingCount.Text = $"（{_mappings.Count} 对）";
            LoadDbConfigs();
            if (config != null) FillForm(config);
        }

        // ===================== 初始化 =====================
        private void LoadDbConfigs()
        {
            _dbConfigs = ConfigStore.LoadDbConfigs();
            CmbSrcDb.Items.Clear();
            CmbDstDb.Items.Clear();
            foreach (var db in _dbConfigs)
            {
                CmbSrcDb.Items.Add(new ComboBoxItem { Content = db.DisplayName, Tag = db.Id });
                CmbDstDb.Items.Add(new ComboBoxItem { Content = db.DisplayName, Tag = db.Id });
            }
            // 加载API配置
            CmbApi.Items.Clear();
            foreach (var api in ConfigStore.LoadApiConfigs())
                CmbApi.Items.Add(new ComboBoxItem { Content = api.ApiName, Tag = api.Id });
            // 加载API目标库
            CmbApiDstDb.Items.Clear();
            CmbApiDstDb.Items.Add(new ComboBoxItem { Content = "-- 不配置 --", Tag = 0 });
            foreach (var db in _dbConfigs)
                CmbApiDstDb.Items.Add(new ComboBoxItem { Content = db.DisplayName, Tag = db.Id });
            // 加载回写目标库
            CmbWriteBackDb.Items.Clear();
            CmbWriteBackDb.Items.Add(new ComboBoxItem { Content = "-- 不配置 --", Tag = 0 });
            foreach (var db in _dbConfigs)
                CmbWriteBackDb.Items.Add(new ComboBoxItem { Content = db.DisplayName, Tag = db.Id });
        }

        private void FillForm(ProcConfig c)
        {
            TxtName.Text = c.ConfigName;
            TxtDesc.Text = c.ConfigDesc ?? "";
            TxtSrcSql.Text = c.SourceSql;
            TxtBatch.Text = c.BatchSize.ToString();
            RdoFull.IsChecked = c.SyncMode == SyncMode.Full;
            RdoIncremental.IsChecked = c.SyncMode == SyncMode.Incremental;
            TxtIncField.Text = c.WhereCondition ?? "";

            for (int i = 0; i < CmbSrcDb.Items.Count; i++)
                if ((CmbSrcDb.Items[i] as ComboBoxItem)?.Tag is int id && id == c.SourceDbConfigId)
                    CmbSrcDb.SelectedIndex = i;
            for (int i = 0; i < CmbDstDb.Items.Count; i++)
                if ((CmbDstDb.Items[i] as ComboBoxItem)?.Tag is int id && id == c.TargetDbConfigId)
                    CmbDstDb.SelectedIndex = i;
            TxtDstTable.Text = c.TargetTable ?? "";

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

            // 输出方式
            RdoDirect.IsChecked = c.OutputMode != "Api";
            RdoApi.IsChecked    = c.OutputMode == "Api";
            if (c.ApiConfigId.HasValue)
                for (int i = 0; i < CmbApi.Items.Count; i++)
                    if ((CmbApi.Items[i] as ComboBoxItem)?.Tag is int aid && aid == c.ApiConfigId.Value)
                        CmbApi.SelectedIndex = i;
            RdoSingle.IsChecked = c.ApiBodyMode != "Batch";
            RdoBatch.IsChecked  = c.ApiBodyMode == "Batch";
            for (int i = 0; i < CmbApiDstDb.Items.Count; i++)
                if ((CmbApiDstDb.Items[i] as ComboBoxItem)?.Tag is int aid && aid == c.ApiTargetDbConfigId)
                    CmbApiDstDb.SelectedIndex = i;
            TxtApiConcurrency.Text = c.ApiConcurrency.ToString();
            TxtBomGroupKey.Text = c.BomGroupKey ?? "";
            TxtDetailsTemplate.Text = c.DetailsTemplate ?? "";
            ChkApiUpsert.IsChecked = c.ApiEnableUpsert;
            TxtApiUpsertTable.Text = c.ApiUpsertTable ?? "";
            TxtApiUpsertKey.Text   = c.ApiUpsertKeyFields ?? "";

            // 回写SQL回填
            if (!string.IsNullOrWhiteSpace(c.WriteBackSql))
            {
                ChkWriteBack.IsChecked = true;
                PnlWriteBack.Visibility = Visibility.Visible;
                TxtWriteBackSql.Text = c.WriteBackSql;
                for (int i = 0; i < CmbWriteBackDb.Items.Count; i++)
                    if ((CmbWriteBackDb.Items[i] as ComboBoxItem)?.Tag is int wbid && wbid == c.WriteBackDbConfigId)
                        CmbWriteBackDb.SelectedIndex = i;
            }

            int sort = 0;
            foreach (var m in c.FieldMappings) { m.SortOrder = ++sort; _mappings.Add(m); }
        }

        // ===================== 源库选择 =====================
        private void ChkApiUpsert_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlApiUpsert == null) return;
            PnlApiUpsert.Visibility = ChkApiUpsert.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OutputMode_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlApiMode == null) return;
            bool isApi = RdoApi.IsChecked == true;
            PnlApiMode.Visibility    = isApi ? Visibility.Visible  : Visibility.Collapsed;
            PnlDirectMode.Visibility = isApi ? Visibility.Collapsed : Visibility.Visible;
        }

        private void CmbSrcDb_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbSrcDb.SelectedItem is ComboBoxItem item && item.Tag is int id)
                BtnValidateSql.IsEnabled = true;
        }

        // ===================== 目标库选择 =====================
        private async void CmbDstDb_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbDstDb.SelectedItem is not ComboBoxItem item || item.Tag is not int id) return;
            var config = _dbConfigs.FirstOrDefault(d => d.Id == id);
            if (config == null) return;
            try
            {
                _dstTables = config.DbType == DbType.SqlServer
                    ? await new SqlServerHelper(config).GetTablesAsync()
                    : await new MySqlHelper(config).GetTablesAsync();

                _dstTableNames = _dstTables.Select(t => t.TableName).ToList();
                LstDstTable.ItemsSource = _dstTableNames;
            }
            catch (Exception ex) { MessageBox.Show($"连接失败：{ex.Message}", "错误"); }
        }

        // ===================== 目标表搜索（TextBox + Popup） =====================
        private void TxtDstTable_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = TxtDstTable.Text.Trim();
            var filtered = string.IsNullOrEmpty(text)
                ? _dstTableNames
                : _dstTableNames.Where(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            LstDstTable.ItemsSource = filtered;
            DstTablePopup.IsOpen = filtered.Count > 0 && !string.IsNullOrEmpty(text);
        }

        private void TxtDstTable_GotFocus(object sender, RoutedEventArgs e)
        {
            if (_dstTableNames.Count > 0)
            {
                LstDstTable.ItemsSource = _dstTableNames;
                DstTablePopup.IsOpen = true;
            }
        }

        private void TxtDstTable_LostFocus(object sender, RoutedEventArgs e)
        {
            // 延迟关闭，让点击列表的事件先触发
            Dispatcher.BeginInvoke(new Action(() => DstTablePopup.IsOpen = false),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void BtnDstTableDropDown_Click(object sender, RoutedEventArgs e)
        {
            LstDstTable.ItemsSource = _dstTableNames;
            DstTablePopup.IsOpen = !DstTablePopup.IsOpen;
            if (DstTablePopup.IsOpen) TxtDstTable.Focus();
        }

        private void LstDstTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstDstTable.SelectedItem is string name)
            {
                TxtDstTable.Text = name;
                DstTablePopup.IsOpen = false;
                TxtDstTable.CaretIndex = name.Length;
            }
        }

        // ===================== 验证SQL =====================
        private async void BtnValidateSql_Click(object sender, RoutedEventArgs e)
        {
            var sql = TxtSrcSql.Text.Trim();
            if (string.IsNullOrWhiteSpace(sql)) { ShowErr("请输入查询SQL"); return; }
            if (CmbSrcDb.SelectedItem is not ComboBoxItem item || item.Tag is not int id)
            { ShowErr("请先选择源数据库"); return; }
            var config = _dbConfigs.FirstOrDefault(d => d.Id == id);
            if (config == null) return;

            BtnValidateSql.IsEnabled = false;
            BtnValidateSql.Content = "验证中...";
            try
            {
                string testSql = config.DbType == DbType.SqlServer
                    ? $"SELECT TOP 0 * FROM ({sql}) AS _t"
                    : $"SELECT * FROM ({sql}) AS _t LIMIT 0";
                if (config.DbType == DbType.SqlServer)
                    await new SqlServerHelper(config).GetCountByTableQueryAsync(testSql);
                else
                    await new MySqlHelper(config).GetCountByTableQueryAsync(testSql);
                MessageBox.Show("SQL验证通过！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"SQL验证失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { BtnValidateSql.IsEnabled = true; BtnValidateSql.Content = "验证SQL"; }
        }

        // ===================== 解析SQL列名自动生成映射 =====================
        private void BtnParseSql_Click(object sender, RoutedEventArgs e)
        {
            var sql = TxtSrcSql.Text.Trim();
            if (string.IsNullOrWhiteSpace(sql)) { ShowErr("请先输入查询SQL"); return; }

            // 从 SELECT ... FROM 之间提取列别名
            var aliases = ParseSelectAliases(sql);
            if (aliases.Count == 0)
            {
                ShowErr("未能从SQL中解析出列别名，请确保每列都有 AS 别名");
                return;
            }

            // 获取目标表字段
            string? tblName = string.IsNullOrWhiteSpace(TxtDstTable.Text) ? null : TxtDstTable.Text.Trim();

            List<string> dstFields = new();
            if (tblName != null)
            {
                var tbl = _dstTables.FirstOrDefault(t => t.TableName == tblName);
                if (tbl != null)
                    dstFields = tbl.Columns.Select(c => c.ColumnName).ToList();
            }

            _mappings.Clear();
            int sort = 1;
            foreach (var alias in aliases)
            {
                // 按名称自动匹配目标字段
                var dstField = dstFields.FirstOrDefault(f =>
                    f.Equals(alias, StringComparison.OrdinalIgnoreCase)) ?? alias;

                _mappings.Add(new ProcFieldMapping
                {
                    SortOrder   = sort++,
                    SourceAlias = alias,
                    TargetField = dstField,
                    MappingType = "Field"
                });
            }

            MessageBox.Show($"已自动生成 {_mappings.Count} 条映射，请检查并补充目标字段名。",
                "解析完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private List<string> ParseSelectAliases(string sql)
        {
            var result = new List<string>();
            // 提取 SELECT ... FROM 之间的内容
            var m = Regex.Match(sql, @"SELECT\s+(.*?)\s+FROM\s+",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!m.Success) return result;

            var selectPart = m.Groups[1].Value;
            // 按逗号分割，提取每列的 AS 别名
            var cols = selectPart.Split(',');
            foreach (var col in cols)
            {
                var asMatch = Regex.Match(col.Trim(), @"AS\s+\[?(\w+)\]?$",
                    RegexOptions.IgnoreCase);
                if (asMatch.Success)
                    result.Add(asMatch.Groups[1].Value);
                else
                {
                    // 没有AS，取最后一个点后面的名称
                    var plain = col.Trim().Split('.').Last().Trim('[', ']', '`', ' ');
                    if (!string.IsNullOrEmpty(plain) && Regex.IsMatch(plain, @"^\w+$"))
                        result.Add(plain);
                }
            }
            return result;
        }

        // ===================== 映射行操作 =====================
        private void AddRow_Click(object sender, RoutedEventArgs e)
        {
            _mappings.Add(new ProcFieldMapping
            {
                SortOrder   = _mappings.Count + 1,
                SourceAlias = "",
                TargetField = "",
                MappingType = "Field"
            });
        }

        private void RemoveRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ProcFieldMapping m)
            {
                _mappings.Remove(m);
                ReIndex();
            }
        }

        private void ClearRows_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确认清空所有映射？", "确认",
                MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                _mappings.Clear();
        }

        private void ReIndex()
        {
            for (int i = 0; i < _mappings.Count; i++)
                _mappings[i].SortOrder = i + 1;
        }

        // ===================== 其他控件事件 =====================
        private void SyncMode_Changed(object sender, RoutedEventArgs e)
        {
            if (LblIncField == null) return;
            bool isInc = RdoIncremental.IsChecked == true;
            LblIncField.Visibility = isInc ? Visibility.Visible : Visibility.Collapsed;
            TxtIncField.Visibility = isInc ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChkUpsert_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlUpsert == null) return;
            PnlUpsert.Visibility = ChkUpsert.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChkPostSql_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlPostSql == null) return;
            PnlPostSql.Visibility = ChkPostSql.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChkWriteBack_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlWriteBack == null) return;
            PnlWriteBack.Visibility = ChkWriteBack.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===================== 保存 =====================
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtName.Text))   { ShowErr("请填写配置名称"); return; }
            if (CmbSrcDb.SelectedItem == null)              { ShowErr("请选择源数据库"); return; }
            if (string.IsNullOrWhiteSpace(TxtSrcSql.Text)) { ShowErr("请填写源库查询SQL"); return; }
            bool isApiMode = RdoApi.IsChecked == true;
            if (isApiMode && CmbApi.SelectedItem == null)   { ShowErr("API模式请选择API配置"); return; }
            if (!isApiMode && CmbDstDb.SelectedItem == null){ ShowErr("请选择目标数据库"); return; }
            if (!isApiMode && string.IsNullOrWhiteSpace(TxtDstTable.Text)) { ShowErr("请选择目标表"); return; }
            if (_mappings.Count == 0)                       { ShowErr("请至少添加一条字段映射"); return; }

            var invalidMappings = _mappings.Where(m =>
                string.IsNullOrWhiteSpace(m.TargetField) ||
                (m.MappingType == "Field" && string.IsNullOrWhiteSpace(m.SourceAlias)) ||
                (m.MappingType == "Const" && string.IsNullOrWhiteSpace(m.DefaultValue))).ToList();
            if (invalidMappings.Count > 0)
            { ShowErr($"第 {invalidMappings[0].SortOrder} 行映射信息不完整"); return; }

            var srcId = (int)(CmbSrcDb.SelectedItem as ComboBoxItem)!.Tag;
            var dstId = isApiMode ? 0 : (int)(CmbDstDb.SelectedItem as ComboBoxItem)!.Tag;
            int? apiId = isApiMode ? (int?)(int)(CmbApi.SelectedItem as ComboBoxItem)!.Tag : null;

            var newConfig = new ProcConfig
            {
                Id               = _editingConfig?.Id ?? 0,
                ConfigName       = TxtName.Text.Trim(),
                ConfigDesc       = TxtDesc.Text.Trim().NullIfEmpty(),
                SourceDbConfigId = srcId,
                TargetDbConfigId = dstId,
                SourceSql        = TxtSrcSql.Text.Trim(),
                TargetTable      = isApiMode ? "" : TxtDstTable.Text.Trim(),
                OutputMode       = isApiMode ? "Api" : "Direct",
                ApiConfigId      = apiId,
                ApiBodyMode      = RdoBatch.IsChecked == true ? "Batch" : "Single",
                ApiConcurrency      = int.TryParse(TxtApiConcurrency.Text, out var ac) ? Math.Max(1, Math.Min(ac, 20)) : 5,
                ApiTargetDbConfigId = (CmbApiDstDb.SelectedItem as ComboBoxItem)?.Tag is int atid ? atid : 0,
                BomGroupKey         = TxtBomGroupKey.Text.Trim().NullIfEmpty(),
                DetailsTemplate     = TxtDetailsTemplate.Text.Trim().NullIfEmpty(),
                ApiEnableUpsert  = ChkApiUpsert.IsChecked == true,
                ApiUpsertTable   = ChkApiUpsert.IsChecked == true ? TxtApiUpsertTable.Text.Trim().NullIfEmpty() : null,
                ApiUpsertKeyFields = ChkApiUpsert.IsChecked == true ? TxtApiUpsertKey.Text.Trim().NullIfEmpty() : null,
                SyncMode         = RdoIncremental.IsChecked == true ? SyncMode.Incremental : SyncMode.Full,
                WhereCondition   = TxtIncField.Text.Trim().NullIfEmpty(),
                BatchSize        = int.TryParse(TxtBatch.Text, out var b) ? b : 500,
                UpsertKeyFields  = ChkUpsert.IsChecked == true ? TxtUpsertKey.Text.Trim().NullIfEmpty() : null,
                PostSyncSql      = ChkPostSql.IsChecked == true ? TxtPostSql.Text.Trim().NullIfEmpty() : null,
                WriteBackSql        = ChkWriteBack.IsChecked == true ? TxtWriteBackSql.Text.Trim().NullIfEmpty() : null,
                WriteBackDbConfigId = ChkWriteBack.IsChecked == true
                    ? ((CmbWriteBackDb.SelectedItem as ComboBoxItem)?.Tag is int wbid ? wbid : 0) : 0,
                FieldMappings    = _mappings.ToList(),
                CreateTime       = _editingConfig?.CreateTime ?? DateTime.Now,
                UpdateTime       = DateTime.Now
            };

            var list = ConfigStore.LoadProcConfigs();
            if (list.Any(c => c.ConfigName == newConfig.ConfigName && c.Id != newConfig.Id))
            { ShowErr("配置名称已存在"); return; }

            if (_editingConfig != null)
            {
                var idx = list.FindIndex(c => c.Id == newConfig.Id);
                if (idx >= 0) list[idx] = newConfig;
            }
            else
            {
                newConfig.Id = ConfigStore.NextProcId();
                list.Insert(0, newConfig);
            }

            ConfigStore.SaveProcConfigs(list);
            LogService.WriteInfo($"保存存储过程配置: {newConfig.ConfigName}", "ProcConfig");
            DialogResult = true;
            Close();
        }

        private void ShowErr(string msg) =>
            MessageBox.Show(msg, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
        private void Close_Click(object sender, RoutedEventArgs e)  { DialogResult = false; Close(); }
    }
}
