using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AmoWinTrans
{
    /// <summary>
    /// 快捷键设置。一个动作一个标签页，每页的控件结构一样：
    /// 启用 / 触发键（鼠标键 或 键盘键）/ 防误触 / 是否拦截。
    /// </summary>
    internal sealed class AutoTriggerDialog : Form
    {
        private sealed class BindingUi
        {
            public string Id;
            public string WarnText;
            public AutoTriggerConfig Cfg;

            public CheckBox Enable;
            public RadioButton RbMouse;
            public ComboBox Cbo;
            public RadioButton RbKey;
            public Button BtnKey;
            public RadioButton RbInstant;
            public RadioButton RbPress;
            public NumericUpDown NumCount;
            public NumericUpDown NumWindow;
            public RadioButton RbHold;
            public NumericUpDown NumHold;
            public CheckBox Swallow;
            public TabPage Page;
        }

        private readonly List<BindingUi> _uis = new List<BindingUi>();
        private BindingUi _capturing;
        private readonly TabControl _tabs;

        /// <summary>点确定后的结果；取消则为 null。</summary>
        public Dictionary<string, AutoTriggerConfig> Result { get; private set; }

        public AutoTriggerDialog(Dictionary<string, AutoTriggerConfig> current)
        {
            Text = "快捷键设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(456, 462);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(250, 250, 252);

            _tabs = new TabControl
            {
                Location = new Point(12, 12),
                Size = new Size(432, 402),
            };

            AddTab(TriggerIds.AutoPress, "连按开关",
                "作用于「最近一次开过连按的窗口」。",
                current, c => { c.Mode = AutoTriggerMode.PressCount; c.PressCount = 2; c.Kind = AutoTriggerKind.MouseX2; });

            AddTab(TriggerIds.Minimize, "最小化窗口",
                "再按一次会把刚最小化的窗口还原回来，所以是「切换」而不是单向最小化。",
                current, c => { c.Mode = AutoTriggerMode.Instant; c.Kind = AutoTriggerKind.MouseMiddle; });

            AddTab(TriggerIds.TopMost, "窗口置顶",
                "对当前前台窗口切换「总在最前」。",
                current, c => { c.Mode = AutoTriggerMode.Instant; c.Kind = AutoTriggerKind.Key; c.VirtualKey = 0x71; });

            AddTab(TriggerIds.ClickThrough, "鼠标穿透",
                "对当前前台窗口切换鼠标穿透。开了之后鼠标点不到那个窗口，但本工具仍认得出它；\n"
                + "实在找不回来可以用托盘菜单的「取消所有鼠标穿透」。",
                current, c => { c.Mode = AutoTriggerMode.LongPress; c.HoldMs = 1200; c.Kind = AutoTriggerKind.Key; c.VirtualKey = 0x72; });

            AddTab(TriggerIds.MoveBack, "挪回来",
                "把所有被「挪到屏幕外」的窗口挪回来。\n"
                + "窗口挪走之后看不见、鼠标也够不到它，只能靠这个快捷键（或托盘菜单）找回来。\n"
                + "出厂就设好了 Ctrl+Alt+B，默认开启 —— 建议别关。",
                current, c =>
                {
                    AutoTriggerConfig d = TriggerIds.DefaultMoveBack();
                    c.Enabled = d.Enabled;
                    c.Kind = d.Kind;
                    c.VirtualKey = d.VirtualKey;
                    c.Ctrl = d.Ctrl;
                    c.Alt = d.Alt;
                    c.Mode = d.Mode;
                });

            Controls.Add(_tabs);

            var btnOk = new Button
            {
                Text = "确定",
                Location = new Point(280, 424),
                Size = new Size(80, 28),
                DialogResult = DialogResult.None,
            };
            btnOk.Click += OnOk;

            var btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(368, 424),
                Size = new Size(80, 28),
                DialogResult = DialogResult.Cancel,
            };

            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            foreach (BindingUi ui in _uis) UpdateEnabledState(ui);
            foreach (BindingUi ui in _uis) UpdateKeyButton(ui);
        }

        private void AddTab(string id, string title, string warnText,
                            Dictionary<string, AutoTriggerConfig> current,
                            Action<AutoTriggerConfig> makeDefault)
        {
            AutoTriggerConfig cfg = null;
            if (current != null && current.TryGetValue(id, out AutoTriggerConfig saved) && saved != null)
                cfg = saved.Clone();
            if (cfg == null)
            {
                cfg = new AutoTriggerConfig { Enabled = false };
                makeDefault(cfg);
                cfg.Normalize();
            }

            var ui = new BindingUi { Id = id, Cfg = cfg, WarnText = warnText };
            var page = new TabPage(title) { BackColor = Color.FromArgb(250, 250, 252) };
            ui.Page = page;

            // ---- 总开关 ----
            ui.Enable = new CheckBox
            {
                Text = "启用这个快捷键（全局生效，任何窗口下都能用）",
                Location = new Point(18, 14),
                Size = new Size(384, 24),
                Checked = cfg.Enabled,
            };

            // ---- 触发键 ----
            var grpKey = new GroupBox
            {
                Text = "触发键",
                Location = new Point(16, 46),
                Size = new Size(388, 100),
            };

            ui.RbMouse = new RadioButton
            {
                Text = "鼠标按键",
                Location = new Point(12, 20),
                Size = new Size(110, 22),
            };

            ui.Cbo = new ComboBox
            {
                Location = new Point(32, 46),
                Size = new Size(180, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            ui.Cbo.Items.AddRange(new object[] { "侧键1（一般是后退）", "侧键2（一般是前进）", "中键（滚轮按下）" });
            ui.Cbo.SelectedIndex = cfg.Kind == AutoTriggerKind.MouseX1 ? 0
                                 : cfg.Kind == AutoTriggerKind.MouseMiddle ? 2 : 1;

            ui.RbKey = new RadioButton
            {
                Text = "键盘按键",
                Location = new Point(12, 74),
                Size = new Size(100, 22),
            };

            ui.BtnKey = new Button
            {
                Location = new Point(114, 70),
                Size = new Size(260, 28),
                FlatStyle = FlatStyle.System,
            };
            ui.BtnKey.Click += (s, e) => BeginCapture(ui);

            grpKey.Controls.Add(ui.RbMouse);
            grpKey.Controls.Add(ui.Cbo);
            grpKey.Controls.Add(ui.RbKey);
            grpKey.Controls.Add(ui.BtnKey);

            if (cfg.Kind == AutoTriggerKind.Key) ui.RbKey.Checked = true;
            else ui.RbMouse.Checked = true;

            // ---- 防误触 ----
            var grpAnti = new GroupBox
            {
                Text = "怎么算「按下了」（防误触）",
                Location = new Point(16, 152),
                Size = new Size(388, 128),
            };

            ui.RbInstant = new RadioButton
            {
                Text = "按一下就触发（不防误触）",
                Location = new Point(12, 20),
                Size = new Size(360, 22),
            };

            ui.RbPress = new RadioButton
            {
                Text = "连按",
                Location = new Point(12, 44),
                Size = new Size(60, 22),
            };

            ui.NumCount = new NumericUpDown
            {
                Location = new Point(72, 41),
                Size = new Size(50, 24),
                Minimum = 2,
                Maximum = 5,
                Value = Math.Max(2, Math.Min(5, cfg.PressCount)),
            };

            var lblA = new Label
            {
                Text = "次（相邻间隔不超过",
                Location = new Point(128, 45),
                Size = new Size(130, 20),
            };

            ui.NumWindow = new NumericUpDown
            {
                Location = new Point(258, 41),
                Size = new Size(62, 24),
                DecimalPlaces = 2,
                Increment = 0.1m,
                Minimum = 0.2m,
                Maximum = 2m,
                Value = Math.Max(0.2m, Math.Min(2m, cfg.PressWindowMs / 1000m)),
            };

            var lblB = new Label
            {
                Text = "秒）",
                Location = new Point(324, 45),
                Size = new Size(50, 20),
            };

            ui.RbHold = new RadioButton
            {
                Text = "长按",
                Location = new Point(12, 68),
                Size = new Size(60, 22),
            };

            ui.NumHold = new NumericUpDown
            {
                Location = new Point(72, 65),
                Size = new Size(62, 24),
                DecimalPlaces = 2,
                Increment = 0.1m,
                Minimum = 0.2m,
                Maximum = 5m,
                Value = Math.Max(0.2m, Math.Min(5m, cfg.HoldMs / 1000m)),
            };

            var lblC = new Label
            {
                Text = "秒后触发",
                Location = new Point(140, 69),
                Size = new Size(80, 20),
            };

            var lblHint = new Label
            {
                Text = "「连按」的时间窗能防止走路时蹭到 —— 两次隔太久就不算数。",
                Location = new Point(12, 96),
                Size = new Size(364, 20),
                ForeColor = Color.FromArgb(140, 140, 150),
            };

            grpAnti.Controls.Add(ui.RbInstant);
            grpAnti.Controls.Add(ui.RbPress);
            grpAnti.Controls.Add(ui.NumCount);
            grpAnti.Controls.Add(lblA);
            grpAnti.Controls.Add(ui.NumWindow);
            grpAnti.Controls.Add(lblB);
            grpAnti.Controls.Add(ui.RbHold);
            grpAnti.Controls.Add(ui.NumHold);
            grpAnti.Controls.Add(lblC);
            grpAnti.Controls.Add(lblHint);

            if (cfg.Mode == AutoTriggerMode.Instant) ui.RbInstant.Checked = true;
            else if (cfg.Mode == AutoTriggerMode.LongPress) ui.RbHold.Checked = true;
            else ui.RbPress.Checked = true;

            // ---- 拦截 ----
            ui.Swallow = new CheckBox
            {
                Text = "拦截这个键（不让它传给其他程序）",
                Location = new Point(18, 290),
                Size = new Size(384, 24),
                Checked = cfg.Swallow,
            };

            var lblWarn = new Label
            {
                Text = warnText + "\n不勾选「拦截」时，这个键的原本功能照常生效。",
                Location = new Point(18, 316),
                Size = new Size(384, 46),
                ForeColor = Color.FromArgb(140, 140, 150),
            };

            page.Controls.AddRange(new Control[]
            {
                ui.Enable, grpKey, grpAnti, ui.Swallow, lblWarn,
            });

            _tabs.TabPages.Add(page);
            _uis.Add(ui);

            ui.RbMouse.CheckedChanged += (s, e) => UpdateEnabledState(ui);
            ui.RbKey.CheckedChanged += (s, e) => UpdateEnabledState(ui);
            ui.RbInstant.CheckedChanged += (s, e) => UpdateEnabledState(ui);
            ui.RbPress.CheckedChanged += (s, e) => UpdateEnabledState(ui);
            ui.RbHold.CheckedChanged += (s, e) => UpdateEnabledState(ui);
            ui.Enable.CheckedChanged += (s, e) => UpdateEnabledState(ui);
        }

        private void UpdateEnabledState(BindingUi ui)
        {
            bool on = ui.Enable.Checked;

            ui.RbMouse.Enabled = on;
            ui.RbKey.Enabled = on;
            ui.Cbo.Enabled = on && ui.RbMouse.Checked;
            ui.BtnKey.Enabled = on && ui.RbKey.Checked;

            ui.RbInstant.Enabled = on;
            ui.RbPress.Enabled = on;
            ui.RbHold.Enabled = on;
            ui.NumCount.Enabled = on && ui.RbPress.Checked;
            ui.NumWindow.Enabled = on && ui.RbPress.Checked;
            ui.NumHold.Enabled = on && ui.RbHold.Checked;

            ui.Swallow.Enabled = on;
        }

        private void BeginCapture(BindingUi ui)
        {
            EndCapture();
            _capturing = ui;
            ui.BtnKey.Text = "请按下要用的键…（Esc 取消）";
            ui.BtnKey.BackColor = Color.FromArgb(255, 244, 214);
        }

        private void EndCapture()
        {
            if (_capturing == null) return;
            BindingUi ui = _capturing;
            _capturing = null;
            ui.BtnKey.BackColor = SystemColors.Control;
            UpdateKeyButton(ui);
        }

        private void UpdateKeyButton(BindingUi ui)
        {
            if (_capturing == ui) return;
            ui.BtnKey.Text = ui.Cfg.Kind == AutoTriggerKind.Key
                ? "当前按键：" + AutoPress.KeyName((ushort)ui.Cfg.VirtualKey) + "    （点这里重新录制）"
                : "点这里，然后按下要用的键";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_capturing == null) return base.ProcessCmdKey(ref msg, keyData);

            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.None || k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu)
                return true;

            if (k == Keys.Escape)
            {
                EndCapture();
                return true;
            }

            BindingUi ui = _capturing;
            ui.Cfg.Kind = AutoTriggerKind.Key;
            ui.Cfg.Ctrl = (keyData & Keys.Control) != 0;
            ui.Cfg.Alt = (keyData & Keys.Alt) != 0;
            ui.Cfg.Shift = (keyData & Keys.Shift) != 0;
            ui.Cfg.VirtualKey = (int)k;

            ui.RbKey.Checked = true;
            EndCapture();
            UpdateEnabledState(ui);
            return true;
        }

        private void OnOk(object sender, EventArgs e)
        {
            // 先做校验
            foreach (BindingUi ui in _uis)
            {
                if (!ui.Enable.Checked) continue;
                if (ui.RbKey.Checked && ui.Cfg.VirtualKey == 0)
                {
                    _tabs.SelectedTab = ui.Page;
                    MessageBox.Show(this,
                        "「" + AutoTriggerWatcher.ActionName(ui.Id) + "」还没录制键盘按键。\n"
                        + "点「点这里，然后按下要用的键」录一个，或者改成鼠标按键。",
                        "快捷键设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            var result = new Dictionary<string, AutoTriggerConfig>(StringComparer.Ordinal);

            foreach (BindingUi ui in _uis)
            {
                AutoTriggerConfig c = ui.Cfg;
                c.Enabled = ui.Enable.Checked;

                if (ui.RbKey.Checked)
                {
                    c.Kind = AutoTriggerKind.Key;
                }
                else
                {
                    c.Kind = ui.Cbo.SelectedIndex == 0 ? AutoTriggerKind.MouseX1
                           : ui.Cbo.SelectedIndex == 2 ? AutoTriggerKind.MouseMiddle
                           : AutoTriggerKind.MouseX2;
                }

                c.Mode = ui.RbInstant.Checked ? AutoTriggerMode.Instant
                        : ui.RbHold.Checked ? AutoTriggerMode.LongPress
                        : AutoTriggerMode.PressCount;

                c.PressCount = (int)ui.NumCount.Value;
                c.PressWindowMs = (int)Math.Round(ui.NumWindow.Value * 1000m);
                c.HoldMs = (int)Math.Round(ui.NumHold.Value * 1000m);
                c.Swallow = ui.Swallow.Checked;
                c.Normalize();

                result[ui.Id] = c;
            }

            Result = result;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            DialogHelper.ForceToFront(this);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            EndCapture();
            base.OnFormClosing(e);
        }
    }
}
