using System;
using System.Drawing;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor
{
    public class MetricItem
    {
        public MonitorItemConfig? BoundConfig { get; set; }

        private string _key = "";
        public string Key 
        { 
            get => _key;
            set => _key = UIUtils.Intern(value ?? "");
        }

        private string _label = "";
        public string Label 
        {
            get 
            {
                string labelResolved = MetricLabelResolver.ResolveLabel(BoundConfig);
                if (!string.IsNullOrEmpty(labelResolved)) return labelResolved;
                return _label;
            }
            set => _label = UIUtils.Intern(value ?? "");
        }

        public float? Value { get; set; } = null;
        public float DisplayValue { get; set; } = 0f;
        public string? TextValue { get; set; } = null;

        private float _cachedDisplayValue = -99999f; 
        private (bool Ac, bool Charging) _cachedPowerState = (false, false);

        public string CachedValueText { get; private set; } = "";
        public string CachedUnitText { get; private set; } = "";
        public int CachedColorState { get; private set; } = 0;

        public Color GetTextColor()
        {
            return CachedColorState switch
            {
                1 => Color.FromArgb(251, 146, 60), // Warning
                2 => Color.FromArgb(239, 68, 68),  // Critical
                _ => Color.White
            };
        }

        public string GetFormattedText(bool isHorizontal = true)
        {
            if (TextValue != null) 
            {
                string defUnit = MetricUtils.GetUnitStr(Key, null, MetricUtils.UnitContext.Taskbar);
                CachedValueText = TextValue;
                CachedUnitText = defUnit;

                if (!string.IsNullOrEmpty(defUnit) && !TextValue.EndsWith(defUnit))
                {
                    return TextValue + defUnit;
                }
                return TextValue;
            }

            bool isBat = Key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase);
            var currentPower = MetricUtils.GetPowerStatus();
            bool powerChanged = isBat && (_cachedPowerState != currentPower);

            if (Math.Abs(DisplayValue - _cachedDisplayValue) > 0.005f || powerChanged)
            {
                _cachedDisplayValue = DisplayValue;
                if (isBat) _cachedPowerState = currentPower;

                string valStr = MetricUtils.GetValueStr(Key, DisplayValue, true);
                string unitStr = MetricUtils.GetUnitStr(Key, DisplayValue, MetricUtils.UnitContext.Taskbar);

                CachedValueText = valStr;
                CachedUnitText = unitStr;
                CachedColorState = MetricUtils.GetState(Key, DisplayValue);
            }

            return CachedValueText + CachedUnitText;
        }
    }
}

