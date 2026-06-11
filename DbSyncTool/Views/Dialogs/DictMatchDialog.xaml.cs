using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DbSyncTool.DataAccess;
using DbSyncTool.Helpers;
using DbSyncTool.Models;

namespace DbSyncTool.Views.Dialogs
{
    public partial class DictMatchDialog : Window
    {
        private readonly DictMatchConfig? _editingConfig;
        private List<DbConfig> _dbConfigs = new();
        private List<TableInfo> _sourceTables = new();
        private List<TableInfo> _targetTables = new();
        private ObservableCollection<DictMatchDetail> _mappings = new();

        // 多选源字段
        private readonly List<FieldRef> _checkedSourceFields = new();
        // 多选目标字段
        private readonly List<FieldRef> _checkedTargetFields = new();
        // 兼容旧逻辑
        private ColumnInfo? _pendingTargetField => _checkedTargetFields.Count == 1 ? _checkedTargetFields[0].Column : null;
        private string? _pendingTargetTable => _checkedTargetFields.Count == 1 ? _checkedTargetFields[0].Table : null;

        // 目标表下拉用的 CVS
        private System.Windows.Data.CollectionViewSource _targetTableCvs = new();
        private bool _searchHooked = false;

        public DictMatchDialog(DictMatchConfig? config)
        {
            InitializeComponent();
            _editingConfig = config;
            DialogTitle.Text = config != null ? "编辑字典匹配" : "新增字典匹配";
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
            CmbSourceDb.Items.Clear();
            CmbTargetDb.Items.Clear();
            foreach (var db in _dbConfigs)
            {
                CmbSourceDb.Items.Add(new ComboBoxItem { Content = db.DisplayName, Tag = db.Id });
                CmbTargetDb.Items.Add(new ComboBoxItem { Content = db.DisplayName, Tag = db.Id });
            }
        }

        private void FillForm(DictMatchConfig c)
        {
            TxtConfigName.Text = c.ConfigName;
            for (int i = 0; i < CmbSourceDb.Items.Count; i++)
                if ((CmbSourceDb.Items[i] as ComboBoxItem)?.Tag is int id && id == c.SourceDbConfigId)
                    CmbSourceDb.SelectedIndex = i;
            for (int i = 0; i < CmbTargetDb.Items.Count; i++)
                if ((CmbTargetDb.Items[i] as ComboBoxItem)?.Tag is int id && id == c.TargetDbConfigId)
                    CmbTargetDb.SelectedIndex = i;
            if (!string.IsNullOrWhiteSpace(c.CustomSql))
            {
                ChkCustomSql.IsChecked = true;
                TxtCustomSql.Text = c.CustomSql;
            }
            int sort = 0;
            foreach (var d in c.Details) { d.SortOrder = ++sort; _mappings.Add(d); }
        }

