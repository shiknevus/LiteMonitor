using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LiteMonitor
{
    public static class SettingsHelper
    {
        private static readonly string _cachedPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        private static readonly string _backupPath = Path.Combine(AppContext.BaseDirectory, "settings.json.bak");
        private static readonly string _tempPath = Path.Combine(AppContext.BaseDirectory, "settings.json.tmp");
        private static readonly object _ioLock = new object();
        public static string FilePath => _cachedPath;

        public static bool GlobalBlockSave { get; set; } = false;

        public static Settings Load(bool forceReload = false)
        {
            Settings s;
            lock (_ioLock)
            {
                s = TryLoadFile(FilePath) ?? TryLoadFile(_backupPath) ?? new Settings();
            }

            if (s.MonitorItems == null || s.MonitorItems.Count == 0)
            {
                s.InitDefaultItems();
            }
            else
            {
                s.CheckAndAppendMissingItems();
            }

            return s;
        }

        public static void Save(this Settings settings)
        {
            if (GlobalBlockSave) return;

            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                lock (_ioLock)
                {
                    AtomicWrite(json);
                }
            }
            catch
            {
                CleanupTempFile();
            }
        }

        public static void DeleteStoredSettings()
        {
            lock (_ioLock)
            {
                DeleteFileIfExists(FilePath);
                DeleteFileIfExists(_backupPath);
                DeleteFileIfExists(_tempPath);
            }
        }

        private static Settings? TryLoadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                return null;
            }
        }

        private static void AtomicWrite(string json)
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_tempPath, json);

            if (File.Exists(FilePath))
            {
                try { File.Copy(FilePath, _backupPath, true); }
                catch { }
            }

            File.Move(_tempPath, FilePath, true);
        }

        private static void CleanupTempFile()
        {
            try
            {
                if (File.Exists(_tempPath)) File.Delete(_tempPath);
            }
            catch { }
        }

        private static void DeleteFileIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public static void InitDefaultItems(this Settings settings)
        {
            settings.MonitorItems = new List<MonitorItemConfig>
            {
                // CPU
                new MonitorItemConfig { Key = "CPU.Load",  SortIndex = 1, VisibleInPanel = true },
                new MonitorItemConfig { Key = "CPU.Temp",  SortIndex = 2, VisibleInPanel = true },
                new MonitorItemConfig { Key = "CPU.Power", SortIndex = 3, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Clock", SortIndex = 4, VisibleInPanel = false },

                // GPU
                new MonitorItemConfig { Key = "GPU.Load",  SortIndex = 5, VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.Temp",  SortIndex = 6, VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.VRAM",  SortIndex = 7, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.Power", SortIndex = 8, VisibleInPanel = false },

                // RAM & Host
                new MonitorItemConfig { Key = "MEM.Load",  SortIndex = 9, VisibleInPanel = true },
                new MonitorItemConfig { Key = "MOBO.Temp", SortIndex = 10, VisibleInPanel = false },

                // NET
                new MonitorItemConfig { Key = "NET.Down",  SortIndex = 11, VisibleInPanel = true },
                new MonitorItemConfig { Key = "NET.Up",    SortIndex = 12, VisibleInPanel = true },

                // 今日流量
                new MonitorItemConfig { Key = "DATA.DayDown", SortIndex = 13, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DATA.DayUp",   SortIndex = 14, VisibleInPanel = true },

                // DISK
                new MonitorItemConfig { Key = "DISK.Read", SortIndex = 15, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DISK.Write",SortIndex = 16, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DISK.Temp", SortIndex = 17, VisibleInPanel = false },

                // UPTIME
                new MonitorItemConfig { Key = "DASH.Uptime", SortIndex = 18, VisibleInPanel = true },

                // BATTERY
                new MonitorItemConfig { Key = "BAT.Percent", SortIndex = 19, VisibleInPanel = false },
            };
        }

        public static void CheckAndAppendMissingItems(this Settings settings)
        {
            var defaultList = new Settings();
            defaultList.InitDefaultItems();

            var currentKeys = new HashSet<string>(settings.MonitorItems.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);

            int maxIndex = settings.MonitorItems.Count > 0 ? settings.MonitorItems.Max(x => x.SortIndex) : 0;
            foreach (var def in defaultList.MonitorItems)
            {
                if (!currentKeys.Contains(def.Key))
                {
                    def.SortIndex = ++maxIndex;
                    settings.MonitorItems.Add(def);
                }
            }
        }
    }
}
