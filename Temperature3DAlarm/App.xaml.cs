using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Temperature3DAlarm
{
    public partial class App : Application
    {
        public App()
        {
            // 捕获未处理异常并写入 crash.log，便于排查运行问题
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log("UnhandledException: " + e.ExceptionObject);

            DispatcherUnhandledException += (_, e) =>
            {
                Log("DispatcherUnhandledException: " + e.Exception);
                MessageBox.Show(e.Exception.ToString(), "运行时异常",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            };
        }

        internal static void Log(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