        // ===================== 数据库选择 =====================
        private async void SourceDb_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbSourceDb.SelectedItem is not ComboBoxItem item || item.Tag is not int id) return;
            var config = _dbConfigs.FirstOrDefault(d => d.Id == id);
            if (config == null) return;
            try
            {
                _sourceTables = config.DbType == DbType.SqlServer
                    ? await new SqlServerHelper(config).GetTablesAsync()
                    : await new MySqlHelper(config).GetTablesAsync();
                PopulateTree(SourceTree, _sourceTables, isSource: true);
            }
            catch (Exception ex) { MessageBox.Show($"连接失败：{ex.Message}", "错误"); }
        }

        private async void TargetDb_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbTargetDb.SelectedItem is not ComboBoxItem item || item.Tag is not int id) return;
            var config = _dbConfigs.FirstOrDefault(d => d.Id == id);
            if (config == null) return;
            try
            {
                _targetTables = config.DbType == DbType.SqlServer
                    ? await new SqlServerHelper(config).GetTablesAsync()
                    : await new MySqlHelper(config).GetTablesAsync();

                // 填充目标表下拉（用于自动匹配）
                var tableNames = _targetTables.Select(t => t.TableName).ToList();
                _targetTableCvs = new System.Windows.Data.CollectionViewSource { Source = tableNames };
                CmbTargetTable.ItemsSource = _targetTableCvs.View;

                // 填充目标树
                PopulateTree(TargetTree, _targetTables, isSource: false);

                if (!_searchHooked)
                {
                    if (CmbTargetTable.IsLoaded) HookCmbTextBox();
                    else CmbTargetTable.Loaded += (s, ev) => HookCmbTextBox();
                    _searchHooked = true;
                }
            }
            catch (Exception ex) { MessageBox.Show($"连接失败：{ex.Message}", "错误"); }
        }

        // ===================== 通用建树 =====================
        private void PopulateTree(TreeView tree, List<TableInfo> tables, bool isSource)
        {
            tree.Items.Clear();
            if (isSource) _checkedSourceFields.Clear();
            else _checkedTargetFields.Clear();

            foreach (var table in tables)
            {
                var treeTable = new TreeViewItem
                {
                    Header = BuildTableHeader(table.TableName),
                    IsExpanded = false,
                    Tag = table.TableName
                };

                foreach (var col in table.Columns)
                {
                    var fr = new FieldRef { Table = table.TableName, Column = col };
                    TreeViewItem colItem;

                    if (isSource)
                    {
                        var cb = new CheckBox
                        {
                            Content = BuildColumnHeader(col),
                            Tag = fr,
                            Margin = new Thickness(2, 1, 0, 1)
                        };
                        cb.Checked += SourceField_CheckChanged;
                        cb.Unchecked += SourceField_CheckChanged;
                        colItem = new TreeViewItem { Header = cb, Tag = fr };
                    }
                    else
                    {
                        var cb = new CheckBox
                        {
                            Content = BuildColumnHeader(col),
                            Tag = fr,
                            Margin = new Thickness(2, 1, 0, 1)
                        };
                        cb.Checked += TargetField_CheckChanged;
                        cb.Unchecked += TargetField_CheckChanged;
                        colItem = new TreeViewItem { Header = cb, Tag = fr };
                    }
                    treeTable.Items.Add(colItem);
                }
                tree.Items.Add(treeTable);
            }
        }

        // ===================== 源字段勾选 =====================
        private void SourceField_CheckChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb || cb.Tag is not FieldRef fr) return;
            if (cb.IsChecked == true)
            {
                if (!_checkedSourceFields.Any(x => x.Column.ColumnName == fr.Column.ColumnName && x.Table == fr.Table))
                    _checkedSourceFields.Add(fr);
            }
            else
            {
                _checkedSourceFields.RemoveAll(x => x.Column.ColumnName == fr.Column.ColumnName && x.Table == fr.Table);
            }
        }

        // ===================== 目标字段勾选 =====================
        private void TargetField_CheckChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb || cb.Tag is not FieldRef fr) return;
            if (cb.IsChecked == true)
            {
                if (!_checkedTargetFields.Any(x => x.Column.ColumnName == fr.Column.ColumnName && x.Table == fr.Table))
                    _checkedTargetFields.Add(fr);
            }
            else
            {
                _checkedTargetFields.RemoveAll(x => x.Column.ColumnName == fr.Column.ColumnName && x.Table == fr.Table);
            }
        }

        // ===================== 目标表下拉 → 自动匹配 =====================
        private void TargetTable_Changed(object sender, SelectionChangedEventArgs e)
        {
            string? tblName = CmbTargetTable.SelectedItem as string
                ?? (string.IsNullOrWhiteSpace(CmbTargetTable.Text) ? null : CmbTargetTable.Text.Trim());
            if (tblName == null) return;

            var tbl = _targetTables.FirstOrDefault(x => x.TableName == tblName);
            if (tbl == null) return;
            _checkedTargetFields.Clear();
            AutoMapByName(tbl);
        }

        // ===================== 搜索 - 源 =====================
        private void TxtSourceSearch_Changed(object sender, TextChangedEventArgs e)
            => FilterTree(SourceTree, TxtSourceSearch.Text.Trim());

        // ===================== 搜索 - 目标 =====================
        private void TxtTargetSearch_Changed(object sender, TextChangedEventArgs e)
            => FilterTree(TargetTree, TxtTargetSearch.Text.Trim());

        private void FilterTree(TreeView tree, string keyword)
        {
            foreach (TreeViewItem tableItem in tree.Items)
            {
                if (string.IsNullOrEmpty(keyword))
                {
                    tableItem.Visibility = Visibility.Visible;
                    foreach (TreeViewItem colItem in tableItem.Items)
                        colItem.Visibility = Visibility.Visible;
                    tableItem.IsExpanded = false;
                    continue;
                }

                bool tableMatch = tableItem.Tag is string tbl &&
                    tbl.Contains(keyword, StringComparison.OrdinalIgnoreCase);

                bool anyColMatch = false;
                foreach (TreeViewItem colItem in tableItem.Items)
                {
                    string colName = colItem.Tag is FieldRef fr ? fr.Column.ColumnName : "";
                    bool colMatch = tableMatch ||
                        colName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                    colItem.Visibility = colMatch ? Visibility.Visible : Visibility.Collapsed;
                    if (colMatch) anyColMatch = true;
                }

                tableItem.Visibility = (tableMatch || anyColMatch) ? Visibility.Visible : Visibility.Collapsed;
                if (anyColMatch) tableItem.IsExpanded = true;
            }
        }

        // ===================== 目标表 ComboBox 搜索 =====================
        private void CmbTargetTable_DropDownOpened(object sender, EventArgs e) => HookCmbTextBox();

        private void HookCmbTextBox()
        {
            CmbTargetTable.ApplyTemplate();
            var tb = CmbTargetTable.Template?.FindName("PART_EditableTextBox", CmbTargetTable)
                     as System.Windows.Controls.TextBox;
            if (tb == null) return;
            tb.TextChanged -= CmbTargetTableInner_TextChanged;
            tb.TextChanged += CmbTargetTableInner_TextChanged;
        }

        private void CmbTargetTableInner_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = CmbTargetTable.Text;
            var view = _targetTableCvs.View;
            if (view == null) return;
            view.Filter = string.IsNullOrWhiteSpace(text)
                ? (Predicate<object>?)null
                : o => o is string s && s.Contains(text, StringComparison.OrdinalIgnoreCase);
            CmbTargetTable.IsDropDownOpen = view.Cast<object>().Any();
        }

        // ===================== 添加映射（支持普通模式和自定义SQL手动输入模式） =====================
        private void AddMapping_Click(object sender, RoutedEventArgs e)
        {
            bool isCustomSql = ChkCustomSql.IsChecked == true;

            // 目标字段：从右侧树选中
            var dstList = new List<FieldRef>();
            CollectSelectedTargetFields(TargetTree, dstList);
            if (dstList.Count == 0 && _pendingTargetField != null && _pendingTargetTable != null)
                dstList.Add(new FieldRef { Table = _pendingTargetTable, Column = _pendingTargetField });

            if (dstList.Count == 0) { ShowErr("请在右侧点击选择至少一个目标字段"); return; }

            if (isCustomSql)
            {
                // 自定义SQL模式：从手动输入框读取源字段名
                var srcFieldName = TxtManualSrcField.Text.Trim();
                var srcFieldType = TxtManualSrcType.Text.Trim();
                if (string.IsNullOrEmpty(srcFieldName)) { ShowErr("请输入源字段名（自定义SQL中的列别名）"); return; }

                int added = 0;
                foreach (var dst in dstList)
                {
                    if (_mappings.Any(m => m.SourceFieldName == srcFieldName
                                       && m.TargetFieldName == dst.Column.ColumnName)) continue;
                    _mappings.Add(new DictMatchDetail
                    {
                        SourceTableName = "CustomSQL",
                        SourceFieldName = srcFieldName,
                        SourceFieldType = string.IsNullOrEmpty(srcFieldType) ? "varchar" : srcFieldType,
                        TargetTableName = dst.Table,
                        TargetFieldName = dst.Column.ColumnName,
                        TargetFieldType = dst.Column.DataType,
                        SortOrder = _mappings.Count + 1
                    });
                    added++;
                }
                TxtManualSrcField.Text = "";
                ClearTargetSelection(TargetTree);
                if (added == 0) ShowErr("该映射已存在，未新增。");
                return;
            }

            // 普通模式：从左侧树勾选
            var srcList = _checkedSourceFields.ToList();
            if (srcList.Count == 0) { ShowErr("请在左侧勾选至少一个源字段"); return; }
            int normalAdded = 0;

            if (srcList.Count > 1 && dstList.Count > 1 && srcList.Count != dstList.Count)
            {
                ShowErr($"多对多时源字段数({srcList.Count})须与目标字段数({dstList.Count})相同");
                return;
            }

            if (dstList.Count == 1)
            {
                // 多源 → 一目标  或  一源 → 一目标
                var dst = dstList[0];
                foreach (var src in srcList)
                    normalAdded += TryAddMapping(src, dst);
            }
            else if (srcList.Count == 1)
            {
                // 一源 → 多目标
                var src = srcList[0];
                foreach (var dst in dstList)
                    normalAdded += TryAddMapping(src, dst);
            }
            else
            {
                // N源 → N目标，按顺序配对
                for (int i = 0; i < srcList.Count; i++)
                    normalAdded += TryAddMapping(srcList[i], dstList[i]);
            }

            ClearSourceChecks();
            ClearTargetSelection(TargetTree);
            _checkedTargetFields.Clear();
            if (normalAdded == 0) ShowErr("所选映射均已存在，未新增。");
        }

        private int TryAddMapping(FieldRef src, FieldRef dst)
        {
            if (_mappings.Any(m => m.SourceFieldName == src.Column.ColumnName
                                && m.TargetFieldName == dst.Column.ColumnName)) return 0;
            _mappings.Add(new DictMatchDetail
            {
                SourceTableName = src.Table,
                SourceFieldName = src.Column.ColumnName,
                SourceFieldType = src.Column.DataType,
                TargetTableName = dst.Table,
                TargetFieldName = dst.Column.ColumnName,
                TargetFieldType = dst.Column.DataType,
                SortOrder = _mappings.Count + 1
            });
            return 1;
        }

        private void CollectSelectedTargetFields(TreeView tree, List<FieldRef> result)
        {
            result.AddRange(_checkedTargetFields);
        }

        private void ClearSourceChecks()
        {
            _checkedSourceFields.Clear();
            foreach (TreeViewItem tableItem in SourceTree.Items)
                foreach (TreeViewItem colItem in tableItem.Items)
                    if (colItem.Header is CheckBox cb) cb.IsChecked = false;
        }

        private void ClearTargetSelection(TreeView tree)
        {
            _checkedTargetFields.Clear();
            foreach (TreeViewItem tableItem in tree.Items)
                foreach (TreeViewItem colItem in tableItem.Items)
                    if (colItem.Header is CheckBox cb) cb.IsChecked = false;
        }

        // ===================== 自动按名称匹配 =====================
        private void AutoMapByName(TableInfo targetTable)
        {
            _mappings.Clear();
            int sort = 1;
            foreach (var srcTable in _sourceTables)
                foreach (var srcCol in srcTable.Columns)
                {
                    var match = targetTable.Columns.FirstOrDefault(tc =>
                        tc.ColumnName.Equals(srcCol.ColumnName, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        _mappings.Add(new DictMatchDetail
                        {
                            SourceTableName = srcTable.TableName,
                            SourceFieldName = srcCol.ColumnName,
                            SourceFieldType = srcCol.DataType,
                            TargetTableName = targetTable.TableName,
                            TargetFieldName = match.ColumnName,
                            TargetFieldType = match.DataType,
                            SortOrder = sort++
                        });
                }
        }

        // ===================== 删除映射 =====================
        private void RemoveOneMapping_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DictMatchDetail detail)
            { _mappings.Remove(detail); ReIndex(); }
        }

        private void DeleteMapping_Click(object sender, RoutedEventArgs e)
        {
            if (_mappings.Count == 0) return;
            if (MessageBox.Show("删除最后一条映射？", "确认", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            { _mappings.RemoveAt(_mappings.Count - 1); ReIndex(); }
        }

        private void ClearMappings_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确认清空所有映射关系？", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                _mappings.Clear();
        }

        private void ReIndex()
        {
            for (int i = 0; i < _mappings.Count; i++)
                _mappings[i].SortOrder = i + 1;
        }

        // ===================== 自定义SQL =====================
        private void ChkCustomSql_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = ChkCustomSql.IsChecked == true;
            TxtCustomSql.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            LblSqlHint.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            BtnValidateSql.IsEnabled = enabled;
            // 自定义SQL模式显示手动输入框，普通模式隐藏
            if (PnlManualInput != null)
                PnlManualInput.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BtnValidateSql_Click(object sender, RoutedEventArgs e)
        {
            var sql = TxtCustomSql.Text.Trim();
            if (string.IsNullOrWhiteSpace(sql)) { ShowErr("请输入SQL语句"); return; }
            if (CmbSourceDb.SelectedItem is not ComboBoxItem item || item.Tag is not int id)
            { ShowErr("请先选择源数据库"); return; }
            var config = _dbConfigs.FirstOrDefault(d => d.Id == id);
            if (config == null) return;
            BtnValidateSql.IsEnabled = false; BtnValidateSql.Content = "验证中...";
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

        // ===================== 保存 =====================
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtConfigName.Text)) { ShowErr("请填写配置名称"); return; }
            if (CmbSourceDb.SelectedItem == null) { ShowErr("请选择源数据库"); return; }
            if (CmbTargetDb.SelectedItem == null) { ShowErr("请选择目标数据库"); return; }
            if (_mappings.Count == 0) { ShowErr("请至少添加一条字段映射"); return; }

            var sourceId = (int)(CmbSourceDb.SelectedItem as ComboBoxItem)!.Tag;
            var targetId = (int)(CmbTargetDb.SelectedItem as ComboBoxItem)!.Tag;
            var srcCfg = _dbConfigs.FirstOrDefault(d => d.Id == sourceId);
            var dstCfg = _dbConfigs.FirstOrDefault(d => d.Id == targetId);

            var newConfig = new DictMatchConfig
            {
                Id = _editingConfig?.Id ?? 0,
                ConfigName = TxtConfigName.Text.Trim(),
                SourceDbConfigId = sourceId,
                TargetDbConfigId = targetId,
                SourceDbName = srcCfg?.DatabaseName ?? "",
                TargetDbName = dstCfg?.DatabaseName ?? "",
                CreateTime = _editingConfig?.CreateTime ?? DateTime.Now,
                UpdateTime = DateTime.Now,
                Details = _mappings.ToList(),
                CustomSql = ChkCustomSql.IsChecked == true && !string.IsNullOrWhiteSpace(TxtCustomSql.Text)
                    ? TxtCustomSql.Text.Trim() : null
            };

            var configs = ConfigStore.LoadDictMatchConfigs();
            if (configs.Any(c => c.ConfigName == newConfig.ConfigName && c.Id != newConfig.Id))
            { ShowErr("配置名称已存在"); return; }

            if (_editingConfig != null)
            { var idx = configs.FindIndex(c => c.Id == newConfig.Id); if (idx >= 0) configs[idx] = newConfig; }
            else
            { newConfig.Id = ConfigStore.NextDictMatchId(); configs.Insert(0, newConfig); }

            ConfigStore.SaveDictMatchConfigs(configs);
            LogService.WriteInfo($"保存字典匹配配置: {newConfig.ConfigName}", "DictMatch");
            DialogResult = true; Close();
        }

        // ===================== 辅助 =====================
        private StackPanel BuildTableHeader(string name)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = "📁 " + name, FontWeight = FontWeights.SemiBold, FontSize = 12 });
            return sp;
        }

        private StackPanel BuildColumnHeader(ColumnInfo col)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = $"  {col.ColumnName}", FontSize = 12, Width = 150 });
            sp.Children.Add(new TextBlock
            {
                Text = col.DataType + (col.MaxLength.HasValue ? $"({col.MaxLength})" : ""),
                FontSize = 11,
                Foreground = System.Windows.Media.Brushes.Gray
            });
            return sp;
        }

        private void ShowErr(string msg) =>
            MessageBox.Show(msg, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
        private void Close_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        private class FieldRef
        {
            public string Table { get; set; } = "";
            public ColumnInfo Column { get; set; } = null!;
        }
    }
}
