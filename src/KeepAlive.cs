using System;
using System.Threading;

namespace AmoWinTrans
{
    /// <summary>保活往窗口里发什么。</summary>
    internal enum KeepAliveInput
    {
        /// <summary>鼠标微动（1 像素来回）。对游戏状态零影响，但见下面的坑。</summary>
        Mouse = 0,
        /// <summary>W 点一下、S 点一下（等长）—— 角色走一小步再退回来，净位移约 0。</summary>
        KeyWS = 1,
        /// <summary>空格轻点。</summary>
        KeySpace = 2,
        /// <summary>Ctrl 轻点（Genshin 里单独按没有效果）。</summary>
        KeyCtrl = 3,
    }

    /// <summary>
    /// 「保活」：定时给某个窗口补一点输入，让窗口背后的服务
    /// 认为你还在操作，不会被判成挂机踢下线。云原神那种"长时间不操作自动退出"就靠它对付。
    ///
    /// 为什么用 PostMessage 而不是 SendInput：
    ///   SendInput 是往**前台**窗口发的 —— 窗口在后台时，输入会打到你正在用的程序上。
    ///   PostMessage 可以定向发给指定窗口，而且实测浏览器会把它当成**真实输入**
    ///   （页面里收到的事件 isTrusted = true），云游戏客户端会照常转发给服务器。
    ///
    /// 为什么默认发**按键**而不是鼠标：
    ///   实测 PostMessage 注入的 mousemove，页面里拿到的 **movementX / movementY 是 0** ——
    ///   浏览器是从真实输入设备的位移算出来的，合成的窗口消息没有这个信息。
    ///   而第三人称游戏（原神这类）基本都用「指针锁定 + 相对位移」控制视角，
    ///   客户端发出去的就是 movementX/Y —— 全是 0，等于没输入，保活白做。
    ///   按键没有"位移"这个概念，keydown 就是 keydown，客户端一定会转发。
    ///
    /// 为什么 W/S 要成对发：
    ///   只按 W 会让角色一直往前走，每分钟一次就会慢慢飘走。
    ///   点一下 W 再点一下等长的 S，角色走一小步再退回来，净位移约 0。
    ///
    /// 为什么不能用最小化替代：
    ///   窗口**最小化**之后浏览器会直接丢弃注入的输入（实测 PostMessage / SendMessage /
    ///   发子窗口，三种方式全部无效）。要"看不见但还活着"，得用 `Fx.MoveAway`
    ///   把窗口挪到屏幕外 —— 那样不最小化，注入照常生效。
    /// </summary>
    internal static class KeepAlive
    {
        /// <summary>多久补一次。云原神的挂机超时是几分钟，60 秒足够安全。</summary>
        public const int IntervalMs = 60 * 1000;

        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;

        private const int VK_W = 0x57;
        private const int VK_S = 0x53;
        private const int VK_SPACE = 0x20;
        private const int VK_CONTROL = 0x11;

        /// <summary>按键按住多久（毫秒）。够短，角色基本不动。</summary>
        private const int TapMs = 60;

        /// <summary>
        /// 给窗口发一次保活输入。返回有没有发出去。
        /// <paramref name="flip"/> 是每窗口的方向标记，鼠标微动来回切换用。
        /// </summary>
        public static bool Jiggle(IntPtr hwnd, KeepAliveInput input, ref bool flip)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
            if (!Native.IsWindowVisible(hwnd)) return false;
            if (Native.IsIconic(hwnd)) return false;   // 最小化的窗口收不到注入的输入

            switch (input)
            {
                case KeepAliveInput.KeyWS:
                    TapKey(hwnd, VK_W);
                    TapKey(hwnd, VK_S);
                    return true;

                case KeepAliveInput.KeySpace:
                    TapKey(hwnd, VK_SPACE);
                    return true;

                case KeepAliveInput.KeyCtrl:
                    TapKey(hwnd, VK_CONTROL);
                    return true;

                default:
                    return JiggleMouse(hwnd, ref flip);
            }
        }

        private static void TapKey(IntPtr hwnd, int vk)
        {
            // 扫描码给 0 也行，浏览器认 wParam 里的虚拟键
            Native.PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
            Thread.Sleep(TapMs);
            Native.PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)(1 << 30));
            Thread.Sleep(TapMs);
        }

        private static bool JiggleMouse(IntPtr hwnd, ref bool flip)
        {
            if (!Native.GetClientRect(hwnd, out Native.RECT rc)) return false;
            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w < 40 || h < 40) return false;

            int cx = w / 2;
            int cy = h / 2;

            // 在 (cx, cy) 和 (cx+1, cy) 之间来回
            int from = flip ? cx + 1 : cx;
            int to = flip ? cx : cx + 1;
            flip = !flip;

            Post(hwnd, from, cy);
            Post(hwnd, to, cy);
            return true;
        }

        private static void Post(IntPtr hwnd, int x, int y)
        {
            IntPtr lp = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
            Native.PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, lp);
            Thread.Sleep(35);
        }

        public static string Describe(KeepAliveInput input)
        {
            switch (input)
            {
                case KeepAliveInput.KeyWS: return "W / S 各点一下";
                case KeepAliveInput.KeySpace: return "空格轻点";
                case KeepAliveInput.KeyCtrl: return "Ctrl 轻点";
                default: return "鼠标微动 1 像素";
            }
        }
    }
}
