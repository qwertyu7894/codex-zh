using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace CodexZhLauncher
{
    internal static class CodexDiscovery
    {
        private const string PackageName = "OpenAI.Codex";
        private static readonly string StateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexZhLauncher");
        private static readonly string PortablePathFile = Path.Combine(StateDirectory, "portable-path.txt");

        public static async Task<CodexInstall> DetectAsync()
        {
            // ① 手动指定优先：用户显式选过的目录不能被任何自动探测顶掉，
            //    否则「选择 Codex」按钮选完，下一次轮询就被覆盖回去了。
            var custom = LoadPortableInstall();
            if (custom != null) return custom;

            // ② 其次是本地化副本：一旦存在，它就是"当前安装"。
            //    这样 CodexProcessManager 按 InstallLocation 比对进程时能认出副本，
            //    "正在运行 / 关闭并重启" 那套流程无需任何改动即可正常工作。
            //    注意：只在【没有手动指定】时才生效。
            var localized = DetectLocalizedCopy();
            if (localized != null)
            {
                // 返回前顺手看一眼源安装有没有升级过 —— 副本是"快照"，
                // 商店包升级后副本会**永远停在旧版本**，而界面看起来一切正常。
                // 不主动提醒的话，用户会一直跑旧版 Codex 且毫无察觉。
                await MarkStaleCopyAsync(localized);
                return localized;
            }

            // ③ 再往下才是自动探测
            var store = await DetectStorePackageAsync();
            if (store != null) return store;

            return DetectCommonPortableInstall();
        }

        /// <summary>
        /// 解析"**原始安装**"（跳过本地化副本）。运行时注入路线要的是这个 ——
        /// 它必须作用在原始安装上；副本只是磁盘补丁那一版留下的旧快照。
        ///
        /// 与 <see cref="DetectAsync"/> 的差别**只有一处**：不尝试 DetectLocalizedCopy()，
        /// 因此也不会跑 MarkStaleCopyAsync（"副本过期"提醒是为磁盘补丁世界设计的）。
        /// 其余优先级（手动指定 → 商店包 → 常见便携路径）完全一致。
        ///
        /// 刻意**不改** DetectAsync：它同时服务 UI 的"当前安装"显示、
        /// CodexProcessManager 的进程归属判断、以及磁盘补丁路线的前置解析 ——
        /// 把"副本优先"从它里面挪走，会一次改坏三处。
        /// </summary>
        public static async Task<CodexInstall> DetectSourceInstallAsync()
        {
            var custom = LoadPortableInstall();
            if (custom != null) return custom;

            var store = await DetectStorePackageAsync();
            if (store != null) return store;

            return DetectCommonPortableInstall();
        }

        private static CodexInstall DetectLocalizedCopy()
        {
            try
            {
                var executable = LocalizationRuntime.LocalizedExecutable;
                if (!File.Exists(executable)) return null;

                return new CodexInstall
                {
                    Kind = "本地化副本",
                    DisplayName = "Codex Desktop（本地化副本）",
                    Version = ReadLocalizedVersion(executable),
                    InstallLocation = LocalizationRuntime.LocalizedAppDir,
                    ExecutablePath = executable,
                    AppUserModelId = null
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 读 App 自带的 resources\owl-app.ini 里的 AppVersion。
        ///
        /// 副本与商店包**必须都用这一个来源**比较：商店包**目录名**里的是另一套编号
        /// （如 26.1007.2314.0），与 owl-app.ini 的（26.1007.21434）格式不同，
        /// 混着比会永远不相等、必然误报"副本已过期"。
        /// </summary>
        private static string ReadAppVersion(string appDir)
        {
            if (String.IsNullOrWhiteSpace(appDir)) return null;
            try
            {
                var ini = Path.Combine(appDir, "resources", "owl-app.ini");
                if (!File.Exists(ini)) return null;
                foreach (var line in File.ReadAllLines(ini))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("AppVersion", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split(new[] { '=' }, 2);
                        if (parts.Length == 2 && !String.IsNullOrWhiteSpace(parts[1]))
                            return parts[1].Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 本地化副本是否落后于源安装。落后就把源安装版本记进 StaleSourceVersion。
        /// 任何失败都只是"不提示"，绝不影响检测主流程。
        /// </summary>
        private static async Task MarkStaleCopyAsync(CodexInstall localized)
        {
            try
            {
                // 先确认"副本确实是从某个商店包拷来的"。
                // 副本也可能来自官网版 / 便携版（目录不可写时脚本同样走 store-copy 模式），
                // 那时机器上若恰好也装了商店包，拿它比对就是**误报**。
                // 宁可漏报，不可误报。
                if (!CopyCameFromStorePackage()) return;

                var store = await DetectStorePackageAsync();
                if (store == null || String.IsNullOrWhiteSpace(store.ExecutablePath)) return;

                var copyVersion = ReadAppVersion(Path.GetDirectoryName(localized.ExecutablePath));
                var storeVersion = ReadAppVersion(Path.GetDirectoryName(store.ExecutablePath));

                // 任一侧读不到就**不判断** —— 宁可漏报，也不要拿残缺信息误报。
                if (String.IsNullOrWhiteSpace(copyVersion) || String.IsNullOrWhiteSpace(storeVersion)) return;
                if (String.Equals(copyVersion, storeVersion, StringComparison.Ordinal)) return;

                localized.StaleSourceVersion = storeVersion;
                AppLog.Write("copy.stale copy=" + copyVersion + " source=" + storeVersion);
            }
            catch (Exception ex)
            {
                AppLog.Write("stale.probe.failed " + ex.Message);
            }
        }

        /// <summary>
        /// 副本是不是从商店包拷来的。
        ///
        /// 只看 state.json：mode 必须是 store-copy，且 sourcePath 落在 WindowsApps 下。
        /// 商店包升级后目录名会带上新版本号，所以**不能**拿 sourcePath 直接跟当前
        /// 商店包路径比（必然不相等）—— 只校验它"是不是 WindowsApps 里的包"。
        /// 读不到 state.json 就返回 false：宁可漏报，也不对着错误的源安装误报。
        /// </summary>
        private static bool CopyCameFromStorePackage()
        {
            try
            {
                var path = Path.Combine(LocalizationRuntime.LocalizedRoot, "state.json");
                if (!File.Exists(path)) return false;

                var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(path, System.Text.Encoding.UTF8));
                if (values == null) return false;

                var mode = GetString(values, "mode");
                var sourcePath = GetString(values, "sourcePath");
                if (!String.Equals(mode, "store-copy", StringComparison.OrdinalIgnoreCase)) return false;
                if (String.IsNullOrWhiteSpace(sourcePath)) return false;

                return sourcePath.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadLocalizedVersion(string executable)
        {
            // owl-app.ini 里的 AppVersion 比 exe 的 FileVersion 更贴近真实版本
            var version = ReadAppVersion(Path.GetDirectoryName(executable));
            if (!String.IsNullOrWhiteSpace(version)) return version;

            try
            {
                var info = FileVersionInfo.GetVersionInfo(executable);
                if (!String.IsNullOrWhiteSpace(info.FileVersion)) return info.FileVersion;
            }
            catch { }

            return "-";
        }

        public static CodexInstall UsePortableExecutable(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentException("请选择 Codex 可执行文件。", "executablePath");

            var fullPath = Path.GetFullPath(executablePath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("找不到所选文件。", fullPath);

            var fileName = Path.GetFileName(fullPath);
            if (!fileName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) &&
                !fileName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("请选择 Codex.exe 或 Codex 应用目录中的 ChatGPT.exe。");
            }

            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(PortablePathFile, fullPath, System.Text.Encoding.UTF8);
            return BuildPortableInstall(fullPath, "手动选择");
        }

        public static void ClearPortableOverride()
        {
            if (File.Exists(PortablePathFile)) File.Delete(PortablePathFile);
        }

        public static int CountRunningCodexProcesses(CodexInstall install)
        {
            return CodexProcessManager.Scan(install).TotalPotentialCount;
        }

        private static Task<CodexInstall> DetectStorePackageAsync()
        {
            // 原生读 HKCU 的 MSIX 包仓库，不再启动 PowerShell 子进程。
            // 返回语义与旧实现一致：精确包名 + 最高版本。
            try
            {
                var package = NativePackageLocator.FindNewest(PackageName);
                if (package == null || String.IsNullOrWhiteSpace(package.InstallLocation))
                    return Task.FromResult<CodexInstall>(null);

                var applicationId = ReadApplicationId(Path.Combine(package.InstallLocation, "AppxManifest.xml"));
                if (String.IsNullOrWhiteSpace(applicationId)) applicationId = "App";

                var executable = FindPackagedExecutable(package.InstallLocation);
                return Task.FromResult(new CodexInstall
                {
                    Kind = "Microsoft Store",
                    DisplayName = "Codex Desktop（Microsoft Store）",
                    Version = String.IsNullOrWhiteSpace(package.Version) ? "-" : package.Version,
                    InstallLocation = package.InstallLocation,
                    ExecutablePath = executable,
                    AppUserModelId = package.PackageFamilyName + "!" + applicationId
                });
            }
            catch
            {
                return Task.FromResult<CodexInstall>(null);
            }
        }

        private static CodexInstall LoadPortableInstall()
        {
            try
            {
                if (!File.Exists(PortablePathFile)) return null;
                var path = File.ReadAllText(PortablePathFile, System.Text.Encoding.UTF8).Trim();
                return File.Exists(path) ? BuildPortableInstall(path, "已保存路径") : null;
            }
            catch
            {
                return null;
            }
        }

        private static CodexInstall DetectCommonPortableInstall()
        {
            // ① 固定路径（沿用上游行为）
            var fixedCandidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Codex", "Codex.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Codex", "Codex.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Codex", "Codex.exe")
            };
            foreach (var candidate in fixedCandidates)
            {
                if (File.Exists(candidate)) return BuildPortableInstall(candidate, "自动检测");
            }

            // ② 各固定盘根目录下的常见目录名
            //    官网安装版经常被随手装到某个盘的根目录（实测遇到过 D:\ChatGPT\），
            //    上面那几个固定路径覆盖不到。只看盘的顶层、不递归，代价极小。
            //    这类目录必须校验 resources\app.asar 存在才认 —— 否则可能把某个
            //    恰好同名的程序误当成 Codex。
            var relPaths = new[] { "ChatGPT.exe", "Codex.exe", "app\\ChatGPT.exe", "app\\Codex.exe" };
            foreach (var drive in FixedDrives())
            {
                foreach (var folder in new[] { "ChatGPT", "Codex" })
                {
                    foreach (var rel in relPaths)
                    {
                        var candidate = Path.Combine(drive, folder, rel);
                        if (!File.Exists(candidate)) continue;

                        var dir = Path.GetDirectoryName(candidate);
                        var asar = Path.Combine(dir, "resources", "app.asar");
                        if (File.Exists(asar)) return BuildPortableInstall(candidate, "自动检测");
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> FixedDrives()
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return new string[0]; }

            var roots = new List<string>();
            foreach (var drive in drives)
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady) roots.Add(drive.Name);
                }
                catch { }
            }
            return roots;
        }

        private static CodexInstall BuildPortableInstall(string executablePath, string source)
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return new CodexInstall
            {
                Kind = "便携版",
                DisplayName = "Codex Desktop（" + source + "）",
                Version = String.IsNullOrWhiteSpace(info.FileVersion) ? "-" : info.FileVersion,
                InstallLocation = Path.GetDirectoryName(executablePath),
                ExecutablePath = executablePath,
                AppUserModelId = null
            };
        }

        private static string ReadApplicationId(string manifestPath)
        {
            try
            {
                var document = XDocument.Load(manifestPath);
                var application = document.Descendants().FirstOrDefault(
                    delegate(XElement element) { return element.Name.LocalName == "Application"; });
                if (application == null) return null;
                var id = application.Attributes().FirstOrDefault(
                    delegate(XAttribute attribute) { return attribute.Name.LocalName == "Id"; });
                return id == null ? null : id.Value;
            }
            catch
            {
                return null;
            }
        }

        private static string FindPackagedExecutable(string installLocation)
        {
            var candidates = new[]
            {
                Path.Combine(installLocation, "app", "ChatGPT.exe"),
                Path.Combine(installLocation, "app", "Codex.exe"),
                Path.Combine(installLocation, "Codex.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static string GetString(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : null;
        }
    }
}
