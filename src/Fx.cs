using System;
using System.Runtime.InteropServices;

namespace AmoWinTrans
{
    /// <summary>
    /// 单个被接管窗口的状态。用于退出 / 还原时把窗口恢复原样。
    /// </summary>
    internal sealed class WinState
    {
        public IntPtr Hwnd;
        public string Process = string.Empty;
        public string Title = string.Empty;
        public int OriginalExStyle;
        public bool AddedLayered;      // 是不是我们给加上的 WS_EX_LAYERED
        public bool UsedAcrylic;
        public int Percent = 100;
        public bool TopMost;
        public bool OriginalTopMost;   // 接管之前它自己就是置顶的吗
        public bool ClickThrough;

        /// <summary>开了「鼠标移出就隐藏」吗（按程序记）。</summary>
        public bool AutoHide;

        /// <summary>当前是不是正被自动隐藏着（alpha = 0）。</summary>
        public bool Hidden;

        /// <summary>开了「保活」吗（定时补一点输入，防挂机踢下线）。</summary>
        public bool KeepAlive;

        /// <summary>下次该补输入的时间。</summary>
        public DateTime KeepAliveNextAt = DateTime.MinValue;

        /// <summary>保活抖动在 (cx,cy) 和 (cx+1,cy) 之间来回用的方向标记。</summary>
        public bool KeepAliveFlip;

        /// <summary>是不是已经提醒过"最小化会让保活失效"了。</summary>
        public bool KeepAliveWarned;

        /// <summary>是不是被「挪到屏幕外」了。</summary>
        public bool MovedAway;

        /// <summary>挪走之前的位置，挪回来时用。</summary>
        public Native.RECT SavedRect;

        /// <summary>连按任务（没配过就是 null）。</summary>
        public AutoKeyJob Auto;
    }

    /// <summary>
    /// 窗口透明度引擎。
    ///
    /// 主方案：给目标窗口挂上 WS_EX_LAYERED，再用 SetLayeredWindowAttributes(LWA_ALPHA)。
    /// 这是最通用可靠的做法 —— 整个窗口（标题栏 + 客户区 + 里面的内容）一起变透明，
    /// 而且不需要目标进程配合。AutoHotkey 的 WinSet Transparent 也是这个原理。
    ///
    /// 备用方案：SetWindowCompositionAttribute 的亚克力混合，给自带透明背景的窗口用。
    /// </summary>
    internal static class Fx
    {
        public const int MinPercent = 10;
        public const int MaxPercent = 100;

        public static int Clamp(int percent)
        {
            if (percent < MinPercent) return MinPercent;
            if (percent > MaxPercent) return MaxPercent;
            return percent;
        }

        /// <summary>应用透明度。percent = 100 时自动还原。</summary>
        public static void Apply(IntPtr hwnd, int percent, FxMethod method, WinState st)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
            percent = Clamp(percent);
            st.Percent = percent;

            if (method == FxMethod.Acrylic)
            {
                ApplyAcrylic(hwnd, percent);
                st.UsedAcrylic = true;
                return;
            }

            if (percent >= MaxPercent)
            {
                Restore(hwnd, st);
                return;
            }
            ApplyLayered(hwnd, percent, st);
        }

        private static void ApplyLayered(IntPtr hwnd, int percent, WinState st)
        {
            int ex = Native.GetExStyle(hwnd);
            if ((ex & Native.WS_EX_LAYERED) == 0)
            {
                if (st.OriginalExStyle == 0) st.OriginalExStyle = ex;
                Native.SetExStyle(hwnd, ex | Native.WS_EX_LAYERED);
                st.AddedLayered = true;
                Native.NudgeRedraw(hwnd);
            }

            byte alpha = (byte)Math.Round(percent * 255.0 / 100.0);
            if (alpha < 1) alpha = 1;
            Native.SetLayeredWindowAttributes(hwnd, 0, alpha, Native.LWA_ALPHA);
        }

        /// <summary>
        /// 「鼠标移出就隐藏」用：把 alpha 直接压到 0，窗口完全看不见。
        ///
        /// 不能走 Apply()，因为那条路有 MinPercent = 10 的下限
        /// （防止用户把窗口调没了找不回来）。这里是用户明确要的「隐藏」，
        /// 窗口本身还在原位，鼠标移回去就能恢复。
        /// </summary>
        public static void SetHiddenAlpha(IntPtr hwnd, WinState st)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

