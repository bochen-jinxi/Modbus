using System;
using System.IO;
using System.Windows;

namespace ChenboScada
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => DumpCrash(ex.ExceptionObject as Exception);
            DispatcherUnhandledException += (s, ex) =>
            {
                DumpCrash(ex.Exception);
                MessageBox.Show("运行异常，详情已写入 crash.log：\n" + ex.Exception.Message,
                    "SCADA 错误", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };
            base.OnStartup(e);
            try
            {
                new MainWindow().Show();
            }
            catch (Exception ex)
            {
                DumpCrash(ex);
                MessageBox.Show("启动失败，详情已写入 crash.log：\n" + ex.Message,
                    "SCADA 启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        static void DumpCrash(Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    DateTime.Now + "\r\n" + ex);
            }
            catch { }
        }
    }
}
