using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Serialization;

namespace LiteMonitor
{
    public class Settings
    {
        // ====== 悬浮条 (FloatBar) ======
        public bool FloatBarEnabled { get; set; } = true;
        public bool FloatBarPinned { get; set; } = false;
        public bool FloatBarAutoHide { get; set; } = true;
        public string FloatBarPosition { get; set; } = "top_center";
        public int FloatBarCustomX { get; set; } = -1;
        public int FloatBarCustomY { get; set; } = -1;

        // ====== 基础选项 ======
        public bool AutoStart { get; set; } = false;
        public int RefreshMs { get; set; } = 1000;
        public int MemoryDisplayMode { get; set; } = 1;

        // ====== 硬件首选绑定 ======
        public string PreferredNetwork { get; set; } = "";
        public string LastAutoNetwork { get; set; } = "";
        public string PreferredDisk { get; set; } = "";
        public string LastAutoDisk { get; set; } = "";
        public string PreferredGpu { get; set; } = "";
        public string PreferredCpuFan { get; set; } = "";
        public string PreferredCpuPump { get; set; } = "";
        public string PreferredCaseFan { get; set; } = "";
        public string PreferredMoboTemp { get; set; } = "";

        // ====== 性能与指标评估 ======
        public bool UseWinPerCounters { get; set; } = true;
        public bool IgnoreSmbTraffic { get; set; } = true;

        [JsonIgnore] public static float DetectedRamTotalGB { get; set; } = 0;
        [JsonIgnore] public static float DetectedGpuVramTotalGB { get; set; } = 0;

        public float RecordedMaxCpuPower { get; set; } = 65.0f;
        public float RecordedMaxCpuClock { get; set; } = 4200.0f;
        public float RecordedMaxGpuPower { get; set; } = 100.0f;
        public float RecordedMaxGpuClock { get; set; } = 1800.0f;
        public float RecordedMaxFps { get; set; } = 144.0f;
        public float RecordedMaxCpuFan { get; set; } = 4000;
        public float RecordedMaxCpuPump { get; set; } = 5000;
        public float RecordedMaxGpuFan { get; set; } = 3500;
        public float RecordedMaxChassisFan { get; set; } = 3000;

        [JsonIgnore] public long SessionUploadBytes { get; set; } = 0;
        [JsonIgnore] public long SessionDownloadBytes { get; set; } = 0;

        public ThresholdsSet Thresholds { get; set; } = new ThresholdsSet();

        // ====== 数值三档颜色 (#RRGGBB) ======
        public const string DefaultColorSafe = "#4ADE80";
        public const string DefaultColorWarn = "#FACC15";
        public const string DefaultColorCrit = "#F87171";
        public string ColorSafe { get; set; } = DefaultColorSafe;
        public string ColorWarn { get; set; } = DefaultColorWarn;
        public string ColorCrit { get; set; } = DefaultColorCrit;

        public static Color ParseColor(string? hex, string fallback)
        {
            try { return ColorTranslator.FromHtml(string.IsNullOrWhiteSpace(hex) ? fallback : hex); }
            catch { return ColorTranslator.FromHtml(fallback); }
        }

        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();

        [JsonIgnore]
        public static bool GlobalBlockSave 
        { 
            get => SettingsHelper.GlobalBlockSave; 
            set => SettingsHelper.GlobalBlockSave = value; 
        }

        private static Settings? _instance;

        public static Settings Load(bool forceReload = false)
        {
            if (_instance != null && !forceReload) return _instance;
            _instance = SettingsHelper.Load(forceReload);
            return _instance;
        }

        public Settings DeepClone()
        {
            var json = System.Text.Json.JsonSerializer.Serialize(this);
            return System.Text.Json.JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }

        public bool IsAnyEnabled(string prefix)
        {
            return MonitorItems.Any(x => x.VisibleInPanel && x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public void UpdateMaxRecord(string key, float val)
        {
            if (key == "CPU.Power" && val > RecordedMaxCpuPower) RecordedMaxCpuPower = val;
            else if (key == "CPU.Clock" && val > RecordedMaxCpuClock) RecordedMaxCpuClock = val;
            else if (key == "GPU.Power" && val > RecordedMaxGpuPower) RecordedMaxGpuPower = val;
            else if (key == "GPU.Clock" && val > RecordedMaxGpuClock) RecordedMaxGpuClock = val;
        }
    }

    public class MonitorItemConfig
    {
        public string Key { get; set; } = "";
        public string UserLabel { get; set; } = "";
        
        [JsonIgnore]
        public string DynamicLabel { get; set; } = "";

        [JsonIgnore]
        public string DisplayLabel => !string.IsNullOrEmpty(UserLabel) ? UserLabel : DynamicLabel;

        public bool VisibleInPanel { get; set; } = true;
        public int SortIndex { get; set; } = 0;
    }

    public class ThresholdsSet
    {
        public ValueRange Load { get; set; } = new ValueRange { Warn = 60, Crit = 85 };
        public ValueRange Temp { get; set; } = new ValueRange { Warn = 50, Crit = 70 };
        public ValueRange DiskIOMB { get; set; } = new ValueRange { Warn = 2, Crit = 8 };
        public ValueRange NetUpMB { get; set; } = new ValueRange { Warn = 1, Crit = 2 };
        public ValueRange NetDownMB { get; set; } = new ValueRange { Warn = 2, Crit = 8 };
        public ValueRange DataUpMB { get; set; } = new ValueRange { Warn = 512, Crit = 1024 };
        public ValueRange DataDownMB { get; set; } = new ValueRange { Warn = 2048, Crit = 5096 };
    }

    public class ValueRange
    {
        public double Warn { get; set; } = 0;
        public double Crit { get; set; } = 0;
    }
}

