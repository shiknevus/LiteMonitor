using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;

namespace LiteMonitor.src.SystemServices
{
    public static class AutoStart
    {
        private const string TaskName = "LiteMonitor_AutoStart";

        public static void Set(bool enabled)
        {
            string exePath = Process.GetCurrentProcess().MainModule!.FileName!;

            // 1. 网络路径拦截
            try
            {
                string root = Path.GetPathRoot(exePath)!;
                if ((!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network) || new Uri(exePath).IsUnc)
                {
                    MessageBox.Show("Windows 计划任务不支持在网络路径下运行。\n请移动到本地硬盘。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            catch { }

            if (enabled)
            {
                if (IsEnabledForCurrentExe(exePath)) return;

                // 使用 XML 方案配置 Windows 计划任务
                string tempXmlPath = Path.Combine(Path.GetTempPath(), $"LiteMonitor_Task_{Guid.NewGuid()}.xml");

                try
                {
                    var doc = GetTaskXml(exePath);
                    doc.Save(tempXmlPath);

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Create /TN \"{TaskName}\" /XML \"{tempXmlPath}\" /F",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };

                    using var p = Process.Start(startInfo);
                    p?.WaitForExit();
                    if (p != null && p.ExitCode != 0)
                        MessageBox.Show($"创建计划任务失败 (schtasks 返回 {p.ExitCode})", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"设置失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath);
                    }
                    catch { }
                }
            }
            else
            {
                // 删除计划任务
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Delete /TN \"{TaskName}\" /F",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(startInfo);
                p?.WaitForExit();
            }
        }

        // 任务存在但指向别的路径(如旧版本目录)时视为未启用
        public static bool IsEnabled() => IsEnabledForCurrentExe(Process.GetCurrentProcess().MainModule!.FileName!);

        // 启动时修复：任务指向的 exe 已不存在则改指当前 exe
        public static void RepairStalePath()
        {
            try
            {
                var (exists, command, _) = QueryTask();
                if (!exists || string.IsNullOrEmpty(command)) return;
                string exePath = Process.GetCurrentProcess().MainModule!.FileName!;
                if (string.Equals(command, exePath, StringComparison.OrdinalIgnoreCase) || File.Exists(command)) return;
                Set(true);
            }
            catch { }
        }

        private static bool IsEnabledForCurrentExe(string exePath)
        {
            var (exists, command, enabled) = QueryTask();
            return exists && enabled && string.Equals(command, exePath, StringComparison.OrdinalIgnoreCase);
        }

        private static (bool exists, string? command, bool enabled) QueryTask()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\" /XML")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                using var p = Process.Start(psi);
                if (p == null) return (false, null, false);

                string xml = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(xml)) return (false, null, false);

                var doc = XDocument.Parse(xml);
                var command = doc.Descendants()
                    .FirstOrDefault(x => x.Name.LocalName == "Command")
                    ?.Value?.Trim().Trim('"');
                var enabled = doc.Descendants()
                    .FirstOrDefault(x => x.Name.LocalName == "Enabled" &&
                                         x.Parent?.Name.LocalName == "Settings")
                    ?.Value;

                bool taskEnabled = !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
                return (true, command, taskEnabled);
            }
            catch
            {
                return (false, null, false);
            }
        }

        private static XDocument GetTaskXml(string exePath)
        {
            string exeDir = Path.GetDirectoryName(exePath)!;
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-16", null),
                new XElement(ns + "Task",
                    new XAttribute("version", "1.2"),
                    new XElement(ns + "RegistrationInfo",
                        new XElement(ns + "Description", "LiteMonitor Auto Start")
                    ),
                    new XElement(ns + "Triggers",
                        new XElement(ns + "LogonTrigger",
                            new XElement(ns + "Enabled", "true"),
                            new XElement(ns + "Delay", "PT5S")
                        )
                    ),
                    new XElement(ns + "Principals",
                        new XElement(ns + "Principal",
                            new XAttribute("id", "Author"),
                            new XElement(ns + "LogonType", "InteractiveToken"),
                            new XElement(ns + "RunLevel", "HighestAvailable")
                        )
                    ),
                    new XElement(ns + "Settings",
                        new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                        new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                        new XElement(ns + "StopIfGoingOnBatteries", "false"),
                        new XElement(ns + "AllowHardTerminate", "true"),
                        new XElement(ns + "StartWhenAvailable", "false"),
                        new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                        new XElement(ns + "IdleSettings",
                            new XElement(ns + "StopOnIdleEnd", "true"),
                            new XElement(ns + "RestartOnIdle", "false")
                        ),
                        new XElement(ns + "AllowStartOnDemand", "true"),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "Hidden", "false"),
                        new XElement(ns + "RunOnlyIfIdle", "false"),
                        new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                        new XElement(ns + "Priority", "7")
                    ),
                    new XElement(ns + "Actions",
                        new XAttribute("Context", "Author"),
                        new XElement(ns + "Exec",
                            new XElement(ns + "Command", exePath),
                            new XElement(ns + "WorkingDirectory", exeDir)
                        )
                    )
                )
            );

            return doc;
        }
    }
}

