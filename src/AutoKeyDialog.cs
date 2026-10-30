using System;
using System.Drawing;
using System.Windows.Forms;

namespace AmoWinTrans
{
    /// <summary>
    /// 连按设置对话框：录一个键、设间隔、选发送方式。
    /// 普通 WinForms 控件，不玩自绘 —— 这是个偶尔才开一次的工具窗，清晰好读最重要。
    /// </summary>
    internal sealed class AutoKeyDialog : Form
    {
        private readonly AutoKeyConfig _cfg;
        private bool _capturing;

        private readonly Label _lblTarget;
        private readonly Button _btnKey;
        private readonly Label _lblHint;
        private readonly NumericUpDown _numMin;
        private readonly NumericUpDown _numMax;
        private readonly RadioButton _rbForegroundOnly;
        private readonly RadioButton _rbHybrid;
        private readonly RadioButton _rbBackgroundOnly;
        private readonly CheckBox _chkEnable;
        private readonly Button _btnOk;
        private readonly Button _btnCancel;
        private readonly Label _lblTrigger;

        /// <summary>点确定后的配置；取消则为 null。</summary>
        public AutoKeyConfig Result { get; private set; }
        public bool EnableNow { get; private set; }

        public AutoKeyDialog(string targetTitle, AutoKeyConfig current, bool currentlyRunning,
                             Func<string> triggerSummary, Action<Form> openTriggers)
        {
            _cfg = current != null ? current.Clone() : new AutoKeyConfig();

            Text = "连按按键设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(400, 330);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(250, 250, 252);

            string shortTitle = targetTitle ?? "";
            if (shortTitle.Length > 34) shortTitle = shortTitle.Substring(0, 33) + "…";

            _lblTarget = new Label
            {
                Text = "作用于窗口：" + shortTitle,
                Location = new Point(16, 14),
                Size = new Size(368, 20),
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(70, 70, 80),
            };

            var lblKey = new Label
            {
                Text = "按键",
                Location = new Point(16, 46),
                Size = new Size(40, 24),
                TextAlign = ContentAlignment.MiddleLeft,
            };

            _btnKey = new Button
            {
                Location = new Point(60, 43),
                Size = new Size(324, 30),
                FlatStyle = FlatStyle.System,
                Text = "点这里，然后按下要连按的键",
            };
            _btnKey.Click += (s, e) => BeginCapture();

            _lblHint = new Label
            {
                Text = "游戏里选对话的 F、空格、回车之类都可以；Esc 取消录制。",
                Location = new Point(60, 77),
                Size = new Size(324, 18),
                ForeColor = Color.FromArgb(140, 140, 150),
            };

            var lblRange = new Label
            {
                Text = "间隔",
                Location = new Point(16, 106),
                Size = new Size(40, 24),
                TextAlign = ContentAlignment.MiddleLeft,
            };

            _numMin = new NumericUpDown
            {
                Location = new Point(60, 108),
                Size = new Size(72, 26),
                DecimalPlaces = 2,
                Increment = 0.05m,
                Minimum = AutoKeyConfig.MinIntervalMs / 1000m,
                Maximum = AutoKeyConfig.MaxIntervalMs / 1000m,
                Value = ClampSeconds(_cfg.MinMs),
            };

            var lblTo = new Label
            {
                Text = "秒  ~",
                Location = new Point(136, 110),
                Size = new Size(46, 22),
                TextAlign = ContentAlignment.MiddleCenter,
            };

            _numMax = new NumericUpDown
            {
                Location = new Point(184, 108),
                Size = new Size(72, 26),
                DecimalPlaces = 2,
                Increment = 0.05m,
                Minimum = AutoKeyConfig.MinIntervalMs / 1000m,
                Maximum = AutoKeyConfig.MaxIntervalMs / 1000m,
                Value = ClampSeconds(_cfg.MaxMs),
            };

            var lblSec = new Label
            {
                Text = "秒（每次随机）",
                Location = new Point(260, 110),
                Size = new Size(136, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(140, 140, 150),
            };

            var grp = new GroupBox
            {
                Text = "发送方式",
                Location = new Point(16, 144),
                Size = new Size(368, 100),
            };

            _rbHybrid = new RadioButton
            {
                Text = "前台用真实按键，后台投递给窗口（推荐）",
                Location = new Point(12, 20),
                Size = new Size(348, 22),
            };

            _rbForegroundOnly = new RadioButton
            {
                Text = "仅当该窗口在前台时发送（最保守）",
                Location = new Point(12, 44),
                Size = new Size(348, 22),
            };

            _rbBackgroundOnly = new RadioButton
            {
                Text = "始终后台投递给窗口（部分程序无效）",
                Location = new Point(12, 68),
                Size = new Size(348, 22),
            };

            switch (_cfg.Mode)
            {
                case AutoSendMode.ForegroundOnly: _rbForegroundOnly.Checked = true; break;
                case AutoSendMode.BackgroundOnly: _rbBackgroundOnly.Checked = true; break;
                default: _rbHybrid.Checked = true; break;
            }

            grp.Controls.Add(_rbHybrid);
            grp.Controls.Add(_rbForegroundOnly);
            grp.Controls.Add(_rbBackgroundOnly);

            _chkEnable = new CheckBox
            {
                Text = "保存后立即开始连按",
                Location = new Point(18, 288),
                Size = new Size(200, 24),
                Checked = currentlyRunning || !_cfg.IsConfigured,
            };

            // 快捷键入口放在这里，用户配完连按顺手就能看到
            _lblTrigger = new Label
            {
                Location = new Point(18, 254),
                Size = new Size(262, 22),
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(90, 90, 100),
            };
            UpdateTriggerLabel(triggerSummary);

            var btnTrigger = new Button
            {
                Text = "快捷键设置…",
                Location = new Point(286, 250),
                Size = new Size(98, 28),
                FlatStyle = FlatStyle.System,
            };
            btnTrigger.Click += (s, e) =>
            {
                // 把自己当 owner 传过去，否则新弹的窗口会被这个对话框盖住
                openTriggers?.Invoke(this);
                UpdateTriggerLabel(triggerSummary);
            };

            _btnOk = new Button
            {
                Text = "确定",
                Location = new Point(216, 286),
                Size = new Size(80, 28),
                DialogResult = DialogResult.None,
            };
            _btnOk.Click += OnOk;

            _btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(304, 286),
                Size = new Size(80, 28),
                DialogResult = DialogResult.Cancel,
            };

            Controls.AddRange(new Control[]
            {
                _lblTarget, lblKey, _btnKey, _lblHint,
                lblRange, _numMin, lblTo, _numMax, lblSec,
                grp, _lblTrigger, btnTrigger, _chkEnable, _btnOk, _btnCancel,
            });
            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            UpdateKeyButton();
        }

