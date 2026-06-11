using Microsoft.Win32;

namespace DbSyncTool.Helpers
{
    public static class StartupHelper
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "DbSyncTool";

        /// <summary>是否在调试模式（开发环境）</summary>
        private static bool IsDebug =>
#if DEBUG
            true;
#else
            false;
#endif

        /// <summary>是否已设置开机自启</summary>
        public static bool IsStartupEnabled()
        {
            if (IsDebug) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        /// <summary>设置/取消开机自启</summary>
        public static bool SetStartup(bool enable)
        {
            if (IsDebug)
            {
                LogService.WriteInfo($"[开发模式] 跳过开机自启设置（enable={enable}）", "StartupHelper");
                return true;
            }
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null) return false;
                if (enable)
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (string.IsNullOrEmpty(exePath)) return false;
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                LogService.WriteError("设置开机自启失败", ex, null, "StartupHelper");
                return false;
            }
        }
    }
}
