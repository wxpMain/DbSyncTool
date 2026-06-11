using System.Windows;
using DbSyncTool.Helpers;

namespace DbSyncTool
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            LogService.WriteInfo("应用程序启动", "App");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            LogService.WriteInfo("应用程序退出", "App");
            base.OnExit(e);
        }
    }
}
