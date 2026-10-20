using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.src.UI
{
    public class SettingsForm : Form
    {
        private readonly Settings _cfg;

        private TabControl _tabCtrl = new();
        private TabPage _tabMonitor = new();
        private TabPage _tabGeneral = new();

        private CheckedListBox _clbItems = new();
        private Button _btnUp = new();
        private Button _btnDown = new();
        private Button _btnDefault = new();

        private CheckBox _chkAutoStart = new();
        private CheckBox _chkPinned = new();
        private ComboBox _cbPosition = new();
        private ComboBox _cbInterval = new();

        private TabPage _tabColor = new();
        private readonly Button[] _btnColors = { new(), new(), new() }; // 绿/黄/红
        private readonly NumericUpDown[] _numWarn = new NumericUpDown[ThresholdRows.Length];
        private readonly NumericUpDown[] _numCrit = new NumericUpDown[ThresholdRows.Length];

        private static readonly (string Name, Func<ThresholdsSet, ValueRange> Pick)[] ThresholdRows =
        {
            ("CPU/GPU/内存 占用 (%)", t => t.Load),
            ("温度 (℃)", t => t.Temp),
            ("磁盘读写 (MB/s)", t => t.DiskIOMB),
            ("网络上传 (MB/s)", t => t.NetUpMB),
            ("网络下载 (MB/s)", t => t.NetDownMB),
            ("今日上传 (MB)", t => t.DataUpMB),
            ("今日下载 (MB)", t => t.DataDownMB),
        };

        private Button _btnOk = new();
        private Button _btnCancel = new();
        private Button _btnApply = new();

        private class ItemEntry
        {
            public string Key { get; set; } = "";
            public string Name { get; set; } = "";
            public bool Visible { get; set; } = true;
            public override string ToString() => $"{Name} ({Key})";
        }

        private List<ItemEntry> _itemList = new();

        private static IReadOnlyDictionary<string, string> KeyFriendlyNames => MetricLabelResolver.FriendlyNames;

        public SettingsForm(Settings cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

            InitializeUI();
            LoadData();
        }

        private void InitializeUI()
        {
            this.AutoScaleDimensions = new SizeF(96F, 96F);
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.Text = "LiteMonitor 设置";
            try { this.Icon = Properties.Resources.AppIcon; } catch { }
            this.Size = new Size(580, 560);
            this.MinimumSize = new Size(500, 450);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = true;
            this.Font = new Font("Microsoft YaHei UI", 9F);
            this.BackColor = Color.FromArgb(245, 245, 247);

            // TabControl
            _tabCtrl.Dock = DockStyle.Fill;
            _tabCtrl.Padding = new Point(12, 6);

            // Tab 1: 监控项设置
            _tabMonitor.Text = "📊 监控项显示";
            _tabMonitor.BackColor = Color.White;
            _tabMonitor.Padding = new Padding(12);

            var pnlMonitorTop = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.Transparent };
            var lblHint = new Label
            {
                Text = "请勾选需要在悬浮窗上显示的监控项，可使用右侧按钮调整显示顺序：",
                AutoSize = true,
                Location = new Point(0, 8),
                ForeColor = Color.FromArgb(80, 80, 80)
            };
            pnlMonitorTop.Controls.Add(lblHint);

            _clbItems.Dock = DockStyle.Fill;
            _clbItems.CheckOnClick = true;
            _clbItems.Font = new Font("Microsoft YaHei UI", 9.5F);
            _clbItems.IntegralHeight = false;

            var pnlMonitorRight = new Panel { Dock = DockStyle.Right, Width = 110, Padding = new Padding(10, 0, 0, 0) };
            _btnUp.Text = "▲ 上移";
            _btnUp.Size = new Size(95, 32);
            _btnUp.Location = new Point(10, 0);
            _btnUp.Click += (_, __) => MoveItem(-1);

            _btnDown.Text = "▼ 下移";
            _btnDown.Size = new Size(95, 32);
            _btnDown.Location = new Point(10, 40);
            _btnDown.Click += (_, __) => MoveItem(1);

            _btnDefault.Text = "恢复默认";
            _btnDefault.Size = new Size(95, 32);
            _btnDefault.Location = new Point(10, 95);
            _btnDefault.Click += (_, __) => RestoreDefaultItems();

            pnlMonitorRight.Controls.AddRange(new Control[] { _btnUp, _btnDown, _btnDefault });

            _tabMonitor.Controls.Add(_clbItems);
            _tabMonitor.Controls.Add(pnlMonitorRight);
            _tabMonitor.Controls.Add(pnlMonitorTop);

            // Tab 2: 常规设置
            _tabGeneral.Text = "⚙ 常规设置";
            _tabGeneral.BackColor = Color.White;
            _tabGeneral.Padding = new Padding(16);

            // Group 1: 开机自启
            var gbBoot = new GroupBox
            {
                Text = " 开机自启 ",
                Location = new Point(16, 16),
                Size = new Size(515, 75),
                ForeColor = Color.FromArgb(50, 50, 50)
            };
            _chkAutoStart.Text = "开机自动启动 LiteMonitor (使用系统计划任务最高权限)";
            _chkAutoStart.AutoSize = true;
            _chkAutoStart.Location = new Point(20, 30);
            gbBoot.Controls.Add(_chkAutoStart);

            // Group 2: 悬浮窗设置
            var gbBar = new GroupBox
            {
                Text = " 悬浮窗行为 ",
                Location = new Point(16, 105),
                Size = new Size(515, 170),
                ForeColor = Color.FromArgb(50, 50, 50)
            };

            _chkPinned.Text = "固定展开悬浮窗（鼠标离开时不自动折叠）";
            _chkPinned.AutoSize = true;
            _chkPinned.Location = new Point(20, 30);

            var lblPos = new Label { Text = "默认停靠位置：", Location = new Point(20, 72), AutoSize = true };
            _cbPosition.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbPosition.Items.AddRange(new object[] { "顶部居中 (推荐)", "顶部靠左", "顶部靠右" });
            _cbPosition.Location = new Point(140, 68);
            _cbPosition.Width = 180;

            var lblInterval = new Label { Text = "刷新采集频率：", Location = new Point(20, 115), AutoSize = true };
            _cbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbInterval.Items.AddRange(new object[] { "500 毫秒 (快速)", "1000 毫秒 (标准)", "1500 毫秒", "2000 毫秒 (节能)" });
            _cbInterval.Location = new Point(140, 112);
            _cbInterval.Width = 180;

            gbBar.Controls.AddRange(new Control[] { _chkPinned, lblPos, _cbPosition, lblInterval, _cbInterval });

            _tabGeneral.Controls.AddRange(new Control[] { gbBoot, gbBar });

            InitColorTab();

            _tabCtrl.TabPages.Add(_tabMonitor);
            _tabCtrl.TabPages.Add(_tabGeneral);
            _tabCtrl.TabPages.Add(_tabColor);

            // Bottom Buttons
            var pnlBottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                BackColor = Color.FromArgb(240, 240, 242),
                Padding = new Padding(0, 10, 16, 10)
            };

            _btnOk.Text = "确定";
            _btnOk.Size = new Size(82, 32);
            _btnOk.Location = new Point(300, 10);
            _btnOk.BackColor = Color.FromArgb(0, 120, 215);
            _btnOk.ForeColor = Color.White;
            _btnOk.FlatStyle = FlatStyle.Flat;
            _btnOk.Click += (_, __) => { SaveSettings(); this.Close(); };

            _btnCancel.Text = "取消";
            _btnCancel.Size = new Size(82, 32);
            _btnCancel.Location = new Point(390, 10);
            _btnCancel.Click += (_, __) => this.Close();

            _btnApply.Text = "应用";
            _btnApply.Size = new Size(82, 32);
            _btnApply.Location = new Point(480, 10);
            _btnApply.Click += (_, __) => SaveSettings();

            pnlBottom.Controls.AddRange(new Control[] { _btnOk, _btnCancel, _btnApply });

            this.Controls.Add(_tabCtrl);
            this.Controls.Add(pnlBottom);
        }

        private void InitColorTab()
        {
            _tabColor.Text = "🎨 颜色与阈值";
            _tabColor.BackColor = Color.White;
            _tabColor.Padding = new Padding(16);

            var gbColor = new GroupBox
            {
                Text = " 数值颜色 (单位固定白色) ",
                Location = new Point(16, 16),
                Size = new Size(515, 70),
                ForeColor = Color.FromArgb(50, 50, 50)
            };
            string[] names = { "正常", "警告", "严重" };
            for (int i = 0; i < 3; i++)
            {
                var lbl = new Label { Text = names[i], AutoSize = true, Location = new Point(20 + i * 165, 32) };
                var btn = _btnColors[i];
                btn.Size = new Size(80, 26);
                btn.Location = new Point(65 + i * 165, 27);
                btn.FlatStyle = FlatStyle.Flat;
                btn.Click += (_, __) => PickColor(btn);
                gbColor.Controls.Add(lbl);
                gbColor.Controls.Add(btn);
            }

            var gbTh = new GroupBox
            {
                Text = " 阈值 (达到即变色) ",
                Location = new Point(16, 98),
                Size = new Size(515, 320),
                ForeColor = Color.FromArgb(50, 50, 50)
            };
            gbTh.Controls.Add(new Label { Text = "指标", AutoSize = true, Location = new Point(20, 26) });
            gbTh.Controls.Add(new Label { Text = "警告 ≥", AutoSize = true, Location = new Point(240, 26) });
            gbTh.Controls.Add(new Label { Text = "严重 ≥", AutoSize = true, Location = new Point(360, 26) });

            for (int i = 0; i < ThresholdRows.Length; i++)
            {
                int y = 52 + i * 30;
                gbTh.Controls.Add(new Label { Text = ThresholdRows[i].Name, AutoSize = true, Location = new Point(20, y + 3) });
                _numWarn[i] = NewNum(new Point(240, y));
                _numCrit[i] = NewNum(new Point(360, y));
                gbTh.Controls.Add(_numWarn[i]);
                gbTh.Controls.Add(_numCrit[i]);
            }

            var lblNote = new Label
            {
                Text = "频率/功耗/转速/FPS 按历史最大值的 80%/90% 判定；电池电量反向 (≤60 警告，≤20 严重)。",
                AutoSize = false,
                Size = new Size(370, 40),
                Location = new Point(20, 52 + ThresholdRows.Length * 30 + 4),
                ForeColor = Color.FromArgb(110, 110, 110)
            };
            var btnReset = new Button { Text = "恢复默认", Size = new Size(95, 30), Location = new Point(400, 270) };
            btnReset.Click += (_, __) => LoadColorTab(new Settings());
            gbTh.Controls.Add(lblNote);
            gbTh.Controls.Add(btnReset);

            _tabColor.Controls.AddRange(new Control[] { gbColor, gbTh });
        }

        private static NumericUpDown NewNum(Point loc) => new()
        {
            Location = loc,
            Width = 90,
            DecimalPlaces = 1,
            Minimum = 0,
            Maximum = 1000000,
            Increment = 1
        };

        private static void PickColor(Button btn)
        {
            using var dlg = new ColorDialog { Color = btn.BackColor, FullOpen = true };
            if (dlg.ShowDialog() == DialogResult.OK) btn.BackColor = dlg.Color;
        }

        private void LoadColorTab(Settings src)
        {
            _btnColors[0].BackColor = Settings.ParseColor(src.ColorSafe, Settings.DefaultColorSafe);
            _btnColors[1].BackColor = Settings.ParseColor(src.ColorWarn, Settings.DefaultColorWarn);
            _btnColors[2].BackColor = Settings.ParseColor(src.ColorCrit, Settings.DefaultColorCrit);

            for (int i = 0; i < ThresholdRows.Length; i++)
            {
                var r = ThresholdRows[i].Pick(src.Thresholds);
                _numWarn[i].Value = ClampNum(_numWarn[i], r.Warn);
                _numCrit[i].Value = ClampNum(_numCrit[i], r.Crit);
            }
        }

        private static decimal ClampNum(NumericUpDown n, double v) =>
            Math.Clamp((decimal)v, n.Minimum, n.Maximum);

        private void LoadData()
        {
            LoadColorTab(_cfg);

            // AutoStart
            _chkAutoStart.Checked = AutoStart.IsEnabled();

            // Pinned
            _chkPinned.Checked = _cfg.FloatBarPinned;

            // Position
            string pos = _cfg.FloatBarPosition?.ToLowerInvariant() ?? "top_center";
            if (pos == "custom")
            {
                // 拖动得到的自定义位置，避免点确定时被重置为居中
                _cbPosition.Items.Add("自定义 (拖动后的位置)");
                _cbPosition.SelectedIndex = 3;
            }
            else if (pos == "top_left") _cbPosition.SelectedIndex = 1;
            else if (pos == "top_right") _cbPosition.SelectedIndex = 2;
            else _cbPosition.SelectedIndex = 0;

            // Interval
            int ms = _cfg.RefreshMs;
            if (ms <= 600) _cbInterval.SelectedIndex = 0;
            else if (ms <= 1200) _cbInterval.SelectedIndex = 1;
            else if (ms <= 1700) _cbInterval.SelectedIndex = 2;
            else _cbInterval.SelectedIndex = 3;

            // Items
            _itemList.Clear();
            var sorted = _cfg.MonitorItems.OrderBy(x => x.SortIndex).ToList();
            foreach (var item in sorted)
            {
                string name = KeyFriendlyNames.TryGetValue(item.Key, out var friendly) ? friendly : item.Key;
                _itemList.Add(new ItemEntry
                {
                    Key = item.Key,
                    Name = name,
                    Visible = item.VisibleInPanel
                });
            }

            // Append any missing items from dictionary
            foreach (var kv in KeyFriendlyNames)
            {
                if (!_itemList.Any(x => x.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    _itemList.Add(new ItemEntry
                    {
                        Key = kv.Key,
                        Name = kv.Value,
                        Visible = false
                    });
                }
            }

            RenderItemsList();
        }

        private void RenderItemsList(int selectIndex = -1)
        {
            _clbItems.BeginUpdate();
            _clbItems.Items.Clear();

            for (int i = 0; i < _itemList.Count; i++)
            {
                _clbItems.Items.Add(_itemList[i], _itemList[i].Visible);
            }

            if (selectIndex >= 0 && selectIndex < _clbItems.Items.Count)
            {
                _clbItems.SelectedIndex = selectIndex;
            }

            _clbItems.EndUpdate();
        }

        private void MoveItem(int direction)
        {
            int idx = _clbItems.SelectedIndex;
            if (idx < 0) return;

            int target = idx + direction;
            if (target < 0 || target >= _itemList.Count) return;

            // Sync checked state first
            _itemList[idx].Visible = _clbItems.GetItemChecked(idx);
            _itemList[target].Visible = _clbItems.GetItemChecked(target);

            var tmp = _itemList[idx];
            _itemList[idx] = _itemList[target];
            _itemList[target] = tmp;

            RenderItemsList(target);
        }

        private void RestoreDefaultItems()
        {
            var def = new Settings();
            def.InitDefaultItems();

            _itemList.Clear();
            foreach (var d in def.MonitorItems.OrderBy(x => x.SortIndex))
            {
                string name = KeyFriendlyNames.TryGetValue(d.Key, out var friendly) ? friendly : d.Key;
                _itemList.Add(new ItemEntry
                {
                    Key = d.Key,
                    Name = name,
                    Visible = d.VisibleInPanel
                });
            }

            RenderItemsList();
        }

        private void SaveSettings()
        {
            // 1. Sync checked state
            for (int i = 0; i < _itemList.Count; i++)
            {
                _itemList[i].Visible = _clbItems.GetItemChecked(i);
            }

            // 2. Update _cfg.MonitorItems
            _cfg.MonitorItems.Clear();
            for (int i = 0; i < _itemList.Count; i++)
            {
                _cfg.MonitorItems.Add(new MonitorItemConfig
                {
                    Key = _itemList[i].Key,
                    SortIndex = i + 1,
                    VisibleInPanel = _itemList[i].Visible
                });
            }

            // 3. AutoStart
            bool currentAutoStart = AutoStart.IsEnabled();
            if (_chkAutoStart.Checked != currentAutoStart)
            {
                AutoStart.Set(_chkAutoStart.Checked);
                _cfg.AutoStart = _chkAutoStart.Checked;
            }

            // 4. FloatBar options
            _cfg.FloatBarPinned = _chkPinned.Checked;

            int posIdx = _cbPosition.SelectedIndex;
            _cfg.FloatBarPosition = posIdx switch
            {
                1 => "top_left",
                2 => "top_right",
                3 => "custom",
                _ => "top_center"
            };

            int intIdx = _cbInterval.SelectedIndex;
            _cfg.RefreshMs = intIdx switch
            {
                0 => 500,
                1 => 1000,
                2 => 1500,
                3 => 2000,
                _ => 1000
            };

            // 5. 颜色与阈值
            _cfg.ColorSafe = Settings.ToHex(_btnColors[0].BackColor);
            _cfg.ColorWarn = Settings.ToHex(_btnColors[1].BackColor);
            _cfg.ColorCrit = Settings.ToHex(_btnColors[2].BackColor);
            for (int i = 0; i < ThresholdRows.Length; i++)
            {
                var r = ThresholdRows[i].Pick(_cfg.Thresholds);
                r.Warn = (double)_numWarn[i].Value;
                r.Crit = Math.Max(r.Warn, (double)_numCrit[i].Value);
            }

            _cfg.Save();

            // Refresh FloatBar
            FloatBarForm.Instance?.ApplySettingsChange();
        }
    }
}

