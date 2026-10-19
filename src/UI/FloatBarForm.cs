using LiteMonitor.src.SystemServices.InfoService;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.src.UI
{
    /// <summary>
    /// 桌面灵动胶囊悬浮栏 (FloatBar)
    /// 严格对齐 D:\ccswitch\cc-switch-lite 的悬浮顶栏显示效果、尺寸与交互细节：
    /// 1. 高度完全齐平：展开胶囊高度严格为 38px (对齐 cc-switch-lite 的 h-[40px])，收起胶囊高度 10px (对齐 h-2.5)；
    /// 2. 动效纯净优雅：展开为垂直平滑滑降 + 透明度淡入，收起为垂直平滑滑升 + 淡出，全程零横向发散变形；
    /// 3. 双呼吸灯紧贴：收起状态下双呼吸灯紧贴居中文字两端 (gap 约 6px)，绝不分散在两端；
    /// 4. 字体排版与清洗：数值采用 Consolas 等宽粗体，单位采用 Segoe UI 7pt 浅灰小字，自适应流式排版杜绝字体重叠；
    /// 5. 拖拽与屏幕边缘磁吸：拖拽放手后智能保持在屏幕边缘或正中，收起指示条永远共轴对齐，绝不出现拖到边缘后隐藏丢失；
    /// 6. 贴顶防抖：鼠标在屏幕最顶端滑动稳定感应，绝不误判移出。
    /// </summary>
    public class FloatBarForm : Form
    {
        private readonly Settings _cfg;
        private readonly UIController _ui;
        private readonly MainForm _mainForm;

        // 定时器
        private readonly System.Windows.Forms.Timer _dataTimer = new();
        private readonly System.Windows.Forms.Timer _animTimer = new();
        private readonly System.Windows.Forms.Timer _pulseTimer = new();
        private readonly System.Windows.Forms.Timer _mouseCheckTimer = new();
        private readonly System.Windows.Forms.Timer _collapseDelayTimer = new();

        // 高精度补间动画
        private readonly Stopwatch _animStopwatch = new();
        private bool _isExpanded = true;      // 默认启动时展开显示
        private bool _isPinned = false;
        private float _animProgress = 1.0f;   // 0.0 = 完全收缩, 1.0 = 完全展开
        private float _animStartProgress = 1.0f;
        private float _animTargetProgress = 1.0f;
        private const float ANIM_DURATION_EXPAND_MS = 250f;   // 展开 250ms
        private const float ANIM_DURATION_COLLAPSE_MS = 210f; // 收起 210ms

        // 精确对齐 cc-switch-lite 的物理尺寸基准
        private const float COLLAPSED_W = 176f; // cc-switch-lite: w-44 = 176px
        private const float COLLAPSED_H = 10f;  // cc-switch-lite: h-2.5 = 10px
        private const float EXPANDED_W = 768f;  // 展开胶囊宽度
        private const float EXPANDED_H = 38f;   // 高度严格减小，与 cc-switch-lite 的 h-[40px] 齐平
        private const float PHYSICAL_W = 786f;  // 窗体物理宽度
        private const float PHYSICAL_H = 46f;   // 窗体物理高度

        // 呼吸灯脉冲计数
        private int _pulseTick = 0;

        // 拖拽与中心位置保持
        private bool _isDragging = false;
        private Point _dragStartCursor;
        private Point _dragStartForm;

        private float _lastMetricsWidth = 0f;

        private float GetCurrentExpandedWidth(float dpi)
        {
            float sideWidth = 152f * dpi;
            float mw = _lastMetricsWidth > 0 ? _lastMetricsWidth : 420f * dpi;
            float total = mw + sideWidth;
            return Math.Clamp((float)Math.Ceiling(total / 4f) * 4f, 380f * dpi, (PHYSICAL_W - 12f) * dpi);
        }

        // 按钮交互
        private enum HoverTarget { None, Badge, Refresh, Pin, OpenSettings, Close }
        private HoverTarget _hoverTarget = HoverTarget.None;

        // 视觉矩形与按钮热区
        private RectangleF _visualCapsuleRect;
        private RectangleF _rcBadge;
        private RectangleF _rcRefresh;
        private RectangleF _rcPin;
        private RectangleF _rcSettings;
        private RectangleF _rcClose;

        // 悬浮提示
        private readonly ToolTip _toolTip = new()
        {
            InitialDelay = 400,
            ReshowDelay = 100,
            AutoPopDelay = 4000
        };

        // Win32 常量
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

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        public FloatBarForm(Settings cfg, UIController ui, MainForm mainForm)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;

            float dpi = UIUtils.ScaleFactor > 0 ? UIUtils.ScaleFactor : 1.0f;
            this.Size = new Size((int)(PHYSICAL_W * dpi), (int)(PHYSICAL_H * dpi));

            _isPinned = _cfg.FloatBarPinned;
            if (_isPinned)
            {
                _isExpanded = true;
                _animProgress = 1.0f;
                _animTargetProgress = 1.0f;
            }

            AlignToTop();

            // 数据刷新定时器 (120ms)
            _dataTimer.Interval = Math.Max(120, _cfg.RefreshMs);
            _dataTimer.Tick += (_, __) => { if (Visible) RedrawLayeredWindow(); };
            _dataTimer.Start();

            // 动画补间定时器 (15ms 约 66FPS)
            _animTimer.Interval = 15;
            _animTimer.Tick += AnimTick;

            // 呼吸灯脉冲定时器 (35ms)
            _pulseTimer.Interval = 35;
            _pulseTimer.Tick += (_, __) =>
            {
                _pulseTick++;
                if (_animProgress < 0.5f && Visible) RedrawLayeredWindow();
            };
            _pulseTimer.Start();

            // 鼠标位置巡检 (25ms)
            _mouseCheckTimer.Interval = 25;
            _mouseCheckTimer.Tick += MouseCheckTick;
            _mouseCheckTimer.Start();

            // 离开延迟 (280ms，与 cc-switch-lite 一致)
            _collapseDelayTimer.Interval = 280;
            _collapseDelayTimer.Tick += (_, __) =>
            {
                _collapseDelayTimer.Stop();
                if (!_isPinned && !IsCursorInsideActiveZone())
                {
                    Collapse();
                }
            };
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
        }

        public void AlignToTop()
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(this);
            int x = area.X + (area.Width - this.Width) / 2;
            int y = area.Y; // 严格贴顶

            string pos = _cfg.FloatBarPosition?.ToLowerInvariant() ?? "top_center";
            if (pos == "top_left")
            {
                x = area.X + 24;
            }
            else if (pos == "top_right")
            {
                x = area.X + area.Width - this.Width - 24;
            }
            else if (pos == "custom" && _cfg.FloatBarCustomX >= 0)
            {
                x = Math.Clamp(_cfg.FloatBarCustomX, area.X, area.X + area.Width - this.Width);
            }

            this.Location = new Point(x, y);
        }

        // ====================================================================
        // 感应判定 (严格覆盖贴顶区域，无论在正中还是边缘都永不丢失)
        // ====================================================================

        private bool IsCursorInsideActiveZone()
        {
            Point pt = PointToClient(Cursor.Position);
            float dpi = UIUtils.ScaleFactor > 0 ? UIUtils.ScaleFactor : 1.0f;

            if (_isExpanded || _animProgress > 0.3f)
            {
                // 展开状态：覆盖展开大胶囊区域 (高度减小到与 cc-switch-lite 齐平)
                float expW = GetCurrentExpandedWidth(dpi);
                float leftBound = (this.Width - expW) / 2f - 12f * dpi;
                float rightBound = (this.Width + expW) / 2f + 12f * dpi;
                float topBound = -10f; // 允许贴顶边缘容错
                float bottomBound = 44f * dpi;

                return (pt.X >= leftBound && pt.X <= rightBound && pt.Y >= topBound && pt.Y <= bottomBound);
            }
            else
            {
                // 收起状态：紧贴居中的收起指示条区域
                float colW = COLLAPSED_W * dpi;
                float leftBound = (this.Width - colW) / 2f - 16f * dpi;
                float rightBound = (this.Width + colW) / 2f + 16f * dpi;
                float topBound = -10f; // 屏幕最顶端 Y<=0 始终有效
                float bottomBound = 20f * dpi;

                return (pt.X >= leftBound && pt.X <= rightBound && pt.Y >= topBound && pt.Y <= bottomBound);
            }
        }

        private void MouseCheckTick(object? sender, EventArgs e)
        {
            if (_isDragging || !Visible) return;

            bool isInside = IsCursorInsideActiveZone();

            if (isInside)
            {
                _collapseDelayTimer.Stop();
                if (!_isExpanded && _animTargetProgress == 0f)
                {
                    Expand();
                }
            }
            else
            {
                if (_isExpanded && !_isPinned && !_collapseDelayTimer.Enabled)
                {
                    _collapseDelayTimer.Start();
                }
            }
        }

        public void Expand()
        {
            _collapseDelayTimer.Stop();
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

            // Ease-Out 阻尼减速曲线
            float easedT = 1.0f - (float)Math.Pow(1.0f - t, 3);
            _animProgress = _animStartProgress + (_animTargetProgress - _animStartProgress) * easedT;

            if (t >= 1.0f)
            {
                _animProgress = _animTargetProgress;
                _animStopwatch.Stop();
                _animTimer.Stop();
            }

            RedrawLayeredWindow();
        }

        // ====================================================================
        // 击中测试：穿透全透明区域
        // ====================================================================
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                int lParam = m.LParam.ToInt32();
                int screenX = lParam & 0xFFFF;
                int screenY = (lParam >> 16) & 0xFFFF;
                if (screenX > 32767) screenX -= 65536;
                if (screenY > 32767) screenY -= 65536;

                Point clientPt = PointToClient(new Point(screenX, screenY));

                if (IsPointInHitArea(clientPt))
                {
                    m.Result = (IntPtr)HTCLIENT;
                    return;
                }
                else
                {
                    m.Result = (IntPtr)HTTRANSPARENT;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private bool IsPointInHitArea(Point pt)
        {
            float left = _visualCapsuleRect.Left;
            float right = _visualCapsuleRect.Right;
            float bottom = _visualCapsuleRect.Bottom;

            return (pt.X >= left && pt.X <= right && pt.Y >= 0 && pt.Y <= bottom);
        }

        // ====================================================================
        // 鼠标事件与智能吸顶拖拽 (拖到边缘后收起状态永远可见、绝不丢失)
        // ====================================================================

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isDragging)
            {
                Point cur = Cursor.Position;
                int deltaX = cur.X - _dragStartCursor.X;
                int newX = _dragStartForm.X + deltaX;

                var area = Screen.FromControl(this).WorkingArea;
                // 限制在屏幕工作区左右边界内，Y 严格保持贴顶
                newX = Math.Clamp(newX, area.Left, area.Right - this.Width);

                this.Location = new Point(newX, area.Y);
                return;
            }

            if (!_isExpanded)
            {
                Expand();
                return;
            }

            HoverTarget newTarget = HoverTarget.None;
            if (_rcBadge.Contains(e.Location)) newTarget = HoverTarget.Badge;
            else if (_rcRefresh.Contains(e.Location)) newTarget = HoverTarget.Refresh;
            else if (_rcPin.Contains(e.Location)) newTarget = HoverTarget.Pin;
            else if (_rcSettings.Contains(e.Location)) newTarget = HoverTarget.OpenSettings;
            else if (_rcClose.Contains(e.Location)) newTarget = HoverTarget.Close;

            if (newTarget != _hoverTarget)
            {
                _hoverTarget = newTarget;
                UpdateTooltip(newTarget);
                RedrawLayeredWindow();
            }
        }

        private void UpdateTooltip(HoverTarget target)
        {
            string text = target switch
            {
                HoverTarget.Badge => "LiteMonitor 硬件监控面板",
                HoverTarget.Refresh => "立即刷新硬件数据",
                HoverTarget.Pin => _isPinned ? "取消固定 (鼠标移出自动贴顶收起)" : "固定常驻显示 (不自动收起)",
                HoverTarget.OpenSettings => "展开监控设置主窗口",
                HoverTarget.Close => "收起/隐藏桌面悬浮栏",
                _ => ""
            };

            if (!string.IsNullOrEmpty(text)) _toolTip.SetToolTip(this, text);
            else _toolTip.Hide(this);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverTarget = HoverTarget.None;
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
                if (_rcBadge.Contains(e.Location))
                {
                    OpenSettingsWindow();
                    return;
                }
                if (_rcRefresh.Contains(e.Location))
                {
                    TriggerManualRefresh();
                    return;
                }
                if (_rcPin.Contains(e.Location))
                {
                    TogglePin();
                    return;
                }
                if (_rcSettings.Contains(e.Location))
                {
                    OpenSettingsWindow();
                    return;
                }
                if (_rcClose.Contains(e.Location))
                {
                    Collapse();
                    return;
                }

                // 胶囊空白区拖拽
                if (_visualCapsuleRect.Contains(e.Location))
                {
                    _isDragging = true;
                    _dragStartCursor = Cursor.Position;
                    _dragStartForm = this.Location;
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_isDragging)
            {
                _isDragging = false;

                // 智能屏幕边缘吸附 (吸附到中间、左上或右上，或记忆自定义 X 坐标)
                var area = Screen.FromControl(this).WorkingArea;
                int midX = area.Left + (area.Width - this.Width) / 2;

                if (Math.Abs(this.Left - midX) < 70)
                {
                    // 屏幕中线上磁吸居中
                    this.Location = new Point(midX, area.Y);
                    _cfg.FloatBarPosition = "top_center";
                }
                else if (this.Left - area.Left < 60)
                {
                    // 靠近左边缘，吸附到左上
                    this.Location = new Point(area.Left + 16, area.Y);
                    _cfg.FloatBarPosition = "top_left";
                }
                else if (area.Right - (this.Left + this.Width) < 60)
                {
                    // 靠近右边缘，吸附到右上
                    this.Location = new Point(area.Right - this.Width - 16, area.Y);
                    _cfg.FloatBarPosition = "top_right";
                }
                else
                {
                    // 自定义水平位置
                    _cfg.FloatBarPosition = "custom";
                    _cfg.FloatBarCustomX = this.Left;
                }

                _cfg.Save();
                RedrawLayeredWindow();
            }
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
            RedrawLayeredWindow();
        }

        private void OpenSettingsWindow()
        {
            _mainForm.ShowMainWindow();
        }

        private void TriggerManualRefresh()
        {
            HardwareMonitor.Instance?.UpdateAll();
            RedrawLayeredWindow();
        }

        private void ShowContextMenu()
        {
            var menu = new ContextMenuStrip();

            var itemPin = new ToolStripMenuItem("📌 固定在桌面顶部") { Checked = _isPinned };
            itemPin.Click += (_, __) => TogglePin();
            menu.Items.Add(itemPin);

            var itemResetPos = new ToolStripMenuItem("重新居中对齐");
            itemResetPos.Click += (_, __) => { _cfg.FloatBarPosition = "top_center"; _cfg.Save(); AlignToTop(); };
            menu.Items.Add(itemResetPos);

            menu.Items.Add(new ToolStripSeparator());

            var itemSettings = new ToolStripMenuItem("⚙️ 监控设置面板");
            itemSettings.Click += (_, __) => OpenSettingsWindow();
            menu.Items.Add(itemSettings);

            var itemTaskmgr = new ToolStripMenuItem("任务管理器");
            itemTaskmgr.Click += (_, __) => _mainForm.OpenTaskManager();
            menu.Items.Add(itemTaskmgr);

            var itemTraffic = new ToolStripMenuItem("📈 历史流量统计");
            itemTraffic.Click += (_, __) => _mainForm.OpenTrafficHistory();
            menu.Items.Add(itemTraffic);

            var itemCleanMem = new ToolStripMenuItem("🧹 清理物理内存");
            itemCleanMem.Click += (_, __) => _mainForm.CleanMemory();
            menu.Items.Add(itemCleanMem);

            menu.Items.Add(new ToolStripSeparator());

            var itemExit = new ToolStripMenuItem("退出 LiteMonitor");
            itemExit.Click += (_, __) => Application.Exit();
            menu.Items.Add(itemExit);

            menu.Show(Cursor.Position);
        }

        // ====================================================================
        // 核心渲染引擎：UpdateLayeredWindow (高度严格与 cc-switch-lite 齐平)
        // ====================================================================

        public void RedrawLayeredWindow()
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            int w = this.Width;
            int h = this.Height;
            if (w <= 0 || h <= 0) return;

            float dpi = UIUtils.ScaleFactor > 0 ? UIUtils.ScaleFactor : 1.0f;

            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                    // 1. 贴顶收缩指示条 (固定宽度 COLLAPSED_W，高度严格对齐 h-2.5 = 10px)
                    float colAlpha = Math.Clamp(1.0f - _animProgress * 1.5f, 0f, 1f);
                    if (colAlpha > 0.01f)
                    {
                        float colW = COLLAPSED_W * dpi;
                        float colH = COLLAPSED_H * dpi;
                        float colX = (w - colW) / 2f;
                        float colY = -8f * dpi * _animProgress; // 展开时向上微移
                        var colRect = new RectangleF(colX, colY, colW, colH);

                        DrawCollapsedCapsule(g, colRect, colAlpha, dpi);

                        if (_animProgress <= 0.5f)
                        {
                            _visualCapsuleRect = colRect;
                        }
                    }

                    // 2. 悬浮展开的胶囊数据条 (高度严格减小为 38px，与 cc-switch-lite 的 h-[40px] 齐平)
                    float expAlpha = Math.Clamp((_animProgress - 0.08f) / 0.92f, 0f, 1f);
                    if (expAlpha > 0.01f)
                    {
                        float expW = GetCurrentExpandedWidth(dpi);
                        float expH = EXPANDED_H * dpi;
                        float expX = (w - expW) / 2f;
                        // 从 -20px 平滑滑落至 3px，高度紧凑精致
                        float expY = (-20f + 23f * _animProgress) * dpi;
                        var expRect = new RectangleF(expX, expY, expW, expH);

                        DrawExpandedBar(g, expRect, expAlpha, dpi);

                        if (_animProgress > 0.5f)
                        {
                            _visualCapsuleRect = expRect;
                        }
                    }
                }

                // 提交给 Windows DWM 硬件混合层
                ApplyLayeredBitmap(bmp);
            }
        }

        private void ApplyLayeredBitmap(Bitmap bmp)
        {
            IntPtr screenDC = GetDC(IntPtr.Zero);
            IntPtr memDC = CreateCompatibleDC(screenDC);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                BITMAPINFO bi = new BITMAPINFO();
                bi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bi.bmiHeader.biWidth = bmp.Width;
                bi.bmiHeader.biHeight = -bmp.Height;
                bi.bmiHeader.biPlanes = 1;
                bi.bmiHeader.biBitCount = 32;
                bi.bmiHeader.biCompression = 0;

                IntPtr pBits;
                hBitmap = CreateDIBSection(memDC, ref bi, 0, out pBits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero) return;

                oldBitmap = SelectObject(memDC, hBitmap);

                BitmapData bmpData = bmp.LockBits(
                    new Rectangle(0, 0, bmp.Width, bmp.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppPArgb);

                try
                {
                    uint byteCount = (uint)(bmp.Width * bmp.Height * 4);
                    CopyMemory(pBits, bmpData.Scan0, byteCount);
                }
                finally
                {
                    bmp.UnlockBits(bmpData);
                }

                SIZE size = new SIZE(bmp.Width, bmp.Height);
                POINT pointSource = new POINT(0, 0);
                POINT topPos = new POINT(this.Left, this.Top);
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(this.Handle, screenDC, ref topPos, ref size, memDC, ref pointSource, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    SelectObject(memDC, oldBitmap);
                    DeleteObject(hBitmap);
                }
                DeleteDC(memDC);
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        /// <summary>
        /// 绘制贴顶微光指示条 (高度严格为 10px，对齐 cc-switch-lite 的 h-2.5)
        /// 呼吸灯紧随文字两旁 (gap 约 6px)
        /// </summary>
        private void DrawCollapsedCapsule(Graphics g, RectangleF rect, float alpha, float dpi)
        {
            float w = rect.Width;
            float h = rect.Height;

            using var path = new GraphicsPath();
            float r = h * 0.9f;
            path.AddLine(rect.Left, rect.Top, rect.Right, rect.Top);
            path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddLine(rect.Right - r, rect.Bottom, rect.Left + r, rect.Bottom);
            path.AddArc(rect.Left, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();

            using (var bgBrush = new LinearGradientBrush(
                new PointF(0, rect.Top), new PointF(0, rect.Bottom),
                Color.FromArgb((int)(235 * alpha), 18, 18, 24),
                Color.FromArgb((int)(245 * alpha), 10, 10, 14)))
            {
                g.FillPath(bgBrush, path);
            }

            using (var borderPen = new Pen(Color.FromArgb((int)(90 * alpha), 168, 85, 247), 1f))
            {
                g.DrawPath(borderPen, path);
            }

            double wave = Math.Sin(_pulseTick * 0.12);
            int basePulseAlpha = (int)(175 + 75 * wave);
            int pulseAlpha = (int)(Math.Clamp(basePulseAlpha, 90, 255) * alpha);

            using var font = new Font("Consolas", 7.5f * dpi, FontStyle.Bold);
            string brandText = "LITEMONITOR";
            var textSize = g.MeasureString(brandText, font);
            float textW = textSize.Width;

            float cx = rect.Left + w / 2f;
            float cy = rect.Top + h / 2f + 0.5f;

            float gap = 6.0f * dpi;
            float dotRadius = 1.9f * dpi;
            float glowRadius = 3.6f * dpi;

            // 左呼吸点 (紫色发光)
            float leftDotX = cx - (textW / 2f) - gap;
            using (var glowBrush = new SolidBrush(Color.FromArgb((int)(pulseAlpha * 0.35f), 192, 132, 252)))
                g.FillEllipse(glowBrush, leftDotX - glowRadius, cy - glowRadius, glowRadius * 2, glowRadius * 2);
            using (var coreBrush = new SolidBrush(Color.FromArgb(pulseAlpha, 216, 180, 254)))
                g.FillEllipse(coreBrush, leftDotX - dotRadius, cy - dotRadius, dotRadius * 2, dotRadius * 2);

            // 右呼吸点 (靛青发光)
            float rightDotX = cx + (textW / 2f) + gap;
            using (var glowBrush = new SolidBrush(Color.FromArgb((int)(pulseAlpha * 0.35f), 99, 102, 241)))
                g.FillEllipse(glowBrush, rightDotX - glowRadius, cy - glowRadius, glowRadius * 2, glowRadius * 2);
            using (var coreBrush = new SolidBrush(Color.FromArgb(pulseAlpha, 129, 140, 248)))
                g.FillEllipse(coreBrush, rightDotX - dotRadius, cy - dotRadius, dotRadius * 2, dotRadius * 2);

            using var textBrush = new SolidBrush(Color.FromArgb((int)(225 * alpha), 216, 180, 254));
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(brandText, font, textBrush, rect, sf);
        }

        /// <summary>
        /// 绘制展开状态的灵动长条 (高度严格为 38px，与 cc-switch-lite 的 h-[40px] 齐平)
        /// </summary>
        private void DrawExpandedBar(Graphics g, RectangleF rect, float alpha, float dpi)
        {
            using var path = CreateCapsulePath(rect);

            int bgAlpha = (int)(alpha * 242);
            bgAlpha = Math.Clamp(bgAlpha, 10, 248);

            // 1. 深色半透明亚克力渐变底色
            using (var bgBrush = new LinearGradientBrush(
                new PointF(0, rect.Top), new PointF(0, rect.Bottom),
                Color.FromArgb(bgAlpha, 14, 14, 20),
                Color.FromArgb(Math.Min(255, bgAlpha + 6), 9, 9, 13)))
            {
                g.FillPath(bgBrush, path);
            }

            // 2. 亮白微透外边框
            using (var borderPen = new Pen(Color.FromArgb((int)(alpha * 34), 255, 255, 255), 1f))
            {
                g.DrawPath(borderPen, path);
            }

            // 3. 顶部微光高光弧线
            using (var topPen = new Pen(Color.FromArgb((int)(alpha * 26), 255, 255, 255), 1f))
            {
                g.DrawLine(topPen, rect.Left + rect.Height / 2, rect.Top + 1, rect.Right - rect.Height / 2, rect.Top + 1);
            }

            if (alpha < 0.25f) return;

            // -------------------------------------------------------------
            // A. 左侧：「实时」药丸徽章 (高 19px)
            // -------------------------------------------------------------
            float curX = rect.Left + 8f * dpi;
            float badgeW = 38f * dpi;
            float badgeH = 19f * dpi;
            float badgeY = rect.Top + (rect.Height - badgeH) / 2f;
            _rcBadge = new RectangleF(curX, badgeY, badgeW, badgeH);

            using (var badgePath = CreateCapsulePath(_rcBadge))
            {
                Color badgeBg = (_hoverTarget == HoverTarget.Badge)
                    ? Color.FromArgb((int)(70 * alpha), 168, 85, 247)
                    : Color.FromArgb((int)(35 * alpha), 168, 85, 247);

                using var bBrush = new SolidBrush(badgeBg);
                g.FillPath(bBrush, badgePath);

                using var bPen = new Pen(Color.FromArgb((int)(70 * alpha), 168, 85, 247), 1f);
                g.DrawPath(bPen, badgePath);

                using var bFont = new Font("Microsoft YaHei UI", 8.2f * dpi, FontStyle.Bold);
                using var bTextBrush = new SolidBrush(Color.FromArgb((int)(235 * alpha), 216, 180, 254));
                var bsf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("实时", bFont, bTextBrush, _rcBadge, bsf);
            }

            curX += badgeW + 5f * dpi;

            // -------------------------------------------------------------
            // B. 右侧：快捷操作按钮组 (尺寸 19px，紧凑精致)
            // -------------------------------------------------------------
            float btnSize = 18f * dpi;
            float btnY = rect.Top + (rect.Height - btnSize) / 2f;
            float rightEdge = rect.Right - 8f * dpi;

            // 4. Close 按钮
            rightEdge -= btnSize;
            _rcClose = new RectangleF(rightEdge, btnY, btnSize, btnSize);
            DrawCircleButton(g, _rcClose, HoverTarget.Close, DrawCloseIcon, alpha);

            // 3. Maximize/Settings 按钮
            rightEdge -= (btnSize + 2.5f * dpi);
            _rcSettings = new RectangleF(rightEdge, btnY, btnSize, btnSize);
            DrawCircleButton(g, _rcSettings, HoverTarget.OpenSettings, DrawMaximizeIcon, alpha);

            // 2. Pin 按钮
            rightEdge -= (btnSize + 2.5f * dpi);
            _rcPin = new RectangleF(rightEdge, btnY, btnSize, btnSize);
            DrawPinButton(g, _rcPin, _isPinned, _hoverTarget == HoverTarget.Pin, alpha);

            // 1. Refresh 按钮
            rightEdge -= (btnSize + 2.5f * dpi);
            _rcRefresh = new RectangleF(rightEdge, btnY, btnSize, btnSize);
            DrawCircleButton(g, _rcRefresh, HoverTarget.Refresh, DrawRefreshIcon, alpha);

            // 独立分割线
            rightEdge -= 6f * dpi;
            using (var divPen = new Pen(Color.FromArgb((int)(28 * alpha), 255, 255, 255), 1f))
            {
                float divY1 = rect.Top + (rect.Height - 12f * dpi) / 2f;
                g.DrawLine(divPen, rightEdge, divY1, rightEdge, divY1 + 12f * dpi);
            }

            // -------------------------------------------------------------
            // C. 中间核心统计区：[图标 + Consolas等宽加粗数值 + 灰调单位 + 垂直分割线]
            // 流式排版，高度严格齐平
            // -------------------------------------------------------------
            float metricsStartX = curX + 5f * dpi;
            float metricsEndX = rightEdge - 5f * dpi;
            var metricsRect = new RectangleF(metricsStartX, rect.Top, metricsEndX - metricsStartX, rect.Height);

            DrawMetricsArea(g, metricsRect, alpha, dpi);
        }

        private void DrawMetricsArea(Graphics g, RectangleF rect, float alpha, float dpi)
        {
            var metricDisplayList = GetCurrentMetrics();
            if (metricDisplayList.Count == 0) return;

            int count = Math.Min(metricDisplayList.Count, 10);

            // 字体与字号：与 cc-switch-lite 的 text-xs font-bold 精确齐平
            using var fontVal = new Font("Consolas", 9.5f * dpi, FontStyle.Bold);
            using var fontUnit = new Font("Segoe UI", 7.0f * dpi, FontStyle.Regular);
            using var unitBrush = new SolidBrush(Color.FromArgb((int)(161 * alpha), 161, 161, 170));
            using var divPen = new Pen(Color.FromArgb((int)(26 * alpha), 255, 255, 255), 1f);

            float iconSize = 12f * dpi;
            float iconGap = 3.5f * dpi;
            float unitGap = 1.5f * dpi;
            float divSpacing = 6f * dpi;

            // 预先测量各项目自适应宽度
            var itemWidths = new float[count];
            var valSizes = new SizeF[count];
            var unitSizes = new SizeF[count];
            float totalWidth = 0f;

            for (int i = 0; i < count; i++)
            {
                var it = metricDisplayList[i];
                valSizes[i] = g.MeasureString(it.ValueText, fontVal);
                unitSizes[i] = !string.IsNullOrEmpty(it.UnitText) ? g.MeasureString(it.UnitText, fontUnit) : SizeF.Empty;

                float w = iconSize + iconGap + valSizes[i].Width;
                if (!string.IsNullOrEmpty(it.UnitText))
                {
                    w += unitGap + unitSizes[i].Width - 2.5f * dpi;
                }
                itemWidths[i] = w;
                totalWidth += w;
                if (i < count - 1) totalWidth += (divSpacing * 2 + 1f);
            }

            _lastMetricsWidth = totalWidth;

            float curX = rect.Left + Math.Max(0, (rect.Width - totalWidth) / 2f);

            // 流式绘制每个指标项
            for (int i = 0; i < count; i++)
            {
                var it = metricDisplayList[i];
                float itemW = itemWidths[i];

                // 1. 矢量微小图标 (12x12)
                float iconX = curX;
                float iconY = rect.Top + (rect.Height - iconSize) / 2f;
                var iconRect = new RectangleF(iconX, iconY, iconSize, iconSize);

                Color itemColor = Color.FromArgb((int)(it.Color.A * alpha), it.Color.R, it.Color.G, it.Color.B);
                DrawMetricIcon(g, it.Key, iconRect, itemColor);

                // 2. Consolas 等宽纯数字
                using var valBrush = new SolidBrush(itemColor);
                float valX = iconX + iconSize + iconGap;
                float valY = rect.Top + (rect.Height - fontVal.Height) / 2f;

                g.DrawString(it.ValueText, fontVal, valBrush, valX, valY);

                // 3. 次级灰调单位
                if (!string.IsNullOrEmpty(it.UnitText))
                {
                    float unitX = valX + valSizes[i].Width - 2.5f * dpi;
                    float unitY = valY + (fontVal.Height - fontUnit.Height);
                    g.DrawString(it.UnitText, fontUnit, unitBrush, unitX, unitY);
                }

                curX += itemW;

                // 4. 垂直分割线
                if (i < count - 1)
                {
                    curX += divSpacing;
                    float divY = rect.Top + (rect.Height - 12f * dpi) / 2f;
                    g.DrawLine(divPen, curX, divY, curX, divY + 12f * dpi);
                    curX += divSpacing + 1f;
                }
            }
        }

        private class MetricDisplayModel
        {
            public string Key { get; set; } = "";
            public string ValueText { get; set; } = "";
            public string UnitText { get; set; } = "";
            public Color Color { get; set; } = Color.White;
        }

        private List<MetricDisplayModel> GetCurrentMetrics()
        {
            var list = new List<MetricDisplayModel>();
            var theme = ThemeManager.Current;

            var cols = _ui.GetHorizontalColumns();
            var rawItems = new List<MetricItem>();

            if (cols != null && cols.Count > 0)
            {
                foreach (var c in cols)
                {
                    if (c.Top != null && !string.IsNullOrEmpty(c.Top.Key)) rawItems.Add(c.Top);
                    if (c.Bottom != null && !string.IsNullOrEmpty(c.Bottom.Key)) rawItems.Add(c.Bottom);
                }
            }

            foreach (var item in rawItems)
            {
                string keyUpper = item.Key.ToUpperInvariant();

                // 1. DASH.Uptime (宸插紑鏈烘椂闂?
                if (keyUpper.StartsWith("DASH.UPTIME") || keyUpper.Contains("UPTIME"))
                {
                    TimeSpan ts = InfoService.Instance.GetUptimeTimeSpan();
                    string timeVal = (ts.TotalDays < 1) 
                        ? $"{ts.Hours}h {ts.Minutes:D2}m" 
                        : $"{(int)ts.TotalDays}d {ts.Hours}h";

                    list.Add(new MetricDisplayModel
                    {
                        Key = "DASH.Uptime",
                        ValueText = timeVal,
                        UnitText = "",
                        Color = Color.FromArgb(52, 211, 153)
                    });
                    continue;
                }

                // 2. DATA.Day* (今日流量统计: 上传与下载
                if (keyUpper.StartsWith("DATA.DAY") || keyUpper.Contains("DAYUP") || keyUpper.Contains("DAYDOWN"))
                {
                    var stats = TrafficLogger.GetTodayStats();
                    long bytes = keyUpper.Contains("DOWN") ? stats.down : stats.up;
                    if (bytes == 0 && item.DisplayValue > 0) bytes = (long)item.DisplayValue;

                    var (tVal, tUnit) = MetricUtils.FormatDataSizeParts(bytes, -1);
                    Color color = keyUpper.Contains("DOWN")
                        ? Color.FromArgb(45, 212, 191)  // 钃濋潚鑹?(浠婃棩涓嬭浇)
                        : Color.FromArgb(168, 85, 247); // 绱壊 (浠婃棩涓婁紶)

                    list.Add(new MetricDisplayModel
                    {
                        Key = item.Key,
                        ValueText = tVal,
                        UnitText = tUnit,
                        Color = color
                    });
                    continue;
                }

                string formatted = item.GetFormattedText(true);
                string val = item.CachedValueText;
                string unit = item.CachedUnitText;

                CleanMetricText(formatted, ref val, ref unit, item.Key);

                list.Add(new MetricDisplayModel
                {
                    Key = item.Key,
                    ValueText = val,
                    UnitText = unit,
                    Color = item.GetTextColor(theme)
                });
            }

            // 备用硬件实时通道直读兜底
            if (list.Count < 3 && HardwareMonitor.Instance != null)
            {
                var mon = HardwareMonitor.Instance;
                list.Clear();
                TimeSpan tsFallback = InfoService.Instance.GetUptimeTimeSpan();
                string upText = (tsFallback.TotalDays < 1) ? $"{tsFallback.Hours}h {tsFallback.Minutes:D2}m" : $"{(int)tsFallback.TotalDays}d {tsFallback.Hours}h";
                list.Add(new MetricDisplayModel { Key = "DASH.Uptime", ValueText = upText, UnitText = "", Color = Color.FromArgb(52, 211, 153) });

                float cpu = mon.Get("CPU.Load") ?? 0f;
                list.Add(new MetricDisplayModel { Key = "CPU.Load", ValueText = $"{cpu:F0}", UnitText = "%", Color = Color.FromArgb(52, 211, 153) });

                float cpuTemp = mon.Get("CPU.Temp") ?? 0f;
                if (cpuTemp > 0)
                    list.Add(new MetricDisplayModel { Key = "CPU.Temp", ValueText = $"{cpuTemp:F0}", UnitText = "\u2103", Color = Color.FromArgb(248, 113, 113) });

                float gpu = mon.Get("GPU.Load") ?? 0f;
                list.Add(new MetricDisplayModel { Key = "GPU.Load", ValueText = $"{gpu:F0}", UnitText = "%", Color = Color.FromArgb(251, 191, 36) });

                float ram = mon.Get("MEM.Load") ?? 0f;
                list.Add(new MetricDisplayModel { Key = "MEM.Load", ValueText = $"{ram:F0}", UnitText = "%", Color = Color.FromArgb(129, 140, 248) });

                float down = mon.Get("NET.Down") ?? 0f;
                var (dVal, dUnit) = MetricUtils.FormatDataSizeParts(down, -1);
                list.Add(new MetricDisplayModel { Key = "NET.Down", ValueText = dVal, UnitText = dUnit + "/s", Color = Color.FromArgb(52, 211, 153) });

                float up = mon.Get("NET.Up") ?? 0f;
                var (uVal, uUnit) = MetricUtils.FormatDataSizeParts(up, -1);
                list.Add(new MetricDisplayModel { Key = "NET.Up", ValueText = uVal, UnitText = uUnit + "/s", Color = Color.FromArgb(56, 189, 248) });

                var todayStats = TrafficLogger.GetTodayStats();
                if (todayStats.down > 0)
                {
                    var (tdVal, tdUnit) = MetricUtils.FormatDataSizeParts(todayStats.down, -1);
                    list.Add(new MetricDisplayModel { Key = "DATA.DayDown", ValueText = tdVal, UnitText = tdUnit, Color = Color.FromArgb(45, 212, 191) });
                }
                if (todayStats.up > 0)
                {
                    var (tuVal, tuUnit) = MetricUtils.FormatDataSizeParts(todayStats.up, -1);
                    list.Add(new MetricDisplayModel { Key = "DATA.DayUp", ValueText = tuVal, UnitText = tuUnit, Color = Color.FromArgb(168, 85, 247) });
                }
            }

            return list;
        }

        private static void CleanMetricText(string formatted, ref string val, ref string unit, string key)
        {
            if (string.IsNullOrEmpty(val))
            {
                val = formatted ?? "";
            }

            val = val.Trim();
            unit = (unit ?? "").Trim();

            if (!string.IsNullOrEmpty(unit) && val.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
            {
                val = val.Substring(0, val.Length - unit.Length).TrimEnd();
            }

            if (val.EndsWith("%"))
            {
                val = val.TrimEnd('%').TrimEnd();
                unit = "%";
            }
            else if (val.EndsWith("℃") || val.EndsWith("°C"))
            {
                val = val.Replace("℃", "").Replace("°C", "").TrimEnd();
                unit = "\u2103";
            }
            else if (Regex.IsMatch(val, @"([0-9\.]+)\s*([KMGTP]?B(?:/s)?)", RegexOptions.IgnoreCase))
            {
                var match = Regex.Match(val, @"^([0-9\.]+)\s*([KMGTP]?B(?:/s)?)$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    val = match.Groups[1].Value;
                    unit = match.Groups[2].Value;
                }
            }

            if (string.IsNullOrEmpty(unit))
            {
                string k = key.ToUpperInvariant();
                if (k.Contains("LOAD") || k.Contains("USAGE")) unit = "%";
                else if (k.Contains("TEMP")) unit = "\u2103";
            }
        }

        private void DrawMetricIcon(Graphics g, string key, RectangleF r, Color themeColor)
        {
            string upper = key.ToUpperInvariant();

            if (upper.Contains("UPTIME") || upper.Contains("TIME"))
            {
                using var p = new Pen(themeColor, 1.1f);
                g.DrawEllipse(p, r.X + 1f, r.Y + 1f, r.Width - 2f, r.Height - 2f);
                float cx = r.X + r.Width / 2f;
                float cy = r.Y + r.Height / 2f;
                g.DrawLine(p, cx, cy, cx, cy - 3.2f);
                g.DrawLine(p, cx, cy, cx + 2.4f, cy);
                return;
            }

            // 流量统计：今日上传 ⏫ (双向折线向上箭头)
            if (upper.Contains("DAYUP") || (upper.Contains("DATA") && upper.Contains("UP")))
            {
                using var p = new Pen(themeColor, 1.25f) { EndCap = LineCap.Round, StartCap = LineCap.Round };
                float cx = r.X + r.Width / 2f;
                // 上层折线 (尖端朝正上)
                g.DrawLine(p, cx - 2.8f, r.Y + 5.2f, cx, r.Y + 2.5f);
                g.DrawLine(p, cx + 2.8f, r.Y + 5.2f, cx, r.Y + 2.5f);
                // 下层折线 (尖端朝正上)
                g.DrawLine(p, cx - 2.8f, r.Y + 8.9f, cx, r.Y + 6.2f);
                g.DrawLine(p, cx + 2.8f, r.Y + 8.9f, cx, r.Y + 6.2f);
                return;
            }

            // 流量统计：今日下载 ⏬ (双向折线向下箭头)
            if (upper.Contains("DAYDOWN") || (upper.Contains("DATA") && upper.Contains("DOWN")))
            {
                using var p = new Pen(themeColor, 1.25f) { EndCap = LineCap.Round, StartCap = LineCap.Round };
                float cx = r.X + r.Width / 2f;
                // 上层折线 (尖端朝正下)
                g.DrawLine(p, cx - 2.8f, r.Y + 2.5f, cx, r.Y + 5.2f);
                g.DrawLine(p, cx + 2.8f, r.Y + 2.5f, cx, r.Y + 5.2f);
                // 下层折线 (尖端朝正下)
                g.DrawLine(p, cx - 2.8f, r.Y + 6.2f, cx, r.Y + 8.9f);
                g.DrawLine(p, cx + 2.8f, r.Y + 6.2f, cx, r.Y + 8.9f);
                return;
            }

            if (upper.Contains("CPU"))
            {
                using var p = new Pen(themeColor, 1.1f);
                using var b = new SolidBrush(themeColor);
                g.DrawRectangle(p, r.X + 1.5f, r.Y + 1.5f, r.Width - 3f, r.Height - 3f);
                g.FillRectangle(b, r.X + 3.5f, r.Y + 3.5f, r.Width - 7f, r.Height - 7f);
                g.DrawLine(p, r.X + 3.5f, r.Y, r.X + 3.5f, r.Y + 1.5f);
                g.DrawLine(p, r.Right - 3.5f, r.Y, r.Right - 3.5f, r.Y + 1.5f);
                g.DrawLine(p, r.X + 3.5f, r.Bottom - 1.5f, r.X + 3.5f, r.Bottom);
                g.DrawLine(p, r.Right - 3.5f, r.Bottom - 1.5f, r.Right - 3.5f, r.Bottom);
            }
            else if (upper.Contains("MEM") || upper.Contains("RAM"))
            {
                using var p = new Pen(themeColor, 1.1f);
                using var b = new SolidBrush(themeColor);
                g.DrawRectangle(p, r.X + 1, r.Y + 2.5f, r.Width - 2, r.Height - 5f);
                g.FillRectangle(b, r.X + 3f, r.Y + 4.5f, 1.8f, 1.8f);
                g.FillRectangle(b, r.X + 6.5f, r.Y + 4.5f, 1.8f, 1.8f);
            }
            else if (upper.Contains("GPU"))
            {
                using var p = new Pen(themeColor, 1.1f);
                g.DrawRectangle(p, r.X + 1, r.Y + 1.5f, r.Width - 2, r.Height - 3f);
                g.DrawEllipse(p, r.X + 3.5f, r.Y + 3f, 4.5f, 4.5f);
            }
            else if (upper.Contains("DOWN") || upper.Contains("NET.D"))
            {
                using var p = new Pen(themeColor, 1.2f) { EndCap = LineCap.Round, StartCap = LineCap.Round };
                float cx = r.X + r.Width / 2f;
                g.DrawLine(p, cx, r.Y + 1.5f, cx, r.Bottom - 1.5f);
                g.DrawLine(p, cx - 2.5f, r.Bottom - 4f, cx, r.Bottom - 1.5f);
                g.DrawLine(p, cx + 2.5f, r.Bottom - 4f, cx, r.Bottom - 1.5f);
            }
            else if (upper.Contains("UP") || upper.Contains("NET.U"))
            {
                using var p = new Pen(themeColor, 1.2f) { EndCap = LineCap.Round, StartCap = LineCap.Round };
                float cx = r.X + r.Width / 2f;
                g.DrawLine(p, cx, r.Bottom - 1.5f, cx, r.Y + 1.5f);
                g.DrawLine(p, cx - 2.5f, r.Y + 4f, cx, r.Y + 1.5f);
                g.DrawLine(p, cx + 2.5f, r.Y + 4f, cx, r.Y + 1.5f);
            }
            else if (upper.Contains("DISK"))
            {
                using var p = new Pen(themeColor, 1.1f);
                using var b = new SolidBrush(themeColor);
                g.DrawRectangle(p, r.X + 1, r.Y + 2.5f, r.Width - 2, r.Height - 5f);
                g.FillEllipse(b, r.Right - 3.5f, r.Y + 4.5f, 1.8f, 1.8f);
            }
            else
            {
                using var p = new Pen(themeColor, 1.1f) { EndCap = LineCap.Round, StartCap = LineCap.Round };
                float yMid = r.Y + r.Height / 2f;
                g.DrawLine(p, r.X + 1, yMid, r.X + 3.5f, yMid);
                g.DrawLine(p, r.X + 3.5f, yMid, r.X + 5.5f, r.Y + 1.5f);
                g.DrawLine(p, r.X + 5.5f, r.Y + 1.5f, r.X + 7.5f, r.Bottom - 1.5f);
                g.DrawLine(p, r.X + 7.5f, r.Bottom - 1.5f, r.X + 9.5f, yMid);
                g.DrawLine(p, r.X + 9.5f, yMid, r.Right - 1, yMid);
            }
        }

        private void DrawCircleButton(Graphics g, RectangleF rect, HoverTarget target, Action<Graphics, RectangleF, Color> drawIcon, float alpha)
        {
            bool isHover = (_hoverTarget == target);

            if (isHover)
            {
                using var hBrush = new SolidBrush(Color.FromArgb((int)(35 * alpha), 255, 255, 255));
                g.FillEllipse(hBrush, rect);
            }

            Color iconColor = isHover
                ? Color.FromArgb((int)(245 * alpha), 244, 244, 245)
                : Color.FromArgb((int)(160 * alpha), 161, 161, 170);

            drawIcon(g, rect, iconColor);
        }

        private void DrawPinButton(Graphics g, RectangleF rect, bool pinned, bool isHover, float alpha)
        {
            if (isHover || pinned)
            {
                Color pinBg = pinned
                    ? Color.FromArgb((int)(50 * alpha), 168, 85, 247)
                    : Color.FromArgb((int)(35 * alpha), 255, 255, 255);
                using var hBrush = new SolidBrush(pinBg);
                g.FillEllipse(hBrush, rect);
            }

            Color iconColor = pinned
                ? Color.FromArgb((int)(250 * alpha), 192, 132, 252)
                : (isHover ? Color.FromArgb((int)(245 * alpha), 244, 244, 245) : Color.FromArgb((int)(160 * alpha), 161, 161, 170));

            using var pen = new Pen(iconColor, 1.1f) { EndCap = LineCap.Round, StartCap = LineCap.Round };

            float cx = rect.X + rect.Width / 2f;
            float cy = rect.Y + rect.Height / 2f;

            if (pinned)
            {
                g.DrawLine(pen, cx - 3f, cy - 3f, cx + 3f, cy - 3f);
                g.DrawLine(pen, cx - 1.5f, cy - 3f, cx - 1.5f, cy + 1f);
                g.DrawLine(pen, cx + 1.5f, cy - 3f, cx + 1.5f, cy + 1f);
                g.DrawLine(pen, cx - 3.5f, cy + 1f, cx + 3.5f, cy + 1f);
                g.DrawLine(pen, cx, cy + 1f, cx, cy + 5f);
            }
            else
            {
                g.DrawLine(pen, cx - 2.5f, cy - 2.5f, cx + 2.5f, cy + 2.5f);
                g.DrawLine(pen, cx - 1f, cy - 3.5f, cx + 3.5f, cy + 1f);
                g.DrawLine(pen, cx, cy, cx - 3.5f, cy + 3.5f);
            }
        }

        private void DrawRefreshIcon(Graphics g, RectangleF rect, Color color)
        {
            using var pen = new Pen(color, 1.1f) { EndCap = LineCap.Round, StartCap = LineCap.Round };
            float cx = rect.X + rect.Width / 2f;
            float cy = rect.Y + rect.Height / 2f;
            float r = 4.0f;

            g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -45, 270);
            g.DrawLine(pen, cx + r, cy - 1.2f, cx + r + 2.2f, cy + 1.2f);
            g.DrawLine(pen, cx + r, cy - 1.2f, cx + r - 2.2f, cy + 1.2f);
        }

        private void DrawMaximizeIcon(Graphics g, RectangleF rect, Color color)
        {
            using var pen = new Pen(color, 1.1f) { EndCap = LineCap.Round, StartCap = LineCap.Round };
            float cx = rect.X + rect.Width / 2f;
            float cy = rect.Y + rect.Height / 2f;
            float s = 3.5f;

            g.DrawLine(pen, cx - s, cy - s + 2.5f, cx - s, cy - s);
            g.DrawLine(pen, cx - s, cy - s, cx - s + 2.5f, cy - s);

            g.DrawLine(pen, cx + s, cy + s - 2.5f, cx + s, cy + s);
            g.DrawLine(pen, cx + s, cy + s, cx + s - 2.5f, cy + s);

            g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
        }

        private void DrawCloseIcon(Graphics g, RectangleF rect, Color color)
        {
            using var pen = new Pen(color, 1.1f) { EndCap = LineCap.Round, StartCap = LineCap.Round };
            float cx = rect.X + rect.Width / 2f;
            float cy = rect.Y + rect.Height / 2f;
            float s = 3.0f;

            g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
            g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
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
            }
            base.Dispose(disposing);
        }
    }
}

