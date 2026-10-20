using System;
using System.IO;

namespace CodexZhLauncher
{
    /// <summary>
    /// 本地化副本的固定路径。独立成类是为了让不依赖 WPF/运行时实现的
    /// 组件（例如 CodexProcessManager、单元测试）也能复用同一套路径。
    /// </summary>
    internal static class LocalizationPaths
    {
        private static string UserProfile
        {
            get
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (String.IsNullOrWhiteSpace(profile))
                    profile = Environment.GetEnvironmentVariable("USERPROFILE");
                return profile;
            }
        }

        public static string Root
        {
            get { return Path.Combine(UserProfile, ".codex", "codex-zh"); }
        }

        public static string AppDir { get { return Path.Combine(Root, "app"); } }

        public static string Executable { get { return ResolveExecutable(AppDir); } }

        /// <summary>
        /// 商店版/官网版的 exe 名可能是 ChatGPT.exe，也可能是 Codex.exe。
        /// 所有状态、快路径、启动与完整性检查都必须用同一个解析结果。
        /// </summary>
        public static string ResolveExecutable(string appDir)
        {
            var chatGpt = Path.Combine(appDir, "ChatGPT.exe");
            if (IsNonEmptyFile(chatGpt)) return chatGpt;
            var codex = Path.Combine(appDir, "Codex.exe");
            if (IsNonEmptyFile(codex)) return codex;
            return chatGpt;
        }

        private static bool IsNonEmptyFile(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length > 0; }
            catch { return false; }
        }

        /// <summary>
        /// 与 Codex 版本无关的稳定图标目录。快捷方式和 AUMID 注册都用这里，
        /// 避免指向 WindowsApps 里带版本号的目录。
        /// </summary>
        public static string IconDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexZhLauncher", "icons");
            }
        }

        public static string AppIconPath { get { return Path.Combine(IconDirectory, "codex-app.ico"); } }
    }
}
