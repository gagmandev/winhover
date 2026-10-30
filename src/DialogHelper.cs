using System;
using System.Windows.Forms;

namespace AmoWinTrans
{
    /// <summary>
    /// 托盘程序在弹对话框时通常拿不到前台权限，新窗口有可能被已经打开的窗口盖住
    /// （尤其是从一个模态对话框里再弹一个的时候）。
    ///
    /// 这种时候 SetForegroundWindow / Activate 都不管用，因为它们同样需要前台权限。
    /// 「TopMost 闪一下」是最可靠的办法：先置顶再取消置顶，窗口会被放到
    /// 普通窗口层的最上面。
    /// </summary>
    internal static class DialogHelper
    {
        public static void ForceToFront(Form f)
        {
            if (f == null || !f.Visible) return;

            try
            {
                bool wasTopMost = f.TopMost;
                f.TopMost = true;
                f.TopMost = wasTopMost;
                f.BringToFront();
                f.Activate();
            }
            catch
            {
                // 提不到最前也不该影响功能，忽略
            }
        }
    }
}
