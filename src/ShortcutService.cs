using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CodexZhLauncher
{
    internal sealed class ShortcutResult
    {
        public string Status;   // dry | ok
        public string Icon;
        public List<string> Targets = new List<string>();
        public List<string> Made = new List<string>();
    }

    /// <summary>
    /// 与 CodexZh.ps1 的 New-AppShortcuts 对应。
    ///
    /// 目标必须是**工具自身** + --launch-zh，绝不能指向本地化副本
    /// （指向副本会启动一份没有注入的英文 Codex）。
    /// </summary>
    internal static class ShortcutService
    {
        public static ShortcutResult Create(string exePath, string workDir, string name,
            string arguments, string description, string iconDir, bool apply,
            Action<string> progress)
        {
            var targets = new List<string>();
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!String.IsNullOrEmpty(desktop))
                targets.Add(Path.Combine(desktop, name + ".lnk"));
            if (!String.IsNullOrEmpty(programs))
                targets.Add(Path.Combine(programs, name + ".lnk"));
            return CreateAtTargets(exePath, workDir, name, arguments, description,
                iconDir, apply, progress, targets);
        }

        /// <summary>
        /// 测试用入口：把快捷方式创建到显式给定的路径，不碰真实桌面/开始菜单。
        /// </summary>
        public static ShortcutResult CreateAtTargets(string exePath, string workDir, string name,
            string arguments, string description, string iconDir, bool apply,
            Action<string> progress, List<string> targets)
        {
            var result = new ShortcutResult();
            if (targets != null) result.Targets.AddRange(targets);

            if (!apply)
            {
                result.Status = "dry";
                return result;
            }

            if (arguments == null || arguments.IndexOf("--launch-zh", StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (progress != null)
                    progress("快捷方式参数是「" + arguments + "」而不是 --launch-zh —— 指向副本的图标会打开一份英文 Codex。请确认这是你要的。");
            }

            var iconFile = exePath;
            var hosts = new List<string>();
            if (!String.IsNullOrWhiteSpace(iconDir)) hosts.Add(iconDir);
            if (!String.IsNullOrWhiteSpace(workDir)) hosts.Add(workDir);
            foreach (var host in hosts)
            {
                string found = null;
                foreach (var rel in new[] { @"resources\chatgpt-app-light.ico", @"resources\icon-chatgpt.ico" })
                {
                    var candidate = Path.Combine(host, rel);
                    if (File.Exists(candidate)) { found = candidate; break; }
                }
                if (found != null) { iconFile = found; break; }
            }
            result.Icon = iconFile;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用");
            var shell = Activator.CreateInstance(shellType);

            foreach (var target in result.Targets)
            {
                try
                {
                    var dir = Path.GetDirectoryName(target);
                    if (!String.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var shortcut = shellType.InvokeMember("CreateShortcut",
                        BindingFlags.InvokeMethod, null, shell, new object[] { target });
                    var shortcutType = shortcut.GetType();
                    SetProperty(shortcutType, shortcut, "TargetPath", exePath);
                    SetProperty(shortcutType, shortcut, "WorkingDirectory", workDir);
                    SetProperty(shortcutType, shortcut, "IconLocation", iconFile + ",0");
                    SetProperty(shortcutType, shortcut, "Arguments", arguments);
                    SetProperty(shortcutType, shortcut, "Description", description);
                    shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    result.Made.Add(target);
                }
                catch (Exception ex)
                {
                    if (progress != null)
                        progress("快捷方式创建失败：" + target + " —— " + ex.Message);
                }
            }

            result.Status = "ok";
            return result;
        }

        private static void SetProperty(Type type, object instance, string name, string value)
        {
            type.InvokeMember(name, BindingFlags.SetProperty, null, instance, new object[] { value });
        }
    }
}
