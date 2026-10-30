using System;
using System.IO;
using System.Text;

namespace AmoWinTrans
{
    /// <summary>
    /// 极简日志。默认只写错误；带 --verbose 启动时把流程也记下来，方便排查。
    /// 日志位置：%APPDATA%\AmoWinTrans\AmoWinTrans.log
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmoWinTrans");

        public static bool Verbose;

        public static string FilePath => Path.Combine(Dir, "AmoWinTrans.log");

        public static void Info(string message)
        {
            if (!Verbose) return;
            Write("INFO ", message);
        }

        /// <summary>
        /// 不管有没有开 --verbose 都写。给"用户主动触发的动作"用
        /// （快捷键按了、连按开了停了），频率很低，但出问题时是唯一的线索。
        /// </summary>
        public static void Always(string message)
        {
            Write("EVENT", message);
        }

        public static void Error(string where, Exception ex)
        {
            Write("ERROR", where + " -> " + (ex == null ? "(null)" : ex.ToString()));
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    var sb = new StringBuilder();
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                      .Append(" [").Append(level).Append("] ")
                      .Append(message)
                      .AppendLine();
                    File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void TruncateIfHuge()
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            }
            catch { }
        }
    }
}
