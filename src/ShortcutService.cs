using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

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
    /// 图标优先用 App 自带的 ChatGPT .ico，并复制到稳定路径，
    /// 避免 Codex 升级后带版本号的 WindowsApps 目录消失导致图标变白。
    /// </summary>
    internal static class ShortcutService
    {
        private const int ShcneAssocChanged = 0x08000000;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        public static ShortcutResult Create(string exePath, string workDir, string name,
            string arguments, string description, string iconDir, string executablePath,
            bool apply, Action<string> progress)
        {
            var targets = new List<string>();
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!String.IsNullOrEmpty(desktop))
                targets.Add(Path.Combine(desktop, name + ".lnk"));
            if (!String.IsNullOrEmpty(programs))
                targets.Add(Path.Combine(programs, name + ".lnk"));
            return CreateAtTargets(exePath, workDir, name, arguments, description,
                iconDir, executablePath, apply, progress, targets);
        }

        /// <summary>
        /// 测试用入口：把快捷方式创建到显式给定的路径，不碰真实桌面/开始菜单。
        /// </summary>
        public static ShortcutResult CreateAtTargets(string exePath, string workDir, string name,
            string arguments, string description, string iconDir, string executablePath,
            bool apply, Action<string> progress, List<string> targets)
        {
            var result = new ShortcutResult();
            if (targets != null) result.Targets.AddRange(targets);

            if (!apply)
            {
                result.Status = "dry";
                result.Icon = ResolveIcon(exePath, workDir, iconDir, executablePath);
                return result;
            }

            if (arguments == null || arguments.IndexOf("--launch-zh", StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (progress != null)
                    progress("快捷方式参数是「" + arguments + "」而不是 --launch-zh —— 指向副本的图标会打开一份英文 Codex。请确认这是你要的。");
            }

            var iconFile = ResolveIcon(exePath, workDir, iconDir, executablePath);
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

            if (result.Made.Count > 0)
            {
                try { SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero); }
                catch { }
            }

            result.Status = "ok";
            return result;
        }

        /// <summary>
        /// 找 App 自带的 ChatGPT 图标。顺序：
        ///   1. 已经复制到稳定路径的 codex-app.ico（AppIconRegistration 放的）
        ///   2. IconDir 本身 / IconDir\app（商店包的 InstallLocation 是包根）
        ///   3. install.ExecutablePath 所在目录
        ///   4. 工具自身目录
        /// 找到后复制到稳定路径，避免 WindowsApps 带版本号目录消失后图标变白。
        /// </summary>
        private static string ResolveIcon(string exePath, string workDir, string iconDir, string executablePath)
        {
            var stable = LocalizationPaths.AppIconPath;
            if (!String.IsNullOrWhiteSpace(stable) && File.Exists(stable)) return stable;

            var hosts = new List<string>();
            if (!String.IsNullOrWhiteSpace(iconDir))
            {
                hosts.Add(iconDir);
                hosts.Add(Path.Combine(iconDir, "app"));
            }
            if (!String.IsNullOrWhiteSpace(executablePath))
            {
                var exeDir = File.Exists(executablePath)
                    ? Path.GetDirectoryName(executablePath)
                    : executablePath;
                if (!String.IsNullOrWhiteSpace(exeDir)) hosts.Add(exeDir);
            }
            if (!String.IsNullOrWhiteSpace(workDir)) hosts.Add(workDir);

            foreach (var host in hosts)
            {
                foreach (var rel in new[] { @"resources\chatgpt-app-light.ico", @"resources\icon-chatgpt.ico" })
                {
                    var candidate = Path.Combine(host, rel);
                    if (!File.Exists(candidate)) continue;

                    try
                    {
                        Directory.CreateDirectory(LocalizationPaths.IconDirectory);
                        File.Copy(candidate, stable, true);
                        return stable;
                    }
                    catch
                    {
                        return candidate;
                    }
                }
            }

            return exePath;
        }

        private static void SetProperty(Type type, object instance, string name, string value)
        {
            type.InvokeMember(name, BindingFlags.SetProperty, null, instance, new object[] { value });
        }
    }
}
