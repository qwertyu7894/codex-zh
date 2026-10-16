using System;
using System.IO;
using Microsoft.Win32;

namespace CodexZhLauncher
{
    /// <summary>
    /// 修未打包启动时"任务栏图标变成一张白纸"的问题。
    ///
    /// 为什么会白：Codex 在**带 MSIX 包标识**运行时，Windows 用包自己声明的
    /// AUMID（<c>OpenAI.Codex_2p2nqsd0c76g0!App</c>）去找图标，一切正常。
    /// 而我们的汉化必须**未打包启动**（直接跑 exe，否则开不了调试端口、注入不进去），
    /// 这时 App 会声明它自己的 AUMID（见下），而这个 AUMID **从没注册过图标**
    /// —— 任务栏和开始菜单只好画一张白纸。
    ///
    /// 修法是两条，都很小：
    ///   1. 把 App 自带的多尺寸 .ico 拷到**稳定路径**（不能直接指 WindowsApps 里的：
    ///      Codex 一升级，那个带版本号的目录就没了，图标又会变白）
    ///   2. 在 HKCU 下给该 AUMID 注册 <c>DisplayName</c> 与 <c>IconUri</c>
    ///
    /// ⚠️ 这是本项目里**唯一**写注册表的地方，且只写 HKCU 下一个键：
    ///    不给系统或别的用户装任何东西，删掉即恢复原样。
    ///    README 与使用说明里的"不写注册表"写的就是这个例外。
    /// </summary>
    internal static class AppIconRegistration
    {
        /// <summary>
        /// App 自己声明的 AUMID（未打包运行时任务栏用的就是它）。
        ///
        /// 这是**实测值**，不是猜的：把 .lnk 解开能看到这个串，任务栏查图标也查它。
        /// 如果哪天 Codex 换了 AUMID，这里就失效了 —— 表现是白纸又回来，
        /// 排查办法：新建一个指向 ChatGPT.exe 的快捷方式，看它的 AppID 是什么。
        /// </summary>
        private const string AppUserModelId = "com.openai.codex";

        private const string DisplayName = "ChatGPT";
        private const string IconFileName = "codex-app.ico";

        private static readonly object Sync = new object();
        private static bool done;

        /// <summary>
        /// 幂等：同一次运行里只做一次，注册表值已经对得上就什么都不写。
        /// **绝不抛异常** —— 图标不好看是小事，不能因为它把启动流程搞挂。
        /// </summary>
        public static void Ensure(CodexInstall install)
        {
            lock (Sync)
            {
                if (done) return;
                done = true;
                try
                {
                    var iconPath = StageIcon(install);
                    if (String.IsNullOrWhiteSpace(iconPath)) return;
                    var changed = RegisterIcon(iconPath);
                    AppLog.Write("icon.register ok=" + changed + " aumid=" + AppUserModelId + " icon=" + iconPath);
                }
                catch (Exception ex)
                {
                    AppLog.Write("icon.register.failed " + ex.Message);
                }
            }
        }

        /// <summary>图标放哪 —— 必须是与 Codex 版本无关的稳定路径。</summary>
        internal static string IconDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexZhLauncher",
                    "icons");
            }
        }

        internal static string IconPath
        {
            get { return Path.Combine(IconDirectory, IconFileName); }
        }

        /// <summary>
        /// 从安装目录里挑一个高质量 .ico 拷到稳定路径。返回目标路径；找不到源就返回 null。
        ///
        /// 优先 App 自带的 .ico：exe 内嵌图标尺寸不全会发虚（实测 16/32/48 那种
        /// 混合位图，缩到任务栏那几档就糊）。
        /// </summary>
        private static string StageIcon(CodexInstall install)
        {
            var source = FindSourceIcon(install);
            if (source == null) return null;

            var target = IconPath;
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length)
            {
                return target;   // 已经是同一份了，不重复拷
            }

            Directory.CreateDirectory(IconDirectory);
            File.Copy(source, target, true);
            return target;
        }

        private static string FindSourceIcon(CodexInstall install)
        {
            if (install == null) return null;

            foreach (var dir in new[] { install.InstallLocation, install.ExecutablePath })
            {
                if (String.IsNullOrWhiteSpace(dir)) continue;
                var root = File.Exists(dir) ? Path.GetDirectoryName(dir) : dir;
                foreach (var name in new[] { "chatgpt-app-light.ico", "icon-chatgpt.ico" })
                {
                    var candidate = Path.Combine(root, "resources", name);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            // 退而求其次：exe 自身（内嵌图标）。
            if (!String.IsNullOrWhiteSpace(install.ExecutablePath) && File.Exists(install.ExecutablePath))
                return install.ExecutablePath;

            return null;
        }

        /// <summary>写 HKCU 下的 AUMID 键。值已经正确就返回 false（不碰注册表）。</summary>
        private static bool RegisterIcon(string iconPath)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\" + AppUserModelId))
            {
                if (key == null) return false;

                var currentIcon = key.GetValue("IconUri") as string;
                var currentName = key.GetValue("DisplayName") as string;
                if (String.Equals(currentIcon, iconPath, StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(currentName, DisplayName, StringComparison.Ordinal))
                {
                    return false;
                }

                key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
                key.SetValue("IconUri", iconPath, RegistryValueKind.String);
                return true;
            }
        }

        internal static string SelfTest()
        {
            if (AppUserModelId != "com.openai.codex")
                throw new InvalidOperationException("图标自检：AUMID 常量被改过了，请先确认新值仍是 App 声明的那个。");
            if (!IconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
                IconPath.IndexOf("icons", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("图标自检：稳定路径不对。");
            // 路径必须落在 LOCALAPPDATA 下 —— 不能指回带版本号的 WindowsApps 目录。
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (IconPath.IndexOf(local, StringComparison.OrdinalIgnoreCase) != 0)
                throw new InvalidOperationException("图标自检：图标没有落在 LOCALAPPDATA 下。");
            return "icon-registration=ok";
        }
    }
}
