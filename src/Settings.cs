using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AmoWinTrans
{
    internal enum FxMethod
    {
        /// <summary>分层窗口（WS_EX_LAYERED + LWA_ALPHA）：通用、可靠，整窗（含内容）一起变透明。</summary>
        Layered = 0,
        /// <summary>亚克力混合：对自带透明背景的窗口有毛玻璃效果，普通窗口可能看不出变化。</summary>
        Acrylic = 1,
    }

    /// <summary>
    /// 简易设置文件（key=value 纯文本，避免引入 JSON 依赖）。
    /// </summary>
    internal sealed class Settings
    {
        public bool Enabled = true;
        public bool TriggerWholeWindow = false;
        public bool RememberPerApp = true;
        public bool AutoApplyRemembered = false;
        public bool HotkeysEnabled = true;
        public FxMethod Method = FxMethod.Layered;

        /// <summary>保活往窗口里发什么（见 KeepAliveInput）。</summary>
        public KeepAliveInput KeepAliveInput = KeepAliveInput.KeyWS;

        public readonly Dictionary<string, int> AppOpacity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, bool> AppTopMost = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>哪些程序开了「鼠标移出就隐藏」。</summary>
        public readonly Dictionary<string, bool> AppAutoHide = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>哪些程序开了「保活」（定时补输入防挂机踢下线）。</summary>
        public readonly Dictionary<string, bool> AppKeepAlive = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>每个程序的连按配置（序列化后的字符串），键是进程名。</summary>
        public readonly Dictionary<string, string> AutoKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>快捷键绑定：动作 id（见 TriggerIds）-> 配置。</summary>
        public readonly Dictionary<string, AutoTriggerConfig> Triggers =
            new Dictionary<string, AutoTriggerConfig>(StringComparer.Ordinal);

        private static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmoWinTrans");

        public static string FilePath => Path.Combine(Dir, "settings.ini");

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) { EnsureDefaults(s); return s; }
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();

                    switch (k)
                    {
                        case "enabled": s.Enabled = v == "1"; break;
                        case "trigger_whole_window": s.TriggerWholeWindow = v == "1"; break;
                        case "remember_per_app": s.RememberPerApp = v == "1"; break;
                        case "auto_apply_remembered": s.AutoApplyRemembered = v == "1"; break;
                        case "hotkeys_enabled": s.HotkeysEnabled = v == "1"; break;
                        case "method": s.Method = v == "acrylic" ? FxMethod.Acrylic : FxMethod.Layered; break;
                        case "keepalive_input":
                            {
                                int n;
                                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                                    && n >= 0 && n <= 3)
                                    s.KeepAliveInput = (KeepAliveInput)n;
                            }
                            break;
                        case "autotrigger":
                            {
                                // 老格式：那时候只有「连按开关」一个快捷键
                                AutoTriggerConfig t = AutoTriggerConfig.Parse(v);
                                if (t != null) s.Triggers[TriggerIds.AutoPress] = t;
                            }
                            break;
                        default:
                            if (k.StartsWith("opacity.", StringComparison.Ordinal))
                            {
                                string app = k.Substring("opacity.".Length);
                                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                                    s.AppOpacity[app] = Math.Max(10, Math.Min(100, p));
                            }
                            else if (k.StartsWith("topmost.", StringComparison.Ordinal))
                            {
                                s.AppTopMost[k.Substring("topmost.".Length)] = v == "1";
                            }
                            else if (k.StartsWith("autohide.", StringComparison.Ordinal))
                            {
                                s.AppAutoHide[k.Substring("autohide.".Length)] = v == "1";
                            }
                            else if (k.StartsWith("keepalive.", StringComparison.Ordinal))
                            {
                                s.AppKeepAlive[k.Substring("keepalive.".Length)] = v == "1";
                            }
                            else if (k.StartsWith("trigger.", StringComparison.Ordinal))
                            {
                                string id = k.Substring("trigger.".Length);
                                AutoTriggerConfig t = AutoTriggerConfig.Parse(v);
                                if (t != null) s.Triggers[id] = t;
                            }
                            else if (k.StartsWith("autokey.", StringComparison.Ordinal))
                            {
                                string app = k.Substring("autokey.".Length);
                                if (AutoKeyConfig.Parse(v) != null) s.AutoKeys[app] = v;
                            }
                            break;
                    }
                }
            }
            catch { /* 配置损坏时用默认值，不打扰用户 */ }
            EnsureDefaults(s);
            return s;
        }

        /// <summary>
        /// 补上出厂就该有的东西（老的配置文件里可能没有）。
        /// </summary>
        private static void EnsureDefaults(Settings s)
        {
            // 「挪回窗口」是**逃生快捷键**：窗口被挪到屏幕外之后看不见、鼠标也够不到，
            // 只能靠它（或托盘菜单）找回来。所以不管配置里有没有，都保证它存在且默认可用。
            if (!s.Triggers.ContainsKey(TriggerIds.MoveBack))
                s.Triggers[TriggerIds.MoveBack] = TriggerIds.DefaultMoveBack();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# 阿墨 · 窗口透明度 配置文件（改完需要重启程序）");
                sb.AppendLine("enabled=" + (Enabled ? "1" : "0"));
                sb.AppendLine("trigger_whole_window=" + (TriggerWholeWindow ? "1" : "0"));
                sb.AppendLine("remember_per_app=" + (RememberPerApp ? "1" : "0"));
                sb.AppendLine("auto_apply_remembered=" + (AutoApplyRemembered ? "1" : "0"));
                sb.AppendLine("hotkeys_enabled=" + (HotkeysEnabled ? "1" : "0"));
                sb.AppendLine("method=" + (Method == FxMethod.Acrylic ? "acrylic" : "layered"));
                sb.AppendLine("keepalive_input=" + ((int)KeepAliveInput).ToString(CultureInfo.InvariantCulture));
                foreach (var kv in Triggers) sb.AppendLine("trigger." + kv.Key + "=" + kv.Value.Serialize());
                foreach (var kv in AppOpacity) sb.AppendLine("opacity." + kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
                foreach (var kv in AppTopMost) sb.AppendLine("topmost." + kv.Key + "=" + (kv.Value ? "1" : "0"));
                foreach (var kv in AppAutoHide) sb.AppendLine("autohide." + kv.Key + "=" + (kv.Value ? "1" : "0"));
                foreach (var kv in AppKeepAlive) sb.AppendLine("keepalive." + kv.Key + "=" + (kv.Value ? "1" : "0"));
                foreach (var kv in AutoKeys) sb.AppendLine("autokey." + kv.Key + "=" + kv.Value);
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch { }
        }
    }
}