            int ex = Native.GetExStyle(hwnd);
            if ((ex & Native.WS_EX_LAYERED) == 0)
            {
                if (st.OriginalExStyle == 0) st.OriginalExStyle = ex;
                Native.SetExStyle(hwnd, ex | Native.WS_EX_LAYERED);
                st.AddedLayered = true;
                Native.NudgeRedraw(hwnd);
            }
            Native.SetLayeredWindowAttributes(hwnd, 0, 0, Native.LWA_ALPHA);
        }

        private static void ApplyAcrylic(IntPtr hwnd, int percent)
        {
            var accent = new Native.AccentPolicy();
            if (percent >= MaxPercent)
            {
                accent.AccentState = Native.ACCENT_DISABLED;
            }
            else
            {
                accent.AccentState = Native.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                accent.AccentFlags = 2;
                // GradientColor 是 ABGR：A 在最高位。透明度越低 → 色调越淡 → 背景越透出来。
                byte a = (byte)Math.Round((MaxPercent - percent) * 255.0 / (MaxPercent - MinPercent));
                accent.GradientColor = (a << 24);
            }

            int size = Marshal.SizeOf(typeof(Native.AccentPolicy));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new Native.WindowCompositionAttributeData
                {
                    Attribute = Native.WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size,
                };
                Native.SetWindowCompositionAttribute(hwnd, ref data);
            }
            catch { }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        /// <summary>把窗口恢复成原样（去掉我们加的分层样式 / 关掉亚克力）。</summary>
        public static void Restore(IntPtr hwnd, WinState st)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

            if (st.UsedAcrylic)
            {
                ApplyAcrylic(hwnd, MaxPercent);
                st.UsedAcrylic = false;
            }

            if (st.AddedLayered)
            {
                int ex = Native.GetExStyle(hwnd);
                Native.SetExStyle(hwnd, ex & ~Native.WS_EX_LAYERED);
                st.AddedLayered = false;
                Native.NudgeRedraw(hwnd);
            }
            else if ((Native.GetExStyle(hwnd) & Native.WS_EX_LAYERED) != 0)
            {
                // 窗口本来就是分层的（应用自己在用），别动它的样式，只把 alpha 拉回 255
                Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA);
            }

            st.Percent = MaxPercent;
        }

        public static void SetTopMost(IntPtr hwnd, bool on, WinState st)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
            Native.SetWindowPos(hwnd, on ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            st.TopMost = on;
        }

        public static void SetClickThrough(IntPtr hwnd, bool on, WinState st)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
            int ex = Native.GetExStyle(hwnd);
            int want = on ? (ex | Native.WS_EX_TRANSPARENT) : (ex & ~Native.WS_EX_TRANSPARENT);
            if (want != ex)
            {
                Native.SetExStyle(hwnd, want);
                Native.NudgeRedraw(hwnd);
            }
            st.ClickThrough = on;
        }

        /// <summary>尝试把窗口带到最前面（不激活）。</summary>
        public static void BringToTop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // ------------------------------------------------------------------
        //  「挪到屏幕外」—— 保活的搭档
        //
        //  为什么要这个：窗口**最小化**之后浏览器会直接丢弃注入的输入
        //  （实测 PostMessage / SendMessage / 发子窗口，三种方式全部无效），
        //  但把窗口**挪到屏幕外**、不最小化，注入就照常生效（实测通过）。
        //
        //  所以想"看不见但还活着"，别按最小化，用「挪走」。
        // ------------------------------------------------------------------

        /// <summary>把窗口挪到虚拟屏幕右边之外。</summary>
        public static bool MoveAway(IntPtr hwnd, WinState st)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
            if (st.MovedAway) return true;

            if (!Native.GetWindowRect(hwnd, out Native.RECT r)) return false;
            st.SavedRect = r;

            // 挪到所有显示器右边 400 像素处
            int x = System.Windows.Forms.SystemInformation.VirtualScreen.Right + 400;
            int y = r.Top;

            if (!Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE))
                return false;

            st.MovedAway = true;
            return true;
        }

        /// <summary>把挪走的窗口挪回原位。</summary>
        public static bool MoveBack(IntPtr hwnd, WinState st)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
            if (!st.MovedAway) return true;

            Native.SetWindowPos(hwnd, IntPtr.Zero, st.SavedRect.Left, st.SavedRect.Top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

            st.MovedAway = false;
            return true;
        }
    }
}
