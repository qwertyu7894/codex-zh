using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;

namespace CodexZhLauncher
{
    /// <summary>
    /// 注入路线的**只读**诊断。
    ///
    /// 起因：客户机上 <c>Process.Start</c> 直接跑
    /// <c>C:\Program Files\WindowsApps\...\ChatGPT.exe</c> 报"拒绝访问"，
    /// 那台机器因此永远用不上运行时注入，只能靠约 2 GB 的本地化副本。
    ///
    /// 这个诊断**什么都不改、也不启动 Codex**，只把能解释"为什么被拒绝"的事实
    /// 一次收齐：包是注册给谁的、exe 的 ACL、有没有 Smart App Control / 代码完整性
    /// 策略 / 杀软拦路、当前账户是不是管理员。
    ///
    /// 用法：<c>GPTHanhua.exe --diagnose-inject</c>，然后把
    /// <c>%LOCALAPPDATA%\CodexZhLauncher\logs\launcher.log</c> 发回来
    /// （输出会同时写进日志，客户端不用会看控制台）。
    /// </summary>
    internal static class InjectionDiagnostics
    {
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(25);

        public static void Run(CodexInstall install, Action<string> output)
        {
            output("=== 注入故障诊断（请把整段发回给提供者）===");

            EnvironmentSection(output);
            InstallSection(output, install);
            LogSection(output);
            PackageSection(output);
            PermissionSection(output, install);
            InterferenceSection(output);

            output("=== 诊断结束 ===");
        }

        // ------------------------------------------------------------ [1] 环境
        private static void EnvironmentSection(Action<string> output)
        {
            output("");
            output("[1] 工具与系统");
            output("    工具版本        " + AppInfo.Version);
            output("    系统            " + Environment.OSVersion.VersionString +
                "（64 位：" + Environment.Is64BitOperatingSystem + "）");
            output("    当前用户        " + Environment.UserDomainName + "\\" + Environment.UserName);
            output("    管理员权限      " + (IsAdministrator() ? "是" : "否"));
            output("    日志文件        " + AppLog.FilePath);
        }

        // ------------------------------------------------------ [2] Codex 安装
        private static void InstallSection(Action<string> output, CodexInstall install)
        {
            output("");
            output("[2] 检测到的 Codex 安装");
            if (install == null)
            {
                output("    未检测到安装 —— 这一项本身就是线索。");
                return;
            }
            output("    类型            " + install.Kind);
            output("    版本            " + install.Version);
            output("    安装目录        " + install.InstallLocation);
            output("    可执行文件      " + install.ExecutablePath);
            output("    文件存在        " + (FileExists(install.ExecutablePath) ? "是" : "否"));
            output("    AUMID           " + (install.AppUserModelId ?? "(无 —— 非商店版)"));
        }

        // -------------------------------------------------- [3] 上次注入失败
        private static void LogSection(Action<string> output)
        {
            output("");
            output("[3] 日志里最近的注入相关记录");
            try
            {
                if (!File.Exists(AppLog.FilePath))
                {
                    output("    （日志文件还不存在）");
                    return;
                }
                var lines = File.ReadAllLines(AppLog.FilePath);
                var picked = new List<string>();
                for (var i = lines.Length - 1; i >= 0 && picked.Count < 15; i--)
                {
                    if (lines[i] == null) continue;
                    if (lines[i].IndexOf("inject", StringComparison.OrdinalIgnoreCase) >= 0)
                        picked.Add(lines[i]);
                }
                if (picked.Count == 0)
                {
                    output("    （没有 inject 记录 —— 可能还没在这台机器上跑过汉化）");
                    return;
                }
                picked.Reverse();
                foreach (var line in picked) output("    " + line.Trim());
            }
            catch (Exception ex)
            {
                output("    ! 读日志失败：" + ex.Message);
            }
        }

        // ------------------------------------------------ [4] 包注册在谁名下
        private static void PackageSection(Action<string> output)
        {
            output("");
            output("[4] 商店包的注册情况（**关键**：装给了哪个 Windows 账户）");
            output("    以下为当前用户（" + Environment.UserName + "）：");
            Query(output,
                "Get-AppxPackage -Name 'OpenAI.Codex' | " +
                "Format-List Name,Version,Status,SignatureKind,PackageFullName,InstallLocation,PackageUserInformation");
            output("    以下为所有用户（需要管理员，非管理员会报错，那也是有用的信息）：");
            Query(output,
                "Get-AppxPackage -Name 'OpenAI.Codex' -AllUsers | " +
                "Format-List Name,Version,PackageUserInformation");
        }

