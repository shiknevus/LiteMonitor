using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.UI
{
    public class FloatBarForm : Form
    {
        public static FloatBarForm? Instance { get; private set; }

        private readonly Settings _cfg;
        private readonly HardwareMonitor _mon;
        private SettingsForm? _settingsForm;

        // 系统托盘图标与菜单
        private readonly NotifyIcon _trayIcon = new();
        private readonly ContextMenuStrip _menu = new();
        private ToolStripMenuItem? _menuPin;
        private ToolStripMenuItem? _menuAutoStart;

        // 定时器
        private readonly System.Windows.Forms.Timer _dataTimer = new();
        private readonly System.Windows.Forms.Timer _animTimer = new();
        private readonly System.Windows.Forms.Timer _pulseTimer = new();
        private readonly System.Windows.Forms.Timer _mouseCheckTimer = new();
        private readonly System.Windows.Forms.Timer _collapseDelayTimer = new();
        private readonly Stopwatch _trafficSaveWatch = Stopwatch.StartNew();

        // 动画控制
        private readonly Stopwatch _animStopwatch = new();
        private bool _isExpanded = true;
        private bool _isPinned = false;
        private bool _suppressAutoExpand = false; // 手动收起后，鼠标离开前不自动展开
        private float _animProgress = 1.0f; // 0.0 = 折叠, 1.0 = 展开
        private float _animStartProgress = 1.0f;
        private float _animTargetProgress = 1.0f;
        private const float ANIM_DURATION_EXPAND_MS = 300f; // 同 cc-switch duration-300
        private const float ANIM_DURATION_COLLAPSE_MS = 300f;

        // 尺寸标准 (逻辑像素，绘制时乘 dpi)
        private const float COLLAPSED_W = 176f;
        private const float COLLAPSED_H = 10f;
        // 视觉规格对齐 cc-switch-lite FloatBar (Tailwind px)
        private const float EXPANDED_H = 40f;   // h-[40px]
        private const float EXPANDED_TOP = 4f;  // top-1
        private const float SLIDE_Y = 24f;      // -translate-y-6
        private const float PHYSICAL_H = 48f;
        private const float FORM_MARGIN = 12f;
        private const float PAD_L = 12f;        // ps-3
        private const float PAD_R = 14f;        // pe-3.5
        private const float BTN_SIZE = 24f;     // h-6 w-6
        private const float BTN_ICON = 14f;     // h-3.5
        private const float BTN_GAP = 4f;       // gap-1
        private const float TOOLS_GAP = 8f;     // gap-2
        private const float ICON_SIZE = 14f;    // h-3.5
        private const float ICON_GAP = 4f;      // gap-1
        private const float UNIT_GAP = 2f;
        private const float ITEM_GAP = 5f;
        private const int MAX_METRICS = 14;

        // 脉冲与拖动
        private bool _isDragging = false;
        private bool _dragMoved = false;
        private Point _dragStartCursor;
        private Point _dragStartForm;

        // 指标布局缓存 (数据定时器刷新，动画帧只绘制)
        private sealed class MetricSlot
        {
            public string Key = "";
            public string Name = "";
            public string ValueText = "";
            public string UnitText = "";
            public Color Color = Color.White; // 图标与数值色(三档)
            public float SlotWidth;
            public RectangleF Bounds;
        }
        private List<MetricSlot> _slots = new();
        private float _expandedWidth = 380f;

        // 后台采集结果 (UI 线程只读快照)
        private Dictionary<string, float?> _values = new();
        private int _sampling;

        // 交互按钮
        private enum HoverTarget { None, Metric, Refresh, Pin, OpenSettings, Close }
        private HoverTarget _hoverTarget = HoverTarget.None;
        private int _hoverMetric = -1;

        private RectangleF _visualCapsuleRect;
        private RectangleF _rcRefresh;
        private RectangleF _rcPin;
        private RectangleF _rcSettings;
        private RectangleF _rcClose;

        private readonly ToolTip _toolTip = new()
        {
            InitialDelay = 400,
            ReshowDelay = 100,
            AutoPopDelay = 4000
        };

        // 字体缓存
        private Font? _fontVal;
        private Font? _fontUnit;
        private Font? _fontBrand;
        private static readonly StringFormat _typoFmt = CreateTypoFormat();
        private readonly Bitmap _measureBmp = new(1, 1, PixelFormat.Format32bppPArgb);
        private readonly Graphics _measureG;

        // 分层窗口离屏缓冲 (复用 DIB，避免每帧分配)
        private IntPtr _memDC = IntPtr.Zero;
        private IntPtr _hBmp = IntPtr.Zero;
        private IntPtr _oldBmp = IntPtr.Zero;
        private Bitmap? _buf;
        private int _bufW, _bufH;

        // Win32 常量与结构
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOPMOST = 0x00000008;

        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTTRANSPARENT = -1;

        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int ULW_ALPHA = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes;
            public ushort biBitCount; public uint biCompression; public uint biSizeImage;
            public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public static readonly int WM_SHOW_ME = RegisterWindowMessage("LiteMonitor_ShowInstance_Message");

        private static float Dpi => UIUtils.ScaleFactor > 0 ? UIUtils.ScaleFactor : 1.0f;

        public FloatBarForm(Settings? cfg = null)
        {
            Instance = this;
            _cfg = cfg ?? Settings.Load();
            _mon = HardwareMonitor.Instance ?? new HardwareMonitor(_cfg);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;

            _measureBmp.SetResolution(96, 96);
            _measureG = Graphics.FromImage(_measureBmp);
            _measureG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            CreateFonts();

            _isPinned = _cfg.FloatBarPinned;

            RefreshMetrics(resizeForm: false);
            this.Size = new Size(CalcFormWidth(), (int)Math.Ceiling(PHYSICAL_H * Dpi));
            AlignToTop();

            // 定时刷新硬件数据
            _dataTimer.Interval = Math.Max(120, _cfg.RefreshMs);
            _dataTimer.Tick += DataTick;
            _dataTimer.Start();

            // 动画定时器 (15ms 约 66FPS)
            _animTimer.Interval = 15;
            _animTimer.Tick += AnimTick;

            // 折叠态呼吸灯
            _pulseTimer.Interval = 40;
            _pulseTimer.Tick += (_, __) =>
            {
                if (_animProgress < 0.5f && !_animTimer.Enabled && Visible) RedrawLayeredWindow();
            };
            _pulseTimer.Start();

            // 鼠标位置巡检
            _mouseCheckTimer.Interval = 25;
            _mouseCheckTimer.Tick += MouseCheckTick;
            _mouseCheckTimer.Start();

            // 离开延迟折叠
            _collapseDelayTimer.Interval = 280;
            _collapseDelayTimer.Tick += (_, __) =>
            {
                _collapseDelayTimer.Stop();
                if (!_isPinned && !IsCursorInsideActiveZone())
                {
                    Collapse();
                }
            };

            InitMenu();
            InitTrayIcon();
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        private static StringFormat CreateTypoFormat()
        {
            var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return sf;
        }

        private void CreateFonts()
        {
            float dpi = Dpi;
            _fontVal?.Dispose();
            _fontUnit?.Dispose();
            _fontBrand?.Dispose();
            _fontVal = new Font("Consolas", 10f * dpi, FontStyle.Bold);
            _fontUnit = new Font("Segoe UI", 8f * dpi, FontStyle.Regular);
            _fontBrand = new Font("Consolas", 6.75f * dpi, FontStyle.Bold); // text-[9px]
        }

        private void InitTrayIcon()
        {
            try
            {
                _trayIcon.Icon = Properties.Resources.AppIcon ?? SystemIcons.Application;
            }
            catch
            {
                _trayIcon.Icon = SystemIcons.Application;
            }

            _trayIcon.Text = "LiteMonitor 悬浮窗";
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += (_, __) => ShowAndExpand();
            _trayIcon.ContextMenuStrip = _menu;
        }

        private void ShowAndExpand()
        {
            this.Show();
            AlignToTop();
            _suppressAutoExpand = false;
            Expand();
            this.BringToFront();
        }

        public void ApplySettingsChange()
        {
            _isPinned = _cfg.FloatBarPinned;
            if (_isPinned)
            {
                Expand();
            }

            _dataTimer.Interval = Math.Max(120, _cfg.RefreshMs);
            RefreshMetrics();
            AlignToTop();
            RedrawLayeredWindow();
            RequestSample();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                cp.ExStyle |= WS_EX_NOACTIVATE;
                cp.ExStyle |= WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RedrawLayeredWindow();
            RequestSample();
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                RefreshMetrics();
                AlignToTop();
                RedrawLayeredWindow();
            }));
        }

        public void AlignToTop()
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(this);
            int x = area.X + (area.Width - this.Width) / 2;
            int y = area.Y;

            string pos = _cfg.FloatBarPosition?.ToLowerInvariant() ?? "top_center";
            if (pos == "top_left")
            {
                x = area.X + 16;
            }
            else if (pos == "top_right")
            {
                x = area.X + area.Width - this.Width - 16;
            }
            else if (pos == "custom" && _cfg.FloatBarCustomX >= 0)
            {
                x = Math.Clamp(_cfg.FloatBarCustomX, area.X, Math.Max(area.X, area.X + area.Width - this.Width));
            }

            this.Location = new Point(x, y);
        }

        // ================= 数据刷新 =================

        private void DataTick(object? sender, EventArgs e)
        {
            if (!Visible) return;

            if (_trafficSaveWatch.Elapsed.TotalSeconds >= 60)
            {
                _trafficSaveWatch.Restart();
                Task.Run(TrafficLogger.Save);
            }

            RequestSample();
        }

        // LHM 采集耗时数十 ms，放后台避免卡住展开动画
        private void RequestSample()
        {
            if (Interlocked.Exchange(ref _sampling, 1) == 1) return;

            var keys = VisibleKeys();
            Task.Run(() =>
            {
                var vals = new Dictionary<string, float?>(keys.Count);
                try { _mon.UpdateAll(); } catch { }
                foreach (var k in keys)
                {
                    if (IsUptimeKey(k)) continue;
                    try { vals[k] = _mon.Get(k); } catch { vals[k] = null; }
                }

                try { BeginInvoke(new Action(() => OnSampled(vals))); }
                catch { Interlocked.Exchange(ref _sampling, 0); }
            });
        }

        private void OnSampled(Dictionary<string, float?> vals)
        {
            _values = vals;
            Interlocked.Exchange(ref _sampling, 0);

            // 完全折叠时只采集不绘制，展开时再刷新
            if (_animProgress <= 0f && !_animTimer.Enabled) return;

            RefreshMetrics();
            if (!_animTimer.Enabled) RedrawLayeredWindow();
        }

        private List<string> VisibleKeys()
        {
            var keys = _cfg.MonitorItems
                .Where(x => x.VisibleInPanel)
                .OrderBy(x => x.SortIndex)
                .Select(x => x.Key)
                .Take(MAX_METRICS)
                .ToList();
            return keys.Count > 0 ? keys : new List<string> { "CPU.Load", "MEM.Load", "NET.Down", "NET.Up" };
        }

        private void RefreshMetrics(bool resizeForm = true)
        {
            if (_fontVal == null || _fontUnit == null) return;

            float dpi = Dpi;
            var keys = VisibleKeys();

            var slots = new List<MetricSlot>(keys.Count);
            foreach (var key in keys)
            {
                var slot = CreateSlot(key);
                float valW = Math.Max(Measure(slot.ValueText, _fontVal), Measure(ValueTemplate(key), _fontVal));
                string unitTpl = UnitTemplate(key);
                float unitW = Math.Max(Measure(slot.UnitText, _fontUnit), Measure(unitTpl, _fontUnit));
                float w = ICON_SIZE * dpi + ICON_GAP * dpi + valW;
                if (unitW > 0) w += UNIT_GAP * dpi + unitW;
                slot.SlotWidth = (float)Math.Ceiling(w);
                slots.Add(slot);
            }

            // 超出屏幕宽度时丢弃末尾指标
            var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(this);
            float maxBar = area.Width - FORM_MARGIN * 2 * dpi - 32 * dpi;
            while (slots.Count > 1 && CalcExpandedWidth(slots, dpi) > maxBar) slots.RemoveAt(slots.Count - 1);

            _slots = slots;
            _expandedWidth = CalcExpandedWidth(slots, dpi);
            if (_hoverMetric >= _slots.Count) _hoverMetric = -1;

            if (resizeForm && !_isDragging)
            {
                int want = CalcFormWidth();
                if (want != this.Width)
                {
                    this.Width = want;
                    AlignToTop();
                }
            }
        }

        private static float CalcExpandedWidth(List<MetricSlot> slots, float dpi)
        {
            float metrics = 0f;
            for (int i = 0; i < slots.Count; i++)
            {
                metrics += slots[i].SlotWidth;
                if (i < slots.Count - 1) metrics += ITEM_GAP * 2 * dpi + 1f;
            }
            float tools = BTN_SIZE * 4 + BTN_GAP * 3;
            float w = PAD_L * dpi + metrics + (TOOLS_GAP * 2 + tools + PAD_R) * dpi + 1f;
            return (float)Math.Ceiling(Math.Max(w, (COLLAPSED_W + 40f) * dpi));
        }

        private int CalcFormWidth() => (int)Math.Ceiling(_expandedWidth + FORM_MARGIN * 2 * Dpi);

        private float Measure(string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            return (float)Math.Ceiling(_measureG.MeasureString(text, font, PointF.Empty, _typoFmt).Width);
        }

        private MetricSlot CreateSlot(string key)
        {
            var slot = new MetricSlot { Key = key, Name = MetricLabelResolver.GetFriendlyName(key) };

            if (IsUptimeKey(key))
            {
                slot.ValueText = FormatUptime(InfoService.Instance.GetUptimeTimeSpan());
                slot.Color = GetStateColor(MetricUtils.STATE_SAFE);
                return slot;
            }

            float? v = _values.TryGetValue(key, out var cached) ? cached : null;

            if (!v.HasValue || float.IsNaN(v.Value))
            {
                slot.ValueText = "--";
                slot.Color = Color.FromArgb(150, 150, 160);
                return slot;
            }

            slot.ValueText = MetricUtils.GetValueStr(key, v.Value, true);
            slot.UnitText = NormalizeUnit(MetricUtils.GetUnitStr(key, v.Value, MetricUtils.UnitContext.Panel));
            slot.Color = GetStateColor(MetricUtils.GetState(key, v.Value));
            return slot;
        }

        private static bool IsUptimeKey(string key) => key.IndexOf("UPTIME", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string FormatUptime(TimeSpan ts)
        {
            return ts.TotalDays < 1
                ? $"{ts.Hours}h {ts.Minutes:D2}m"
                : $"{(int)ts.TotalDays}d {ts.Hours}h";
        }

        private static string NormalizeUnit(string unit)
        {
            unit = (unit ?? "").Trim();
            return unit == "°C" ? "℃" : unit;
        }

        // 宽度模板：保证数值变化时槽位宽度稳定，不抖动不挤压
        private static string ValueTemplate(string key) => IsUptimeKey(key) ? "00h 00m" : "00.0";

        private string UnitTemplate(string key)
        {
            if (IsUptimeKey(key)) return "";
            return MetricUtils.GetType(key) switch
            {
                MetricType.DataSpeed => "MB/s",
                MetricType.DataSize => "MB",
                MetricType.Memory => _cfg.MemoryDisplayMode == 1 ? "GB" : "%",
                MetricType.Temperature => "℃",
                MetricType.Percent => "%",
                MetricType.Frequency => "GHz",
                MetricType.Power => "W",
                MetricType.RPM => "RPM",
                MetricType.FPS => "FPS",
                MetricType.Voltage => "V",
                MetricType.Current => "A",
                _ => ""
            };
        }

        private Color GetStateColor(int state)
        {
            if (state == MetricUtils.STATE_CRIT) return Settings.ParseColor(_cfg.ColorCrit, Settings.DefaultColorCrit);
            if (state == MetricUtils.STATE_WARN) return Settings.ParseColor(_cfg.ColorWarn, Settings.DefaultColorWarn);
            return Settings.ParseColor(_cfg.ColorSafe, Settings.DefaultColorSafe);
        }

        // ================= 展开/折叠 =================

        private bool IsCursorInsideActiveZone()
        {
            Point pt = PointToClient(Cursor.Position);
            float dpi = Dpi;

            if (_isExpanded || _animProgress > 0.3f)
            {
                float expW = _expandedWidth;
                float leftBound = (this.Width - expW) / 2f - 12f * dpi;
                float rightBound = (this.Width + expW) / 2f + 12f * dpi;
                return pt.X >= leftBound && pt.X <= rightBound && pt.Y >= -10f && pt.Y <= (EXPANDED_TOP + EXPANDED_H + 4f) * dpi;
            }
            else
            {
                float colW = COLLAPSED_W * dpi;
                float leftBound = (this.Width - colW) / 2f - 16f * dpi;
                float rightBound = (this.Width + colW) / 2f + 16f * dpi;
                return pt.X >= leftBound && pt.X <= rightBound && pt.Y >= -10f && pt.Y <= 20f * dpi;
            }
        }

        private void MouseCheckTick(object? sender, EventArgs e)
        {
            if (_isDragging || !Visible) return;

            bool isInside = IsCursorInsideActiveZone();

            if (isInside)
            {
                _collapseDelayTimer.Stop();
                if (!_isExpanded && _animTargetProgress == 0f && !_suppressAutoExpand)
                {
                    Expand();
                }
            }
            else
            {
                if (!_isExpanded) _suppressAutoExpand = false;
                if (_isExpanded && !_isPinned && !_collapseDelayTimer.Enabled)
                {
                    _collapseDelayTimer.Start();
                }
            }
        }

        public void Expand()
        {
            _collapseDelayTimer.Stop();
            if (_animProgress <= 0f) RefreshMetrics();
            _isExpanded = true;
            _animStartProgress = _animProgress;
            _animTargetProgress = 1.0f;
            _animStopwatch.Restart();
            _animTimer.Start();
        }

        public void Collapse()
        {
            if (_isPinned) return;

            _collapseDelayTimer.Stop();
            _isExpanded = false;
            _hoverTarget = HoverTarget.None;
            _hoverMetric = -1;
            _toolTip.Hide(this);
            _animStartProgress = _animProgress;
            _animTargetProgress = 0.0f;
            _animStopwatch.Restart();
            _animTimer.Start();
        }

        private void AnimTick(object? sender, EventArgs e)
        {
            float elapsed = _animStopwatch.ElapsedMilliseconds;
            float duration = (_animTargetProgress > _animStartProgress) ? ANIM_DURATION_EXPAND_MS : ANIM_DURATION_COLLAPSE_MS;
            float t = Math.Clamp(elapsed / duration, 0.0f, 1.0f);

            // cubic-bezier(0.16,1,0.3,1) 即 easeOutExpo
            float easedT = t >= 1f ? 1f : 1f - (float)Math.Pow(2, -10 * t);
            _animProgress = _animStartProgress + (_animTargetProgress - _animStartProgress) * easedT;

            if (t >= 1.0f)
            {
                _animProgress = _animTargetProgress;
                _animStopwatch.Stop();
                _animTimer.Stop();
            }

            RedrawLayeredWindow();
        }

        // ================= 窗口消息与鼠标 =================

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SHOW_ME && WM_SHOW_ME != 0)
            {
                this.BeginInvoke(new Action(ShowAndExpand));
                return;
            }

            if (m.Msg == WM_NCHITTEST)
            {
                long lParam = m.LParam.ToInt64();
                int screenX = (short)(lParam & 0xFFFF);
                int screenY = (short)((lParam >> 16) & 0xFFFF);

                Point clientPt = PointToClient(new Point(screenX, screenY));
                m.Result = (IntPtr)(IsPointInHitArea(clientPt) ? HTCLIENT : HTTRANSPARENT);
                return;
            }
            base.WndProc(ref m);
        }

        private bool IsPointInHitArea(Point pt)
        {
            return pt.X >= _visualCapsuleRect.Left && pt.X <= _visualCapsuleRect.Right
                && pt.Y >= 0 && pt.Y <= _visualCapsuleRect.Bottom;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isDragging)
            {
                Point cur = Cursor.Position;
                int deltaX = cur.X - _dragStartCursor.X;
                if (!_dragMoved && Math.Abs(deltaX) < 4) return;
                _dragMoved = true;

                var area = Screen.FromControl(this).WorkingArea;
                int newX = Math.Clamp(_dragStartForm.X + deltaX, area.Left, Math.Max(area.Left, area.Right - this.Width));
                this.Location = new Point(newX, area.Y);
                return;
            }

            if (!_isExpanded)
            {
                if (!_suppressAutoExpand) Expand();
                return;
            }

            HoverTarget newTarget = HoverTarget.None;
            int newMetric = -1;
            if (_rcRefresh.Contains(e.Location)) newTarget = HoverTarget.Refresh;
            else if (_rcPin.Contains(e.Location)) newTarget = HoverTarget.Pin;
            else if (_rcSettings.Contains(e.Location)) newTarget = HoverTarget.OpenSettings;
            else if (_rcClose.Contains(e.Location)) newTarget = HoverTarget.Close;
            else
            {
                for (int i = 0; i < _slots.Count; i++)
                {
                    if (_slots[i].Bounds.Contains(e.Location)) { newTarget = HoverTarget.Metric; newMetric = i; break; }
                }
            }

            if (newTarget != _hoverTarget || newMetric != _hoverMetric)
            {
                _hoverTarget = newTarget;
                _hoverMetric = newMetric;
                UpdateTooltip();
                RedrawLayeredWindow();
            }
        }

        private void UpdateTooltip()
        {
            string text = _hoverTarget switch
            {
                HoverTarget.Metric when _hoverMetric >= 0 && _hoverMetric < _slots.Count =>
                    $"{_slots[_hoverMetric].Name}：{_slots[_hoverMetric].ValueText} {_slots[_hoverMetric].UnitText}".TrimEnd(),
                HoverTarget.Refresh => "立即刷新硬件数据",
                HoverTarget.Pin => _isPinned ? "取消固定 (鼠标离开自动折叠)" : "固定常驻显示 (不自动折叠)",
                HoverTarget.OpenSettings => "打开设置面板",
                HoverTarget.Close => _isPinned ? "取消固定并收起" : "折叠收起",
                _ => ""
            };

            if (!string.IsNullOrEmpty(text)) _toolTip.SetToolTip(this, text);
            else _toolTip.Hide(this);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverTarget = HoverTarget.None;
            _hoverMetric = -1;
            _toolTip.Hide(this);
            RedrawLayeredWindow();

            if (_isExpanded && !_isPinned && !_collapseDelayTimer.Enabled)
            {
                _collapseDelayTimer.Start();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Left)
            {
                if (_isExpanded)
                {
                    if (_rcRefresh.Contains(e.Location)) { TriggerManualRefresh(); return; }
                    if (_rcPin.Contains(e.Location)) { TogglePin(); return; }
                    if (_rcSettings.Contains(e.Location)) { OpenSettingsWindow(); return; }
                    if (_rcClose.Contains(e.Location)) { ManualCollapse(); return; }
                }

                if (_visualCapsuleRect.Contains(e.Location))
                {
                    _isDragging = true;
                    _dragMoved = false;
                    _dragStartCursor = Cursor.Position;
                    _dragStartForm = this.Location;
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                _menu.Show(Cursor.Position);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_isDragging) return;

            _isDragging = false;
            if (!_dragMoved) return;

            var area = Screen.FromControl(this).WorkingArea;
            int midX = area.Left + (area.Width - this.Width) / 2;

            if (Math.Abs(this.Left - midX) < 70)
            {
                _cfg.FloatBarPosition = "top_center";
            }
            else if (this.Left - area.Left < 60)
            {
                _cfg.FloatBarPosition = "top_left";
            }
            else if (area.Right - (this.Left + this.Width) < 60)
            {
                _cfg.FloatBarPosition = "top_right";
            }
            else
            {
                _cfg.FloatBarPosition = "custom";
                _cfg.FloatBarCustomX = this.Left;
            }

            AlignToTop();
            _cfg.Save();
            RefreshMetrics();
            RedrawLayeredWindow();
        }

        private void ManualCollapse()
        {
            if (_isPinned)
            {
                _isPinned = false;
                _cfg.FloatBarPinned = false;
                _cfg.Save();
            }
            _suppressAutoExpand = true;
            Collapse();
        }

        private void TogglePin()
        {
            _isPinned = !_isPinned;
            _cfg.FloatBarPinned = _isPinned;
            _cfg.Save();

            if (_isPinned)
            {
                Expand();
            }
            UpdateTooltip();
            RedrawLayeredWindow();
        }

        public void OpenSettingsWindow()
        {
            if (_settingsForm == null || _settingsForm.IsDisposed)
            {
                _settingsForm = new SettingsForm(_cfg);
                _settingsForm.FormClosed += (_, __) =>
                {
                    _settingsForm = null;
                    ApplySettingsChange();
                };
                _settingsForm.Show();
            }
            else
            {
                if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
                _settingsForm.BringToFront();
                _settingsForm.Activate();
            }
        }

        private void TriggerManualRefresh() => RequestSample();

        private void InitMenu()
        {
            _menuPin = new ToolStripMenuItem("📌 固定在顶部");
            _menuPin.Click += (_, __) => TogglePin();
            _menu.Items.Add(_menuPin);

            var itemResetPos = new ToolStripMenuItem("居中对齐");
            itemResetPos.Click += (_, __) => { _cfg.FloatBarPosition = "top_center"; _cfg.Save(); AlignToTop(); RedrawLayeredWindow(); };
            _menu.Items.Add(itemResetPos);

            _menu.Items.Add(new ToolStripSeparator());

            _menuAutoStart = new ToolStripMenuItem("🚀 开机自启");
            _menuAutoStart.Click += (_, __) =>
            {
                bool next = !AutoStart.IsEnabled();
                AutoStart.Set(next);
                _cfg.AutoStart = next;
                _cfg.Save();
            };
            _menu.Items.Add(_menuAutoStart);

            var itemSettings = new ToolStripMenuItem("⚙ 监控项设置");
            itemSettings.Click += (_, __) => OpenSettingsWindow();
            _menu.Items.Add(itemSettings);

            _menu.Items.Add(new ToolStripSeparator());

            var itemExit = new ToolStripMenuItem("退出 LiteMonitor");
            itemExit.Click += (_, __) => Application.Exit();
            _menu.Items.Add(itemExit);

            _menu.Opening += (_, __) =>
            {
                _menuPin.Checked = _isPinned;
                _menuAutoStart.Checked = AutoStart.IsEnabled();
            };
        }

        // ================= 绘制 =================

        private bool EnsureBuffer(int w, int h)
        {
            if (_buf != null && _bufW == w && _bufH == h) return true;
            ReleaseBuffer();

            IntPtr screenDC = GetDC(IntPtr.Zero);
            try
            {
                _memDC = CreateCompatibleDC(screenDC);
                var bi = new BITMAPINFO();
                bi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bi.bmiHeader.biWidth = w;
                bi.bmiHeader.biHeight = -h; // 自上而下
                bi.bmiHeader.biPlanes = 1;
                bi.bmiHeader.biBitCount = 32;

                _hBmp = CreateDIBSection(_memDC, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (_hBmp == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    ReleaseBuffer();
                    return false;
                }
                _oldBmp = SelectObject(_memDC, _hBmp);

                // GDI+ 直接画进 DIB 内存，省去逐帧拷贝
                _buf = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
                _buf.SetResolution(96, 96);
                _bufW = w;
                _bufH = h;
                return true;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        private void ReleaseBuffer()
        {
            _buf?.Dispose();
            _buf = null;
            if (_hBmp != IntPtr.Zero)
            {
                SelectObject(_memDC, _oldBmp);
                DeleteObject(_hBmp);
            }
            if (_memDC != IntPtr.Zero) DeleteDC(_memDC);
            _hBmp = _oldBmp = _memDC = IntPtr.Zero;
            _bufW = _bufH = 0;
        }

        public void RedrawLayeredWindow()
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            int w = this.Width;
            int h = this.Height;
            if (w <= 0 || h <= 0 || !EnsureBuffer(w, h) || _buf == null) return;

            float dpi = Dpi;

            using (var g = Graphics.FromImage(_buf))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                float p = _animProgress;

                // 1. 折叠指示条：opacity 1→0，translate-y 0→-8
                float colAlpha = 1f - p;
                if (colAlpha > 0.01f)
                {
                    float colW = COLLAPSED_W * dpi;
                    float colH = COLLAPSED_H * dpi;
                    var colRect = new RectangleF((w - colW) / 2f, -8f * dpi * p, colW, colH);

                    DrawCollapsedCapsule(g, colRect, colAlpha, dpi);

                    if (p <= 0.5f) _visualCapsuleRect = colRect;
                }

                // 2. 展开监控栏：opacity 0→1，translate-y -24→0，scale 0.95→1
                if (p > 0.01f)
                {
                    float expW = _expandedWidth;
                    float expH = EXPANDED_H * dpi;
                    var expRect = new RectangleF((float)Math.Floor((w - expW) / 2f), EXPANDED_TOP * dpi, expW, expH);

                    var st = g.Save();
                    float scale = 0.95f + 0.05f * p;
                    float cx = expRect.Left + expRect.Width / 2f;
                    float cy = expRect.Top + expRect.Height / 2f;
                    g.TranslateTransform(cx, cy - SLIDE_Y * dpi * (1f - p));
                    g.ScaleTransform(scale, scale);
                    g.TranslateTransform(-cx, -cy);
                    DrawExpandedBar(g, expRect, p, dpi);
                    g.Restore(st);

                    if (p > 0.5f) _visualCapsuleRect = expRect;
                }
            }

            IntPtr screenDC = GetDC(IntPtr.Zero);
            try
            {
                var size = new SIZE(w, h);
                var src = new POINT(0, 0);
                var dst = new POINT(this.Left, this.Top);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };
                UpdateLayeredWindow(this.Handle, screenDC, ref dst, ref size, _memDC, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        private void DrawCollapsedCapsule(Graphics g, RectangleF rect, float alpha, float dpi)
        {
            // rounded-b-full：底角半径=高度，仅底边与两侧描边
            float r = rect.Height;
            using var fill = new GraphicsPath();
            fill.AddArc(rect.Left, rect.Bottom - r * 2, r * 2, r * 2, 180, -90);
            fill.AddLine(rect.Left + r, rect.Bottom, rect.Right - r, rect.Bottom);
            fill.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 90, -90);
            using var border = (GraphicsPath)fill.Clone();
            fill.CloseFigure();

            using (var bgBrush = new SolidBrush(Color.FromArgb((int)(242 * alpha), 24, 24, 27))) // zinc-900/95
                g.FillPath(bgBrush, fill);
            using (var borderPen = new Pen(Color.FromArgb((int)(77 * alpha), 168, 85, 247), 1f)) // purple-500/30
                g.DrawPath(borderPen, border);

            if (_fontBrand == null) return;

            // animate-pulse：2s 周期 opacity 1↔0.5
            double phase = (Environment.TickCount64 % 2000) / 2000.0;
            float pulse = (float)(0.75 + 0.25 * Math.Cos(phase * Math.PI * 2));
            int dotAlpha = (int)(255 * pulse * alpha);

            const string brandText = "LITEMONITOR";
            float textW = Measure(brandText, _fontBrand);
            float cx = rect.Left + rect.Width / 2f;
            float cy = rect.Top + rect.Height / 2f;

            float gap = 6f * dpi;   // gap-1.5
            float dotR = 3f * dpi;  // h-1.5 w-1.5
            using (var b1 = new SolidBrush(Color.FromArgb(dotAlpha, 192, 132, 252))) // purple-400
                g.FillEllipse(b1, cx - textW / 2f - gap - dotR * 2, cy - dotR, dotR * 2, dotR * 2);
            using (var b2 = new SolidBrush(Color.FromArgb(dotAlpha, 129, 140, 248))) // indigo-400
                g.FillEllipse(b2, cx + textW / 2f + gap, cy - dotR, dotR * 2, dotR * 2);

            using var textBrush = new SolidBrush(Color.FromArgb((int)(230 * alpha), 216, 180, 254)); // purple-300/90
            float textH = _fontBrand.GetHeight(g);
            g.DrawString(brandText, _fontBrand, textBrush, cx - textW / 2f, cy - textH / 2f, _typoFmt);
        }

        private void DrawExpandedBar(Graphics g, RectangleF rect, float alpha, float dpi)
        {
            using var path = CreateCapsulePath(rect);

            using (var brush = new SolidBrush(Color.FromArgb((int)(242 * alpha), 9, 9, 11))) // zinc-950/95
                g.FillPath(brush, path);
            using (var pen = new Pen(Color.FromArgb((int)(31 * alpha), 255, 255, 255), 1f)) // border-white/12
                g.DrawPath(pen, path);

            float btnSize = BTN_SIZE * dpi;
            float btnGap = BTN_GAP * dpi;
            float btnY = rect.Top + (rect.Height - btnSize) / 2f;

            // A. 右侧工具按钮
            float rightEdge = rect.Right - PAD_R * dpi - btnSize;
            _rcClose = new RectangleF(rightEdge, btnY, btnSize, btnSize);
            rightEdge -= btnGap + btnSize;
            _rcSettings = new RectangleF(rightEdge, btnY, btnSize, btnSize);
            rightEdge -= btnGap + btnSize;
            _rcPin = new RectangleF(rightEdge, btnY, btnSize, btnSize);
            rightEdge -= btnGap + btnSize;
            _rcRefresh = new RectangleF(rightEdge, btnY, btnSize, btnSize);

            DrawCircleButton(g, _rcClose, HoverTarget.Close, DrawCloseIcon, alpha);
            DrawCircleButton(g, _rcSettings, HoverTarget.OpenSettings, DrawSettingsIcon, alpha);
            DrawPinButton(g, _rcPin, _isPinned, _hoverTarget == HoverTarget.Pin, alpha);
            DrawCircleButton(g, _rcRefresh, HoverTarget.Refresh, DrawRefreshIcon, alpha);

            float divX = rightEdge - TOOLS_GAP * dpi;
            float divH = 12f * dpi;
            float divY = rect.Top + (rect.Height - divH) / 2f;
            using (var divPen = new Pen(Color.FromArgb((int)(26 * alpha), 255, 255, 255), 1f))
            {
                g.DrawLine(divPen, divX, divY, divX, divY + divH);
            }

            // B. 指标区域 (左对齐，宽度已按内容精确计算)
            DrawMetrics(g, rect.Left + PAD_L * dpi, rect, alpha, dpi);
        }

        private void DrawMetrics(Graphics g, float startX, RectangleF bar, float alpha, float dpi)
        {
            if (_fontVal == null || _fontUnit == null || _slots.Count == 0) return;

            float iconSize = ICON_SIZE * dpi;
            float valH = _fontVal.GetHeight(g);
            float valY = bar.Top + (bar.Height - valH) / 2f;
            float valAscent = Ascent(_fontVal, g);
            float unitY = valY + valAscent - Ascent(_fontUnit, g);
            float divH = 12f * dpi;
            float divY = bar.Top + (bar.Height - divH) / 2f;

            using var divPen = new Pen(Color.FromArgb((int)(26 * alpha), 255, 255, 255), 1f);
            using var hoverBrush = new SolidBrush(Color.FromArgb((int)(16 * alpha), 255, 255, 255));

            float x = startX;
            for (int i = 0; i < _slots.Count; i++)
            {
                var it = _slots[i];
                it.Bounds = new RectangleF(x - 3f * dpi, bar.Top + 4f * dpi, it.SlotWidth + 6f * dpi, bar.Height - 8f * dpi);

                if (_hoverTarget == HoverTarget.Metric && _hoverMetric == i)
                {
                    using var hp = CreateCapsulePath(it.Bounds);
                    g.FillPath(hoverBrush, hp);
                }

                Color c = Color.FromArgb((int)(it.Color.A * alpha), it.Color.R, it.Color.G, it.Color.B);
                var iconRect = new RectangleF(x, bar.Top + (bar.Height - iconSize) / 2f, iconSize, iconSize);
                DrawMetricIcon(g, it.Key, iconRect, c);

                float valX = x + iconSize + ICON_GAP * dpi;
                using (var valBrush = new SolidBrush(c))
                    g.DrawString(it.ValueText, _fontVal, valBrush, valX, valY, _typoFmt);

                if (!string.IsNullOrEmpty(it.UnitText))
                {
                    float unitX = valX + Measure(it.ValueText, _fontVal) + UNIT_GAP * dpi;
                    using var unitBrush = new SolidBrush(Color.FromArgb((int)(225 * alpha), 255, 255, 255));
                    g.DrawString(it.UnitText, _fontUnit, unitBrush, unitX, unitY, _typoFmt);
                }

                x += it.SlotWidth;
                if (i < _slots.Count - 1)
                {
                    x += ITEM_GAP * dpi;
                    g.DrawLine(divPen, x, divY, x, divY + divH);
                    x += ITEM_GAP * dpi + 1f;
                }
            }
        }

        private static float Ascent(Font f, Graphics g)
        {
            var ff = f.FontFamily;
            return f.GetHeight(g) * ff.GetCellAscent(f.Style) / ff.GetLineSpacing(f.Style);
        }

        // 图标在 12x12 设计空间内绘制，再按实际尺寸缩放
        private static void DrawMetricIcon(Graphics g, string key, RectangleF r, Color color)
        {
            var state = g.Save();
            g.TranslateTransform(r.X, r.Y);
            g.ScaleTransform(r.Width / 12f, r.Height / 12f);

            using var p = new Pen(color, 1.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var b = new SolidBrush(color);
            string k = key.ToUpperInvariant();

            if (k.Contains("UPTIME"))
            {
                g.DrawEllipse(p, 1f, 1f, 10f, 10f);
                g.DrawLines(p, new[] { new PointF(6f, 3.4f), new PointF(6f, 6f), new PointF(8.2f, 7.2f) });
            }
            else if (k.StartsWith("DATA."))
            {
                bool down = k.Contains("DOWN");
                float trayY = down ? 10.6f : 1.4f;
                g.DrawLine(p, 2f, trayY, 10f, trayY);
                if (down)
                {
                    g.DrawLine(p, 6f, 1.4f, 6f, 7.6f);
                    g.DrawLines(p, new[] { new PointF(3.4f, 5.2f), new PointF(6f, 7.8f), new PointF(8.6f, 5.2f) });
                }
                else
                {
                    g.DrawLine(p, 6f, 10.6f, 6f, 4.4f);
                    g.DrawLines(p, new[] { new PointF(3.4f, 6.8f), new PointF(6f, 4.2f), new PointF(8.6f, 6.8f) });
                }
            }
            else if (k.StartsWith("NET."))
            {
                if (k.Contains("DOWN"))
                {
                    g.DrawLine(p, 6f, 1.5f, 6f, 10.5f);
                    g.DrawLines(p, new[] { new PointF(2.8f, 7.3f), new PointF(6f, 10.5f), new PointF(9.2f, 7.3f) });
                }
                else
                {
                    g.DrawLine(p, 6f, 10.5f, 6f, 1.5f);
                    g.DrawLines(p, new[] { new PointF(2.8f, 4.7f), new PointF(6f, 1.5f), new PointF(9.2f, 4.7f) });
                }
            }
            else if (k.StartsWith("CPU"))
            {
                g.DrawRectangle(p, 2.5f, 2.5f, 7f, 7f);
                g.FillRectangle(b, 4.6f, 4.6f, 2.8f, 2.8f);
                foreach (float t in new[] { 4.5f, 7.5f })
                {
                    g.DrawLine(p, t, 0.6f, t, 2.5f);
                    g.DrawLine(p, t, 9.5f, t, 11.4f);
                    g.DrawLine(p, 0.6f, t, 2.5f, t);
                    g.DrawLine(p, 9.5f, t, 11.4f, t);
                }
            }
            else if (k.StartsWith("GPU"))
            {
                g.DrawRectangle(p, 1f, 2.8f, 10f, 6.4f);
                g.DrawEllipse(p, 4f, 4.2f, 3.6f, 3.6f);
                g.DrawLine(p, 3f, 9.2f, 3f, 11f);
                g.DrawLine(p, 9f, 9.2f, 9f, 11f);
            }
            else if (k.StartsWith("MEM"))
            {
                g.DrawRectangle(p, 1f, 3f, 10f, 5f);
                g.FillRectangle(b, 2.8f, 4.6f, 1.6f, 1.8f);
                g.FillRectangle(b, 5.2f, 4.6f, 1.6f, 1.8f);
                g.FillRectangle(b, 7.6f, 4.6f, 1.6f, 1.8f);
                for (float t = 2.5f; t <= 9.6f; t += 2.35f) g.DrawLine(p, t, 8f, t, 9.8f);
            }
            else if (k.StartsWith("DISK"))
            {
                g.DrawRectangle(p, 1.2f, 2.5f, 9.6f, 7f);
                g.DrawLine(p, 1.2f, 6.8f, 10.8f, 6.8f);
                g.FillEllipse(b, 8.2f, 7.6f, 1.4f, 1.4f);
            }
            else if (k.StartsWith("BAT"))
            {
                g.DrawRectangle(p, 1f, 3.2f, 9f, 5.6f);
                g.DrawLine(p, 11.2f, 5f, 11.2f, 7f);
                g.FillRectangle(b, 2.4f, 4.6f, 4.4f, 2.8f);
            }
            else if (k.Contains("TEMP"))
            {
                g.DrawLine(p, 6f, 1.2f, 6f, 7f);
                g.DrawEllipse(p, 3.8f, 6.8f, 4.4f, 4.4f);
                g.FillEllipse(b, 4.9f, 7.9f, 2.2f, 2.2f);
            }
            else
            {
                g.DrawLines(p, new[]
                {
                    new PointF(0.8f, 6.5f), new PointF(3.5f, 6.5f), new PointF(5f, 2f),
                    new PointF(7f, 10f), new PointF(8.5f, 6.5f), new PointF(11.2f, 6.5f)
                });
            }

            g.Restore(state);
        }

        private void DrawCircleButton(Graphics g, RectangleF rect, HoverTarget target, Action<Graphics, RectangleF, Color> drawIcon, float alpha)
        {
            bool isHover = (_hoverTarget == target);

            // hover:bg-white/10，图标 zinc-400 → zinc-100
            if (isHover)
            {
                using var hBrush = new SolidBrush(Color.FromArgb((int)(26 * alpha), 255, 255, 255));
                g.FillEllipse(hBrush, rect);
            }

            Color iconColor = isHover
                ? Color.FromArgb((int)(255 * alpha), 244, 244, 245)
                : Color.FromArgb((int)(255 * alpha), 161, 161, 170);

            drawIcon(g, rect, iconColor);
        }

        // Lucide 图标：24 单位坐标映射到按钮中心 BTN_ICON 大小，线宽 2
        private static GraphicsState BeginLucide(Graphics g, RectangleF btn)
        {
            var st = g.Save();
            float s = btn.Width / BTN_SIZE * BTN_ICON / 24f;
            g.TranslateTransform(btn.X + btn.Width / 2f, btn.Y + btn.Height / 2f);
            g.ScaleTransform(s, s);
            g.TranslateTransform(-12f, -12f);
            return st;
        }

        private static Pen LucidePen(Color c) =>
            new Pen(c, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        private static PointF P(float x, float y) => new(x, y);

        private void DrawPinButton(Graphics g, RectangleF rect, bool pinned, bool isHover, float alpha)
        {
            // 固定态 text-purple-400 bg-purple-500/20
            if (isHover || pinned)
            {
                Color pinBg = pinned
                    ? Color.FromArgb((int)(51 * alpha), 168, 85, 247)
                    : Color.FromArgb((int)(26 * alpha), 255, 255, 255);
                using var hBrush = new SolidBrush(pinBg);
                g.FillEllipse(hBrush, rect);
            }

            Color iconColor = pinned
                ? Color.FromArgb((int)(255 * alpha), 192, 132, 252)
                : (isHover ? Color.FromArgb((int)(255 * alpha), 244, 244, 245) : Color.FromArgb((int)(255 * alpha), 161, 161, 170));

            var st = BeginLucide(g, rect);
            using var pen = LucidePen(iconColor);
            g.DrawLine(pen, 12f, 17f, 12f, 22f);

            if (pinned)
            {
                using var body = new GraphicsPath();
                body.AddLines(new[] { P(9, 7), P(9, 10.76f), P(7.89f, 12.55f), P(6.11f, 13.45f), P(5, 15.24f), P(5, 17),
                                      P(19, 17), P(19, 15.24f), P(17.89f, 13.45f), P(16.11f, 12.55f), P(15, 10.76f), P(15, 7), P(16, 6) });
                body.AddArc(14, 2, 4, 4, 90, -180);
                body.AddLine(16, 2, 8, 2);
                body.AddArc(6, 2, 4, 4, 270, -180);
                body.CloseFigure();
                g.DrawPath(pen, body);
            }
            else
            {
                using var top = new GraphicsPath();
                top.AddLines(new[] { P(15, 9.34f), P(15, 7), P(16, 6) });
                top.AddArc(14, 2, 4, 4, 90, -180);
                top.AddLine(16, 2, 7.89f, 2);
                g.DrawPath(pen, top);
                g.DrawLines(pen, new[] { P(9, 9), P(9, 10.76f), P(7.89f, 12.55f), P(6.11f, 13.45f), P(5, 15.24f), P(5, 17), P(17, 17) });
                g.DrawLine(pen, 2f, 2f, 22f, 22f);
            }
            g.Restore(st);
        }

        // lucide refresh-cw
        private static void DrawRefreshIcon(Graphics g, RectangleF rect, Color color)
        {
            var st = BeginLucide(g, rect);
            using var pen = LucidePen(color);
            g.DrawArc(pen, 3, 3, 18, 18, 180, 150);
            g.DrawLines(pen, new[] { P(21, 3), P(21, 8), P(16, 8) });
            g.DrawArc(pen, 3, 3, 18, 18, 0, 150);
            g.DrawLines(pen, new[] { P(8, 16), P(3, 16), P(3, 21) });
            g.Restore(st);
        }

        // lucide sliders-horizontal
        private static void DrawSettingsIcon(Graphics g, RectangleF rect, Color color)
        {
            var st = BeginLucide(g, rect);
            using var pen = LucidePen(color);
            g.DrawLine(pen, 21, 4, 14, 4); g.DrawLine(pen, 10, 4, 3, 4);
            g.DrawLine(pen, 21, 12, 12, 12); g.DrawLine(pen, 8, 12, 3, 12);
            g.DrawLine(pen, 21, 20, 16, 20); g.DrawLine(pen, 12, 20, 3, 20);
            g.DrawLine(pen, 14, 2, 14, 6); g.DrawLine(pen, 8, 10, 8, 14); g.DrawLine(pen, 16, 18, 16, 22);
            g.Restore(st);
        }

        // lucide chevron-up (收起)
        private static void DrawCloseIcon(Graphics g, RectangleF rect, Color color)
        {
            var st = BeginLucide(g, rect);
            using var pen = LucidePen(color);
            g.DrawLines(pen, new[] { P(6, 15), P(12, 9), P(18, 15) });
            g.Restore(st);
        }

        private static GraphicsPath CreateCapsulePath(RectangleF rect)
        {
            var path = new GraphicsPath();
            float r = rect.Height / 2f;
            if (r <= 0) r = 1f;

            path.AddArc(rect.Left, rect.Top, r * 2, r * 2, 90, 180);
            path.AddLine(rect.Left + r, rect.Top, rect.Right - r, rect.Top);
            path.AddArc(rect.Right - r * 2, rect.Top, r * 2, r * 2, 270, 180);
            path.AddLine(rect.Right - r, rect.Bottom, rect.Left + r, rect.Bottom);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _dataTimer.Stop();
                _dataTimer.Dispose();
                _animTimer.Stop();
                _animTimer.Dispose();
                _pulseTimer.Stop();
                _pulseTimer.Dispose();
                _mouseCheckTimer.Stop();
                _mouseCheckTimer.Dispose();
                _collapseDelayTimer.Stop();
                _collapseDelayTimer.Dispose();
                _toolTip.Dispose();
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _menu.Dispose();
                _fontVal?.Dispose();
                _fontUnit?.Dispose();
                _fontBrand?.Dispose();
                _measureG.Dispose();
                _measureBmp.Dispose();
                _mon.Dispose();
            }
            ReleaseBuffer();
            base.Dispose(disposing);
        }
    }
}
