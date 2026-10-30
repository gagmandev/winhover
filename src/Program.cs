using System;
using System.Threading;
using System.Windows.Forms;

namespace AmoWinTrans
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            foreach (string a in args)
                if (a == "--verbose" || a == "-v") Log.Verbose = true;
            if (Environment.GetEnvironmentVariable("AMOWINTRANS_VERBOSE") == "1") Log.Verbose = true;

            Log.TruncateIfHuge();
            Log.Info("=== 启动 === exe=" + (Environment.ProcessPath ?? "?") + " args=" + string.Join(" ", args));

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Error("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            };
            Application.ThreadException += (s, e) =>
            {
                Log.Error("Application.ThreadException", e.Exception);
            };

            try
            {
                using (var mutex = new Mutex(true, @"Local\AmoWinTrans.SingleInstance", out bool created))
                {
                    if (!created)
                    {
                        Log.Info("已有实例在运行，退出");
                        MessageBox.Show(
                            "「阿墨 · 窗口透明度」已经在运行了。\n\n看屏幕右下角的托盘图标（可能被折叠在 ▲ 里）。",
                            "AmoWinTrans", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    Log.Info("创建 TrayApp");
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

                    var app = new TrayApp();
                    Log.Info("TrayApp 就绪，进入消息循环");
                    Application.Run(app);
                    Log.Info("消息循环结束，退出");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Main", ex);
                MessageBox.Show("启动失败：\n" + ex.Message + "\n\n详情见：\n" + Log.FilePath,
                    "AmoWinTrans", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
