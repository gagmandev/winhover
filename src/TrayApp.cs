using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

namespace AmoWinTrans
{
    /// <summary>只用来收 WM_HOTKEY 的隐形窗口。</summary>
    internal sealed class MessageWindow : Form
    {
        public event Action<int> HotKeyPressed;

        public MessageWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
        }

        protected override void SetVisibleCore(bool value)
        {
            // 永远不显示，只保留消息循环里的那个句柄
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                HotKeyPressed?.Invoke(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private const int HK_UP = 1;
        private const int HK_DOWN = 2;
        private const int HK_RESET = 3;
        private const int HK_TOP = 4;
        private const int HK_PAUSE = 5;
        private const int HK_THROUGH = 6;
        private const int HK_AUTOTOGGLE = 7;   // Ctrl+Alt+K 切换当前窗口连按
        private const int HK_AUTOSTOP = 8;     // Ctrl+Alt+P 停止所有连按（应急）

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "AmoWinTrans";

        private static readonly HashSet<string> BlackClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TaskListThumbnailWnd",
            "SysShadow", "ForegroundStaging", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
            "EdgeUiInputTopWndClass", "MultitaskingViewFrame", "tooltips_class32", "#32768",
            "ApplicationManager_ImmersiveShellWindow", "Shell_InputSwitchTopLevelWindow",
            "Windows.Internal.Shell.TabProxyWindow", "TaskManagerWindow", "NarratorHelperWindow",
            "DV2ControlHost", "MsgrIMEWindowClass", "SysTreeView32Shadow",
        };

        private readonly Settings _cfg = Settings.Load();
        private readonly Widget _widget = new Widget();
        private readonly MessageWindow _msg = new MessageWindow();
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly WinTimer _tick = new WinTimer { Interval = 55 };
        private readonly WinTimer _autoTimer = new WinTimer { Interval = 25 };
        private readonly WinTimer _saveTimer = new WinTimer { Interval = 1400 };
        private readonly WinTimer _pruneTimer = new WinTimer { Interval = 5000 };
        private readonly Dictionary<IntPtr, WinState> _states = new Dictionary<IntPtr, WinState>();

        /// <summary>
        /// 被我们设成「鼠标穿透」的窗口。
        /// 这些窗口收不到鼠标消息，WindowFromPoint 只会返回它背后的窗口，
        /// 所以必须自己记一份，否则用户开完穿透就再也认不出、改不回来了。
        /// </summary>
        private readonly HashSet<IntPtr> _clickThrough = new HashSet<IntPtr>();

        /// <summary>连按开关的全局触发键（鼠标侧键 / 键盘按键）。</summary>
        private readonly AutoTriggerWatcher _trigger = new AutoTriggerWatcher();

        /// <summary>快捷键绑定（动作 id -> 配置 + 运行状态）。</summary>
        private readonly List<TriggerBinding> _triggerBindings = new List<TriggerBinding>();

        /// <summary>最近一次配过 / 开过连按的窗口，触发键就作用在它身上。</summary>
        private IntPtr _lastAutoHwnd = IntPtr.Zero;

        /// <summary>最近一次被快捷键最小化的窗口，再按一次就还原它。</summary>
        private IntPtr _lastMinimized = IntPtr.Zero;

        private IntPtr _widgetHwnd;
        private IntPtr _msgHwnd;
        private IntPtr _target = IntPtr.Zero;
        private DateTime _lastAlive = DateTime.MinValue;
        /// <summary>
        /// 最近一次 Tick 为什么没把悬停按钮显示出来（给托盘菜单的「诊断」用）。
        /// 以前这里还有个 _paused 开关，和 _cfg.Enabled 管同一件事、又都不显示状态，
        /// 用户点来点去都点不对 —— 已经合并成一个了。
        /// </summary>
        private string _lastReject = "程序刚启动，还没检测过";

        /// <summary>「保活发送什么」那几个菜单项，用来同步勾选状态。</summary>
        private readonly List<KeyValuePair<KeepAliveInput, ToolStripMenuItem>> _miKeepAliveInputs
            = new List<KeyValuePair<KeepAliveInput, ToolStripMenuItem>>();
        private bool _hotkeysOk;
        private Icon _trayIcon;
        private bool _firstHintShown;
        private int _dbg;
        private int _autoSkip;

        private ToolStripMenuItem _miEnabled;
        private ToolStripMenuItem _miWhy;
        private ToolStripMenuItem _miTriggers;
        private ToolStripMenuItem _miTitleZone;
        private ToolStripMenuItem _miWholeZone;
        private ToolStripMenuItem _miRemember;
        private ToolStripMenuItem _miAutoApply;
        private ToolStripMenuItem _miHotkeys;
        private ToolStripMenuItem _miMethodLayered;
        private ToolStripMenuItem _miMethodAcrylic;
        private ToolStripMenuItem _miAutoStart;

        public TrayApp()
        {
            _widgetHwnd = _widget.Handle;
            _msgHwnd = _msg.Handle;

            BuildTray();

            _widget.OpacityChanged += OnWidgetOpacityChanged;
            _widget.TopMostToggled += OnWidgetTopMostToggled;
            _widget.ClickThroughToggled += OnWidgetClickThroughToggled;
            _widget.AutoHideToggled += OnWidgetAutoHideToggled;
            _widget.KeepAliveToggled += OnWidgetKeepAliveToggled;
            _widget.KeepAliveMoveRequested += OnWidgetKeepAliveMove;
            _widget.AutoPressToggled += OnWidgetAutoPressToggled;
            _widget.AutoPressConfigure += OnWidgetAutoPressConfigure;
            _widget.ResetRequested += OnWidgetReset;

            _msg.HotKeyPressed += OnHotKey;
            _tick.Tick += (s, e) =>
            {
                try { Tick(); }
                catch (Exception ex) { Log.Error("Tick", ex); }
            };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); _cfg.Save(); };
            _pruneTimer.Tick += (s, e) => PruneStates();
            _autoTimer.Tick += (s, e) =>
            {
                try
                {
                    _trigger.Poll();
                    while (_trigger.TryDequeueFired(out string firedId)) OnTriggerFired(firedId);
                    TickAuto();
                }
                catch (Exception ex) { Log.Error("TickAuto", ex); }
            };

            _tick.Start();
            _pruneTimer.Start();
            _autoTimer.Start();

            Log.Info("TrayApp: 注册热键");
            ApplyHotkeySetting();

            ApplyTriggerSetting();

