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

            // PowerShell 的 -Install 自己调用 Find-CodexInstall，不使用 C# 传进来的
            // CodexInstall。这里保留同样的行为，但允许把"非副本"的探测结果当提示，
            // 以便手动指定的便携版也能正确工作。
            var found = CodexInstallLocator.FromHint(request.SourceHint, localizedAppDir);
            if (found == null) found = CodexInstallLocator.Find(request.TargetDir);

            if (found == null && String.IsNullOrWhiteSpace(request.TargetDir))
            {
                outcome.ExitCode = 1;
                outcome.Message = "未检测到 Codex。用 -Target 手动指定 app 目录。";
                Report(request.Progress, "[错误] " + outcome.Message);
                return outcome;
            }

            var statePrev = PatchState.Load(statePath);
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

            if (!request.Apply && String.Equals(mode, "store-copy", StringComparison.OrdinalIgnoreCase))
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
            if (fastSkip)
            {
                Report(request.Progress, "[信息] 已是最新 ChatGPT.exe（副本未变化，无需重算）");
            }
            else
            {
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
                    Report(request.Progress, "[警告] 未在二进制中找到嵌入式 asar 完整性记录（该构建可能未开启此校验）");
                }
            }

            Report(request.Progress, String.Empty);
            Report(request.Progress, "[信息] --- 语言设置 ---");
            var locale = CodexConfigToml.SetLocaleOverride(configToml, request.Locale, request.Apply);
            if (locale.Changed)
                Report(request.Progress, "[成功] localeOverride -> " + request.Locale + "（" + locale.Reason + "）");
            else
                Report(request.Progress, "[信息] localeOverride 已是 " + request.Locale);

            if (request.Apply && !fastSkip)
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
                var exeInfo = new FileInfo(Path.Combine(targetApp, "ChatGPT.exe"));

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

            outcome.ExitCode = 0;
            return outcome;
        }

        // 启动本地化副本所需的 Chromium / Electron 关键文件。
        // 缺任何一个都说明副本不完整（复制中断、杀软隔离、用户误删），必须重拷。
        private static readonly string[] RequiredCopyFiles =
        {
            "ChatGPT.exe",
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
                    var prevVersion = statePrev != null ? statePrev.Version : null;
                    if (!String.IsNullOrEmpty(prevVersion) && !String.IsNullOrEmpty(nowVersion) &&
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
            foreach (var rel in RequiredCopyFiles)
            {
                if (!File.Exists(Path.Combine(appDir, rel))) return rel;
            }
            return null;
        }

        public static int Launch(string targetApp, string lang, Action<string> progress)
        {
            if (String.IsNullOrWhiteSpace(targetApp))
            {
                Report(progress, "[错误] 没有可启动的本地化副本。先运行 -Install，或用 -Target 指定目录。");
                return 1;
            }

            var exe = Path.Combine(targetApp, "ChatGPT.exe");
            if (!File.Exists(exe)) exe = Path.Combine(targetApp, "Codex.exe");
            if (!File.Exists(exe))
            {
                Report(progress, "[错误] 找不到可执行文件：" + targetApp);
                return 1;
            }

            var arguments = String.IsNullOrEmpty(lang) ? String.Empty : "--lang=" + lang;
            Report(progress, "[信息] 启动：" + exe + " " + arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true
            };
            Process.Start(startInfo);
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
