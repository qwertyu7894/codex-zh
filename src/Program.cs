using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

[assembly: System.Reflection.AssemblyTitle(CodexZhLauncher.AppInfo.BrandName)]
[assembly: System.Reflection.AssemblyDescription(CodexZhLauncher.AppInfo.BrandTagline)]
[assembly: System.Reflection.AssemblyCompany("Codex Localization Enhancer Community")]
[assembly: System.Reflection.AssemblyProduct(CodexZhLauncher.AppInfo.BrandName)]
[assembly: System.Reflection.AssemblyCopyright("Copyright 2026")]
[assembly: System.Reflection.AssemblyVersion("1.4.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.4.1.0")]

namespace CodexZhLauncher
{
    internal static class Program
    {
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [STAThread]
        private static void Main(string[] args)
        {
            // winexe 的致命伤：启动阶段任何未捕获异常都不会有任何提示 ——
            // 没有控制台输出、没有弹框，进程直接静默消失，
            // 用户看到的就是"双击没反应"。这里兜住所有未处理异常。
            AppDomain.CurrentDomain.UnhandledException +=
                delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    ReportFatal(e.ExceptionObject as Exception, "运行时发生未处理异常");
                };

            try
            {
                RunMain(args);
            }
            catch (Exception ex)
            {
                ReportFatal(ex, "启动失败");
                Environment.ExitCode = 1;
            }
        }

        private static void RunMain(string[] args)
        {
            if (args.Length > 0 && String.Equals(args[0], "--apply-update-windows", StringComparison.Ordinal))
            {
                Environment.ExitCode = UpdateInstaller.ApplyWindowsUpdate(args);
                return;
            }

            if (args.Length > 0)
            {
                AttachConsole(AttachParentProcess);
                // 没有控制台时（被脚本/CI 调起、stdout 是管道）set_OutputEncoding
                // 会抛 IOException "句柄无效"。它原先写在 try 外面，结果整套命令行
                // 模式在那种环境下**什么都没跑就死了** —— 而这正是最需要它出诊断的场合。
                // 设不上编码不影响功能：Output() 会同时把结果写进日志文件。
                try { Console.OutputEncoding = Encoding.UTF8; }
                catch (Exception ex) { AppLog.Write("console.encoding.failed " + ex.Message); }

                try
                {
                    RunCommand(args);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("error: " + ex.Message);
                    AppLog.Write("command.failed " + ex);
                    Environment.ExitCode = 1;
                }
                return;
            }

            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException +=
                delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                {
                    ReportFatal(e.Exception, "运行时发生未处理异常");
                    e.Handled = true;
                };
            UpdateInstaller.ScheduleCleanup();
            app.Run(new MainWindow());
        }