            _tray.Visible = true;
            Log.Info("TrayApp: 托盘已显示");
            ShowHintOnce();
        }

        // ==================================================================
        //  托盘
        // ==================================================================

        private void BuildTray()
        {
            _trayIcon = MakeTrayIcon(true);
            _tray.Icon = _trayIcon;
            _tray.Text = "阿墨 · 窗口透明度\n把鼠标移到任意窗口的标题栏右上角试试";
            _tray.Visible = false;

            var menu = new ContextMenuStrip { ShowImageMargin = false };

            _miEnabled = new ToolStripMenuItem("悬停按钮", null, (s, e) => ToggleEnabledFromTray());
            menu.Items.Add(_miEnabled);

            // 用户反馈"不知道悬停窗到底有没有启用" —— 给一个能自己查的入口
            _miWhy = new ToolStripMenuItem("为什么悬停按钮没出来？", null, (s, e) => DiagnoseHover());
            menu.Items.Add(_miWhy);

            // 快捷键设置放在最显眼的位置 —— 之前埋在菜单底部，用户根本没找到
            _miTriggers = new ToolStripMenuItem("快捷键设置…", null, (s, e) => OpenTriggerDialog(FindDialogOwner()));
            menu.Items.Add(_miTriggers);
            menu.Items.Add(new ToolStripSeparator());

            var zone = new ToolStripMenuItem("触发区域");
            _miTitleZone = new ToolStripMenuItem("只在标题栏（推荐）", null, (s, e) =>
            {
                _cfg.TriggerWholeWindow = false; SyncMenu(); ScheduleSave();
            });
            _miWholeZone = new ToolStripMenuItem("整个窗口", null, (s, e) =>
            {
                _cfg.TriggerWholeWindow = true; SyncMenu(); ScheduleSave();
            });
            zone.DropDownItems.Add(_miTitleZone);
            zone.DropDownItems.Add(_miWholeZone);
            menu.Items.Add(zone);

            _miRemember = new ToolStripMenuItem("记住每个程序的透明度", null, (s, e) =>
            {
                _cfg.RememberPerApp = !_cfg.RememberPerApp; SyncMenu(); ScheduleSave();
            });
            menu.Items.Add(_miRemember);

            _miAutoApply = new ToolStripMenuItem("新窗口自动套用记住的透明度", null, (s, e) =>
            {
                _cfg.AutoApplyRemembered = !_cfg.AutoApplyRemembered; SyncMenu(); ScheduleSave();
            });
            menu.Items.Add(_miAutoApply);

            _miHotkeys = new ToolStripMenuItem("启用全局热键", null, (s, e) =>
            {
                _cfg.HotkeysEnabled = !_cfg.HotkeysEnabled;
                ApplyHotkeySetting();
                SyncMenu();
                ScheduleSave();
            });
            menu.Items.Add(_miHotkeys);

            var method = new ToolStripMenuItem("渲染方式");
            _miMethodLayered = new ToolStripMenuItem("分层窗口（推荐，通用）", null, (s, e) =>
            {
                SwitchMethod(FxMethod.Layered);
            });
            _miMethodAcrylic = new ToolStripMenuItem("亚克力混合（少数窗口可见）", null, (s, e) =>
            {
                SwitchMethod(FxMethod.Acrylic);
            });
            method.DropDownItems.Add(_miMethodLayered);
            method.DropDownItems.Add(_miMethodAcrylic);
            menu.Items.Add(method);

            menu.Items.Add(new ToolStripSeparator());

            var quick = new ToolStripMenuItem("调整当前窗口");
            foreach (int p in new[] { 100, 90, 80, 70, 60, 50, 40, 30, 20 })
            {
                int captured = p;
                quick.DropDownItems.Add(new ToolStripMenuItem(p + "%", null, (s, e) => ApplyToForeground(captured)));
            }
            menu.Items.Add(quick);

            menu.Items.Add(new ToolStripMenuItem("恢复所有窗口", null, (s, e) =>
            {
                RestoreAll();
                _states.Clear();
            }));

            // 鼠标穿透是个"自锁"功能：开了之后窗口就点不到了。
            // 万一用户忘了是哪个窗口（或那个窗口被别的窗口盖住），得有个一键脱困的口子。
            menu.Items.Add(new ToolStripMenuItem("取消所有鼠标穿透", null, (s, e) =>
            {
                int n = ClearAllClickThrough();
                ShowBalloon(n > 0 ? ("已取消 " + n + " 个窗口的鼠标穿透") : "当前没有处于鼠标穿透状态的窗口");
            }));

            menu.Items.Add(new ToolStripMenuItem("取消所有自动隐藏", null, (s, e) =>
            {
                int n = ClearAllAutoHide();
                ShowBalloon(n > 0 ? ("已关闭 " + n + " 个窗口的自动隐藏") : "当前没有开着自动隐藏的窗口");
            }));

            menu.Items.Add(new ToolStripMenuItem("取消所有保活", null, (s, e) =>
            {
                int n = ClearAllKeepAlive();
                int m = MoveAllBack();
                ShowBalloon(n > 0
                    ? ("已关闭 " + n + " 个窗口的保活" + (m > 0 ? "，并挪回了 " + m + " 个窗口" : ""))
                    : "当前没有开着保活的窗口");
            }));

            // ---- 保活发什么 ----
            var miKaInput = new ToolStripMenuItem("保活发送什么");
            foreach (KeepAliveInput opt in new[]
                     { KeepAliveInput.KeyWS, KeepAliveInput.KeySpace, KeepAliveInput.KeyCtrl, KeepAliveInput.Mouse })
            {
                KeepAliveInput captured = opt;
                var item = new ToolStripMenuItem(KeepAlive.Describe(captured), null, (s, e) =>
                {
                    _cfg.KeepAliveInput = captured;
                    ScheduleSave();
                    SyncKeepAliveInputMenu();
                    ShowBalloon("保活改成：" + KeepAlive.Describe(captured));
                });
                item.Checked = _cfg.KeepAliveInput == captured;
                _miKeepAliveInputs.Add(new KeyValuePair<KeepAliveInput, ToolStripMenuItem>(captured, item));
                miKaInput.DropDownItems.Add(item);
            }
            menu.Items.Add(miKaInput);

            menu.Items.Add(new ToolStripMenuItem("把所有挪走的窗口挪回来（Ctrl+Alt+B）", null, (s, e) =>
            {
                int n = MoveAllBack();
                ShowBalloon(n > 0 ? ("已挪回 " + n + " 个窗口") : "当前没有被挪走的窗口");
            }));

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem("连按按键设置（当前窗口）…", null, (s, e) =>
            {
                IntPtr h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero || !Native.IsWindow(h)) return;
                if (h == _widgetHwnd || h == _msgHwnd) return;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == (uint)Environment.ProcessId) return;

                OpenAutoDialog(h, EnsureState(h));
            }));

            menu.Items.Add(new ToolStripMenuItem("停止所有连按", null, (s, e) =>
            {
                int n = StopAllAuto();
                ShowBalloon(n > 0 ? ("已停止 " + n + " 个窗口的连按") : "当前没有在连按的窗口");
            }));

            menu.Items.Add(new ToolStripSeparator());

            _miAutoStart = new ToolStripMenuItem("开机自动启动", null, (s, e) =>
            {
                SetAutoStart(!IsAutoStartOn());
                SyncMenu();
            });
            menu.Items.Add(_miAutoStart);

            menu.Items.Add(new ToolStripMenuItem("使用说明", null, (s, e) => ShowHelp()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => Quit()));

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ToggleEnabledFromTray();

            SyncMenu();
        }

        private void SyncMenu()
        {
            // 状态直接写在菜单项文字上，省得用户点进去猜
            _miEnabled.Text = _cfg.Enabled
                ? "悬停按钮：已启用（点一下停用）"
                : "悬停按钮：已停用（点一下启用）";
            _miEnabled.Checked = _cfg.Enabled;

            UpdateTrayText();

            _miTitleZone.Checked = !_cfg.TriggerWholeWindow;
            _miWholeZone.Checked = _cfg.TriggerWholeWindow;
            _miRemember.Checked = _cfg.RememberPerApp;
            _miAutoApply.Checked = _cfg.AutoApplyRemembered;
            _miHotkeys.Checked = _cfg.HotkeysEnabled;
            _miMethodLayered.Checked = _cfg.Method == FxMethod.Layered;
            _miMethodAcrylic.Checked = _cfg.Method == FxMethod.Acrylic;
            _miAutoStart.Checked = IsAutoStartOn();

            // 把已启用的快捷键直接写在菜单项上，省得用户还要点进去看
            string summary = _trigger != null ? _trigger.DescribeAll() : "";
            _miTriggers.Text = string.IsNullOrEmpty(summary) || summary == "（无）"
                ? "快捷键设置…"
                : "快捷键设置…（" + summary + "）";
        }

        /// <summary>托盘图标的悬停提示，带上当前状态 —— 鼠标停在图标上就能看到。</summary>
        private void UpdateTrayText()
        {
            _tray.Text = _cfg.Enabled
                ? "阿墨 · 窗口透明度\n悬停按钮：已启用\n把鼠标移到任意窗口的标题栏右上角试试"
                : "阿墨 · 窗口透明度\n悬停按钮：已停用\n右键托盘图标 →「悬停按钮：已停用」可以打开";

            // 图标也跟着变灰，扫一眼就知道状态
            Icon old = _trayIcon;
            _trayIcon = MakeTrayIcon(_cfg.Enabled);
            _tray.Icon = _trayIcon;
            if (old != null) old.Dispose();

            Log.Info("悬停按钮状态 = " + (_cfg.Enabled ? "已启用" : "已停用"));
        }

        /// <summary>切换悬停按钮的总开关。托盘双击 / 菜单项 / Ctrl+Alt+H 都走这里。</summary>
        private void ToggleEnabledFromTray()
        {
            _cfg.Enabled = !_cfg.Enabled;
            if (!_cfg.Enabled) _widget.HideWidget();
            SyncMenu();
            ScheduleSave();
            ShowBalloon(_cfg.Enabled
                ? "悬停按钮：已启用"
                : "悬停按钮：已停用 —— 想恢复就再双击一次托盘图标，或右键托盘图标点「悬停按钮：已停用」");
        }

        /// <summary>
        /// 「为什么悬停按钮没出来？」—— 把当前状态和最近一次判定结果摊开给用户看。
        /// 用户反馈"不知道悬停窗到底有没有启用"，光靠气泡提示不够，得能自己查。
        /// </summary>
        private void DiagnoseHover()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("悬停按钮：" + (_cfg.Enabled ? "已启用" : "已停用  ← 这就是没显示的原因"));
            sb.AppendLine("触发区域：" + (_cfg.TriggerWholeWindow ? "整个窗口" : "只在标题栏"));
            sb.AppendLine();

            if (!_cfg.Enabled)
            {
                sb.AppendLine("右键托盘图标，点「悬停按钮：已停用」就能打开。");
            }
            else
            {
                Point cur = Cursor.Position;
                sb.AppendLine("鼠标位置：" + cur.X + ", " + cur.Y);

                IntPtr under = Native.WindowFromPoint(new Native.POINT { X = cur.X, Y = cur.Y });
                IntPtr root = under == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(under, Native.GA_ROOT);
                sb.AppendLine("鼠标下面的窗口：" + Native.GetClassName(root));
                sb.AppendLine("  hwnd=" + root + "  标题=" + Native.GetWindowText(root));
                sb.AppendLine();
                sb.AppendLine("最近一次判定：" + _lastReject);
                sb.AppendLine();
                sb.AppendLine("· 写着「不在触发区域」→ 把鼠标移到窗口标题栏上再看");
                sb.AppendLine("· 写着「窗口太小 / 铺满整屏 / 没有标题栏」→ 是这个窗口本身不支持，换个窗口试");
            }

            Log.Info("悬停按钮诊断：\n" + sb);
            // 托盘程序没有前台权限，带上 owner 免得被别的窗口盖住
            Form owner = FindDialogOwner();
            if (owner != null)
                MessageBox.Show(owner, sb.ToString(), "悬停按钮诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(sb.ToString(), "悬停按钮诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SwitchMethod(FxMethod m)
        {
            if (_cfg.Method == m) return;
            // 先把现有窗口还原，避免两套样式叠在一起
            RestoreAll();
            _cfg.Method = m;
            foreach (var kv in _states) kv.Value.Percent = 100;
            SyncMenu();
            ScheduleSave();
            _widget.Invalidate();
        }

        private void ShowHintOnce()
        {
            if (_firstHintShown) return;
            _firstHintShown = true;
            ShowBalloon("把鼠标移到任意窗口标题栏的右上角，会出现一个小按钮。");
        }

        private void ShowBalloon(string text)
        {
            try { _tray.ShowBalloonTip(2000, "阿墨 · 窗口透明度", text, ToolTipIcon.Info); }
            catch { }
        }

        private static Icon MakeTrayIcon(bool enabled)
        {
            // 停用时整体变灰 —— 扫一眼托盘就知道当前是什么状态
            Color accent = enabled
                ? Color.FromArgb(255, 76, 194, 255)    // 蓝：已启用
                : Color.FromArgb(255, 120, 120, 128);  // 灰：已停用

            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    using (var b = new SolidBrush(Color.FromArgb(255, 34, 34, 40)))
                        g.FillEllipse(b, 1f, 1f, 30f, 30f);

                    using (var b = new SolidBrush(accent))
                    {
                        Region old = g.Clip;
                        g.SetClip(new RectangleF(7f, 15f, 18f, 11f), CombineMode.Intersect);
                        g.FillEllipse(b, 7f, 7f, 18f, 18f);
                        g.Clip = old;
                    }

                    using (var p = new Pen(accent, 2.6f))
                        g.DrawEllipse(p, 7f, 7f, 18f, 18f);
                }

                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally
                {
                    Native.DestroyIcon(h);
                }
            }
        }

        // ==================================================================
        //  主循环：找出鼠标下的窗口，决定小按钮显示在哪里
        // ==================================================================

        private void Tick()
        {
            Point cur = Cursor.Position;

            // 「鼠标移出就隐藏」要最先处理 —— 窗口被压成全透明之后，
            // 鼠标移回去还得能把它认出来并恢复
            TickAutoHide(cur);
            TickKeepAlive();

            _dbg++;
            bool dbgNow = Log.Verbose && (_dbg % 25) == 0;

            if (_widget.Visible && _widget.ContainsScreenPoint(cur))
            {
                _lastAlive = DateTime.UtcNow;
                return;
            }

            if (!_cfg.Enabled)
            {
                _lastReject = "悬停按钮已停用（托盘菜单 →「悬停按钮：已停用」点一下就能开）";
                if (dbgNow) Log.Info("Tick: 已停用");
                HideWidget();
                return;
            }

            IntPtr under = Native.WindowFromPoint(new Native.POINT { X = cur.X, Y = cur.Y });
            IntPtr root = under == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(under, Native.GA_ROOT);

            Rectangle wr;
            int titleH;

            // 被设成「鼠标穿透」的窗口 WindowFromPoint 是找不到的，先单独认一遍
            IntPtr through = FindClickThroughUnderCursor(root, cur, out wr, out titleH);
            if (through != IntPtr.Zero)
            {
                root = through;
            }
            else if (!TryResolve(root, cur, out wr, out titleH))
            {
                if (dbgNow) Log.Info("Tick: 没找到目标 cur=" + cur + " root=" + root
                    + " cls=" + Native.GetClassName(root) + " | 原因：" + _lastReject);
                if ((DateTime.UtcNow - _lastAlive).TotalMilliseconds > 260) HideWidget();
                return;
            }

            _lastAlive = DateTime.UtcNow;

            if (dbgNow)
                Log.Info("Tick: cur=" + cur + " root=" + root + " through=" + through
                         + " wr=" + wr + " titleH=" + titleH + " target=" + _target);

            double scale = Native.GetDpiScale(root);
            WinState st = EnsureState(root);

            if (root != _target)
            {
                _target = root;
                _widget.HideWidget();
                ApplyRemembered(root, st);
            }

            SyncWidget(st);

            int pw = _widget.PillWidth(scale);
            int ph = _widget.PillHeight(scale);
            int margin = (int)Math.Round(10 * scale);

            // 有标准标题栏的窗口，给右侧的最小化/最大化/关闭留出位置
            int reserve = 0;
            if ((Native.GetStyle(root) & Native.WS_CAPTION) == Native.WS_CAPTION)
                reserve = Native.GetCaptionButtonsWidth(root);

            int x = wr.Right - reserve - margin - pw;
            if (x < wr.Left + 6) x = wr.Left + (wr.Width - pw) / 2;   // 窗口太窄就居中放
            int y = wr.Top + Math.Max((int)Math.Round(2 * scale), (titleH - ph) / 2);

            if (!_widget.Visible || _widget.Target != root)
                _widget.AttachTo(root, x + pw, y, scale);
            else
                _widget.MoveAnchor(x + pw, y);
        }

        private void HideWidget()
        {
            if (_widget.Visible) _widget.HideWidget();
            _target = IntPtr.Zero;
        }

        /// <summary>
        /// 「鼠标移出就隐藏」：开了这个开关的窗口，鼠标离开它的范围就压到全透明，
        /// 移回来恢复成用户设定的透明度。
        ///
        /// 判定用窗口矩形而不是 WindowFromPoint —— 窗口被压成透明之后
        /// 用取点判断会不稳，而且「移出窗口」按字面就是看矩形范围。
        /// 窗口本身还在原位（只是看不见），鼠标移回去就能恢复。
        /// </summary>
        private void TickAutoHide(Point cur)
        {
            if (_states.Count == 0) return;

            foreach (var kv in _states)
            {
                WinState st = kv.Value;
                if (!st.AutoHide) continue;

                IntPtr h = kv.Key;
                if (!Native.IsWindow(h)) continue;

                // 工具暂停 / 停用，或者窗口最小化了 —— 别让它留在隐藏状态
                bool skip = !_cfg.Enabled || Native.IsIconic(h);

                bool wantHidden;
                Native.RECT r = default;
                if (skip) wantHidden = false;
                else if (!Native.GetWindowRect(h, out r)) continue;
                else wantHidden = !(cur.X >= r.Left && cur.X < r.Right && cur.Y >= r.Top && cur.Y < r.Bottom);

                if (wantHidden == st.Hidden) continue;

                st.Hidden = wantHidden;
                if (wantHidden)
                {
                    Fx.SetHiddenAlpha(h, st);
                    Log.Info("自动隐藏：压到全透明 hwnd=" + h);
                }
                else
                {
                    Fx.Apply(h, st.Percent, _cfg.Method, st);
                    Log.Info("自动隐藏：恢复 hwnd=" + h + " 到 " + st.Percent + "%");
                }

                if (h == _widget.Target) SyncWidget(st);
            }
        }

        private int ComputeTitleHeight(IntPtr hwnd, Rectangle wr)
        {
            double s = Native.GetDpiScale(hwnd);
            var c0 = new Native.POINT { X = 0, Y = 0 };
            int titleH = 0;
            if (Native.ClientToScreen(hwnd, ref c0)) titleH = c0.Y - wr.Top;
            if (titleH < 6 || titleH > wr.Height / 3) titleH = (int)Math.Round(36 * s);
            if (titleH > wr.Height / 3) titleH = wr.Height / 3;
            return titleH;
        }

        private bool TryResolve(IntPtr root, Point cursor, out Rectangle wr, out int titleH)
        {
            wr = Rectangle.Empty;
            titleH = 0;

            if (root == IntPtr.Zero) { _lastReject = "鼠标下面没有窗口（在桌面或任务栏上）"; return false; }
            if (root == _widgetHwnd || root == _msgHwnd) { _lastReject = "鼠标在本工具自己的窗口上"; return false; }
            if (!Native.IsWindow(root)) { _lastReject = "鼠标下面的不是有效窗口"; return false; }
            if (!Native.IsWindowVisible(root)) { _lastReject = "窗口不可见"; return false; }
            if (Native.IsIconic(root)) { _lastReject = "窗口已最小化"; return false; }
            if (root == Native.GetShellWindow()) { _lastReject = "这是桌面本身"; return false; }

            Native.GetWindowThreadProcessId(root, out uint pid);
            if (pid == (uint)Environment.ProcessId) { _lastReject = "这是本工具自己的窗口"; return false; }

            if (!Native.GetWindowRect(root, out Native.RECT rc)) { _lastReject = "取窗口位置失败"; return false; }
            wr = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
            if (wr.Width < 180 || wr.Height < 90)
            {
                _lastReject = "窗口太小（" + wr.Width + "x" + wr.Height + "，需要至少 180x90）";
                return false;
            }

            int style = Native.GetStyle(root);
            if ((style & Native.WS_VISIBLE) == 0) { _lastReject = "窗口没有 WS_VISIBLE 样式"; return false; }

            if (BlackClasses.Contains(Native.GetClassName(root)))
            {
                _lastReject = "这类窗口在黑名单里（" + Native.GetClassName(root) + "），不接管";
                return false;
            }

            foreach (Screen scr in Screen.AllScreens)
                if (wr == scr.Bounds)
                {
                    _lastReject = "窗口正好铺满整块屏幕（全屏游戏/桌面），不接管";
                    return false;
                }

            bool hasCaption = (style & Native.WS_CAPTION) == Native.WS_CAPTION;
            if (!hasCaption && Native.GetWindowText(root).Length == 0)
            {
                _lastReject = "这个窗口没有标题栏也没有标题，认不出该把按钮放哪";
                return false;
            }

            titleH = ComputeTitleHeight(root, wr);
            if (!InTriggerZone(root, wr, titleH, cursor))
            {
                _lastReject = "鼠标不在触发区域（当前是「" + (_cfg.TriggerWholeWindow ? "整个窗口" : "只在标题栏")
                    + "」，鼠标在 " + cursor.X + "," + cursor.Y + "，窗口 " + wr.Left + "," + wr.Top + " 起 "
                    + wr.Width + "x" + wr.Height + "，标题栏高 " + titleH + "）";
                return false;
            }

            _lastReject = "应该已经显示出来了";
            return true;
        }

        /// <summary>鼠标是否落在窗口的「触发区域」里（标题栏，或整个窗口）。</summary>
        private bool InTriggerZone(IntPtr hwnd, Rectangle wr, int titleH, Point cursor)
        {
            if (_cfg.TriggerWholeWindow)
                return wr.Contains(cursor);

            int slack = (int)Math.Round(4 * Native.GetDpiScale(hwnd));
            if (cursor.Y < wr.Top) return false;
            if (cursor.Y > wr.Top + titleH + slack) return false;
            if (cursor.X < wr.Left + slack) return false;
            if (cursor.X > wr.Right - slack) return false;
            return true;
        }

        /// <summary>
        /// 鼠标穿透的窗口在系统眼里是"透明"的，鼠标事件会直接穿到它背后的窗口，
        /// WindowFromPoint 因此永远找不到它 —— 用户一旦开了穿透就再也悬停不到、
        /// 也就没法把穿透关掉（只能退出软件，体验极差）。
        ///
        /// 这里从鼠标下的窗口沿 z 序往上走，找出我们自己设过穿透、
        /// 且鼠标正落在它触发区域内的窗口，把它重新认出来。
        /// </summary>
        private IntPtr FindClickThroughUnderCursor(IntPtr root, Point cursor, out Rectangle wr, out int titleH)
        {
            wr = Rectangle.Empty;
            titleH = 0;
            if (_clickThrough.Count == 0) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            Rectangle bestRect = Rectangle.Empty;
            IntPtr h = root != IntPtr.Zero ? root : Native.GetDesktopWindow();

            // GW_HWNDPREV 一路往上，越往后越靠前；取最后一个命中的就是最上层那个
            for (int guard = 0; guard < 400; guard++)
            {
                h = Native.GetWindow(h, Native.GW_HWNDPREV);
                if (h == IntPtr.Zero) break;

                if (!_clickThrough.Contains(h)) continue;

                if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h))
                {
                    _clickThrough.Remove(h);
                    continue;
                }

                if (!Native.GetWindowRect(h, out Native.RECT rc)) continue;
                Rectangle r = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
                if (r.Width < 60 || r.Height < 40) continue;
                if (!r.Contains(cursor)) continue;

                int th = ComputeTitleHeight(h, r);
                if (!InTriggerZone(h, r, th, cursor)) continue;

                best = h;
                bestRect = r;
                titleH = th;
            }

            wr = bestRect;
            return best;
        }

        // ==================================================================
        //  状态管理
        // ==================================================================

        private WinState EnsureState(IntPtr h)
        {
            if (_states.TryGetValue(h, out WinState st) && Native.IsWindow(h)) return st;

            st = new WinState { Hwnd = h, Percent = 100 };
            try
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                st.Process = Process.GetProcessById((int)pid).ProcessName;
            }
            catch { st.Process = string.Empty; }

            st.Title = Native.GetWindowText(h);
            st.OriginalExStyle = Native.GetExStyle(h);
            st.OriginalTopMost = (st.OriginalExStyle & Native.WS_EX_TOPMOST) != 0;
            _states[h] = st;
            return st;
        }

        private void PruneStates()
        {
            var dead = new List<IntPtr>();
            foreach (var kv in _states)
                if (!Native.IsWindow(kv.Key)) dead.Add(kv.Key);
            foreach (IntPtr h in dead)
            {
                StopAuto(h, false);
                _states.Remove(h);
            }

            var deadThrough = new List<IntPtr>();
            foreach (IntPtr h in _clickThrough)
                if (!Native.IsWindow(h)) deadThrough.Add(h);
            foreach (IntPtr h in deadThrough) _clickThrough.Remove(h);
        }

        private void ApplyRemembered(IntPtr h, WinState st)
        {
            if (!_cfg.AutoApplyRemembered || !_cfg.RememberPerApp) return;

            // 「鼠标移出就隐藏」是按程序记的，跟透明度是不是 100 无关。
            // 注意别动 st.Hidden —— 那是「当前实际压没压成透明」，
            // 在这里改成 false 而不真去恢复窗口，状态就跟实际对不上了。
            if (_cfg.AppAutoHide.TryGetValue(st.Process, out bool ah) && ah)
                st.AutoHide = true;

            // 「保活」同理，也是按程序记的
            if (_cfg.AppKeepAlive.TryGetValue(st.Process, out bool ka) && ka)
            {
                // 只有"刚从关变开"才立刻补一次。否则鼠标在窗口上扫来扫去、
                // 目标反复切换时，每次都会重置计时器 → 一秒补好几次。
                if (!st.KeepAlive) st.KeepAliveNextAt = DateTime.MinValue;
                st.KeepAlive = true;
            }

            if (st.Percent != 100) return;
            if (!_cfg.AppOpacity.TryGetValue(st.Process, out int remembered) || remembered >= 100) return;

            Fx.Apply(h, remembered, _cfg.Method, st);
            if (_cfg.AppTopMost.TryGetValue(st.Process, out bool tm) && tm)
                Fx.SetTopMost(h, true, st);
        }

        private void SetOpacity(IntPtr h, WinState st, int percent)
        {
            percent = Fx.Clamp(percent);
            Fx.Apply(h, percent, _cfg.Method, st);
            PersistApp(st);
        }

        private void PersistApp(WinState st)
        {
            if (!_cfg.RememberPerApp || string.IsNullOrEmpty(st.Process)) return;
            _cfg.AppOpacity[st.Process] = st.Percent;
            _cfg.AppTopMost[st.Process] = st.TopMost;
            _cfg.AppAutoHide[st.Process] = st.AutoHide;
            _cfg.AppKeepAlive[st.Process] = st.KeepAlive;
            ScheduleSave();
        }

        /// <summary>设置鼠标穿透，同时维护"穿透窗口清单"（悬停识别要用）。</summary>
        private void SetClickThrough(IntPtr h, WinState st, bool on)
        {
            Fx.SetClickThrough(h, on, st);
            if (on) _clickThrough.Add(h);
            else _clickThrough.Remove(h);
        }

        /// <summary>把所有处于鼠标穿透状态的窗口一次性取消掉，返回处理了几个。</summary>
        private int ClearAllClickThrough()
        {
            int n = 0;
            foreach (IntPtr h in new List<IntPtr>(_clickThrough))
            {
                if (!Native.IsWindow(h)) continue;
                if (!_states.TryGetValue(h, out WinState st)) continue;
                Fx.SetClickThrough(h, false, st);
                n++;
            }
            _clickThrough.Clear();
            return n;
        }

        /// <summary>
        /// 一次性关掉所有「自动隐藏」并立刻把窗口恢复出来。
        /// 窗口被压成全透明时用户可能一时找不到，这是逃生口。
        /// </summary>
        private int ClearAllAutoHide()
        {
            int n = 0;
            foreach (var kv in _states)
            {
                WinState st = kv.Value;
                if (!st.AutoHide) continue;

                IntPtr h = kv.Key;
                st.AutoHide = false;
                st.Hidden = false;
                if (Native.IsWindow(h)) Fx.Apply(h, st.Percent, _cfg.Method, st);
                if (!string.IsNullOrEmpty(st.Process)) _cfg.AppAutoHide[st.Process] = false;
                n++;
            }
            if (n > 0) ScheduleSave();
            return n;
        }

        /// <summary>
        /// 「保活」：给开了这个开关的窗口定时补一点输入，
        /// 让窗口背后的服务认为你还在操作（云原神那种挂机超时就靠它对付）。
        ///
        /// 发的是鼠标抖动而不是键盘 —— 抖动净位移为 0，游戏里视角不会真的转过去。
        /// </summary>
        private void TickKeepAlive()
        {
            if (_states.Count == 0) return;
            if (!_cfg.Enabled) return;

            DateTime now = DateTime.UtcNow;
            foreach (var kv in _states)
            {
                WinState st = kv.Value;
                if (!st.KeepAlive) continue;
                if (now < st.KeepAliveNextAt) continue;

                IntPtr h = kv.Key;
                if (!Native.IsWindow(h)) continue;

                // 最小化的窗口收不到注入的输入（浏览器直接丢弃），提醒一次
                if (Native.IsIconic(h))
                {
                    if (!st.KeepAliveWarned)
                    {
                        st.KeepAliveWarned = true;
                        ShowBalloon("这个窗口最小化了，保活会失效。"
                            + "想让它看不见又不断线：还原窗口后右键点面板上的「保活」，把它挪到屏幕外。");
                    }
                    continue;
                }

                // 先把时间推后，免得窗口发不出去时每个 Tick 都重试
                st.KeepAliveNextAt = now.AddMilliseconds(KeepAlive.IntervalMs);

                if (KeepAlive.Jiggle(h, _cfg.KeepAliveInput, ref st.KeepAliveFlip))
                    Log.Info("保活：补了一次输入 hwnd=" + h + " 方式=" + KeepAlive.Describe(_cfg.KeepAliveInput));
            }
        }

        /// <summary>
        /// 把所有被挪到屏幕外的窗口挪回来。返回挪了几个。
        /// 窗口挪走之后看不见、鼠标也够不到，这是唯一的找回入口。
        /// </summary>
        private int MoveAllBack()
        {
            int n = 0;
            foreach (var kv in _states)
            {
                WinState st = kv.Value;
                if (!st.MovedAway) continue;
                if (Fx.MoveBack(kv.Key, st)) n++;
            }
            return n;
        }

        /// <summary>
        /// 一次性关掉所有「保活」。返回关了几个。
        /// </summary>
        private int ClearAllKeepAlive()
        {
            int n = 0;
            foreach (var kv in _states)
            {
                WinState st = kv.Value;
                if (!st.KeepAlive) continue;

                st.KeepAlive = false;
                if (!string.IsNullOrEmpty(st.Process)) _cfg.AppKeepAlive[st.Process] = false;
                n++;
            }
            if (n > 0) ScheduleSave();
            return n;
        }

        private void SyncKeepAliveInputMenu()
        {
            foreach (var kv in _miKeepAliveInputs)
                kv.Value.Checked = _cfg.KeepAliveInput == kv.Key;
        }

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void RestoreAll()
        {
            StopAllAuto();
            foreach (var kv in _states)
            {
                IntPtr h = kv.Key;
                WinState st = kv.Value;
                if (!Native.IsWindow(h)) continue;
                if (st.ClickThrough) SetClickThrough(h, st, false);
                if (st.TopMost && !st.OriginalTopMost) Fx.SetTopMost(h, false, st);
                if (st.MovedAway) Fx.MoveBack(h, st);
                st.Hidden = false;
                Fx.Restore(h, st);
            }
            _clickThrough.Clear();
        }

        // ==================================================================
        //  连按按键
        // ==================================================================

        /// <summary>把窗口状态推给悬停面板。</summary>
        private void SyncWidget(WinState st)
        {
            if (st == null) return;
            bool running = st.Auto != null && st.Auto.Running;
            string keyName = st.Auto != null ? AutoPress.KeyName(st.Auto.Config) : "";
            _widget.SyncFrom(st.Percent, st.TopMost, st.ClickThrough, running, keyName,
                             st.AutoHide, st.KeepAlive, st.MovedAway);
        }

        /// <summary>取该窗口的连按配置，没有就从"按进程记住"的设置里恢复一份。</summary>
        private AutoKeyConfig EnsureAutoConfig(WinState st)
        {
            if (st.Auto == null) st.Auto = new AutoKeyJob { Hwnd = st.Hwnd };

            if (!st.Auto.Config.IsConfigured && !string.IsNullOrEmpty(st.Process)
                && _cfg.AutoKeys.TryGetValue(st.Process, out string saved))
            {
                AutoKeyConfig parsed = AutoKeyConfig.Parse(saved);
                if (parsed != null) st.Auto.Config = parsed;
            }
            return st.Auto.Config;
        }

        private void StartAuto(IntPtr h, WinState st)
        {
            if (st.Auto == null) st.Auto = new AutoKeyJob { Hwnd = h };
            st.Auto.Hwnd = h;
            st.Auto.Running = true;
            st.Auto.KeyIsDown = false;
            st.Auto.PressCount = 0;
            st.Auto.NextAt = DateTime.UtcNow.AddMilliseconds(400);   // 起手缓一下，别一开就猛按
            _lastAutoHwnd = h;   // 触发键以后就作用在它身上
            Log.Always("连按开始 hwnd=" + h + " key=" + st.Auto.KeyDisplay
                     + " " + st.Auto.Config.MinMs + "~" + st.Auto.Config.MaxMs + "ms"
                     + " mode=" + st.Auto.Config.Mode);
        }

        /// <summary>停止连按。release=true 时补一次抬键，免得游戏以为键一直按着。</summary>
        private void StopAuto(IntPtr h, bool release)
        {
            if (!_states.TryGetValue(h, out WinState st) || st.Auto == null) return;
            if (!st.Auto.Running) return;

            if (st.Auto.KeyIsDown && release) AutoPress.Send(h, st.Auto.Config, false, st.Auto.LastUsedInput);
            st.Auto.Running = false;
            st.Auto.KeyIsDown = false;
            Log.Always("连按停止 hwnd=" + h + " 共按了 " + st.Auto.PressCount + " 次");
        }

        private int StopAllAuto()
        {
            int n = 0;
            foreach (var kv in _states)
            {
                if (kv.Value.Auto != null && kv.Value.Auto.Running)
                {
                    StopAuto(kv.Key, true);
                    n++;
                }
            }
            return n;
        }

        private void PersistAuto(WinState st)
        {
            if (st.Auto == null || !st.Auto.Config.IsConfigured) return;
            if (string.IsNullOrEmpty(st.Process)) return;
            _cfg.AutoKeys[st.Process] = st.Auto.Config.Serialize();
            ScheduleSave();
        }

        private string AutoDesc(AutoKeyConfig c)
        {
            string mode;
            switch (c.Mode)
            {
                case AutoSendMode.ForegroundOnly: mode = "仅窗口在前台时发送"; break;
                case AutoSendMode.BackgroundOnly: mode = "始终后台发送"; break;
                default: mode = "前台发真实按键，后台投递给窗口"; break;
            }
            return "按键 " + c.Display
                   + "，每 " + (c.MinMs / 1000.0).ToString("0.0#") + " ~ " + (c.MaxMs / 1000.0).ToString("0.0#") + " 秒一次"
                   + "（" + mode + "）";
        }

        // ------------------------------------------------------------------
        //  触发键（全局开关）
        // ------------------------------------------------------------------

        /// <summary>「连按开关」这个快捷键的当前设置，用来在别处显示。</summary>
        private string AutoPressTriggerSummary()
        {
            if (_cfg.Triggers.TryGetValue(TriggerIds.AutoPress, out AutoTriggerConfig c) && c != null && c.Enabled)
                return c.Describe();
            return "";
        }

        private void ApplyTriggerSetting()
        {
            _triggerBindings.Clear();
            foreach (string id in new[] { TriggerIds.AutoPress, TriggerIds.Minimize, TriggerIds.TopMost,
                                          TriggerIds.ClickThrough, TriggerIds.MoveBack })
            {
                _cfg.Triggers.TryGetValue(id, out AutoTriggerConfig c);
                _triggerBindings.Add(new TriggerBinding(id, c ?? new AutoTriggerConfig()));
            }

            bool ok = _trigger.Apply(_triggerBindings);
            SyncMenu();
            if (ok) return;

            bool any = false;
            foreach (TriggerBinding b in _triggerBindings)
            {
                if (!b.Config.Enabled) continue;
                b.Config.Enabled = false;
                any = true;
            }
            if (any)
            {
                ScheduleSave();
                ShowBalloon("快捷键启用失败（可能被安全软件拦了），已自动关闭。");
            }
        }

        /// <summary>这个窗口能不能被我们操作。</summary>
        private bool IsControllable(IntPtr h)
        {
            if (h == IntPtr.Zero || !Native.IsWindow(h)) return false;
            if (h == _widgetHwnd || h == _msgHwnd) return false;
            if (h == Native.GetShellWindow()) return false;

            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;

            string cls = Native.GetClassName(h);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false;

            return true;
        }

        /// <summary>快捷键被按出来了。</summary>
        private void OnTriggerFired(string id)
        {
            Log.Always("快捷键触发：" + id);

            if (id == TriggerIds.Minimize) { ToggleMinimize(); return; }

            if (id == TriggerIds.AutoPress) { ToggleAutoPressFromShortcut(); return; }

            if (id == TriggerIds.MoveBack)
            {
                int n = MoveAllBack();
                ShowBalloon(n > 0 ? ("已挪回 " + n + " 个窗口") : "当前没有被挪走的窗口");
                return;
            }

            // 置顶 / 穿透：作用于当前前台窗口
            IntPtr fg = Native.GetForegroundWindow();
            if (!IsControllable(fg)) return;

            WinState st = EnsureState(fg);
            if (id == TriggerIds.TopMost)
            {
                Fx.SetTopMost(fg, !st.TopMost, st);
                PersistApp(st);
                ShowBalloon(st.TopMost ? "已置顶" : "已取消置顶");
            }
            else
            {
                SetClickThrough(fg, st, !st.ClickThrough);
                ShowBalloon(st.ClickThrough ? "鼠标穿透：开（再按一次关闭）" : "鼠标穿透：关");
            }
            if (_widget.Target == fg) SyncWidget(st);
        }

        /// <summary>
        /// 快捷键切换连按。
        ///
        /// 停止这一侧必须"永远能停"：只要还有窗口在连按，就直接停掉它，
        /// 不去管"最近一次连按的窗口"是不是还对得上 —— 否则一旦对不上，
        /// 用户按了停止却什么也没发生，看起来就像快捷键坏了。
        /// </summary>
        private void ToggleAutoPressFromShortcut()
        {
            IntPtr running = FindRunningAuto();
            if (running != IntPtr.Zero)
            {
                StopAuto(running, true);
                if (_states.TryGetValue(running, out WinState rs)) SyncWidget(rs);
                ShowBalloon("已停止连按");
                return;
            }

            IntPtr h = _lastAutoHwnd;
            if (h == IntPtr.Zero || !Native.IsWindow(h)) h = Native.GetForegroundWindow();
            if (!IsControllable(h)) return;
            ToggleAutoPress(h);
        }

        /// <summary>找一个正在连按的窗口，优先"最近一次开过连按的那个"。</summary>
        private IntPtr FindRunningAuto()
        {
            if (_lastAutoHwnd != IntPtr.Zero && Native.IsWindow(_lastAutoHwnd)
                && _states.TryGetValue(_lastAutoHwnd, out WinState last)
                && last.Auto != null && last.Auto.Running)
                return _lastAutoHwnd;

            foreach (var kv in _states)
            {
                if (kv.Value.Auto == null || !kv.Value.Auto.Running) continue;
                if (!Native.IsWindow(kv.Key)) continue;
                return kv.Key;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 最小化 / 还原当前窗口。按一次最小化，再按一次把刚才那个还原回来，
        /// 所以是「切换」而不是单向最小化。
        /// </summary>
        private void ToggleMinimize()
        {
            if (_lastMinimized != IntPtr.Zero)
            {
                if (Native.IsWindow(_lastMinimized) && Native.IsIconic(_lastMinimized))
                {
                    Native.ShowWindow(_lastMinimized, Native.SW_RESTORE);
                    Native.SetForegroundWindow(_lastMinimized);
                    Log.Info("还原窗口 hwnd=" + _lastMinimized);
                    _lastMinimized = IntPtr.Zero;
                    return;
                }
                _lastMinimized = IntPtr.Zero;
            }

            IntPtr h = Native.GetForegroundWindow();
            if (!IsControllable(h)) return;

            if (Native.IsIconic(h))
            {
                Native.ShowWindow(h, Native.SW_RESTORE);
                Native.SetForegroundWindow(h);
                Log.Info("还原窗口 hwnd=" + h);
                return;
            }

            Native.ShowWindow(h, Native.SW_MINIMIZE);
            _lastMinimized = h;
            Log.Info("最小化窗口 hwnd=" + h);
        }

        /// <summary>
        /// 找一个合适的对话框当 owner。
        /// 不设 owner 的话，从一个模态对话框里再弹一个对话框时，
        /// 新窗口不保证在上面，会被前一个盖住。
        /// </summary>
        private Form FindDialogOwner()
        {
            Form active = Form.ActiveForm;
            if (active != null && active.Visible && IsOurDialog(active)) return active;

            foreach (Form f in Application.OpenForms)
                if (IsOurDialog(f) && f.Visible) return f;

            return null;
        }

        private bool IsOurDialog(Form f)
        {
            return f != null && f != _widget && f != _msg;
        }

        private void OpenTriggerDialog(Form owner = null)
        {
            // 改配置时先卸钩子，免得改的过程中又把旧键按出来
            _trigger.Uninstall();

            using (var dlg = new AutoTriggerDialog(_cfg.Triggers))
            {
                DialogResult r = owner != null && owner.Visible ? dlg.ShowDialog(owner) : dlg.ShowDialog();
                if (r != DialogResult.OK)
                {
                    ApplyTriggerSetting();
                    return;
                }

                _cfg.Triggers.Clear();
                foreach (var kv in dlg.Result) _cfg.Triggers[kv.Key] = kv.Value;
            }

            ScheduleSave();
            ApplyTriggerSetting();
            ShowBalloon("快捷键：" + _trigger.DescribeAll());
        }

        /// <summary>点面板上的「连按」：开 / 关；还没配过就先弹设置。</summary>
        private void ToggleAutoPress(IntPtr h)
        {
            if (h == IntPtr.Zero || !Native.IsWindow(h)) return;

            // 触发键 / 热键进来的窗口可能还没被鼠标悬停过，这里补一次登记
            WinState st = EnsureState(h);

            AutoKeyConfig cfg = EnsureAutoConfig(st);

            if (st.Auto.Running)
            {
                StopAuto(h, true);
                ShowBalloon("已停止连按");
            }
            else
            {
                if (!cfg.IsConfigured)
                {
                    OpenAutoDialog(h, st);
                    return;
                }
                StartAuto(h, st);
                ShowBalloon("连按已开始 —— " + AutoDesc(cfg));
            }
            SyncWidget(st);
        }

        /// <summary>打开连按设置对话框。</summary>
        private void OpenAutoDialog(IntPtr h, WinState st)
        {
            if (h == IntPtr.Zero || st == null) return;

            bool wasRunning = st.Auto != null && st.Auto.Running;
            // 开对话框前先停：不然按键会打进对话框里
            if (wasRunning) StopAuto(h, true);

            AutoKeyConfig cur = EnsureAutoConfig(st);
            bool enable;
            AutoKeyConfig result;

            using (var dlg = new AutoKeyDialog(st.Title, cur, wasRunning, AutoPressTriggerSummary, OpenTriggerDialog))
            {
                Form owner = FindDialogOwner();
                DialogResult r = owner != null && owner.Visible ? dlg.ShowDialog(owner) : dlg.ShowDialog();
                if (r != DialogResult.OK)
                {
                    if (wasRunning) StartAuto(h, st);
                    SyncWidget(st);
                    return;
                }
                result = dlg.Result;
                enable = dlg.EnableNow;
            }

            if (st.Auto == null) st.Auto = new AutoKeyJob { Hwnd = h };
            st.Auto.Config = result;
            st.Auto.Hwnd = h;
            PersistAuto(st);

            if (enable)
            {
                StartAuto(h, st);
                ShowBalloon("连按已开始 —— " + AutoDesc(result));
            }
            else
            {
                StopAuto(h, true);
                ShowBalloon("连按已关闭（配置已保存）");
            }

            SyncWidget(st);
        }

        /// <summary>目标窗口是不是当前前台窗口（它自己弹出的子窗口也算）。</summary>
        private static bool IsForegroundTarget(IntPtr h)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (fg == h) return true;
            return Native.GetAncestor(fg, Native.GA_ROOTOWNER) == h;
        }

        /// <summary>连按主循环：25ms 一跳，负责"按下 → 停 40ms → 抬起 → 等随机间隔"。</summary>
        private void TickAuto()
        {
            if (_states.Count == 0) return;

            DateTime now = DateTime.UtcNow;
            List<IntPtr> dead = null;

            foreach (var kv in _states)
            {
                WinState st = kv.Value;
                AutoKeyJob job = st.Auto;
                if (job == null || !job.Running) continue;

                IntPtr h = kv.Key;

                if (!Native.IsWindow(h))
                {
                    (dead ?? (dead = new List<IntPtr>())).Add(h);
                    continue;
                }
                if (Native.IsIconic(h)) continue;

                bool isForeground = IsForegroundTarget(h);

                if (job.Config.Mode == AutoSendMode.ForegroundOnly && !isForeground)
                {
                    // 最保守的模式：窗口切到后台就暂停，绝不往别处发
                    if (Log.Verbose && (++_autoSkip % 200) == 0)
                        Log.Info("连按跳过（不在前台，模式=仅前台）hwnd=" + h);
                    continue;
                }

                // 前台优先用 SendInput（真实按键，游戏基本都吃这套）；
                // 后台、或者选了"始终后台"，就改用 PostMessage 直接投给窗口
                bool useInput = job.Config.Mode != AutoSendMode.BackgroundOnly && isForeground;

                if (!job.KeyIsDown)
                {
                    if (now < job.NextAt) continue;
                    AutoPress.Send(h, job.Config, true, useInput);
                    job.KeyIsDown = true;
                    job.LastUsedInput = useInput;
                    job.ReleaseAt = now.AddMilliseconds(40);
                }
                else
                {
                    if (now < job.ReleaseAt) continue;
                    AutoPress.Send(h, job.Config, false, job.LastUsedInput);
                    job.KeyIsDown = false;
                    job.PressCount++;
                    job.NextAt = now.AddMilliseconds(AutoPress.NextInterval(job.Config));
                }
            }

            if (dead != null)
            {
                foreach (IntPtr h in dead)
                {
                    StopAuto(h, false);
                    if (_states.TryGetValue(h, out WinState st)) st.Auto = null;
                    _states.Remove(h);
                }
                ShowBalloon("连按的窗口已关闭，已自动停止");
            }
        }

        // ==================================================================
        //  控件回调
        // ==================================================================

        private void OnWidgetOpacityChanged(int percent)
        {
            IntPtr h = _widget.Target;
            if (h == IntPtr.Zero || !_states.TryGetValue(h, out WinState st)) return;
            SetOpacity(h, st, percent);
            SyncWidget(st);
        }

        private void OnWidgetTopMostToggled(bool on)
        {
            IntPtr h = _widget.Target;
            if (h == IntPtr.Zero || !_states.TryGetValue(h, out WinState st)) return;
            Fx.SetTopMost(h, on, st);
            PersistApp(st);
            SyncWidget(st);
        }

        private void OnWidgetKeepAliveToggled(bool on)
        {
            IntPtr h = _widget.Target;
            if (h == IntPtr.Zero || !_states.TryGetValue(h, out WinState st)) return;

            st.KeepAlive = on;
            st.KeepAliveNextAt = DateTime.MinValue;   // 立刻补一次，让你马上能看到效果
            st.KeepAliveWarned = false;
            PersistApp(st);
            SyncWidget(st);

            ShowBalloon(on
                ? ("保活已开启：每分钟给这个窗口补一次「" + KeepAlive.Describe(_cfg.KeepAliveInput) + "」。"
                   + "想让它看不见又不断线，右键点「保活」把窗口挪到屏幕外。")
                : "保活已关闭。");
        }

        /// <summary>
        /// 右键「保活」：把窗口挪到屏幕外 / 挪回来。
        ///
        /// 为什么不直接最小化：窗口最小化之后浏览器会**直接丢弃**注入的输入
        /// （实测 PostMessage / SendMessage / 发子窗口三种方式全部无效），
        /// 而挪到屏幕外（不最小化）注入照常生效。所以想"看不见还活着"得用挪走。
        /// </summary>
        private void OnWidgetKeepAliveMove()
        {
            IntPtr h = _widget.Target;
            if (h == IntPtr.Zero || !_states.TryGetValue(h, out WinState st)) return;

            if (st.MovedAway)
            {
                if (Fx.MoveBack(h, st)) ShowBalloon("已挪回原位。");
            }
            else
            {
                if (Fx.MoveAway(h, st))
                    ShowBalloon("已挪到屏幕外：看不见了，但保活照常。"
                        + "按 Ctrl+Alt+B 可以挪回来（也可以再右键点「保活」，或托盘菜单）。");
                else
                    ShowBalloon("挪走失败。");
            }

            SyncWidget(st);
        }

        private void OnWidgetAutoHideToggled(bool on)
        {
            IntPtr h = _widget.Target;
            if (h == IntPtr.Zero || !_states.TryGetValue(h, out WinState st)) return;

            st.AutoHide = on;
            st.Hidden = false;
            if (!on) Fx.Apply(h, st.Percent, _cfg.Method, st);   // 关掉时立刻恢复，别留着透明
            PersistApp(st);
            SyncWidget(st);

            ShowBalloon(on
                ? "已开启：鼠标离开这个窗口就会隐藏，移回来恢复。"
                : "已关闭自动隐藏。");
        }

        private void OnWidgetClickThroughToggled(bool on)
        {
            IntPtr h = _widget.Target;
            if (h == IntPtr.Zero || !_states.TryGetValue(h, out WinState st)) return;
            SetClickThrough(h, st, on);
            SyncWidget(st);
            if (on) ShowBalloon("已开启鼠标穿透。再按 Ctrl+Alt+Z，或在托盘菜单点「取消所有鼠标穿透」可以关掉。");
        }

        private void OnWidgetAutoPressToggled()
        {
            ToggleAutoPress(_widget.Target);
        }

        private void OnWidgetAutoPressConfigure()
        {
            IntPtr h = _widget.Target;
            if (h == IntPtr.Zero || !_states.TryGetValue(h, out WinState st)) return;
            OpenAutoDialog(h, st);
        }

        private void OnWidgetReset()
        {
            IntPtr h = _widget.Target;
            if (h == IntPtr.Zero || !_states.TryGetValue(h, out WinState st)) return;
            StopAuto(h, true);
            if (st.ClickThrough) SetClickThrough(h, st, false);
            if (st.TopMost && !st.OriginalTopMost) Fx.SetTopMost(h, false, st);
            Fx.Restore(h, st);
            PersistApp(st);
            SyncWidget(st);
        }

        private void ApplyToForeground(int percent)
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero || !Native.IsWindow(h)) return;
            if (h == _widgetHwnd || h == _msgHwnd) return;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)Environment.ProcessId) return;

            WinState st = EnsureState(h);
            SetOpacity(h, st, percent);
            if (_widget.Target == h) SyncWidget(st);
        }

        // ==================================================================
        //  全局热键
        // ==================================================================

        private void ApplyHotkeySetting()
        {
            UnregisterHotkeys();
            if (!_cfg.HotkeysEnabled) return;

            uint mod = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
            bool a = Native.RegisterHotKey(_msgHwnd, HK_UP, mod, 0x26);      // Ctrl+Alt+↑
            bool b = Native.RegisterHotKey(_msgHwnd, HK_DOWN, mod, 0x28);    // Ctrl+Alt+↓
            bool c = Native.RegisterHotKey(_msgHwnd, HK_RESET, mod, 0x30);   // Ctrl+Alt+0
            bool d = Native.RegisterHotKey(_msgHwnd, HK_TOP, mod, 0x54);     // Ctrl+Alt+T
            bool e = Native.RegisterHotKey(_msgHwnd, HK_PAUSE, mod, 0x48);   // Ctrl+Alt+H
            bool f = Native.RegisterHotKey(_msgHwnd, HK_THROUGH, mod, 0x5A); // Ctrl+Alt+Z
            bool g = Native.RegisterHotKey(_msgHwnd, HK_AUTOTOGGLE, mod, 0x4B); // Ctrl+Alt+K
            bool i = Native.RegisterHotKey(_msgHwnd, HK_AUTOSTOP, mod, 0x50);   // Ctrl+Alt+P
            _hotkeysOk = a && b && c && d && e && f && g && i;

            // 注册失败多半是被别的软件占了。以前这里不吭声，用户按了没反应也不知道为什么。
            if (!_hotkeysOk)
                Log.Info("热键注册结果：↑=" + a + " ↓=" + b + " 0=" + c + " T=" + d
                         + " H=" + e + " Z=" + f + " K=" + g + " P=" + i
                         + "（false = 被别的软件占用了，那个快捷键就用不了）");
            else
                Log.Info("热键全部注册成功");
        }

        private void UnregisterHotkeys()
        {
            if (!_hotkeysOk) return;
            Native.UnregisterHotKey(_msgHwnd, HK_UP);
            Native.UnregisterHotKey(_msgHwnd, HK_DOWN);
            Native.UnregisterHotKey(_msgHwnd, HK_RESET);
            Native.UnregisterHotKey(_msgHwnd, HK_TOP);
            Native.UnregisterHotKey(_msgHwnd, HK_PAUSE);
            Native.UnregisterHotKey(_msgHwnd, HK_THROUGH);
            Native.UnregisterHotKey(_msgHwnd, HK_AUTOTOGGLE);
            Native.UnregisterHotKey(_msgHwnd, HK_AUTOSTOP);
            _hotkeysOk = false;
        }

        private void OnHotKey(int id)
        {
            if (id == HK_PAUSE)
            {
                // 应急键：一键把悬停按钮关掉，顺便停掉所有连按
                _cfg.Enabled = !_cfg.Enabled;
                SyncMenu();
                ScheduleSave();
                if (!_cfg.Enabled)
                {
                    HideWidget();
                    int n = StopAllAuto();
                    ShowBalloon(n > 0
                        ? ("悬停按钮已停用，并停止了 " + n + " 个窗口的连按。再按一次 Ctrl+Alt+H 恢复")
                        : "悬停按钮已停用。再按一次 Ctrl+Alt+H 恢复");
                }
                else ShowBalloon("悬停按钮已启用");
                return;
            }

            if (id == HK_AUTOSTOP)
            {
                int n = StopAllAuto();
                ShowBalloon(n > 0 ? ("已停止 " + n + " 个窗口的连按") : "当前没有在连按的窗口");
                return;
            }

            IntPtr h = (_widget.Visible && _widget.Target != IntPtr.Zero)
                ? _widget.Target
                : Native.GetForegroundWindow();

            if (h == IntPtr.Zero || !Native.IsWindow(h)) return;
            if (h == _widgetHwnd || h == _msgHwnd) return;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)Environment.ProcessId) return;

            WinState st = EnsureState(h);
            switch (id)
            {
                case HK_UP: SetOpacity(h, st, st.Percent + 10); break;
                case HK_DOWN: SetOpacity(h, st, st.Percent - 10); break;
                case HK_AUTOTOGGLE: ToggleAutoPress(h); return;
                case HK_RESET:
                    StopAuto(h, true);
                    if (st.ClickThrough) SetClickThrough(h, st, false);
                    if (st.TopMost && !st.OriginalTopMost) Fx.SetTopMost(h, false, st);
                    Fx.Restore(h, st);
                    PersistApp(st);
                    break;
                case HK_TOP:
                    Fx.SetTopMost(h, !st.TopMost, st);
                    PersistApp(st);
                    break;
                case HK_THROUGH:
                    SetClickThrough(h, st, !st.ClickThrough);
                    ShowBalloon(st.ClickThrough ? "鼠标穿透：开（再按一次关闭）" : "鼠标穿透：关");
                    break;
            }

            if (_widget.Target == h) SyncWidget(st);
        }

        // ==================================================================
        //  开机自启 / 帮助 / 退出
        // ==================================================================

        private static string BuildAutostartCommand()
        {
            string p = Environment.ProcessPath;
            if (string.IsNullOrEmpty(p)) p = Application.ExecutablePath;

            if (string.Equals(Path.GetFileNameWithoutExtension(p), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                // 通过 dotnet AmoWinTrans.dll 启动的情况
                string dll = Path.Combine(AppContext.BaseDirectory, "AmoWinTrans.dll");
                if (File.Exists(dll)) return "\"" + p + "\" \"" + dll + "\"";
            }
            return "\"" + p + "\"";
        }

        private static bool IsAutoStartOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                    return k != null && k.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunValueName, BuildAutostartCommand());
                    else k.DeleteValue(RunValueName, false);
                }
            }
            catch { }
        }

        private static void ShowHelp()
        {
            MessageBox.Show(
                "阿墨 · 窗口透明度\n" +
                "────────────────────────\n" +
                "· 把鼠标移到任意窗口标题栏的右上角，会冒出一个小胶囊按钮。\n" +
                "· 鼠标停在按钮上，它展开成面板：拖滑块调透明度，也可以点预设。\n" +
                "· 面板里的「置顶 / 穿透 / 连按 / 隐藏 / 保活 / 重置」分别对应窗口置顶、鼠标穿透、\n" +
                "  自动连按、鼠标移出就隐藏、防挂机保活、恢复原样。\n" +
                "· 面板上滚轮也能微调透明度。\n\n" +
                "保活（防止挂机被踢下线，比如云原神）：\n" +
                "  · 开启后每分钟给这个窗口补一次按键，让它背后的服务认为你还在操作。\n" +
                "  · 默认发「W / S 各点一下」：角色走一小步再退回来，净位移约 0。\n" +
                "    想换别的：托盘菜单 →「保活发送什么」（可选空格 / Ctrl / 鼠标微动）。\n" +
                "  · 走的是窗口消息，**窗口在后台也能生效**，不会干扰你正在用的程序。\n" +
                "  · 只对没最小化的窗口有效。想全停：托盘菜单 →「取消所有保活」。\n" +
                "  · **要让它看不见又不断线：右键点「保活」把窗口挪到屏幕外**。\n" +
                "    注意别按最小化 —— 窗口最小化之后浏览器会直接丢弃注入的输入，保活就失效了。\n" +
                "  · 挪走之后窗口看不见、鼠标也够不到它。**按 Ctrl+Alt+B 挪回来**\n" +
                "    （也可以在托盘菜单点「把所有挪走的窗口挪回来」）。\n\n" +
                "鼠标移出就隐藏（点面板上的「隐藏」）：\n" +
                "  · 开启后，鼠标离开这个窗口它就完全看不见（透明度压到 0%）；\n" +
                "    鼠标移回窗口原来的位置，立刻恢复成你设定的透明度。\n" +
                "  · 窗口还在原位、没被最小化，所以移回去就能认出来。\n" +
                "  · 万一找不回来：托盘菜单 →「取消所有自动隐藏」，或直接退出程序。\n\n" +
                "连按按键（挂机用，比如游戏里自动按 F 过对话）：\n" +
                "  · 左键点「连按」= 开 / 关；第一次点会先让你录一个键。\n" +
                "  · 右键点「连按」= 打开设置，可以改键、改间隔、换发送方式。\n" +
                "  · 连按进行中时，小胶囊会变成绿色，一眼能看出它正在自己按。\n" +
                "  · 默认「前台发真实按键，后台投递给窗口」，切到别处也不会乱按键。\n\n" +
                "快捷键（不动鼠标就能操作）：\n" +
                "  · 托盘菜单 →「快捷键设置…」里配，四个动作各一个标签页：\n" +
                "      连按开关 / 最小化窗口 / 窗口置顶 / 鼠标穿透。\n" +
                "  · 触发键可以是鼠标侧键1、侧键2、中键，或任意键盘按键（含 Ctrl+ 组合）。\n" +
                "  · 防误触三选一：「按一下就触发」/「连按 N 次」/「长按 N 秒」。\n" +
                "  · 默认不拦截该键，它的原本功能照常；勾了「拦截」才变成专用开关。\n" +
                "  · 最小化是「切换」：按一次最小化，再按一次把刚才那个还原回来。\n\n" +
                "全局热键（作用于鼠标下 / 前台窗口）：\n" +
                "  Ctrl+Alt+↑ / ↓   透明度 +10% / -10%\n" +
                "  Ctrl+Alt+0       恢复 100%\n" +
                "  Ctrl+Alt+T       切换窗口置顶\n" +
                "  Ctrl+Alt+Z       切换鼠标穿透\n" +
                "  Ctrl+Alt+K       切换当前窗口连按\n" +
                "  Ctrl+Alt+P       停止所有连按（应急）\n" +
                "  Ctrl+Alt+H       停用 / 启用悬停按钮（也会停掉所有连按）\n\n" +
                "托盘菜单里可以切换触发区域（标题栏 / 整个窗口）、\n" +
                "切换渲染方式、设置开机自启。\n\n" +
                "悬停按钮不显示的时候：\n" +
                "  · 鼠标停在托盘图标上，提示文字里直接写着「悬停按钮：已启用 / 已停用」。\n" +
                "  · 托盘菜单 →「为什么悬停按钮没出来？」，会把当前状态和最近一次的\n" +
                "    判定原因摊开给你看（鼠标在哪个窗口、为什么被拒绝）。\n" +
                "  · 双击托盘图标 = 一键停用 / 启用（和 Ctrl+Alt+H 一样）。\n\n" +
                "关于「鼠标穿透」：开了之后鼠标就点不到那个窗口了（这是它的用途），\n" +
                "但本工具仍然认得出它 —— 把鼠标移到它标题栏上照样能调出面板关掉。\n" +
                "万一它被别的窗口盖住，用托盘菜单的「取消所有鼠标穿透」一键脱困。\n\n" +
                "注意：透明度最低 10%，免得把窗口调没了找不回来。\n" +
                "以管理员身份运行的程序（任务管理器等）需要本工具也用管理员权限启动才能控制。\n" +
                "退出时会自动把所有窗口恢复原样。",
                "使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Quit()
        {
            _tick.Stop();
            _autoTimer.Stop();
            _saveTimer.Stop();
            _pruneTimer.Stop();
            UnregisterHotkeys();

            _trigger.Dispose();   // 一定要卸钩子，不然会拖累全系统的输入

            StopAllAuto();   // 退出前一定要停，否则键可能停在按下状态
            try { _widget.HideWidget(); } catch { }
            RestoreAll();
            _cfg.Save();

            _tray.Visible = false;
            _tray.Dispose();
            _widget.Dispose();
            _msg.Dispose();
            _trayIcon?.Dispose();

            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tick.Dispose();
                _autoTimer.Dispose();
                _saveTimer.Dispose();
                _pruneTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
