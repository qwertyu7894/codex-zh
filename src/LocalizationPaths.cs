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
        public static string Root
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex", "codex-zh");
            }
        }

        public static string AppDir { get { return Path.Combine(Root, "app"); } }

        public static string Executable { get { return Path.Combine(AppDir, "ChatGPT.exe"); } }
    }
}
