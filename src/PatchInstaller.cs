using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace CodexZhLauncher
{
    internal sealed class InstallRequest
    {
        public string TargetDir;
        public string Locale = "zh-CN";
        public bool Apply = true;
        public string RootDir;
        public string ConfigTomlPath;
        public CodexInstall SourceHint;
        public Action<string> Progress;
    }

    internal sealed class InstallOutcome
    {
        public int ExitCode;
        public string TargetApp;
        public string Mode;
        public string Message;
        public bool CriticalMiss;
        public bool HadError;
        public bool FastSkip;
    }

    /// <summary>
    /// 与 CodexZh.ps1 的 Invoke-Install / Invoke-Launch 对应。
    /// 顺序严格保持：探测 → 复制决策 → 备份 → 快路径 → 补丁 → 嵌入完整性
    /// → localeOverride → state.json。
    /// </summary>
    internal static class PatchInstaller
    {
        public static InstallOutcome Install(InstallRequest request)
        {
            var outcome = new InstallOutcome();
            try
            {
                return InstallCore(request, outcome);
            }
            catch (Exception ex)
            {
                outcome.ExitCode = 1;
                outcome.HadError = true;
                outcome.Message = ex.Message;
                Report(request.Progress, "[错误] " + ex.Message);
                return outcome;
            }
        }

        private static InstallOutcome InstallCore(InstallRequest request, InstallOutcome outcome)
        {
            var root = String.IsNullOrWhiteSpace(request.RootDir) ? DefaultRootDir() : Path.GetFullPath(request.RootDir);
            var statePath = Path.Combine(root, "state.json");
            var configToml = String.IsNullOrWhiteSpace(request.ConfigTomlPath)
                ? DefaultConfigTomlPath()
                : Path.GetFullPath(request.ConfigTomlPath);
            var localizedAppDir = Path.Combine(root, "app");

            var statePrev = PatchState.Load(statePath);

            // PowerShell 的 -Install 自己调用 Find-CodexInstall，不使用 C# 传进来的
            // CodexInstall。这里保留同样的行为，但允许把"非副本"的探测结果当提示，
            // 以便手动指定的便携版也能正确工作。
            var found = CodexInstallLocator.FromHint(request.SourceHint, localizedAppDir);
            if (found == null) found = CodexInstallLocator.Find(request.TargetDir);

            // 源安装可能已经卸载，但本地化副本本身是独立目录、仍然可用。
            // 旧 PS CLI 在 -Install 失败后仍会 -Launch 这份副本；C# 不能因为源
            // 探测失败就把一份完好的副本判死刑。
            bool existingCopyComplete =
                FindMissingCopyFile(localizedAppDir) == null &&
                File.Exists(Path.Combine(localizedAppDir, "resources", "app.asar"));

            if (found == null && String.IsNullOrWhiteSpace(request.TargetDir) && !existingCopyComplete)
            {
                outcome.ExitCode = 1;
                outcome.Message = "未检测到 Codex，且本地化副本不完整。请先安装 Codex，或删除 " +
                    localizedAppDir + " 后重试。";
                Report(request.Progress, "[错误] " + outcome.Message);
                return outcome;
            }

            var ruleSignature = LocalizationRules.Signature();
            var nowVersion = found != null ? CodexInstallLocator.GetVersion(found.AppDir) : "unknown";

            string targetApp;
            string mode;

            if (!String.IsNullOrWhiteSpace(request.TargetDir))
            {
                if (!Directory.Exists(request.TargetDir)) throw new DirectoryNotFoundException("目标目录不存在：" + request.TargetDir);
                targetApp = Path.GetFullPath(request.TargetDir);
                mode = "explicit";
            }
            else if (found == null)
            {
                // 源安装没了，但副本完整 —— 直接在这份副本上做快路径/全量复查。
                targetApp = localizedAppDir;
                mode = "store-copy";
                if (statePrev != null && !String.IsNullOrWhiteSpace(statePrev.Version) &&
                    !String.Equals(statePrev.Version, "unknown", StringComparison.OrdinalIgnoreCase))
                    nowVersion = statePrev.Version;
                else
                    nowVersion = CodexInstallLocator.GetVersion(localizedAppDir);
                Report(request.Progress, "[警告] 未检测到源安装，使用现有本地化副本继续。");
            }
            else if (String.Equals(found.Kind, "store", StringComparison.OrdinalIgnoreCase) ||
                     !FileSystemUtils.IsDirectoryWritable(found.AppDir))
            {
                targetApp = localizedAppDir;
                mode = "store-copy";
                if (!request.Apply)
                {
                    Report(request.Progress, "[信息] DryRun：将把 " + found.AppDir + " 复制到 " + targetApp);
                }
                else
                {
                    HandleStoreCopy(request, found, targetApp, statePrev, nowVersion);
                }
            }
            else
            {
                targetApp = found.AppDir;
                mode = "direct";
            }

            outcome.TargetApp = targetApp;
            outcome.Mode = mode;

            if (!request.Apply && String.Equals(mode, "store-copy", StringComparison.OrdinalIgnoreCase) &&
                found != null)
            {
                Report(request.Progress, "[信息] （DryRun 下副本尚未复制，补丁环节改为对源 asar 做只读检测）");
                var sourceAsar = Path.Combine(found.AppDir, "resources", "app.asar");
                AsarPatchPass.Run(sourceAsar, false, request.Progress);
                outcome.ExitCode = 0;
                return outcome;
            }

            var asarPath = Path.Combine(targetApp, "resources", "app.asar");
            if (!File.Exists(asarPath))
            {
                outcome.ExitCode = 1;
                outcome.Message = "目标 asar 不存在：" + asarPath;
                Report(request.Progress, "[错误] " + outcome.Message);
                return outcome;
            }

            // direct 模式改的是用户原本的 app.asar，没有副本可退，必须先备份。
            if (String.Equals(mode, "direct", StringComparison.OrdinalIgnoreCase) && request.Apply)
            {
                var backup = asarPath + ".bak";
                if (!File.Exists(backup))
                {
                    Report(request.Progress, "[信息] 为原安装目录留一份备份：" + backup);
                    try
                    {
                        File.Copy(asarPath, backup, true);
                    }
                    catch (Exception ex)
                    {
                        outcome.ExitCode = 1;
                        outcome.Message = "备份失败，为安全起见中止：" + ex.Message;
                        Report(request.Progress, "[错误] " + outcome.Message);
                        return outcome;
                    }
                }
            }

            Report(request.Progress, String.Empty);
            Report(request.Progress, "[信息] --- 应用补丁 ---");

            bool fastSkip = false;
            PatchPassResult patch = null;

            if (request.Apply && PatchState.IsStillValid(statePrev, targetApp, nowVersion, ruleSignature))
            {
                fastSkip = true;
                outcome.FastSkip = true;
                Report(request.Progress, "[成功] 副本自上次打补丁后未变化（版本 " + nowVersion + "），跳过全量扫描。");
                foreach (var rule in LocalizationRules.All)
                {
                    bool hit = false;
                    foreach (var record in statePrev.Patch.Entries)
                    {
                        if (record != null && String.Equals(record.Id, rule.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            hit = true;
                            break;
                        }
                    }
                    Report(request.Progress, hit
                        ? "[信息]     复查通过（已打过补丁）：" + rule.Desc
                        : "[信息]     与上次相同（未命中）：" + rule.Desc);
                }
            }

            if (!fastSkip)
            {
                patch = AsarPatchPass.Run(asarPath, request.Apply, request.Progress);
                outcome.CriticalMiss = patch.CriticalMiss > 0;
                outcome.HadError = patch.Errors > 0;
                if (outcome.CriticalMiss)
                {
                    Report(request.Progress,
                        "[警告] 关键补丁点未命中：界面很可能仍是英文。App 版本结构已变化，需要更新规则表。");
                }
            }

            Report(request.Progress, String.Empty);
            Report(request.Progress, "[信息] --- 嵌入式完整性哈希 ---");

            // 即使走了快路径也要做这一步：开销只有读 5.8 MB 头部 + 扫几 MB exe，
            // 但能保证“state 说没变、exe 里的记录却是旧值”这种情况不会漏掉。
            // 记录找不到时不能继续 —— 否则 App 启动会 FATAL，而工具还报成功。
            var embedded = EmbeddedAsarIntegrity.Update(targetApp, asarPath, request.Apply);
            if (String.Equals(embedded.Status, "updated", StringComparison.OrdinalIgnoreCase))
            {
                Report(request.Progress, "[成功] 已同步 " + Path.GetFileName(embedded.File));
                Report(request.Progress, "[信息]   " + embedded.Old);
                Report(request.Progress, "[信息]   -> " + embedded.New);
            }
            else if (String.Equals(embedded.Status, "already-ok", StringComparison.OrdinalIgnoreCase))
            {
                Report(request.Progress, "[信息] 已是最新 " + Path.GetFileName(embedded.File));
            }
            else if (String.Equals(embedded.Status, "needs-update", StringComparison.OrdinalIgnoreCase))
            {
                Report(request.Progress, "[警告] 待更新 " + Path.GetFileName(embedded.File) + "（DryRun 未写入）");
            }
            else
            {
                outcome.HadError = true;
                outcome.Message = "未找到 " + Path.GetFileName(LocalizationPaths.ResolveExecutable(targetApp)) +
                    " 里的嵌入式 asar 完整性记录；为避免 App 启动崩溃，已中止。请更新工具。";
                Report(request.Progress, "[错误] " + outcome.Message);
            }

            Report(request.Progress, String.Empty);
            Report(request.Progress, "[信息] --- 语言设置 ---");
            var locale = CodexConfigToml.SetLocaleOverride(configToml, request.Locale, request.Apply);
            if (locale.Changed)
                Report(request.Progress, "[成功] localeOverride -> " + request.Locale + "（" + locale.Reason + "）");
            else
                Report(request.Progress, "[信息] localeOverride 已是 " + request.Locale);

            // 失败时不写“可被快路径信任”的 state：否则下一次会 fastSkip 并报成功。
            if (request.Apply && !fastSkip && !outcome.HadError)
            {
                var entries = new List<PatchEntryRecord>();
                foreach (var ruleResult in patch.Results)
                {
                    foreach (var record in ruleResult.Entries)
                    {
                        if (record != null) entries.Add(record);
                    }
                }

                var asarInfo = new FileInfo(asarPath);
                var exeInfo = new FileInfo(LocalizationPaths.ResolveExecutable(targetApp));

                var state = new LocalizationState
                {
                    SchemaVersion = 2,
                    Mode = mode,
                    SourcePath = found != null ? found.AppDir : null,
                    InstallPath = targetApp,
                    Version = nowVersion,
                    Language = request.Locale,
                    PatchedAt = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                    Patch = new PatchRecord
                    {
                        InstallPath = targetApp,
                        RulesSignature = ruleSignature,
                        CriticalMiss = patch.CriticalMiss,
                        DataOffset = patch.DataOffset,
                        AsarSize = asarInfo.Length,
                        AsarTicks = asarInfo.LastWriteTimeUtc.Ticks,
                        ExeSize = exeInfo.Length,
                        ExeTicks = exeInfo.LastWriteTimeUtc.Ticks,
                        Entries = entries
                    }
                };
                PatchState.Save(statePath, state);
            }

            // 硬错误（副本不完整、嵌入完整性 miss、length-mismatch 等）必须体现在
            // 退出码上；调用方还会看 HadError，但命令行/测试也该看到失败。
            outcome.ExitCode = outcome.HadError ? 1 : 0;
            return outcome;
        }

        // 启动本地化副本所需的 Chromium / Electron 关键文件。
        // 缺任何一个都说明副本不完整（复制中断、杀软隔离、用户误删），必须重拷。
        private static readonly string[] RequiredCopyFiles =
        {
            "chrome_elf.dll",
            "chrome.dll",
            "resources.pak",
            "icudtl.dat",
            "v8_context_snapshot.bin",
            "snapshot_blob.bin",
            Path.Combine("resources", "app.asar")
        };

        private static void HandleStoreCopy(InstallRequest request, DiscoveredInstall found,
            string targetApp, LocalizationState statePrev, string nowVersion)
        {
            var copyAsar = Path.Combine(targetApp, "resources", "app.asar");
            bool needCopy = false;

            if (!File.Exists(copyAsar))
            {
                needCopy = true;
                Report(request.Progress, "[信息] 首次安装，需要复制副本。");
            }
            else
            {
                var missing = FindMissingCopyFile(targetApp);
                if (missing != null)
                {
                    // 旧副本可能是上次复制中断留下的半成品（客户机真实案例：
                    // chrome_elf.dll 缺失 -> ChatGPT.exe 启动时报“找不到模块”）。
                    // 只要关键文件不齐就强制重拷，不能因为它“看起来存在”而跳过。
                    needCopy = true;
                    Report(request.Progress, "[警告] 本地化副本不完整（缺少 " + missing + "），需要重新复制。");
                }
                else
                {
                    // 只有 state 明确属于这个目标副本时才信任版本号。
                    // state 被删/写坏/来自 direct 模式时，不能把旧副本当成“最新”。
                    bool trustState = statePrev != null && statePrev.Patch != null &&
                        String.Equals(statePrev.Mode, "store-copy", StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(statePrev.InstallPath, targetApp, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(statePrev.Patch.InstallPath, targetApp, StringComparison.OrdinalIgnoreCase);
                    var prevVersion = trustState ? statePrev.Version : null;

                    if (!trustState)
                    {
                        needCopy = true;
                        Report(request.Progress, "[警告] state.json 缺失或与当前副本不匹配，无法确认版本；将重新复制。");
                    }
                    else if (String.IsNullOrEmpty(prevVersion))
                    {
                        needCopy = true;
                        Report(request.Progress, "[警告] state.json 里没有版本号，无法确认副本版本；将重新复制。");
                    }
                    else if (!String.IsNullOrEmpty(nowVersion) &&
                             !String.Equals(prevVersion, nowVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        Report(request.Progress, "[警告] 检测到 Codex 版本变化：" + prevVersion + " -> " + nowVersion);
                        Report(request.Progress, "[信息] 旧的本地化副本已过期，需要重新复制。");
                        needCopy = true;
                    }
                    else
                    {
                        Report(request.Progress, "[信息] 副本已是最新（" + nowVersion + "），跳过复制。");
                    }
                }
            }

            if (!needCopy) return;

            // 先复制到临时目录，成功后再替换旧副本。
            // 这样即使复制中途失败/被杀软打断，也不会留下一个“半成品 app”被下次误用。
            var tempDir = targetApp + ".copying-" + Guid.NewGuid().ToString("N");
            Report(request.Progress, "[信息] 复制 Codex 到临时目录（约 1-2 GB，可能需要一会）...");
            var stopwatch = Stopwatch.StartNew();
            try
            {
                FileSystemUtils.CopyTree(found.AppDir, tempDir);
                VerifyCopiedApp(found.AppDir, tempDir);
            }
            catch
            {
                try { if (Directory.Exists(tempDir)) FileSystemUtils.DeleteTreeForce(tempDir); }
                catch { }
                throw;
            }
            stopwatch.Stop();

            if (Directory.Exists(targetApp))
            {
                Report(request.Progress, "[信息] 替换旧的本地化副本…");
                try
                {
                    FileSystemUtils.DeleteTreeForce(targetApp);
                }
                catch (Exception ex)
                {
                    try { if (Directory.Exists(tempDir)) FileSystemUtils.DeleteTreeForce(tempDir); }
                    catch { }
                    throw new IOException("无法删除旧副本：" + ex.Message + "。请确认 Codex 已完全退出后重试。", ex);
                }
            }

            try
            {
                Directory.Move(tempDir, targetApp);
            }
            catch (Exception ex)
            {
                try { if (Directory.Exists(tempDir)) FileSystemUtils.DeleteTreeForce(tempDir); }
                catch { }
                throw new IOException("无法启用新副本：" + ex.Message + "。请确认 Codex 已完全退出后重试。", ex);
            }

            Report(request.Progress, "[成功] 复制完成，耗时 " +
                String.Format(CultureInfo.CurrentCulture, "{0:N0}", stopwatch.Elapsed.TotalSeconds) + " 秒");
        }

        private static string FindMissingCopyFile(string appDir)
        {
            var chatGpt = Path.Combine(appDir, "ChatGPT.exe");
            var codex = Path.Combine(appDir, "Codex.exe");
            if (!IsNonEmptyFile(chatGpt) && !IsNonEmptyFile(codex))
                return "ChatGPT.exe/Codex.exe";

            foreach (var rel in RequiredCopyFiles)
            {
                if (!IsNonEmptyFile(Path.Combine(appDir, rel))) return rel;
            }
            return null;
        }

        private static bool IsNonEmptyFile(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length > 0; }
            catch { return false; }
        }

        /// <summary>
        /// 复制结束后、替换旧副本之前，对临时目录做一次完整性校验。
        /// 杀软可能在复制过程中隔离/截断文件；不校验就启用，等于把客户机这次
        /// “缺少 chrome_elf.dll”的故障原样复制一遍。
        /// </summary>
        private static void VerifyCopiedApp(string sourceDir, string copiedDir)
        {
            var missing = FindMissingCopyFile(copiedDir);
            if (missing != null)
                throw new IOException("复制后的副本不完整（缺少 " + missing +
                    "），已中止；请检查磁盘空间或杀软隔离。");

            foreach (var rel in RequiredCopyFiles)
            {
                var sourceFile = Path.Combine(sourceDir, rel);
                if (!File.Exists(sourceFile)) continue;
                var copiedFile = Path.Combine(copiedDir, rel);
                if (new FileInfo(copiedFile).Length != new FileInfo(sourceFile).Length)
                    throw new IOException("复制后的文件大小不一致（" + rel +
                        "），已中止；请检查磁盘空间。");
            }

            var sourceExe = LocalizationPaths.ResolveExecutable(sourceDir);
            var copiedExe = LocalizationPaths.ResolveExecutable(copiedDir);
            if (!File.Exists(sourceExe) || !File.Exists(copiedExe) ||
                new FileInfo(copiedExe).Length != new FileInfo(sourceExe).Length)
                throw new IOException("复制后的可执行文件不完整，已中止。");

            try
            {
                AsarArchive.ReadHeader(Path.Combine(copiedDir, "resources", "app.asar"));
            }
            catch (Exception ex)
            {
                throw new IOException("复制后的 app.asar 无法解析：" + ex.Message, ex);
            }
        }

        public static int Launch(string targetApp, string lang, Action<string> progress)
        {
            if (String.IsNullOrWhiteSpace(targetApp))
            {
                Report(progress, "[错误] 没有可启动的本地化副本。先运行 -Install，或用 -Target 指定目录。");
                return 1;
            }

            var exe = LocalizationPaths.ResolveExecutable(targetApp);
            if (!File.Exists(exe))
            {
                Report(progress, "[错误] 找不到可执行文件：" + targetApp);
                return 1;
            }

            // 补丁路线也是“未打包启动”，同样需要给 AUMID 注册图标；
            // 否则非中文系统/注入失败时任务栏会变成白纸。
            try
            {
                AppIconRegistration.Ensure(new CodexInstall
                {
                    Kind = "patch",
                    DisplayName = "Codex",
                    Version = "-",
                    InstallLocation = targetApp,
                    ExecutablePath = exe
                });
            }
            catch { }

            var arguments = String.IsNullOrEmpty(lang) ? String.Empty : "--lang=" + lang;
            Report(progress, "[信息] 启动：" + exe + " " + arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = targetApp,
                UseShellExecute = true
            };
            var process = Process.Start(startInfo);

            // 与注入路线一致的早退探针：副本被单实例锁顶掉时会立即退出，
            // 不能“Process.Start 一返回就报成功”。
            if (process != null)
            {
                try
                {
                    if (process.WaitForExit(1500))
                    {
                        Report(progress, "[警告] Codex 启动后立即退出；可能被已有的 Codex 实例占用（单实例锁）。");
                        return 1;
                    }
                }
                catch { }
            }

            Report(progress, "[成功] 已启动。");
            return 0;
        }

        private static string DefaultRootDir()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (String.IsNullOrWhiteSpace(profile))
                profile = Environment.GetEnvironmentVariable("USERPROFILE");
            return Path.Combine(profile, ".codex", "codex-zh");
        }

        private static string DefaultConfigTomlPath()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (String.IsNullOrWhiteSpace(profile))
                profile = Environment.GetEnvironmentVariable("USERPROFILE");
            return Path.Combine(profile, ".codex", "config.toml");
        }

        private static void Report(Action<string> progress, string message)
        {
            if (progress != null) progress(message);
        }
    }
}
