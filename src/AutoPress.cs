using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace AmoWinTrans
{
    /// <summary>按键怎么送出去。</summary>
    internal enum AutoSendMode
    {
        /// <summary>只用 SendInput 发真实按键，且只在该窗口处于前台时才发。最保守。</summary>
        ForegroundOnly = 0,

        /// <summary>前台时用 SendInput（游戏基本都吃这套），窗口切到后台改用 PostMessage 投给窗口。默认。</summary>
        Hybrid = 1,

        /// <summary>不管前后台，一律用 PostMessage 投给窗口。只对一部分程序有效。</summary>
        BackgroundOnly = 2,
    }

    /// <summary>
    /// 连按配置：按哪个键、多久按一次、怎么发。
    /// </summary>
    internal sealed class AutoKeyConfig
    {
        public const int MinIntervalMs = 50;      // 0.05 秒
        public const int MaxIntervalMs = 10000;   // 10 秒
        public const int FormatVersion = 2;

        public int VirtualKey;      // 0 = 还没设置
        public bool Ctrl;
        public bool Alt;
        public bool Shift;

        /// <summary>间隔下限（毫秒），实际每次在 [Min, Max] 之间随机，比固定节奏自然。</summary>
        public int MinMs = 500;
        public int MaxMs = 1500;

        public AutoSendMode Mode = AutoSendMode.Hybrid;

        public bool IsConfigured => VirtualKey != 0;

        public void Normalize()
        {
            if (MinMs < MinIntervalMs) MinMs = MinIntervalMs;
            if (MaxMs < MinIntervalMs) MaxMs = MinIntervalMs;
            if (MinMs > MaxIntervalMs) MinMs = MaxIntervalMs;
            if (MaxMs > MaxIntervalMs) MaxMs = MaxIntervalMs;
            if (MaxMs < MinMs) { int t = MinMs; MinMs = MaxMs; MaxMs = t; }
        }

        public AutoKeyConfig Clone()
        {
            return new AutoKeyConfig
            {
                VirtualKey = VirtualKey,
                Ctrl = Ctrl,
                Alt = Alt,
                Shift = Shift,
                MinMs = MinMs,
                MaxMs = MaxMs,
                Mode = Mode,
            };
        }

        public string Display => AutoPress.KeyName(this);

        /// <summary>序列化成一行，存进 settings.ini。</summary>
        public string Serialize()
        {
            return string.Join(",",
                VirtualKey.ToString(CultureInfo.InvariantCulture),
                Ctrl ? "1" : "0",
                Alt ? "1" : "0",
                Shift ? "1" : "0",
                MinMs.ToString(CultureInfo.InvariantCulture),
                MaxMs.ToString(CultureInfo.InvariantCulture),
                ((int)Mode).ToString(CultureInfo.InvariantCulture),
                FormatVersion.ToString(CultureInfo.InvariantCulture));
        }

        public static AutoKeyConfig Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string[] p = s.Split(',');
            if (p.Length < 7) return null;

            var c = new AutoKeyConfig();
            if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out c.VirtualKey)) return null;
            c.Ctrl = p[1] == "1";
            c.Alt = p[2] == "1";
            c.Shift = p[3] == "1";
            if (!int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out c.MinMs)) return null;
            if (!int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out c.MaxMs)) return null;

            if (p.Length >= 8)
            {
                // 新格式：第 7 个字段直接就是模式
                if (int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode)
                    && mode >= 0 && mode <= 2)
                    c.Mode = (AutoSendMode)mode;
            }
            else
            {
                // 老格式：第 7 个字段是 background 布尔值。
                // 当时 background=0 代表"只在前台发"，正是用户抱怨的那个限制，
                // 所以这里把老配置升级成"前台 SendInput + 后台 PostMessage"，
                // 让升级后不用重新配置就能后台继续跑。
                c.Mode = p[6] == "1" ? AutoSendMode.BackgroundOnly : AutoSendMode.Hybrid;
            }

            c.Normalize();
            return c;
        }
    }

    /// <summary>一个窗口的连按任务。</summary>
    internal sealed class AutoKeyJob
    {
        public IntPtr Hwnd;
        public AutoKeyConfig Config = new AutoKeyConfig();
        public bool Running;

        public DateTime NextAt = DateTime.MinValue;
        public DateTime ReleaseAt = DateTime.MinValue;
        public bool KeyIsDown;

        /// <summary>上一次"按下"是走哪条路发出去的，抬键要用同一条路，否则键会卡住。</summary>
        public bool LastUsedInput;

        public long PressCount;

        public string KeyDisplay => AutoPress.KeyName(Config);
    }

    /// <summary>
    /// 连按引擎：只负责"把一次按键发出去"，节奏由外面的定时器控制。
    /// </summary>
    internal static class AutoPress
    {
        private static readonly Random Rng = new Random();

        public static int NextInterval(AutoKeyConfig c)
        {
            c.Normalize();
            if (c.MaxMs <= c.MinMs) return c.MinMs;
            lock (Rng) return Rng.Next(c.MinMs, c.MaxMs + 1);
        }

        /// <summary>
        /// 按下 / 抬起一次按键。
        /// useInput = true 用 SendInput（真实按键，需要目标在前台）；
        /// useInput = false 用 PostMessage 投递给窗口（窗口在后台也能发）。
        /// </summary>
        public static void Send(IntPtr hwnd, AutoKeyConfig cfg, bool down, bool useInput)
        {
            if (cfg.VirtualKey == 0) return;
            try
            {
                if (useInput) SendInputKey(cfg, down);
                else PostKey(hwnd, cfg, down);
            }
            catch { /* 发不出去就算了，别把主循环带崩 */ }
        }

        // ------------------------------------------------------------------
        //  SendInput：真实按键，走系统输入队列，只影响当前前台窗口
        // ------------------------------------------------------------------

        // 按键注入必须走独立线程，原因见 EnqueueSend 的注释
        private static readonly System.Collections.Concurrent.BlockingCollection<Action> SendQueue =
            new System.Collections.Concurrent.BlockingCollection<Action>();

        private static Thread _sendThread;

        private static void EnsureSendThread()
        {
            if (_sendThread != null) return;

            _sendThread = new Thread(() =>
            {
                foreach (Action a in SendQueue.GetConsumingEnumerable())
                {
                    try { a(); }
                    catch { }
                }
            });
            _sendThread.IsBackground = true;
            _sendThread.Name = "AmoKeySender";
            _sendThread.Start();
        }

        private static void EnqueueSend(Action send)
        {
            EnsureSendThread();
            try { SendQueue.Add(send); }
            catch { }
        }

        private static void SendInputKey(AutoKeyConfig cfg, bool down)
        {
            var list = new List<Native.INPUT>(8);

            if (cfg.Ctrl) list.Add(MakeInput(Native.VK_CONTROL, down));
            if (cfg.Alt) list.Add(MakeInput(Native.VK_MENU, down));
            if (cfg.Shift) list.Add(MakeInput(Native.VK_SHIFT, down));
            list.Add(MakeInput((ushort)cfg.VirtualKey, down));
            if (cfg.Shift) list.Add(MakeInput(Native.VK_SHIFT, !down));
            if (cfg.Alt) list.Add(MakeInput(Native.VK_MENU, !down));
            if (cfg.Ctrl) list.Add(MakeInput(Native.VK_CONTROL, !down));

            Native.INPUT[] arr = list.ToArray();
            int size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.INPUT));

            // 不要在装了低级钩子的线程上直接 SendInput。
            // 注入的事件要经过钩子回调，而回调是在装钩子的那个线程上跑的 ——
            // 线程正卡在 SendInput 里等注入完成，回调就没机会执行，
            // 超时（默认 300ms）之后 Windows 会把钩子直接摘掉，
            // 结果就是"连按一开，全局快捷键全失灵"。
            // 丢到独立线程上发，钩子线程就一直是空闲的，随时能处理回调。
            EnqueueSend(() =>
            {
                Native.SendInput((uint)arr.Length, arr, size);
            });
        }

        private static Native.INPUT MakeInput(ushort vk, bool down)
        {
            uint flags = down ? 0u : Native.KEYEVENTF_KEYUP;
            if (IsExtendedKey(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            return new Native.INPUT
            {
                type = Native.INPUT_KEYBOARD,
                U = new Native.InputUnion
                {
                    ki = new Native.KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        // 打上自己的标记，钩子靠它区分"我们自己注入的键"和"用户真的按了"
                        dwExtraInfo = new IntPtr(AutoTriggerWatcher.OurInjectedMagic),
                    },
                },
            };
        }

        // ------------------------------------------------------------------
        //  PostMessage：直接投递给目标窗口，窗口不在前台也能发（对部分程序有效）
        // ------------------------------------------------------------------

        private static void PostKey(IntPtr hwnd, AutoKeyConfig cfg, bool down)
        {
            if (hwnd == IntPtr.Zero) return;

            // 很多程序（尤其游戏）真正收键盘的是某个子窗口，
            // 能问到就直接投给它，比投给顶层窗口命中率高得多。
            IntPtr target = ResolvePostTarget(hwnd);

            if (cfg.Ctrl) PostOne(target, Native.VK_CONTROL, down, false);
            if (cfg.Alt) PostOne(target, Native.VK_MENU, down, false);
            if (cfg.Shift) PostOne(target, Native.VK_SHIFT, down, false);
            PostOne(target, (ushort)cfg.VirtualKey, down, IsExtendedKey((ushort)cfg.VirtualKey));
            if (cfg.Shift) PostOne(target, Native.VK_SHIFT, !down, false);
            if (cfg.Alt) PostOne(target, Native.VK_MENU, !down, false);
            if (cfg.Ctrl) PostOne(target, Native.VK_CONTROL, !down, false);
        }

        /// <summary>
        /// 找该窗口线程里当前拥有键盘焦点的子窗口。窗口在后台时也能问到，
        /// 拿不到就退回顶层窗口本身。
        /// </summary>
        private static IntPtr ResolvePostTarget(IntPtr hwnd)
        {
            try
            {
                uint tid = Native.GetWindowThreadProcessId(hwnd, out uint _);
                if (tid == 0) return hwnd;

                var gti = new Native.GUITHREADINFO();
                gti.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.GUITHREADINFO));
                if (Native.GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero)
                {
                    if (gti.hwndFocus == hwnd || Native.IsChild(hwnd, gti.hwndFocus))
                        return gti.hwndFocus;
                }
            }
            catch { }
            return hwnd;
        }

        private static void PostOne(IntPtr hwnd, ushort vk, bool down, bool extended)
        {
            uint scan = Native.MapVirtualKey(vk, Native.MAPVK_VK_TO_VSC);
            uint lp = 1u | ((scan & 0xFFu) << 16);
            if (extended) lp |= 1u << 24;
            if (!down) lp |= (1u << 30) | (1u << 31);

            uint msg = down ? Native.WM_KEYDOWN : Native.WM_KEYUP;
            Native.PostMessage(hwnd, msg, (IntPtr)vk, (IntPtr)(int)lp);
        }

        // ------------------------------------------------------------------
        //  辅助
        // ------------------------------------------------------------------

        /// <summary>方向键、Insert/Delete 之类在 WM_KEYDOWN 里要带扩展位。</summary>
        public static bool IsExtendedKey(ushort vk)
        {
            switch (vk)
            {
                case 0x21: // PgUp
                case 0x22: // PgDn
                case 0x23: // End
                case 0x24: // Home
                case 0x25: // ←
                case 0x26: // ↑
                case 0x27: // →
                case 0x28: // ↓
                case 0x2D: // Insert
                case 0x2E: // Delete
                case 0x5B: // Win
                case 0x5C: // Win
                case 0x6F: // 小键盘 /
                case 0xA3: // 右 Ctrl
                case 0xA5: // 右 Alt
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>把虚拟键码变成好认的名字，比如 F / 空格 / Ctrl+1。</summary>
        public static string KeyName(AutoKeyConfig cfg)
        {
            if (cfg == null || cfg.VirtualKey == 0) return "未设置";

            var sb = new StringBuilder();
            if (cfg.Ctrl) sb.Append("Ctrl+");
            if (cfg.Alt) sb.Append("Alt+");
            if (cfg.Shift) sb.Append("Shift+");
            sb.Append(KeyName((ushort)cfg.VirtualKey));
            return sb.ToString();
        }

        public static string KeyName(ushort vk)
        {
            switch (vk)
            {
                case 0x08: return "退格";
                case 0x09: return "Tab";
                case 0x0D: return "回车";
                case 0x13: return "Pause";
                case 0x14: return "CapsLock";
                case 0x1B: return "Esc";
                case 0x20: return "空格";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x2C: return "PrtSc";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x5B: return "Win";
                case 0x5C: return "Win";
                case 0x60: return "小键盘0";
                case 0x61: return "小键盘1";
                case 0x62: return "小键盘2";
                case 0x63: return "小键盘3";
                case 0x64: return "小键盘4";
                case 0x65: return "小键盘5";
                case 0x66: return "小键盘6";
                case 0x67: return "小键盘7";
                case 0x68: return "小键盘8";
                case 0x69: return "小键盘9";
                case 0x6A: return "小键盘*";
                case 0x6B: return "小键盘+";
                case 0x6D: return "小键盘-";
                case 0x6E: return "小键盘.";
                case 0x6F: return "小键盘/";
                case 0x90: return "NumLock";
                case 0x91: return "ScrollLock";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
            }

            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);           // F1-F24
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();       // 0-9
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();       // A-Z
            if (vk >= 0x60 && vk <= 0x69) return "小键盘" + (vk - 0x60);
            return "VK" + vk.ToString("X2", CultureInfo.InvariantCulture);
        }
    }
}