        /// <summary>
        /// 把"静默死亡"变成"看得见"：写日志 + 弹框。
        /// 客户端不可能去翻日志文件，但错误框会直接把原因摆在他面前。
        /// </summary>
        private static void ReportFatal(Exception ex, string title)
        {
            var detail = ex == null ? "（无异常详情）" : ex.ToString();
            try { AppLog.Write("fatal [" + title + "] " + detail); }
            catch { }
            try
            {
                MessageBox.Show(
                    title + "\n\n" + detail + "\n\n日志文件：\n" + AppLog.FilePath,
                    AppInfo.BrandName + " 出错了",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { }
        }

        private static void RunCommand(string[] args)
        {
            var command = args[0].ToLowerInvariant();
            if (command == "--self-test")
            {
                Output(LocalizationScripts.SelfTest());
                Output(InjectionRuntime.SelfTest());
                Output(UpdateChecker.SelfTest());
                Output(UpdateInstaller.SelfTest());
                Output(AppIconRegistration.SelfTest());
                return;
            }

            var install = CodexDiscovery.DetectAsync().GetAwaiter().GetResult();
            if (command == "--diagnostics")
            {
                PrintDiagnostics(install);
                return;
            }

            if (command == "--diagnose-inject")
            {
                // 必须用**原始安装**：注入路线要启动的就是它。
                // DetectAsync 是"副本优先"，在有本地化副本的机器上会指向副本，
                // 而那台机器要诊断的恰恰是原始安装为什么起不来。
                InjectionDiagnostics.Run(
                    SourceInstallOrFallback(install),
                    delegate(string message) { Output(message); });
                return;
            }

            if (command == "--launch-zh" || command == "--launch-en" ||
                command == "--launch-zh-inject-only" || command == "--launch-zh-patch-only")
            {
                var locale = command == "--launch-en" ? "en-US" : "zh-CN";
                Action<string> progress = delegate(string message) { Output(message); };
                LaunchReport report;

                if (command == "--launch-zh-patch-only")
                {
                    // 强制磁盘补丁（跳过注入）—— 用来单独验补丁那条路
                    report = LocalizationRuntime.LaunchAsync(install, locale, progress).GetAwaiter().GetResult();
                    report.Mode = "patch";
                }
                else if (command == "--launch-zh-inject-only")
                {
                    // 强制注入（不回落）—— 用来单独验注入那条路
                    var injectSource = SourceInstallOrFallback(install);
                    report = InjectionRuntime.LaunchAsync(injectSource, locale, progress).GetAwaiter().GetResult();
                }
                else
                {
                    // 默认：注入优先，失败自动回退磁盘补丁
                    var source = SourceInstallOrFallback(install);
                    report = LaunchCoordinator.LaunchAsync(source, locale, progress).GetAwaiter().GetResult();
                }

                Output(report.Message);
                Output(String.Format("mode={0}; pid={1}; renderer_port={2}; inspector_port={3}",
                    report.Mode ?? "-", report.ProcessId, report.RendererPort, report.InspectorPort));
                Output(String.Format("locale={0}; menu={1}", report.LocaleApplied, report.MenuApplied));
                if (!String.IsNullOrWhiteSpace(report.InjectionDetail)) Output("injection=" + report.InjectionDetail);
                if (!String.IsNullOrWhiteSpace(report.FallbackReason)) Output("fallback=" + report.FallbackReason);
                Environment.ExitCode = report.Complete ? 0 : 2;
                return;
            }

            throw new ArgumentException(
                "未知参数。可用参数：--diagnostics、--diagnose-inject（注入起不来时用）、" +
                "--self-test、--launch-zh、--launch-en、" +
                "--launch-zh-inject-only（只验注入）、--launch-zh-patch-only（只验磁盘补丁）");
        }

        /// <summary>
        /// 注入路线要的是"**原始安装**"（跳过本地化副本）—— 副本只是磁盘补丁那一版留下的旧快照。
        /// 解析不到就退回传进来的那个，至少不会因为返回 null 而直接抛异常。
        /// </summary>
        private static CodexInstall SourceInstallOrFallback(CodexInstall fallback)
        {
            try
            {
                var source = CodexDiscovery.DetectSourceInstallAsync().GetAwaiter().GetResult();
                return source ?? fallback;
            }
            catch (Exception ex)
            {
                AppLog.Write("source.detect.failed " + ex.Message);
                return fallback;
            }
        }

        private static void PrintDiagnostics(CodexInstall install)
        {
            Output("Codex Localization Enhancer " + AppInfo.Version);
            Output("os=" + Environment.OSVersion.VersionString);
            Output("64bit=" + Environment.Is64BitOperatingSystem);
            if (install == null)
            {
                Output("codex=not-found");
                Environment.ExitCode = 2;
                return;
            }
            Output("codex=found");
            Output("kind=" + install.Kind);
            Output("version=" + install.Version);
            Output("location=" + install.InstallLocation);
            Output("aumid=" + (install.AppUserModelId ?? "-"));
            Output("running_processes=" + CodexDiscovery.CountRunningCodexProcesses(install));
            Output("log=" + AppLog.FilePath);
        }

        private static void Output(string value)
        {
            Console.WriteLine(value);
            AppLog.Write("cli " + value);
        }
    }
}
