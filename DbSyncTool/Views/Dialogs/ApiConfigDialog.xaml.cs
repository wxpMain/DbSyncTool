using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DbSyncTool.Helpers;
using DbSyncTool.Models;
using HttpMethod = DbSyncTool.Models.HttpMethod;

namespace DbSyncTool.Views.Dialogs
{
    public partial class ApiConfigDialog : Window
    {
        private readonly ApiConfig? _editingConfig;
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        public ApiConfigDialog(ApiConfig? config)
        {
            InitializeComponent();
            _editingConfig = config;
            DialogTitle.Text = config != null ? "编辑API配置" : "新增API配置";
            if (config != null) FillForm(config);
        }

        private void FillForm(ApiConfig c)
        {
            TxtApiName.Text = c.ApiName;
            TxtUrl.Text = c.Url;
            for (int i = 0; i < CmbMethod.Items.Count; i++)
                if ((CmbMethod.Items[i] as ComboBoxItem)?.Content?.ToString() == c.Method.ToString())
                    CmbMethod.SelectedIndex = i;
            TxtTimeout.Text = c.Timeout.ToString();
            TxtHeaders.Text = string.Join("\n", c.Headers.Select(h => $"{h.Key}: {h.Value}"));
            TxtBody.Text = c.BodyTemplate ?? "";
            TxtJsonPath.Text = c.JsonPath ?? "";
            TxtSuccessCodes.Text = c.SuccessStatusCodes;

            // 自动登录
            ChkAutoLogin.IsChecked = c.EnableAutoLogin;
            TxtLoginUrl.Text = c.LoginUrl ?? "";
            TxtLoginBody.Text = c.LoginBody ?? "";
            TxtTokenPath.Text = c.TokenJsonPath ?? "response.xtoken";
            TxtTokenHeaderKey.Text = c.TokenHeaderKey ?? "X-token";
        }

        private void Method_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (LblBody == null || TxtBody == null) return;
            var method = (CmbMethod.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var showBody = method == "POST" || method == "PUT";
            LblBody.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
            TxtBody.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChkAutoLogin_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlAutoLogin == null) return;
            PnlAutoLogin.Visibility = ChkAutoLogin.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void TestApi_Click(object sender, RoutedEventArgs e)
        {
            TestResultText.Text = "测试中…";
            TestResultText.Foreground = new SolidColorBrush(Colors.Gray);
            try
            {
                var method  = (CmbMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "POST";
                var url     = TxtUrl.Text.Trim();
                var timeout = int.TryParse(TxtTimeout.Text, out var t) ? t : 30;

                // ── 流程一：启用自动登录 ──────────────────────────
                string? autoToken = null;
                if (ChkAutoLogin.IsChecked == true)
                {
                    TestResultText.Text = "第1步：登录中，获取Token...";
                    var (token, errMsg) = await FetchTokenWithErrorAsync();
                    if (token == null)
                    {
                        TestResultText.Text = $"✘ 登录失败：{errMsg}";
                        TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));
                        return;
                    }
                    autoToken = token;
                    TestResultText.Text = "第1步：登录成功，Token已获取 ✓\n第2步：调用业务API中...";
                }

                // ── 构建业务API请求 ───────────────────────────────
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                var request = new HttpRequestMessage(new System.Net.Http.HttpMethod(method), url);

                request.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                foreach (var line in TxtHeaders.Text.Split('\n'))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length == 2)
                        request.Headers.TryAddWithoutValidation(parts[0].Trim(), parts[1].Trim());
                }

                if (autoToken != null)
                {
                    var headerKey = TxtTokenHeaderKey.Text.Trim().NullIfEmpty() ?? "X-token";
                    request.Headers.Remove(headerKey);
                    request.Headers.TryAddWithoutValidation(headerKey, autoToken);
                }

                if (method == "POST" || method == "PUT")
                    request.Content = new StringContent(TxtBody.Text, System.Text.Encoding.UTF8, "application/json");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _http.SendAsync(request, cts.Token);
                sw.Stop();
                var respBody = await response.Content.ReadAsStringAsync();

                var httpStatus = (int)response.StatusCode;
                var codes  = TxtSuccessCodes.Text.Split(',').Select(s => s.Trim());
                bool httpOk = codes.Contains(httpStatus.ToString());

