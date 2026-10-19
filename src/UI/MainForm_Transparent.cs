using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI;
using LiteMonitor.src.UI.Helpers;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class MainForm : Form
    {
        private readonly Settings _cfg = Settings.Load();
        private UIController? _ui;
        
        // ★★★ 双助手架构 ★★★
        private readonly MainFormWinHelper _winHelper;
        private readonly MainFormBizHelper _bizHelper;
        private readonly int _wmTaskbarCreated;
        private static readonly int WM_SHOW_ME = MainFormWinHelper.RegisterShowInstanceMessage();
        private const int WM_DISPLAYCHANGE = 0x007E;
        private CancellationTokenSource? _displayChangeCts;

        private Point _dragOffset;
        private bool _uiDragging = false;

        // 防止 Win11 自动隐藏无边框 + 无任务栏窗口
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
                cp.ExStyle &= ~0x00040000; // WS_EX_APPWINDOW
                if (_cfg != null && _cfg.TopMost)
                {
                    cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST，防止句柄重建后丢失置顶样式
                }
                
                // [Fix] 启动时应用鼠标穿透配置，防止因句柄重建导致样式丢失
                if (_cfg != null && _cfg.ClickThrough)
                {
                    cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT
                    cp.ExStyle |= 0x80000; // WS_EX_LAYERED (必须配合才能实现完整穿透)
                }

                return cp;
            }
        }

        // ========== 代理方法 (保持兼容性) ==========
        public void SetClickThrough(bool enable) => _winHelper.SetClickThrough(enable);
        public void InitAutoHideTimer() => _bizHelper.StartTimer();
        public void StopAutoHideTimer() => _bizHelper.StopTimer();
        public void HideTrayIcon() => _bizHelper.SetTrayVisible(false);
        public void ShowTrayIcon() => _bizHelper.SetTrayVisible(true);
        public void RebuildMenus() => _bizHelper.RebuildMenus();
        public void ShowNotification(string title, string text, ToolTipIcon icon) => _bizHelper.ShowNotification(title, text, icon);

        public void ToggleLayoutMode() => _bizHelper.ToggleLayoutMode();
        public void RefreshTopMost(bool forceReinsert = false) => _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert);
        public void ApplyRoundedCorners() => _winHelper.ApplyRoundedCorners();
        public void EnsureVisibleAndSavePos()
        {
            _bizHelper.ClampToScreen();
            _bizHelper.SavePos();
        }

        public void OpenSettings() => _bizHelper.OpenSettings();
        public void OpenTaskManager() => _bizHelper.OpenTaskManager();
        public void OpenTrafficHistory() => _bizHelper.OpenTrafficHistory();
        public void CleanMemory() => _bizHelper.CleanMemory();

        // ==== 任务栏快捷窗口代理 ====
        private TaskbarForm? _taskbar;

        public void ToggleTaskbar(bool show)
        {
            if (show)
            {
                if (_taskbar != null && !_taskbar.IsDisposed)
                {
                    if (_taskbar.TargetDevice != _cfg.TaskbarMonitorDevice)
                    {
                        _taskbar.Close();
                        _taskbar.Dispose();
                        _taskbar = null;
                    }
                }

                if (_taskbar == null || _taskbar.IsDisposed)
                {
                    if (_ui != null)
                    {
                        _taskbar = new TaskbarForm(_cfg, _ui, this);
                        _taskbar.Show();
                    }
                }
                else
                {
                    if (!_taskbar.Visible)
                    {
                        _taskbar.Show();
                    }
                    _taskbar.ReloadLayout();
                }
            }
            else
            {
                if (_taskbar != null)
                {
                    _taskbar.Close();
                    _taskbar.Dispose();
                    _taskbar = null;
                }
            }
        }

        public void ReloadTaskbar()
        {
            if (_taskbar != null)
            {
                _taskbar.Close();
                _taskbar.Dispose();
                _taskbar = null;
            }
            if (_cfg.ShowTaskbar)
            {
                ToggleTaskbar(true);
            }
        }

        // ==== 桌面灵动胶囊悬浮栏 (FloatBar) ====
        private FloatBarForm? _floatBar;

        public void ToggleFloatBar(bool show)
        {
            if (show)
            {
                if (_floatBar == null || _floatBar.IsDisposed)
                {
                    if (_ui != null)
                    {
                        _floatBar = new FloatBarForm(_cfg, _ui, this);
                        _floatBar.Show();
                    }
                }
                else
                {
                    if (!_floatBar.Visible)
                    {
                        _floatBar.Show();
                    }
                    _floatBar.RedrawLayeredWindow();
                }
            }
            else
            {
                if (_floatBar != null)
                {
                    _floatBar.Close();
                    _floatBar.Dispose();
                    _floatBar = null;
                }
            }
        }

        public MainForm()
        {
            // 语言初始化
            if (string.IsNullOrEmpty(_cfg.Language))
            {
                string sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLower();
                string langPath = Path.Combine(AppContext.BaseDirectory, "resources/lang", $"{sysLang}.json");
                _cfg.Language = File.Exists(langPath) ? sysLang : "en";
            }
            LanguageManager.Load(_cfg.Language);
            _cfg.SyncToLanguage();

            // 1. 初始化 DPI 缩放系数
            UIUtils.ScaleFactor = this.DeviceDpi / 96f;

            TrafficLogger.Load();
            HardwareHistoryLogger.Load();
            src.Plugins.PluginManager.Instance.LoadPlugins(Path.Combine(AppContext.BaseDirectory, "resources", "plugins"));
            src.Plugins.PluginManager.Instance.Start();
            _ui = new UIController(_cfg, this);
            new src.WebServer.LiteWebServer(_cfg);

            // 基础样式
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;

            BackColor = ThemeManager.ParseColor(ThemeManager.Current.Color.Background);

            // 2. 初始化双助手
            _winHelper = new MainFormWinHelper(this);
            
            // 资源管理器重启监听
            _wmTaskbarCreated = MainFormWinHelper.RegisterTaskbarCreatedMessage();

            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _winHelper.InitializeStyle(_cfg.TopMost, _cfg.ClickThrough);
            this.Opacity = 0; 

            _bizHelper = new MainFormBizHelper(this, _cfg, _ui, _winHelper);
            _bizHelper.Initialize();

            _winHelper.StartFadeIn(_cfg.Opacity);

            // 3. 事件绑定
            BindEvents();

            // 启动时确保灵动胶囊悬浮栏展示
            if (_cfg.FloatBarEnabled)
            {
                ToggleFloatBar(true);
            }
        }

        private void BindEvents()
        {
            // 拖拽
            MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _ui?.SetDragging(true);
                    _uiDragging = true;
                    _bizHelper.IsDragging = true;
                    _dragOffset = e.Location;
                }
            };
            MouseMove += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (Math.Abs(e.X - _dragOffset.X) + Math.Abs(e.Y - _dragOffset.Y) < 1) return;
                    Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);
                }
            };
            MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _ui?.SetDragging(false);
                    _uiDragging = false;
                    _bizHelper.IsDragging = false;
                    _bizHelper.ClampToScreen(); 
                    _bizHelper.SavePos();
                }
            };

            // 双击
            this.DoubleClick += (_, __) => _bizHelper.HandleDoubleClick();
            
            // DPI / Resize
            this.Resize += (_, __) => _winHelper.ApplyRoundedCorners();
            this.VisibleChanged += (_, __) => { if (Visible) _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true); };
            this.HandleCreated += (_, __) => _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true);
        }

        public void ShowMainWindow()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
            _cfg.HideMainForm = false;
            _cfg.Save();

            _bizHelper.ForceShow();
            _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true);
            _bizHelper.RebuildMenus();

            if (_cfg.FloatBarEnabled && _floatBar != null)
            {
                _floatBar.Show();
                _floatBar.RedrawLayeredWindow();
            }
        }

        public void HideMainWindow()
        {
            this.Hide();
            _cfg.HideMainForm = true;
            _cfg.Save();
            _bizHelper.RebuildMenus();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SHOW_ME && WM_SHOW_ME != 0)
            {
                this.BeginInvoke(new Action(() =>
                {
                    ShowMainWindow();
                    _bizHelper.OpenSettings();
                }));
                return;
            }

            if (m.Msg == _wmTaskbarCreated && _wmTaskbarCreated != 0)
            {
                if (_cfg.ShowTaskbar)
                {
                    this.BeginInvoke(new Action(async () =>
                    {
                        try 
                        {
                            await Task.Delay(3000);
                            ToggleTaskbar(false);
                            ToggleTaskbar(true);
                        }
                        catch { }
                    }));
                }
            }

            if (m.Msg == WM_DISPLAYCHANGE)
            {
                _displayChangeCts?.Cancel();
                _displayChangeCts = new CancellationTokenSource();
                var token = _displayChangeCts.Token;

                Task.Delay(500, token).ContinueWith(t => 
                {
                    if (!t.IsCanceled)
                    {
                        this.BeginInvoke(new Action(() => 
                        {
                            _bizHelper?.RestorePos();
                            _floatBar?.AlignToTop();
                        }));
                    }
                });
            }

            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            _ui?.Render(e.Graphics);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _ui?.ApplyTheme(_cfg.Skin);
            _winHelper.ApplyRoundedCorners();
            this.Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            
            // 恢复可见性
            if (_cfg.HideMainForm) this.Hide();
            
            this.Update();
            
            // 恢复位置
            _bizHelper.RestorePos();

            // 确保渲染尺寸正确 (横屏模式)
            if (_cfg.HorizontalMode && _ui != null)
            {
                this.Size = new Size(this.Width, this.Height);
            }
            
            _winHelper.ApplyRoundedCorners();
            _bizHelper.KeepVisible(3.0); // 启动保护期

            if (_cfg.ShowTaskbar) ToggleTaskbar(true);
            if (_cfg.FloatBarEnabled) ToggleFloatBar(true);

            // 启动 WebServer
            if (_cfg.WebServerEnabled)
            {
                if (src.WebServer.LiteWebServer.Instance?.Start(out string err) == false)
                {
                     ShowNotification("WebServer Error", 
                         (_cfg.Language == "zh" ? "Web服务启动失败: " : "Web Server Failed: ") + err, 
                         ToolTipIcon.Error);
                }
            }

            if (_bizHelper != null)
            {
                 _ = _bizHelper.RunStartupChecksAsync();
            }

            if (_cfg.TopMost)
            {
                this.BeginInvoke(new Action(async () =>
                {
                    await Task.Delay(3000);
                    _winHelper.RefreshTopMost(true, forceReinsert: true);
                }));
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _cfg.Save(); 
            TrafficLogger.Save(); 
            HardwareHistoryLogger.Save();
            src.WebServer.LiteWebServer.Instance?.Stop();
            
            base.OnFormClosed(e);
            
            _ui?.Dispose();
            _bizHelper.Dispose();
            _floatBar?.Dispose();
        }
    }
}