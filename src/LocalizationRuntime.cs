using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexZhLauncher
{
    /// <summary>
    /// 汉化运行时。
    ///
    /// 与上一代实现的根本区别：
    ///   旧实现靠 `--inspect` 连上 Electron 主进程、注入 JS 改写原生菜单。
    ///   新版 Codex 关掉了 fuse EnableNodeCliInspectArguments，这条路在新版上不可用；
    ///   注意 fuse 是编译期属性、绑 Codex 版本 —— 旧版 Codex 上仍可用，
    ///   所以不是"永久失效"，而是看客户端有没有让 Codex 升级。
    ///   同时 MSIX 清单只声明 en-US，app.getLocale() 被顶死成英文。
    ///
    /// 现方案（两条干净的路，完全不依赖注入）：
    ///   1. 界面 —— 把 app.asar 里 `enable_i18n` 的**默认值**由 false 改为 true。
    ///      改默认值而非运行时状态，一次生效、永久有效。
    ///   2. 菜单 —— 把 App 复制出 MSIX 包运行。脱离包后 PRI 资源语言限制消失，
    ///      app.getLocale() 跟随系统语言，原生菜单自动加载 native-menu-locales/zh-CN.json。
    ///
    /// 为什么是"调用 PowerShell 脚本"而不是用 C# 重写：
    ///   改 asar 必须三步齐全（改文件字节 → 更新头部 per-file 哈希 → 重算头部哈希写回
    ///   ChatGPT.exe 里的嵌入记录），漏一步 App 启动即 FATAL 崩溃。
    ///   这套逻辑已在真机上端到端验证通过；用 C# 重写等于作废全部验证。
    ///   故脚本作为嵌入资源打进本程序，运行时释放并调用，保持单一事实来源。
    /// </summary>
    internal static class LocalizationRuntime
    {
        private static readonly string[] ScriptNames = { "CodexZh.ps1", "AsarInPlace.ps1", "Rules.ps1" };

        private static readonly string ScriptDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexZhLauncher", "scripts");

        public static string LocalizedRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex", "codex-zh");
            }
        }

        public static string LocalizedAppDir { get { return Path.Combine(LocalizedRoot, "app"); } }

        public static string LocalizedExecutable
        {
            get { return Path.Combine(LocalizedAppDir, "ChatGPT.exe"); }
        }

        public static bool HasLocalizedCopy
        {
            get
            {
                try { return File.Exists(LocalizedExecutable); }
                catch { return false; }
            }
        }

        // 注：v1.2.2 起不再创建/重指任何快捷方式 —— 用户直接启动本工具即可。
        // 此前这里有个 RefreshShortcutsAsync（调 PowerShell 的 -Shortcuts 模式），已删除。

        public static async Task<LaunchReport> LaunchAsync(
            CodexInstall install,
            string locale,
            Action<string> onProgress = null)
        {
            if (install == null || !install.IsValid)
                throw new InvalidOperationException("未检测到可用的 Codex Desktop。 ");

            var report = new LaunchReport();
            var scriptDirectory = EnsureScripts();

            Report(onProgress, "正在准备本地化（复制副本并打补丁）…");
            AppLog.Write("ps.install.begin locale=" + locale);
            var installLines = await RunScriptAsync(
                scriptDirectory,
                "-Install -Quiet -Language " + locale,
                onProgress);
            AppLog.Write("ps.install.end");
            report.LocaleDetail = SummarizeInstall(installLines);

            if (ContainsAny(installLines, "关键补丁点未命中"))
            {
                report.LocaleApplied = false;
                report.MenuApplied = false;
                report.Message = "补丁未命中：App 版本结构可能已变化，需要更新规则表。详见运行日志。";
                AppLog.Write("ps.install.criticalmiss");
                return report;
            }

            // 注意：判断成败**不能**用"本地化副本是否存在"。
            // 官网版 / 便携版目录可写，走的是【原地补丁】、不生成副本，
            // 用副本判断会把它们误报成失败。
            if (ContainsAny(installLines, "[错误]"))
            {
                report.Message = "补丁环节报错，详见运行日志。";
                AppLog.Write("ps.install.error");
                return report;
            }

            report.LocaleApplied = true;
            report.MenuApplied = true;
            report.LocaleDetail = "enable_i18n 默认值已改为 true；localeOverride = " + locale;
            report.MenuDetail = HasLocalizedCopy
                ? "运行于本地化副本（已脱离 MSIX），原生菜单随系统语言加载 native-menu-locales"
                : "运行于官网版/便携版安装目录，原生菜单随系统语言加载 native-menu-locales";

            Report(onProgress, "正在启动本地化副本…");
            AppLog.Write("ps.launch.begin");
            var launchLines = await RunScriptAsync(
                scriptDirectory,
                "-Launch -Quiet -Language " + locale,
                onProgress);
            AppLog.Write("ps.launch.end");

            report.Started = ContainsAny(launchLines, "已启动");
            report.ProcessId = ReadProcessId(launchLines);

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

            AppLog.Write("ps.launch.complete locale=" + report.LocaleApplied +
                " menu=" + report.MenuApplied + " message=" + report.Message);
            return report;
        }

        /// <summary>
        /// 让桌面/开始菜单那个图标**始终指向本工具当前所在位置**。
        ///
        /// 只有 `build.ps1 -DesktopShortcut` 构建出来的**变体版**才会调它
        /// （MainWindow 里按 <see cref="AppInfo.CreateDesktopShortcut"/> 判断）。
        /// 主版本从 v1.2.2 起刻意不建任何快捷方式 —— 指向"本地化副本"的旧图标，
        /// 双击后启动的是一份**没经过注入**的 Codex，界面仍是英文，比任何提示都
        /// 更容易让人以为"汉化没生效"。
        ///
        /// 这里让图标带着 `--launch-zh` 跑**本工具自身**：由它按当前 Codex 版本去
        /// 注入（失败自动回退磁盘补丁），所以点开永远是最新版的汉化。
        /// </summary>
        public static async Task<bool> RefreshShortcutsAsync(CodexInstall install, Action<string> onProgress)
        {
            try
            {
                var launcherPath = Assembly.GetExecutingAssembly().Location;
                if (String.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath)) return false;

                var scriptDirectory = EnsureScripts();
                var arguments =
                    "-Shortcuts -Quiet" +
                    " -LauncherPath " + ProcessRunner.PowerShellLiteral(launcherPath) +
                    " -ShortcutName " + ProcessRunner.PowerShellLiteral(AppInfo.ShortcutName);
                if (install != null && !String.IsNullOrWhiteSpace(install.InstallLocation))
                {
                    // 只是"有就顺便用它取个好图标"，取不到会退回工具自身图标。
                    arguments += " -IconDir " + ProcessRunner.PowerShellLiteral(install.InstallLocation);
                }

                var lines = await RunScriptAsync(scriptDirectory, arguments, null);

                var ok = ContainsAny(lines, "[成功]");
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

        // ---- 嵌入脚本释放 ----

        private static string EnsureScripts()
        {
            Directory.CreateDirectory(ScriptDir);
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in ScriptNames)
            {
                using (var stream = assembly.GetManifestResourceStream(name))
                {
                    if (stream == null)
                        throw new InvalidOperationException("缺少嵌入资源：" + name);
                    using (var file = File.Create(Path.Combine(ScriptDir, name)))
                    {
                        stream.CopyTo(file);
                    }
                }
            }
            return ScriptDir;
        }

        // ---- 子进程执行 ----

        private static Task<List<string>> RunScriptAsync(
            string scriptDirectory, string arguments, Action<string> onProgress)
        {
            return Task.Run(delegate
            {
                var lines = new List<string>();
                var scriptPath = Path.Combine(scriptDirectory, "CodexZh.ps1").Replace("'", "''");

                // 强制子进程以 UTF-8 输出，否则中文在管道里会按 OEM 代码页乱码
                var command =
                    "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " +
                    "& '" + scriptPath + "' " + arguments;

                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                                command.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = scriptDirectory
                };

                using (var process = new Process())
                {
                    process.StartInfo = startInfo;
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (lines) { lines.Add(e.Data); }
                        // 每行都落盘：客户机器上出问题时，launcher.log 里就有完整证据，
                        // 不用再去猜是哪一步断的。
                        AppLog.Write("ps> " + e.Data);
                        Report(onProgress, e.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (String.IsNullOrWhiteSpace(e.Data)) return;
                        lock (lines) { lines.Add("[stderr] " + e.Data); }
                        AppLog.Write("ps! " + e.Data);
                        Report(onProgress, "[stderr] " + e.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    process.WaitForExit();   // 第二次等待：确保异步读取缓冲已排空
                }

                lock (lines) { return new List<string>(lines); }
            });
        }

        private static void Report(Action<string> callback, string message)
        {
            if (callback == null || String.IsNullOrEmpty(message)) return;
            try { callback(message); }
            catch { }
        }

        // ---- 输出解析 ----

        private static bool ContainsAny(List<string> lines, string needle)
        {
            lock (lines)
            {
                foreach (var line in lines)
                {
                    if (line != null && line.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
                }
            }
            return false;
        }

        private static string SummarizeInstall(List<string> lines)
        {
            lock (lines)
            {
                foreach (var line in lines)
                {
                    if (line == null) continue;
                    if (line.IndexOf("已同步", StringComparison.Ordinal) >= 0) return line.Trim();
                    if (line.IndexOf("已打过补丁", StringComparison.Ordinal) >= 0) return line.Trim();
                    if (line.IndexOf("命中", StringComparison.Ordinal) >= 0) return line.Trim();
                }
            }
            return null;
        }

        private static uint ReadProcessId(List<string> lines)
        {
            // 脚本在启动后不打印 pid；这里返回 0 不影响 UI 判断（UI 靠进程扫描而非 pid）
            return 0;
        }
    }
}