        private void UpdateTriggerLabel(Func<string> triggerSummary)
        {
            string s = "";
            try { if (triggerSummary != null) s = triggerSummary(); } catch { }
            _lblTrigger.Text = "开关快捷键：" + (string.IsNullOrEmpty(s) ? "未设置" : s);
        }

        private static decimal ClampSeconds(int ms)
        {
            decimal v = ms / 1000m;
            if (v < AutoKeyConfig.MinIntervalMs / 1000m) v = AutoKeyConfig.MinIntervalMs / 1000m;
            if (v > AutoKeyConfig.MaxIntervalMs / 1000m) v = AutoKeyConfig.MaxIntervalMs / 1000m;
            return v;
        }

        private void BeginCapture()
        {
            _capturing = true;
            _btnKey.Text = "请按下要连按的键…（Esc 取消）";
            _btnKey.BackColor = Color.FromArgb(255, 244, 214);
        }

        private void EndCapture()
        {
            _capturing = false;
            _btnKey.BackColor = SystemColors.Control;
            UpdateKeyButton();
        }

        private void UpdateKeyButton()
        {
            if (_capturing) return;
            _btnKey.Text = _cfg.IsConfigured
                ? "当前按键：" + _cfg.Display + "    （点这里重新录制）"
                : "点这里，然后按下要连按的键";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!_capturing) return base.ProcessCmdKey(ref msg, keyData);

            Keys k = keyData & Keys.KeyCode;

            // 只按了修饰键，继续等
            if (k == Keys.None || k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu)
                return true;

            if (k == Keys.Escape)
            {
                EndCapture();
                return true;
            }

            _cfg.Ctrl = (keyData & Keys.Control) != 0;
            _cfg.Alt = (keyData & Keys.Alt) != 0;
            _cfg.Shift = (keyData & Keys.Shift) != 0;
            _cfg.VirtualKey = (int)k;

            EndCapture();
            return true;
        }

        private void OnOk(object sender, EventArgs e)
        {
            if (!_cfg.IsConfigured)
            {
                MessageBox.Show(this, "还没录制按键。点「点这里，然后按下要连按的键」录一个。",
                    "连按按键设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _cfg.MinMs = (int)Math.Round(_numMin.Value * 1000m);
            _cfg.MaxMs = (int)Math.Round(_numMax.Value * 1000m);
            _cfg.Mode = _rbForegroundOnly.Checked ? AutoSendMode.ForegroundOnly
                      : _rbBackgroundOnly.Checked ? AutoSendMode.BackgroundOnly
                      : AutoSendMode.Hybrid;
            _cfg.Normalize();

            Result = _cfg;
            EnableNow = _chkEnable.Checked;
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
            if (_capturing) EndCapture();
            base.OnFormClosing(e);
        }
    }
}
