using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodexZhLauncher
{
    /// <summary>
    /// 极小的持久化设置：只记**用户做过的选择**，不存路径、账号或环境信息。
    ///
    /// 存成 <c>%LOCALAPPDATA%\CodexZhLauncher\settings.ini</c> 的 key=value 文本。
    /// 不用 JSON —— 用不上结构，纯文本在 C# 5 下更不容易写出坑。
    ///
    /// 任何读写失败都吞掉：设置丢了最坏就是多问一次，不该因此让工具跑不起来。
    /// 删掉这个文件等于恢复默认（会重新询问）。
    /// </summary>
    internal static class AppSettings
    {
        // 旧本地化副本的清理决定：
        //   declined —— 用户点过"否"，以后不再询问
        //   approved —— 用户点过"是"但当时没删掉，下次启动静默重试
        public const string CopyCleanupKey = "copyCleanup";
        public const string CopyCleanupDeclined = "declined";
        public const string CopyCleanupApproved = "approved";

        private static readonly object Sync = new object();

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexZhLauncher",
            "settings.ini");

        private static Dictionary<string, string> cache;

        public static string Get(string key)
        {
            if (String.IsNullOrWhiteSpace(key)) return null;
            lock (Sync)
            {
                EnsureLoaded();
                string value;
                if (cache.TryGetValue(key, out value)) return value;
                return null;
            }
        }

        public static void Set(string key, string value)
        {
            if (String.IsNullOrWhiteSpace(key)) return;
            lock (Sync)
            {
                EnsureLoaded();
                cache[key] = value ?? String.Empty;
                Save();
            }
        }

        private static void EnsureLoaded()
        {
            if (cache != null) return;
            cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var line = raw == null ? String.Empty : raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    var separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    cache[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("settings.load.failed " + ex.Message);
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var builder = new StringBuilder();
                builder.AppendLine("# Codex 汉化增强工具记下的用户选择。删掉本文件 = 恢复默认（会重新询问）。");
                foreach (var pair in cache)
                    builder.AppendLine(pair.Key + "=" + pair.Value);
                File.WriteAllText(FilePath, builder.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                AppLog.Write("settings.save.failed " + ex.Message);
            }
        }
    }
}
