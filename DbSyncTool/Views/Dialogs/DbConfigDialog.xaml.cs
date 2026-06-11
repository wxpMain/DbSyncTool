using System;
using System.Windows;
using System.Windows.Media;
using System.Linq;
using DbSyncTool.DataAccess;
using DbSyncTool.Helpers;
using DbSyncTool.Models;

namespace DbSyncTool.Views.Dialogs
{
    public partial class DbConfigDialog : Window
    {
        private readonly DbConfig? _editingConfig;
        private bool _isEdit;

        public DbConfigDialog(DbConfig? config)
        {
            InitializeComponent();
            _editingConfig = config;
            _isEdit = config != null;
            DialogTitle.Text = _isEdit ? "编辑数据库连接" : "新增数据库连接";

            if (_isEdit && config != null) FillForm(config);
            else CmbDbType.SelectedIndex = 0;
        }

        private void FillForm(DbConfig c)
        {
            TxtConfigName.Text = c.ConfigName;
            CmbDbType.SelectedIndex = c.DbType == Models.DbType.SqlServer ? 0 : 1;
            TxtServer.Text = c.ServerAddress;
            TxtPort.Text = c.Port.ToString();
            TxtDatabase.Text = c.DatabaseName;
            TxtAccount.Text = c.Account;
            // 密码不回显
            TxtTimeout.Text = c.ConnectTimeout.ToString();
            if (c.DbType == Models.DbType.MySQL)
            {
                CmbCharset.Visibility = Visibility.Visible;
                LblCharset.Visibility = Visibility.Visible;
            }
        }

        private void DbType_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var isMysql = CmbDbType.SelectedIndex == 1;
            TxtPort.Text = isMysql ? "3306" : "1433";
            CmbCharset.Visibility = isMysql ? Visibility.Visible : Visibility.Collapsed;
            LblCharset.Visibility = isMysql ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void TestConn_Click(object sender, RoutedEventArgs e)
        {
            TestConnBtn.IsEnabled = false;
            TestConnBtn.Content = "检测中…";
            TestResultText.Text = "";

            var tempConfig = BuildConfig();
            try
            {
                bool ok;
                if (tempConfig.DbType == Models.DbType.SqlServer)
                    ok = await SqlServerHelper.TestConnectionAsync(tempConfig);
                else
                    ok = await MySqlHelper.TestConnectionAsync(tempConfig);

                if (ok)
                {
                    TestResultText.Text = "✔ 连接成功";
                    TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x48, 0xBB, 0x78));
                }
                else
                {
                    TestResultText.Text = "✘ 连接失败：请检查连接参数";
                    TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));
                }
            }
            catch (Exception ex)
            {
                TestResultText.Text = $"✘ 连接失败：{ex.Message}";
                TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));
            }
            finally
            {
                TestConnBtn.IsEnabled = true;
                TestConnBtn.Content = "测试连接";
            }
        }

        private DbConfig BuildConfig()
        {
            var isMysql = CmbDbType.SelectedIndex == 1;
            return new DbConfig
            {
                Id = _editingConfig?.Id ?? 0,
                ConfigName = TxtConfigName.Text.Trim(),
                DbType = isMysql ? Models.DbType.MySQL : Models.DbType.SqlServer,
                ServerAddress = TxtServer.Text.Trim(),
                Port = int.TryParse(TxtPort.Text, out var p) ? p : (isMysql ? 3306 : 1433),
                DatabaseName = TxtDatabase.Text.Trim(),
                Account = TxtAccount.Text.Trim(),
                EncryptedPassword = string.IsNullOrEmpty(TxtPassword.Password)
                    ? (_editingConfig?.EncryptedPassword ?? "")
                    : ConfigStore.Encrypt(TxtPassword.Password),
                ConnectTimeout = int.TryParse(TxtTimeout.Text, out var t) ? t : 30,
                Charset = isMysql ? (CmbCharset.Text.IsNullOrEmpty() ? "utf8mb4" : CmbCharset.Text) : null,
                CreateTime = _editingConfig?.CreateTime ?? DateTime.Now,
                UpdateTime = DateTime.Now
            };
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtConfigName.Text)) { ShowError("请填写配置名称"); return; }
            if (string.IsNullOrWhiteSpace(TxtServer.Text)) { ShowError("请填写服务器地址"); return; }
            if (string.IsNullOrWhiteSpace(TxtDatabase.Text)) { ShowError("请填写数据库名称"); return; }
            if (string.IsNullOrWhiteSpace(TxtAccount.Text)) { ShowError("请填写账号"); return; }
            if (!_isEdit && string.IsNullOrWhiteSpace(TxtPassword.Password)) { ShowError("请填写密码"); return; }

            var configs = ConfigStore.LoadDbConfigs();
            var newConfig = BuildConfig();

            // 名称唯一性检查
            if (configs.Any(c => c.ConfigName == newConfig.ConfigName && c.Id != newConfig.Id))
            {
                ShowError("配置名称已存在，请重新输入");
                return;
            }

            if (_isEdit)
            {
                var idx = configs.FindIndex(c => c.Id == newConfig.Id);
                if (idx >= 0) configs[idx] = newConfig;
            }
            else
            {
                newConfig.Id = ConfigStore.NextDbConfigId();
                configs.Insert(0, newConfig);
            }

            ConfigStore.SaveDbConfigs(configs);
            LogService.WriteInfo($"{(_isEdit ? "编辑" : "新增")}数据库配置: {newConfig.ConfigName}", "DbConfig");
            DialogResult = true;
            Close();
        }

        private void ShowError(string msg) =>
            MessageBox.Show(msg, "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
        private void Close_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    }

    static class StringExtensions
    {
        public static bool IsNullOrEmpty(this string? s) => string.IsNullOrEmpty(s);
    }
}
