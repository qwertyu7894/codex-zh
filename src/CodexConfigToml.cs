using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexZhLauncher
{
    internal sealed class LocaleOverrideResult
    {
        public bool Changed;
        public string Reason;   // already-set | updated | inserted-into-desktop | appended-desktop-section

        public LocaleOverrideResult(bool changed, string reason)
        {
            Changed = changed;
            Reason = reason;
        }
    }

    /// <summary>
    /// 与 CodexZh.ps1 的 Set-LocaleOverride 对应。
    /// 这是唯一允许长度变化的改动 —— 改的是配置，不是 asar。
    ///
    /// 保持旧实现行为：UTF-8 无 BOM、CRLF、末尾补换行；其它段落与键保留，
    /// 但原始换行风格会被统一。与 App 自身写 LF 的差异是独立议题，本次不动。
    /// </summary>
    internal static class CodexConfigToml
    {
        private static readonly Regex DesktopSection = new Regex(@"^\s*\[desktop\]\s*$");
        private static readonly Regex AnySection = new Regex(@"^\s*\[");
        private static readonly Regex LocaleKey = new Regex(@"^\s*localeOverride\s*=");

        public static LocaleOverrideResult SetLocaleOverride(string path, string locale, bool apply)
        {
            var line = "localeOverride = \"" + locale + "\"";
            var lines = new List<string>();
            if (File.Exists(path))
            {
                lines.AddRange(File.ReadAllLines(path, Encoding.UTF8));
            }

            int sectionStart = -1;
            int sectionEnd = lines.Count;
            int existing = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                if (DesktopSection.IsMatch(lines[i]))
                {
                    sectionStart = i;
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        if (AnySection.IsMatch(lines[j])) { sectionEnd = j; break; }
                    }
                    break;
                }
            }

            if (sectionStart >= 0)
            {
                for (int i = sectionStart + 1; i < sectionEnd; i++)
                {
                    if (LocaleKey.IsMatch(lines[i])) { existing = i; break; }
                }
            }

            if (existing >= 0)
            {
                if (String.Equals(lines[existing], line, StringComparison.OrdinalIgnoreCase))
                    return new LocaleOverrideResult(false, "already-set");
                if (apply)
                {
                    lines[existing] = line;
                    WriteLines(path, lines);
                }
                return new LocaleOverrideResult(true, "updated");
            }

            if (sectionStart >= 0)
            {
                if (apply)
                {
                    var output = new List<string>();
                    for (int i = 0; i <= sectionStart; i++) output.Add(lines[i]);
                    output.Add(line);
                    for (int i = sectionStart + 1; i < lines.Count; i++) output.Add(lines[i]);
                    WriteLines(path, output);
                }
                return new LocaleOverrideResult(true, "inserted-into-desktop");
            }

            if (apply)
            {
                var output = new List<string>(lines);
                output.Add(String.Empty);
                output.Add("[desktop]");
                output.Add(line);
                WriteLines(path, output);
            }
            return new LocaleOverrideResult(true, "appended-desktop-section");
        }

        private static void WriteLines(string path, List<string> lines)
        {
            var dir = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }
    }
}
