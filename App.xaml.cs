using System.Windows;

namespace CleanMaster
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 全局异常兜底:清理类工具一旦崩溃退出,容易让文件停留在"隔离区"
            // 造成用户困惑,这里统一捕获并给出提示,而不是让程序静默消失。
            DispatcherUnhandledException += (s, ex) =>
            {
                MessageBox.Show(
                    $"程序遇到错误,已自动停止本次操作以保证数据安全:\n\n{ex.Exception.Message}",
                    "CleanMaster - 出现异常",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                ex.Handled = true;
            };
        }
    }
}