                // 同时检查响应体里的业务状态码
                string? bizStatus = null;
                string? bizMsg    = null;
                try
                {
                    var jobj = Newtonsoft.Json.Linq.JObject.Parse(respBody);
                    bizStatus = jobj["status"]?.ToString();
                    bizMsg    = jobj["msg"]?.ToString();
                }
                catch { }

                bool bizOk = bizStatus == null || bizStatus == "200";
                bool isSuccess = httpOk && bizOk;

                LogService.WriteApiLog(
                    TxtApiName.Text.Trim(), method, url,
                    method == "POST" || method == "PUT" ? TxtBody.Text : "",
                    httpStatus, respBody, sw.ElapsedMilliseconds, isSuccess,
                    isSuccess ? null : $"HTTP:{httpStatus} 业务:{bizStatus} {bizMsg}");

                var sb = new System.Text.StringBuilder();
                if (ChkAutoLogin.IsChecked == true)
                    sb.AppendLine("第1步：登录成功，Token已获取 ✓");

                var step = ChkAutoLogin.IsChecked == true ? "第2步：" : "";
                if (isSuccess)
                    sb.AppendLine($"{step}✔ 调用成功 | HTTP:{httpStatus} | 业务:{bizStatus} | 耗时:{sw.ElapsedMilliseconds}ms");
                else
                    sb.AppendLine($"{step}✘ 调用失败（可能因请求体含占位符，属正常）| HTTP:{httpStatus} | 业务:{bizStatus} | 耗时:{sw.ElapsedMilliseconds}ms");

                if (!string.IsNullOrWhiteSpace(respBody))
                    sb.AppendLine($"响应：{(respBody.Length > 300 ? respBody[..300] + "..." : respBody)}");

                // ── 第三步：校验请求体模板占位符格式 ──────────────
                sb.AppendLine();
                var bodyTemplate = TxtBody.Text.Trim();
                if (string.IsNullOrWhiteSpace(bodyTemplate))
                {
                    sb.AppendLine("第3步：请求体模板为空，跳过校验");
                }
                else
                {
                    var placeholders = System.Text.RegularExpressions.Regex
                        .Matches(bodyTemplate, @"\{\{(\w+)\}\}")
                        .Select(m => m.Groups[1].Value)
                        .Distinct()
                        .ToList();

                    // 检查是否有未闭合的占位符
                    var badPlaceholders = System.Text.RegularExpressions.Regex
                        .Matches(bodyTemplate, @"\{[^{}]*\{|\}[^{}]*\}")
                        .Select(m => m.Value)
                        .ToList();

                    if (badPlaceholders.Count > 0)
                    {
                        sb.AppendLine($"第3步：⚠ 占位符格式有误（应为 {{{{字段名}}}}）：{string.Join(", ", badPlaceholders)}");
                    }
                    else if (placeholders.Count == 0)
                    {
                        sb.AppendLine("第3步：请求体无占位符（固定值模板）✓");
                    }
                    else
                    {
                        sb.AppendLine($"第3步：✔ 请求体模板格式正确，共 {placeholders.Count} 个占位符：");
                        sb.AppendLine($"  {string.Join("、", placeholders.Select(p => $"{{{{{p}}}}}"))}");
                        sb.AppendLine("  ↑ 任务执行时将自动替换为源库查询结果中的对应字段值");
                    }
                }

