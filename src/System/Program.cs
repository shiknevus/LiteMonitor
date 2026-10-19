using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor
{
    internal static class Program
    {
        private static Mutex? _mutex = null;

        [STAThread]
        static void Main()
        {
            bool createNew;
            string mutexName;

            try
            {
                string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;

                if (string.IsNullOrEmpty(exePath))
                {
                    mutexName = "Global\\LiteMonitor_SingleInstance_Mutex_UniqueKey";
                }
                else
                {
                    string? appFolderPath = Path.GetDirectoryName(exePath);

                    string? sanitizedPath = appFolderPath?.ToLower()
                                                        .Replace('\\', '_')
                                                        .Replace(':', '_')
                                                        .Replace('/', '_')
                                                        .Replace(' ', '_');

                    string baseName = $"Global\\LiteMonitor_SingleInstance_{sanitizedPath}_Mutex";
                    if (baseName.Length > 250) 
                    {
                         baseName = $"Global\\LiteMonitor_SingleInstance_{sanitizedPath.GetHashCode()}_Mutex";
                    }
                    
                    mutexName = baseName;
                }

                _mutex = new Mutex(true, mutexName, out createNew);
            }
            catch
            {
                mutexName = "Global\\LiteMonitor_SingleInstance_Mutex_UniqueKey";
                _mutex = new Mutex(true, mutexName, out createNew);
            }

            if (!createNew)
            {
                int showMsg = LiteMonitor.src.UI.Helpers.MainFormWinHelper.RegisterShowInstanceMessage();
                if (showMsg != 0)
                {
                    LiteMonitor.src.UI.Helpers.MainFormWinHelper.PostMessage((IntPtr)0xffff, (uint)showMsg, IntPtr.Zero, IntPtr.Zero);
                }
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            try
            {
                ApplicationConfiguration.Initialize();
                try
                {
                    DriverInstaller.CheckPawnIOBeforeHardware(Settings.Load());
                }
                catch { }

                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                LogCrash(ex, "Main_Catch");
            }
            finally
            {
                try 
                {
                    FpsCounter.ForceKillZombies(); 
                }
                catch { }

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

                MessageBox.Show($"程序遇到致命错误！\n错误日志已保存至：{logPath}\n\n原因：{ex.Message}", 
                                "LiteMonitor Crash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch 
            {
            }
        }
    }
}
