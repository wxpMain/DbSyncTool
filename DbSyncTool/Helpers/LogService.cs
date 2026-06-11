using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DbSyncTool.Models;
using Newtonsoft.Json.Linq;

namespace DbSyncTool.Helpers
{
    /// <summary>
    /// 双日志文件服务 - 运行日志 + 错误日志
    /// </summary>
    public static class LogService
    {
        private const string LogDir = @"E:\MESCenterLog";

        private static readonly long MaxFileSizeBytes = 50 * 1024 * 1024; // 50MB

        static LogService()
        {
            // 有则直接用，没有则创建
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
        }

        private static string GetRunLogPath()
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            int seq = 1;
            while (true)
            {
                var path = Path.Combine(LogDir, $"中间库运行日志-{date}-{seq:D3}.log");
                if (!File.Exists(path)) return path;
                var info = new FileInfo(path);
                if (info.Length < MaxFileSizeBytes) return path;
                seq++;
            }
        }

        private static string GetErrorLogPath()
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            int seq = 1;
            while (true)
            {
                var path = Path.Combine(LogDir, $"中间库日志-{date}-{seq:D3}.log");
                if (!File.Exists(path)) return path;
                var info = new FileInfo(path);
                if (info.Length < MaxFileSizeBytes) return path;
                seq++;
            }
        }

        private static string GetApiLogPath(string apiName = "")
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            // 清理文件名非法字符
            var safeName = string.IsNullOrWhiteSpace(apiName)
                ? "API调用日志"
                : string.Concat(apiName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            int seq = 1;
            while (true)
            {
                var path = Path.Combine(LogDir, $"{safeName}-{date}-{seq:D3}.log");
                if (!File.Exists(path)) return path;
                var info = new FileInfo(path);
                if (info.Length < MaxFileSizeBytes) return path;
                seq++;
            }
        }

        /// <summary>写入API调用专项日志</summary>
        public static void WriteApiLog(
            string apiName, string method, string url,
            string requestBody, int? statusCode, string? responseBody,
            long elapsedMs, bool success, string? errorMsg = null)
        {
            lock (_lockApi)
            {
                var sb = new StringBuilder();
                // 从响应体里取业务状态码
                int? bizStatus = statusCode;
                try
                {
                    if (!string.IsNullOrWhiteSpace(responseBody))
                    {
                        var jobj = Newtonsoft.Json.Linq.JObject.Parse(responseBody);
                        if (jobj["status"] != null) bizStatus = (int)jobj["status"];
                    }
                }
                catch { }

                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{(success ? "SUCCESS" : "unsuccessful")}]");
                sb.AppendLine($"  API名称  : {apiName}");
                sb.AppendLine($"  请求方向 : {method} {url}");
                sb.AppendLine($"  请求体   : {requestBody}");
                if (bizStatus.HasValue)
                    sb.AppendLine($"  状态码   : {bizStatus}");
                if (!string.IsNullOrWhiteSpace(responseBody))
                    sb.AppendLine($"  响应体   : {(responseBody.Length > 500 ? responseBody[..500] + "..." : responseBody)}");
                sb.AppendLine($"  耗时     : {elapsedMs}ms");
                if (!string.IsNullOrWhiteSpace(errorMsg))
                    sb.AppendLine($"  错误信息 : {errorMsg}");
                sb.AppendLine(new string('-', 80));

                File.AppendAllText(GetApiLogPath(apiName), sb.ToString(), Encoding.UTF8);

                // 失败时同时写入错误日志
                if (!success)
                {
                    lock (_lock)
                    {
                        File.AppendAllText(GetErrorLogPath(), sb.ToString(), Encoding.UTF8);
                    }
                }
            }
        }

        private static readonly object _lockApi = new();
        private static readonly object _lock = new();

        public static void WriteInfo(string message, string module = "System")
            => Write(LogLevel.INFO, message, module, null, null);

        public static void WriteWarning(string message, string module = "System")
            => Write(LogLevel.WARNING, message, module, null, null);

        public static void WriteError(string message, Exception? ex = null, string? taskId = null, string module = "System")
            => Write(LogLevel.ERROR, message, module, ex, taskId);

        public static void WriteFatal(string message, Exception? ex = null, string module = "System")
            => Write(LogLevel.FATAL, message, module, ex, null);

        public static void WriteDebug(string message, string module = "System")
            => Write(LogLevel.DEBUG, message, module, null, null);

        private static void Write(LogLevel level, string message, string module, Exception? ex, string? taskId)
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{module}] {message}");
                if (taskId != null) sb.AppendLine($"  TaskId: {taskId}");
                if (ex != null)
                {
                    sb.AppendLine($"  ExType: {ex.GetType().FullName}");
                    sb.AppendLine($"  ExMsg:  {ex.Message}");
                    sb.AppendLine($"  Stack:  {ex.StackTrace}");
                }
                sb.AppendLine();

                var text = sb.ToString();

                // 运行日志 (INFO及以上)
                if (level >= LogLevel.INFO)
                {
                    File.AppendAllText(GetRunLogPath(), text, Encoding.UTF8);
                }

                // 错误日志 (ERROR及以上)
                if (level >= LogLevel.ERROR)
                {
                    File.AppendAllText(GetErrorLogPath(), text, Encoding.UTF8);
                }
            }
        }

        /// <summary>
        /// 读取日志文件内容
        /// </summary>
        public static List<LogEntry> ReadLogs(bool errorOnly, DateTime? from, DateTime? to, LogLevel? minLevel, string? keyword)
        {
            var result = new List<LogEntry>();
            var prefix = errorOnly ? "中间库日志" : "中间库运行日志";
            var files = Directory.GetFiles(LogDir, $"{prefix}-*.log");

            foreach (var file in files)
            {
                try
                {
                    var lines = File.ReadAllLines(file, Encoding.UTF8);
                    LogEntry? current = null;
                    var extraLines = new StringBuilder();

                    foreach (var line in lines)
                    {
                        if (line.StartsWith("[") && line.Length > 25)
                        {
                            if (current != null) AddEntry(result, current, from, to, minLevel, keyword);
                            current = ParseLogLine(line);
                            extraLines.Clear();
                        }
                        else if (current != null)
                        {
                            extraLines.AppendLine(line);
                            if (line.TrimStart().StartsWith("ExType:"))
                                current.ExceptionType = line.Replace("ExType:", "").Trim();
                            if (line.TrimStart().StartsWith("Stack:"))
                                current.StackTrace = line.Replace("Stack:", "").Trim();
                        }
                    }
                    if (current != null) AddEntry(result, current, from, to, minLevel, keyword);
                }
                catch { }
            }

            result.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
            return result;
        }

        private static void AddEntry(List<LogEntry> list, LogEntry entry, DateTime? from, DateTime? to, LogLevel? minLevel, string? keyword)
        {
            if (from.HasValue && entry.Timestamp < from.Value) return;
            if (to.HasValue && entry.Timestamp > to.Value) return;
            if (minLevel.HasValue && entry.Level < minLevel.Value) return;
            if (!string.IsNullOrWhiteSpace(keyword) &&
                !entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(entry);
        }

        private static LogEntry? ParseLogLine(string line)
        {
            try
            {
                // [2024-01-01 12:00:00.000] [INFO] [Module] Message
                var parts = line.Split(']');
                if (parts.Length < 4) return null;
                var ts = DateTime.Parse(parts[0].TrimStart('['));
                var level = Enum.Parse<LogLevel>(parts[1].Trim('[', ' '));
                var module = parts[2].Trim('[', ' ');
                var msg = string.Join("]", parts[3..]).Trim();
                return new LogEntry { Timestamp = ts, Level = level, Module = module, Message = msg };
            }
            catch { return null; }
        }

        public static string LogDirectory => LogDir;
    }
}
