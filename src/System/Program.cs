using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI;

namespace LiteMonitor
{
    internal static class Program
    {
        private static Mutex? _mutex = null;

        [STAThread]
        static void Main()
        {
            bool createNew;
            string mutexName = "Global\\LiteMonitor_SingleInstance_Mutex_UniqueKey";

            try
            {
                string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    string? appFolderPath = Path.GetDirectoryName(exePath);
                    string? sanitizedPath = appFolderPath?.ToLower()
                                                        .Replace('\\', '_')
                                                        .Replace(':', '_')
                                                        .Replace('/', '_')
                                                        .Replace(' ', '_');

                    mutexName = $"Global\\LiteMonitor_SingleInstance_{sanitizedPath}_Mutex";
                }

                _mutex = new Mutex(true, mutexName, out createNew);
            }
            catch
            {
                _mutex = new Mutex(true, mutexName, out createNew);
            }

            if (!createNew)
            {
                int showMsg = FloatBarForm.WM_SHOW_ME;
                if (showMsg != 0)
                {
                    FloatBarForm.PostMessage((IntPtr)0xffff, (uint)showMsg, IntPtr.Zero, IntPtr.Zero);
                }
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            try
            {
                ApplicationConfiguration.Initialize();
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    UIUtils.ScaleFactor = g.DpiX / 96f;
                }
                TrafficLogger.Load();
                System.Threading.Tasks.Task.Run(LiteMonitor.src.SystemServices.AutoStart.RepairStalePath);
                Application.Run(new FloatBarForm());
            }
            catch (Exception ex)
            {
                LogCrash(ex, "Main_Catch");
            }
            finally
            {
                try { TrafficLogger.Save(); } catch { }
                if (_mutex != null)
                {
                    try { _mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            LogCrash(e.Exception, "UI_Thread");
        }

        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogCrash(e.ExceptionObject as Exception, "Background_Thread");
        }

        static void LogCrash(Exception? ex, string source)
        {
            if (ex == null) return;

            try
            {
                string logPath = Path.Combine(AppContext.BaseDirectory, "LiteMonitor_Error.log");
                string errorMsg = "==================================================\n" +
                                  $"[Time]: {DateTime.Now}\n" +
                                  $"[Source]: {source}\n" +
                                  $"[Message]: {ex.Message}\n" +
                                  $"[Stack]:\n{ex.StackTrace}\n" +
                                  "==================================================\n\n";

                File.AppendAllText(logPath, errorMsg);
                MessageBox.Show($"程序发生异常已记录到日志: {logPath}\n\n原因: {ex.Message}", 
                                "LiteMonitor 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch 
            {
            }
        }
    }
}