                TestResultText.Text = sb.ToString();
                // 第三步校验通过才算全绿
                bool step3Ok = !TxtBody.Text.Contains("{{") ||
                    System.Text.RegularExpressions.Regex.IsMatch(TxtBody.Text, @"\{\{\w+\}\}");
                TestResultText.Foreground = new SolidColorBrush(
                    autoToken != null && step3Ok
                        ? Color.FromRgb(0x48, 0xBB, 0x78)
                        : Color.FromRgb(0xED, 0x89, 0x36));
            }
            catch (Exception ex)
            {
                TestResultText.Text = $"✘ 调用异常：{ex.Message}";
                TestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));
            }
        }

        /// <summary>调用登录接口获取Token</summary>
        public async System.Threading.Tasks.Task<string?> FetchTokenAsync()
        {
            var (token, _) = await FetchTokenWithErrorAsync();
            return token;
        }

        public async System.Threading.Tasks.Task<(string? Token, string? Error)> FetchTokenWithErrorAsync()
        {
            try
            {
                var loginUrl  = TxtLoginUrl.Text.Trim();
                var loginBody = TxtLoginBody.Text.Trim();
                var tokenPath = TxtTokenPath.Text.Trim();

                if (string.IsNullOrWhiteSpace(loginUrl))
                    return (null, "登录地址为空");
                if (string.IsNullOrWhiteSpace(loginBody))
                    return (null, "登录请求体为空");

                // 加 User-Agent，部分服务器要求此字段不能为空
                using var loginRequest = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, loginUrl);
                loginRequest.Content = new StringContent(loginBody, System.Text.Encoding.UTF8, "application/json");
                loginRequest.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                var resp = await _http.SendAsync(loginRequest);
                var json = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return (null, $"登录接口返回 {(int)resp.StatusCode}，响应：{json[..Math.Min(200, json.Length)]}");

                Newtonsoft.Json.Linq.JObject jobj;
                try { jobj = Newtonsoft.Json.Linq.JObject.Parse(json); }
                catch { return (null, $"登录响应不是有效JSON：{json[..Math.Min(200, json.Length)]}"); }

                var token = jobj.SelectToken(tokenPath)?.ToString();
                if (string.IsNullOrWhiteSpace(token))
                    return (null, $"Token路径 '{tokenPath}' 在响应中找不到，响应：{json[..Math.Min(300, json.Length)]}");

                return (token, null);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }

        private ApiConfig BuildConfig()
        {
            var method = Enum.Parse<HttpMethod>(
                (CmbMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "POST");
            var headers = new List<ApiHeader>();
            foreach (var line in TxtHeaders.Text.Split('\n'))
            {
                var parts = line.Split(':', 2);
                if (parts.Length == 2)
                    headers.Add(new ApiHeader { Key = parts[0].Trim(), Value = parts[1].Trim() });
            }

            return new ApiConfig
            {
                Id = _editingConfig?.Id ?? 0,
                ApiName = TxtApiName.Text.Trim(),
                Url = TxtUrl.Text.Trim(),
                Method = method,
                Timeout = int.TryParse(TxtTimeout.Text, out var t) ? t : 30,
                Headers = headers,
                BodyTemplate = TxtBody.Text.Trim(),
                JsonPath = TxtJsonPath.Text.Trim(),
                SuccessStatusCodes = TxtSuccessCodes.Text.Trim(),
                IsVerified = TestResultText.Text.StartsWith("✔"),
                EnableAutoLogin  = ChkAutoLogin.IsChecked == true,
                LoginUrl         = TxtLoginUrl.Text.Trim().NullIfEmpty(),
                LoginBody        = TxtLoginBody.Text.Trim().NullIfEmpty(),
                TokenJsonPath    = TxtTokenPath.Text.Trim().NullIfEmpty() ?? "response.xtoken",
                TokenHeaderKey   = TxtTokenHeaderKey.Text.Trim().NullIfEmpty() ?? "X-token",
                CreateTime = _editingConfig?.CreateTime ?? DateTime.Now,
                UpdateTime = DateTime.Now
            };
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtApiName.Text)) { ShowErr("请填写API名称"); return; }
            if (string.IsNullOrWhiteSpace(TxtUrl.Text))     { ShowErr("请填写请求地址"); return; }
            if (ChkAutoLogin.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(TxtLoginUrl.Text))  { ShowErr("请填写登录地址"); return; }
                if (string.IsNullOrWhiteSpace(TxtLoginBody.Text)) { ShowErr("请填写登录请求体"); return; }
            }

            var configs = ConfigStore.LoadApiConfigs();
            var newConfig = BuildConfig();

            if (configs.Any(c => c.ApiName == newConfig.ApiName && c.Id != newConfig.Id))
            { ShowErr("API名称已存在"); return; }

            if (_editingConfig != null)
            {
                var idx = configs.FindIndex(c => c.Id == newConfig.Id);
                if (idx >= 0) configs[idx] = newConfig;
            }
            else
            {
                newConfig.Id = ConfigStore.NextApiConfigId();
                configs.Insert(0, newConfig);
            }

            ConfigStore.SaveApiConfigs(configs);
            LogService.WriteInfo($"保存API配置: {newConfig.ApiName}", "ApiConfig");
            DialogResult = true;
            Close();
        }

        private void ShowErr(string msg) =>
            MessageBox.Show(msg, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
        private void Close_Click(object sender, RoutedEventArgs e)  { DialogResult = false; Close(); }
    }
}
