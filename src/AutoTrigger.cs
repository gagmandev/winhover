using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AmoWinTrans
{
    /// <summary>触发键是哪个。</summary>
    internal enum AutoTriggerKind
    {
        Key = 0,
        MouseX1 = 1,        // 鼠标侧键 1（一般是"后退"）
        MouseX2 = 2,        // 鼠标侧键 2（一般是"前进"）
        MouseMiddle = 3,    // 鼠标中键
    }

    /// <summary>怎么算"按下了"，用来防误触。</summary>
    internal enum AutoTriggerMode
    {
        PressCount = 0,     // 在时间窗内连按 N 次
        LongPress = 1,      // 按住 N 毫秒
        Instant = 2,        // 按一下就触发，不防误触
    }

    /// <summary>一个快捷键的配置。</summary>
    internal sealed class AutoTriggerConfig
    {
        public const int FormatVersion = 1;

        public bool Enabled;
        public AutoTriggerKind Kind = AutoTriggerKind.MouseX2;

        public int VirtualKey = 0x46;   // 默认 F
        public bool Ctrl;
        public bool Alt;
        public bool Shift;

        public AutoTriggerMode Mode = AutoTriggerMode.PressCount;
        public int PressCount = 2;          // 连按几次算触发
        public int PressWindowMs = 700;     // 相邻两次必须在这么久之内
        public int HoldMs = 800;            // 长按多久算触发

        /// <summary>是否把触发键吃掉（不让它传给别的程序）。</summary>
        public bool Swallow;

        public void Normalize()
        {
            if (PressCount < 2) PressCount = 2;
            if (PressCount > 5) PressCount = 5;
            if (PressWindowMs < 200) PressWindowMs = 200;
            if (PressWindowMs > 2000) PressWindowMs = 2000;
            if (HoldMs < 200) HoldMs = 200;
            if (HoldMs > 5000) HoldMs = 5000;
        }

        public AutoTriggerConfig Clone()
        {
            return new AutoTriggerConfig
            {
                Enabled = Enabled,
                Kind = Kind,
                VirtualKey = VirtualKey,
                Ctrl = Ctrl,
                Alt = Alt,
                Shift = Shift,
                Mode = Mode,
                PressCount = PressCount,
                PressWindowMs = PressWindowMs,
                HoldMs = HoldMs,
                Swallow = Swallow,
            };
        }

        public string KindName
        {
            get
            {
                switch (Kind)
                {
                    case AutoTriggerKind.MouseX1: return "鼠标侧键1";
                    case AutoTriggerKind.MouseX2: return "鼠标侧键2";
                    case AutoTriggerKind.MouseMiddle: return "鼠标中键";
                    default: return ModifierPrefix() + AutoPress.KeyName((ushort)VirtualKey);
                }
            }
        }

        /// <summary>
        /// 修饰键前缀。必须显示出来 —— 不然 Ctrl+Alt+B 会被写成「按一下 B」，
        /// 用户单按 B 发现没反应，会以为快捷键坏了。
        /// </summary>
        private string ModifierPrefix()
        {
            var sb = new System.Text.StringBuilder();
            if (Ctrl) sb.Append("Ctrl+");
            if (Alt) sb.Append("Alt+");
            if (Shift) sb.Append("Shift+");
            return sb.ToString();
        }

        public string Describe()
        {
            if (!Enabled) return "未启用";
            string how;
            switch (Mode)
            {
                case AutoTriggerMode.Instant: how = "按一下"; break;
                case AutoTriggerMode.LongPress: how = "长按 " + (HoldMs / 1000.0).ToString("0.0#") + " 秒"; break;
                default: how = "连按 " + PressCount + " 次"; break;
            }
            return how + "「" + KindName + "」";
        }

        public string Serialize()
        {
            return string.Join(",",
                Enabled ? "1" : "0",
                ((int)Kind).ToString(CultureInfo.InvariantCulture),
                VirtualKey.ToString(CultureInfo.InvariantCulture),
                Ctrl ? "1" : "0",
                Alt ? "1" : "0",
                Shift ? "1" : "0",
                ((int)Mode).ToString(CultureInfo.InvariantCulture),
                PressCount.ToString(CultureInfo.InvariantCulture),
                PressWindowMs.ToString(CultureInfo.InvariantCulture),
                HoldMs.ToString(CultureInfo.InvariantCulture),
                Swallow ? "1" : "0",
                FormatVersion.ToString(CultureInfo.InvariantCulture));
        }

        public static AutoTriggerConfig Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string[] p = s.Split(',');
            if (p.Length < 11) return null;

            var c = new AutoTriggerConfig();
            try
            {
                c.Enabled = p[0] == "1";
                int kind = int.Parse(p[1], CultureInfo.InvariantCulture);
                if (kind < 0 || kind > 3) return null;
                c.Kind = (AutoTriggerKind)kind;
                c.VirtualKey = int.Parse(p[2], CultureInfo.InvariantCulture);
                c.Ctrl = p[3] == "1";
                c.Alt = p[4] == "1";
                c.Shift = p[5] == "1";
                int mode = int.Parse(p[6], CultureInfo.InvariantCulture);
                if (mode < 0 || mode > 2) return null;
                c.Mode = (AutoTriggerMode)mode;
                c.PressCount = int.Parse(p[7], CultureInfo.InvariantCulture);
                c.PressWindowMs = int.Parse(p[8], CultureInfo.InvariantCulture);
                c.HoldMs = int.Parse(p[9], CultureInfo.InvariantCulture);
                c.Swallow = p[10] == "1";
            }
            catch { return null; }

            c.Normalize();
            return c;
        }
    }

    /// <summary>一个快捷键绑定：配置 + 运行状态。</summary>
    internal sealed class TriggerBinding
    {
        public string Id;
        public AutoTriggerConfig Config;

        internal bool IsDown;
        internal DateTime DownAt = DateTime.MinValue;
        internal DateTime LastPressAt = DateTime.MinValue;
        internal int PressCount;
        internal bool HoldFired;

        public TriggerBinding(string id, AutoTriggerConfig cfg)
        {
            Id = id;
            Config = cfg ?? new AutoTriggerConfig();
        }

        public void Reset()
        {
            IsDown = false;
            DownAt = DateTime.MinValue;
            LastPressAt = DateTime.MinValue;
            PressCount = 0;
            HoldFired = false;
        }
    }

    /// <summary>快捷键动作的 id。</summary>
    internal static class TriggerIds
    {
        public const string AutoPress = "autopress";
        public const string Minimize = "minimize";
        public const string TopMost = "topmost";
        public const string ClickThrough = "through";
        /// <summary>把所有被「挪到屏幕外」的窗口挪回来。</summary>
        public const string MoveBack = "moveback";

        /// <summary>「挪回窗口」的出厂设置：Ctrl+Alt+B。</summary>
        public static AutoTriggerConfig DefaultMoveBack()
        {
            var c = new AutoTriggerConfig
            {
                Enabled = true,               // 这是逃生快捷键，默认就得能用
                Kind = AutoTriggerKind.Key,
                VirtualKey = 0x42,            // B
                Ctrl = true,
                Alt = true,
                Mode = AutoTriggerMode.Instant,
            };
            c.Normalize();
            return c;
        }
    }

    /// <summary>
    /// 全局键鼠钩子 + 防误触判定，可以同时管好几个快捷键。
    ///
    /// 为什么要低级钩子而不是 RegisterHotKey：
    ///  - 鼠标侧键 / 中键 RegisterHotKey 根本抓不到；
    ///  - 裸键（不带修饰键的 F）注册成全局热键会把它从所有程序手里抢走。
    ///
    /// 钩子回调里**只做几个整数比较**，绝不做重活 —— 低级钩子拖慢了会被系统直接摘掉。
    /// 真正要执行的动作由外面的定时器通过 TryDequeueFired() 取走后执行。
    /// </summary>
    internal sealed class AutoTriggerWatcher : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;

        // 我们自己注入按键时会在 dwExtraInfo 里放这个标记，
        // 钩子看到它就跳过 —— 只忽略"自己发出去的键"，别人的注入输入不受影响。
        // （不能一刀切过滤所有 injected 事件：很多游戏鼠标用软件改侧键，
        //   那些事件同样带 injected 标记，过滤掉的话快捷键就失灵了。）
        public const long OurInjectedMagic = 0x414D4F31;   // 'AMO1'

        // 结构体字段偏移（x64），直接按偏移读比 Marshal.PtrToStructure 快得多。
        // 对应 KBDLLHOOKSTRUCT / MSLLHOOKSTRUCT。
        private const int OffKeyboardVk = 0;
        private const int OffKeyboardFlags = 8;
        private const int OffKeyboardExtra = 16;
        private const int OffMouseData = 8;
        private const int OffMouseFlags = 12;
        private const int OffMouseExtra = 24;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // 委托必须存字段，否则会被 GC 回收，钩子回调直接崩
        private HookProc _kbProc;
        private HookProc _msProc;
        private IntPtr _kbHook = IntPtr.Zero;
        private IntPtr _msHook = IntPtr.Zero;

        private readonly List<TriggerBinding> _bindings = new List<TriggerBinding>();
        private readonly Queue<string> _fired = new Queue<string>();

        // 修饰键状态自己维护：钩子回调里 GetAsyncKeyState 拿到的可能还是上一刻的值
        private bool _kCtrl, _kAlt, _kShift;

        public bool IsInstalled => _kbHook != IntPtr.Zero || _msHook != IntPtr.Zero;

        /// <summary>装 / 卸钩子。返回是否安装成功。</summary>
        public bool Apply(List<TriggerBinding> bindings)
        {
            Uninstall();
            _bindings.Clear();
            if (bindings != null) _bindings.AddRange(bindings);
            foreach (TriggerBinding b in _bindings) b.Reset();

            bool needKeyboard = false, needMouse = false;
            foreach (TriggerBinding b in _bindings)
            {
                if (b.Config == null || !b.Config.Enabled) continue;
                if (b.Config.Kind == AutoTriggerKind.Key) needKeyboard = true;
                else needMouse = true;
            }

            if (!needKeyboard && !needMouse) return true;

            IntPtr hmod = GetModuleHandle(null);

            if (needKeyboard)
            {
                _kbProc = KeyboardProc;
                _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hmod, 0);
                if (_kbHook == IntPtr.Zero)
                {
                    Log.Error("SetWindowsHookEx(键盘)", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                    Uninstall();
                    return false;
                }
            }

            if (needMouse)
            {
                _msProc = MouseProc;
                _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, hmod, 0);
                if (_msHook == IntPtr.Zero)
                {
                    Log.Error("SetWindowsHookEx(鼠标)", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                    Uninstall();
                    return false;
                }
            }

            Log.Always("快捷键已生效：" + DescribeAll());
            return true;
        }

        public string DescribeAll()
        {
            var sb = new StringBuilder();
            foreach (TriggerBinding b in _bindings)
            {
                if (b.Config == null || !b.Config.Enabled) continue;
                if (sb.Length > 0) sb.Append("；");
                sb.Append(ActionName(b.Id)).Append(" = ").Append(b.Config.Describe());
            }
            return sb.Length == 0 ? "（无）" : sb.ToString();
        }

        public static string ActionName(string id)
        {
            switch (id)
            {
                case TriggerIds.Minimize: return "最小化窗口";
                case TriggerIds.TopMost: return "窗口置顶";
                case TriggerIds.ClickThrough: return "鼠标穿透";
                case TriggerIds.MoveBack: return "挪回窗口";
                default: return "连按开关";
            }
        }

        public void Uninstall()
        {
            if (_kbHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_kbHook);
                _kbHook = IntPtr.Zero;
            }
            if (_msHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_msHook);
                _msHook = IntPtr.Zero;
            }
            _kbProc = null;
            _msProc = null;
            _fired.Clear();
            foreach (TriggerBinding b in _bindings) b.Reset();
        }

        /// <summary>定时器里调：处理"长按"判定。</summary>
        public void Poll()
        {
            if (_bindings.Count == 0) return;
            DateTime now = DateTime.UtcNow;

            foreach (TriggerBinding b in _bindings)
            {
                AutoTriggerConfig c = b.Config;
                if (c == null || !c.Enabled) continue;
                if (c.Mode != AutoTriggerMode.LongPress) continue;
                if (!b.IsDown || b.HoldFired) continue;
                if ((now - b.DownAt).TotalMilliseconds < c.HoldMs) continue;

                b.HoldFired = true;
                _fired.Enqueue(b.Id);
            }
        }

        /// <summary>取一个待执行的快捷键动作，没有就返回 false。</summary>
        public bool TryDequeueFired(out string id)
        {
            if (_fired.Count == 0) { id = null; return false; }
            id = _fired.Dequeue();
            return true;
        }

        // ------------------------------------------------------------------
        //  钩子回调（必须极快）
        // ------------------------------------------------------------------

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (isDown || isUp)
                {
                    // 按偏移直接读，不做结构体 marshal
                    int vk = Marshal.ReadInt32(lParam, OffKeyboardVk);

                    // 自己注入的按键不算"用户按的"，否则连按发出去的键会被自己再识别一遍
                    if (Marshal.ReadIntPtr(lParam, OffKeyboardExtra).ToInt64() != OurInjectedMagic)
                    {
                        UpdateModifiers(vk, isDown);

                        bool swallow = false;
                        for (int i = 0; i < _bindings.Count; i++)
                        {
                            TriggerBinding b = _bindings[i];
                            AutoTriggerConfig c = b.Config;
                            if (c == null || !c.Enabled || c.Kind != AutoTriggerKind.Key) continue;
                            if (vk != c.VirtualKey) continue;
                            if (!ModifiersMatch(c)) continue;

                            Edge(b, isDown);
                            if (c.Swallow) swallow = true;
                        }
                        if (swallow) return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();

                // 鼠标移动 / 滚轮占了绝大多数事件，先按消息号滤掉，
                // 否则每次移动都要解析一次结构体，钩子会被拖慢甚至被系统摘掉
                if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP
                    || msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP)
                {
                    // 自己注入的鼠标事件不算（我们目前不注入鼠标，留着以防万一）
                    if (Marshal.ReadIntPtr(lParam, OffMouseExtra).ToInt64() != OurInjectedMagic)
                    {
                        int mouseData = Marshal.ReadInt32(lParam, OffMouseData);
                        int xbtn = (mouseData >> 16) & 0xFFFF;

                        AutoTriggerKind kind;
                        bool isDown;
                        if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
                        {
                            kind = xbtn == 1 ? AutoTriggerKind.MouseX1 : AutoTriggerKind.MouseX2;
                            isDown = msg == WM_XBUTTONDOWN;
                        }
                        else
                        {
                            kind = AutoTriggerKind.MouseMiddle;
                            isDown = msg == WM_MBUTTONDOWN;
                        }

                        bool swallow = false;
                        for (int i = 0; i < _bindings.Count; i++)
                        {
                            TriggerBinding b = _bindings[i];
                            AutoTriggerConfig c = b.Config;
                            if (c == null || !c.Enabled || c.Kind != kind) continue;
                            Edge(b, isDown);
                            if (c.Swallow) swallow = true;
                        }
                        if (swallow) return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_msHook, nCode, wParam, lParam);
        }

        private void UpdateModifiers(int vk, bool down)
        {
            switch (vk)
            {
                case 0x11: case 0xA2: case 0xA3: _kCtrl = down; break;   // Ctrl / LCtrl / RCtrl
                case 0x12: case 0xA4: case 0xA5: _kAlt = down; break;    // Alt / LAlt / RAlt
                case 0x10: case 0xA0: case 0xA1: _kShift = down; break;  // Shift / LShift / RShift
            }
        }

        private bool ModifiersMatch(AutoTriggerConfig c)
        {
            return _kCtrl == c.Ctrl && _kAlt == c.Alt && _kShift == c.Shift;
        }

        /// <summary>按下 / 抬起统一处理，含防误触判定。</summary>
        private void Edge(TriggerBinding b, bool down)
        {
            AutoTriggerConfig c = b.Config;
            DateTime now = DateTime.UtcNow;

            if (down)
            {
                if (b.IsDown) return;   // 键盘自动重复，忽略
                b.IsDown = true;
                b.DownAt = now;
                b.HoldFired = false;

                if (c.Mode == AutoTriggerMode.Instant)
                {
                    _fired.Enqueue(b.Id);
                    return;
                }

                if (c.Mode == AutoTriggerMode.PressCount)
                {
                    bool within = b.PressCount > 0
                                  && (now - b.LastPressAt).TotalMilliseconds <= c.PressWindowMs;

                    b.PressCount = within ? b.PressCount + 1 : 1;
                    b.LastPressAt = now;

                    if (b.PressCount >= c.PressCount)
                    {
                        b.PressCount = 0;
                        _fired.Enqueue(b.Id);
                    }
                }
            }
            else
            {
                b.IsDown = false;
            }
        }

        public void Dispose()
        {
            Uninstall();
        }
    }
}
