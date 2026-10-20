using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace CodexZhLauncher
{
    /// <summary>
    /// 磁盘补丁路线（回退路线）的入口。
    ///
    /// v1.4.0 起逻辑全部在 C# 内完成：不再把 .ps1 释放到
    /// %LOCALAPPDATA%\CodexZhLauncher\scripts，也不再启动 PowerShell 子进程。
    /// 这样既去掉了杀软眼中的"脚本投放器"特征，也消除了脚本被替换的风险。
    ///
    /// 引擎分工：
    ///   AsarArchive / AsarPatcher             —— asar 读写、等长替换、per-file 哈希
    ///   EmbeddedAsarIntegrity                 —— exe 内嵌的头部哈希同步
    ///   AsarPatchPass / LocalizationRules     —— 规则扫描与候选判定
    ///   PatchState                            —— state.json 与快路径
    ///   PatchInstaller                        —— 复制 / 备份 / 补丁 / 语言 / 启动
    /// </summary>
    internal static class LocalizationRuntime
    {
        private static readonly string[] LegacyScriptNames =
        {
            "CodexZh.ps1", "AsarInPlace.ps1", "Rules.ps1"
        };

        static LocalizationRuntime()
        {
            CleanupLegacyScripts();
        }

        public static string LocalizedRoot { get { return LocalizationPaths.Root; } }

        public static string LocalizedAppDir { get { return LocalizationPaths.AppDir; } }

        public static string LocalizedExecutable { get { return LocalizationPaths.Executable; } }

        public static bool HasLocalizedCopy
        {
            get
            {
                try { return File.Exists(LocalizedExecutable); }
                catch { return false; }
            }
        }

        public static async Task<LaunchReport> LaunchAsync(
            CodexInstall install,
            string locale,
            Action<string> onProgress = null)
        {
            if (install == null || !install.IsValid)
                throw new InvalidOperationException("未检测到可用的 Codex Desktop。 ");

            var report = new LaunchReport { Mode = "patch" };

            Report(onProgress, "正在准备本地化（复制副本并打补丁）…");
            AppLog.Write("patch.install.begin locale=" + locale);

            var request = new InstallRequest
            {
                Locale = locale,
                Apply = true,
                SourceHint = install,
                Progress = onProgress
            };
            var outcome = await Task.Run(delegate { return PatchInstaller.Install(request); });
            AppLog.Write("patch.install.end exit=" + outcome.ExitCode +
                " criticalMiss=" + outcome.CriticalMiss +
                " error=" + outcome.HadError +
                " fast=" + outcome.FastSkip);

            if (outcome.CriticalMiss)
            {
                report.LocaleApplied = false;
                report.MenuApplied = false;
                report.Message = "补丁未命中：App 版本结构可能已变化，需要更新规则表。详见运行日志。";
                AppLog.Write("patch.install.criticalmiss");
                return report;
            }

            // 与旧实现一致：任何 [错误] 都视为整体失败（例如 length-mismatch）。
            // 注意不能用"本地化副本是否存在"判断 —— 官网版/便携版走原地补丁，不生成副本。
            if (outcome.HadError || outcome.ExitCode != 0)
            {
                report.Message = "补丁环节报错，详见运行日志。";
                AppLog.Write("patch.install.error");
                return report;
            }

            report.LocaleApplied = true;
            report.MenuApplied = true;
            report.LocaleDetail = "enable_i18n 默认值已改为 true；localeOverride = " + locale;
            report.MenuDetail = HasLocalizedCopy
                ? "运行于本地化副本（已脱离 MSIX），原生菜单随系统语言加载 native-menu-locales"
                : "运行于官网版/便携版安装目录，原生菜单随系统语言加载 native-menu-locales";

            Report(onProgress, "正在启动本地化副本…");
            AppLog.Write("patch.launch.begin");
            var launchExit = await Task.Run(delegate
            {
                return PatchInstaller.Launch(outcome.TargetApp, locale, onProgress);
            });
            AppLog.Write("patch.launch.end exit=" + launchExit);

            report.Started = launchExit == 0;
            report.ProcessId = 0;

            if (report.Started)
            {
                report.Message = "汉化完成：中文界面和原生菜单均已生效。";
            }
            else
            {
                report.Message = "补丁已就位，但启动失败。详见运行日志。";
                report.LocaleApplied = false;
                report.MenuApplied = false;
            }

            AppLog.Write("patch.launch.complete locale=" + report.LocaleApplied +
                " menu=" + report.MenuApplied + " message=" + report.Message);
            return report;
        }

        /// <summary>
        /// 让桌面/开始菜单那个图标**始终指向本工具当前所在位置**。
        ///
        /// 只有 `build.ps1 -DesktopShortcut` 构建出来的**变体版**才会调它。
        /// 图标带着 `--launch-zh` 跑**本工具自身**：由它按当前 Codex 版本去注入
        /// （失败自动回退磁盘补丁），所以点开永远是最新版的汉化。
        /// 绝不能指向本地化副本 —— 那会启动一份没有注入的英文 Codex。
        /// </summary>
        public static async Task<bool> RefreshShortcutsAsync(CodexInstall install, Action<string> onProgress)
        {
            try
            {
                var launcherPath = Assembly.GetExecutingAssembly().Location;
                if (String.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath)) return false;

                var workDir = Path.GetDirectoryName(launcherPath);
                var iconDir = install != null ? install.InstallLocation : null;
                var executablePath = install != null ? install.ExecutablePath : null;
                var result = await Task.Run(delegate
                {
                    return ShortcutService.Create(
                        launcherPath,
                        workDir,
                        AppInfo.ShortcutName,
                        "--launch-zh",
                        "打开中文版 Codex（运行时注入，跟随最新版）",
                        iconDir,
                        executablePath,
                        true,
                        onProgress);
                });

                var ok = String.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase) &&
                         result.Made.Count > 0;
                AppLog.Write("shortcut.refresh ok=" + ok + " target=" + launcherPath);
                if (ok)
                {
                    Report(onProgress, "桌面和开始菜单已放好「" + AppInfo.ShortcutName +
                        "」图标 —— 以后双击它就是中文版。");
                }
                return ok;
            }
            catch (Exception ex)
            {
                AppLog.Write("shortcut.refresh.failed " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 老版本会把三个 .ps1 释放到本地目录。新版本不再释放，但升级用户的机器上
        /// 可能还留着 —— 只删除这三个已知文件名，目录空了才删目录。
        /// </summary>
        private static void CleanupLegacyScripts()
        {
            try
            {
                var scriptDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexZhLauncher", "scripts");
                if (!Directory.Exists(scriptDir)) return;

                foreach (var name in LegacyScriptNames)
                {
                    var path = Path.Combine(scriptDir, name);
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch { }
                }

                try
                {
                    if (Directory.Exists(scriptDir) &&
                        Directory.GetFiles(scriptDir).Length == 0 &&
                        Directory.GetDirectories(scriptDir).Length == 0)
                        Directory.Delete(scriptDir);
                }
                catch { }
            }
            catch { }
        }

        private static void Report(Action<string> callback, string message)
        {
            if (callback == null || String.IsNullOrEmpty(message)) return;
            try { callback(message); }
            catch { }
        }
    }
}
