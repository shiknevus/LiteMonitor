using System;
using System.Collections.Generic;

namespace LiteMonitor.src.Core
{
    public static class MetricLabelResolver
    {
        // 监控项中文名 (设置面板与悬浮窗提示共用)
        public static readonly IReadOnlyDictionary<string, string> FriendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "CPU.Load", "CPU 使用率" },
            { "CPU.Temp", "CPU 温度" },
            { "CPU.Power", "CPU 功耗" },
            { "CPU.Clock", "CPU 频率" },
            { "GPU.Load", "GPU 使用率" },
            { "GPU.Temp", "GPU 温度" },
            { "GPU.VRAM", "GPU 显存使用率" },
            { "GPU.Power", "GPU 功耗" },
            { "MEM.Load", "内存使用率" },
            { "MOBO.Temp", "主板温度" },
            { "NET.Down", "下载速度" },
            { "NET.Up", "上传速度" },
            { "DATA.DayDown", "今日下载流量" },
            { "DATA.DayUp", "今日上传流量" },
            { "DISK.Read", "磁盘读取速度" },
            { "DISK.Write", "磁盘写入速度" },
            { "DISK.Temp", "磁盘温度" },
            { "DASH.Uptime", "系统运行时间" },
            { "BAT.Percent", "电池电量百分比" }
        };

        public static string GetFriendlyName(string key)
        {
            return FriendlyNames.TryGetValue(key ?? "", out var name) ? name : key ?? "";
        }

        public static string ResolveLabel(MonitorItemConfig? config)
        {
            if (config == null) return "";
            if (!string.IsNullOrEmpty(config.UserLabel)) return config.UserLabel;
            if (!string.IsNullOrEmpty(config.DynamicLabel)) return config.DynamicLabel;
            return "";
        }

        public static string ResolveShortLabel(MonitorItemConfig? config)
        {
            return ResolveLabel(config);
        }
    }
}
