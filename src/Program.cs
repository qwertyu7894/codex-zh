using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

[assembly: System.Reflection.AssemblyTitle(CodexZhLauncher.AppInfo.BrandName)]
[assembly: System.Reflection.AssemblyDescription(CodexZhLauncher.AppInfo.BrandTagline)]
[assembly: System.Reflection.AssemblyCompany("Codex Localization Enhancer Community")]
[assembly: System.Reflection.AssemblyProduct(CodexZhLauncher.AppInfo.BrandName)]
[assembly: System.Reflection.AssemblyCopyright("Copyright 2026")]
[assembly: System.Reflection.AssemblyVersion("1.1.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.1.0")]

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
                Console.OutputEncoding = Encoding.UTF8;
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
                Output(UpdateChecker.SelfTest());
                Output(UpdateInstaller.SelfTest());
                return;
            }

            var install = CodexDiscovery.DetectAsync().GetAwaiter().GetResult();
            if (command == "--diagnostics")
            {
                PrintDiagnostics(install);
                return;
            }

            if (command == "--launch-zh" || command == "--launch-en")
            {
                var locale = command == "--launch-zh" ? "zh-CN" : "en-US";
                var report = LocalizationRuntime.LaunchAsync(install, locale).GetAwaiter().GetResult();
                Output(report.Message);
                Output(String.Format("pid={0}; renderer_port={1}; inspector_port={2}",
                    report.ProcessId, report.RendererPort, report.InspectorPort));
                Output(String.Format("locale={0}; menu={1}", report.LocaleApplied, report.MenuApplied));
                Environment.ExitCode = report.Complete ? 0 : 2;
                return;
            }

            throw new ArgumentException("未知参数。可用参数：--diagnostics、--self-test、--launch-zh、--launch-en");
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