        // ---------------------------------------------------- [5] 路径与权限
        private static void PermissionSection(Action<string> output, CodexInstall install)
        {
            output("");
            output("[5] 可执行文件与目录的权限");

            if (install == null || String.IsNullOrWhiteSpace(install.ExecutablePath))
            {
                output("    没有可执行文件路径，跳过。");
                return;
            }

            output("    读权限探测：");
            ProbeRead(output, install.ExecutablePath);
            ProbeRead(output, install.InstallLocation);

            // 用 Get-Acl 而不是 icacls：icacls 是原生程序，按自己的代码页写输出，
            // 中文账户/组名会变成乱码（实测）；Get-Acl 走 .NET 管道，全程 Unicode。
            output("    exe 的 ACL：");
            Query(output, AclScript(install.ExecutablePath));
            output("    安装目录的 ACL：");
            Query(output, AclScript(install.InstallLocation));
        }

        // ------------------------------------------------ [6] 可能拦路的东西
        private static void InterferenceSection(Action<string> output)
        {
            output("");
            output("[6] 可能拦截进程启动的机制");

            output("    Smart App Control（1=开启，0=关闭，2=评估中）：");
            Query(output,
                "(Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\CI\\Policy' " +
                "-Name 'VerifiedAndReputablePolicyState' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState");

            output("    代码完整性策略文件（**系统自带也有**，只列出供参考 —— 只有出现");
            output("    名称不像系统自带的条目时才算线索）：");
            Query(output,
                "Get-ChildItem -Path 'C:\\Windows\\System32\\CodeIntegrity\\CiPolicies\\Active' " +
                "-ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name");

            output("    杀毒软件：");
            Query(output,
                "Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct " +
                "-ErrorAction SilentlyContinue | Select-Object displayName,productState | Format-List");

            output("    AppLocker 策略里是否提到 WindowsApps：");
            Query(output,
                "Get-AppLockerPolicy -Effective -ErrorAction SilentlyContinue -Xml | " +
                "Select-String -Pattern 'WindowsApps' | Select-Object -First 10");
        }

        // ------------------------------------------------------------- 辅助
        private static void Query(Action<string> output, string script)
        {
            // 前置一句设置子进程的输出编码。不设的话 PowerShell 按本机 OEM 代码页写
            // stdout、而这边按 UTF-8 解，中文错误信息会全变乱码（第一版实测就是这样）。
            //
            // 必须包 try：stdout 是管道时设 OutputEncoding 本身就可能抛"句柄无效" ——
            // 正是本项目在 Program.cs 里踩过的同一个坑。设不上顶多乱码，不该让查询整个失败。
            const string preamble =
                "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch { };";

            ProcessResult result;
            try
            {
                result = ProcessRunner.RunAsync(
                    "powershell",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                        ProcessRunner.QuoteArgument(preamble + script),
                    QueryTimeout).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                output("      ! 查询执行失败：" + ex.Message);
                return;
            }

            var text = ((result.Output ?? String.Empty) + "\n" + (result.Error ?? String.Empty))
                .Replace("\r\n", "\n").Trim();
            if (text.Length == 0)
            {
                output("      （无输出）");
                return;
            }
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length > 0) output("      " + line);
            }
        }

        private static void ProbeRead(Action<string> output, string path)
        {
            if (String.IsNullOrWhiteSpace(path)) { output("      （无路径）"); return; }

            // 目录要分开处理：File.Exists 对目录恒为 false，不分就会报出
            // "安装目录 —— 不存在" 这种假信息（第一版就是这么错的）。
            if (Directory.Exists(path))
            {
                try
                {
                    var count = Directory.GetFileSystemEntries(path).Length;
                    output("      " + path + " —— 目录，能列出内容（" + count + " 项）");
                }
                catch (UnauthorizedAccessException ex)
                {
                    output("      " + path + " —— 目录，**列举被拒绝**：" + ex.Message);
                }
                catch (Exception ex)
                {
                    output("      " + path + " —— 目录，列举失败：" + ex.GetType().Name + " " + ex.Message);
                }
                return;
            }

            if (!FileExists(path)) { output("      " + path + " —— 不存在"); return; }
            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    output("      " + path + " —— 能读（读权限正常）");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                output("      " + path + " —— **读也被拒绝**：" + ex.Message);
            }
            catch (Exception ex)
            {
                output("      " + path + " —— 读失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>
        /// 每项一行、不截断。用 Format-Table 会被终端宽度截掉 FileSystemRights，
        /// 而"里有没有 Execute 位"恰恰是这里唯一要看的东西（实测被截成 "...ute,
        /// Synchronize"，等于没打印）。
        /// </summary>
        private static string AclScript(string path)
        {
            return "(Get-Acl -LiteralPath " + ProcessRunner.PowerShellLiteral(path) + ").Access | " +
                "ForEach-Object { $_.IdentityReference.ToString() + '  -->  ' + " +
                "$_.FileSystemRights + '  (' + $_.AccessControlType + ')' }";
        }

        private static bool FileExists(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            try { return File.Exists(path); }
            catch (Exception ex)
            {
                AppLog.Write("diagnose.exists.failed " + ex.Message);
                return false;
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                AppLog.Write("diagnose.admin.failed " + ex.Message);
                return false;
            }
        }
    }
}
