using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CodexZhLauncher
{
    [Flags]
    internal enum ActivateOptions
    {
        None = 0x00000000,
        DesignMode = 0x00000001,
        NoErrorUi = 0x00000002,
        NoSplashScreen = 0x00000004
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    internal class ApplicationActivationManager
    {
    }

    internal static class AppActivation
    {
        public static uint Launch(CodexInstall install, string arguments)
        {
            if (install == null || !install.IsValid)
                throw new InvalidOperationException("未检测到可用的 Codex Desktop。 ");

            if (install.IsStorePackage)
            {
                var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                uint processId;
                var result = manager.ActivateApplication(
                    install.AppUserModelId,
                    arguments,
                    ActivateOptions.NoErrorUi,
                    out processId);
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                return processId;
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = install.ExecutablePath,
                Arguments = arguments,
                UseShellExecute = false,
                WorkingDirectory = install.InstallLocation
            });
            if (process == null) throw new InvalidOperationException("Codex Desktop 启动失败。");
            return unchecked((uint)process.Id);
        }

        /// <summary>
        /// 注入路线专用：**一律用 Process.Start 直接跑 exe**（即"未打包运行"），
        /// 商店版也**不走** ActivateApplication。
        ///
        /// 为什么：真机实测只有这条能注入成功 —— 直接启动 exe，Chromium 的
        /// `--remote-debugging-port` 才会开。走包激活时参数能否传进去尚未验证。
        /// 代价是丢包标识（应用自带更新器失效、userData 从 MSIX 重定向目录移回
        /// `%APPDATA%\Codex`）—— 但这与磁盘补丁路线的副本本来就是同一个代价。
        /// </summary>
        public static uint LaunchUnpackaged(CodexInstall install, string arguments)
        {
            if (install == null || !install.IsValid)
                throw new InvalidOperationException("未检测到可用的 Codex Desktop。 ");

            var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = install.ExecutablePath,
                Arguments = arguments,
                UseShellExecute = false,
                WorkingDirectory = install.InstallLocation,
                // Codex 自己会往 stdout/stderr 喷大量日志（AppServerConnection、sparkle 之类）。
                // 不重定向的话，命令行跑工具时自己的输出会被整个淹掉。
                // 重定向 + 空处理器 = 丢弃它的输出，同时把管道排空、不会把子进程卡住。
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.OutputDataReceived += delegate { };
            process.ErrorDataReceived += delegate { };

            // 未打包启动会让任务栏/开始菜单里的图标退化成一张白纸
            // （App 声明自己的 AUMID，而它没注册过图标）—— 修它，见 AppIconRegistration。
            //
            // 刻意放在 Start **之前**：任务栏按钮是在窗口创建时就去找图标的，
            // 晚一步这一轮就来不及，得等下次启动才好看。
            //
            // 两条路线（注入 / 磁盘补丁）都从这里启动，所以接在这一处就全覆盖了 ——
            // 补丁路线跑的是副本，副本也是未打包运行、AUMID 一样。
            AppIconRegistration.Ensure(install);

            if (!process.Start()) throw new InvalidOperationException("Codex Desktop 启动失败。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return unchecked((uint)process.Id);
        }

        public static int ReserveLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        public static string BuildArguments(int rendererPort, int inspectorPort, string locale)
        {
            return String.Join(" ", new[]
            {
                "--remote-debugging-address=127.0.0.1",
                "--remote-debugging-port=" + rendererPort,
                "--inspect=127.0.0.1:" + inspectorPort,
                "--lang=" + locale
            });
        }

        /// <summary>
        /// 运行时注入路线用的启动参数。
        ///
        /// 与 <see cref="BuildArguments"/> 的唯一区别：**不带 `--inspect`**。
        /// 那条路（连 Electron 主进程）已被新版 Codex 的 Electron fuse
        /// `EnableNodeCliInspectArguments` 关闭，带上也只是被静默忽略 —— 实测
        /// 端口根本不会开。界面注入走的是渲染进程的 `--remote-debugging-port`，
        /// 那是 **Chromium 的开关，不在 fuse 管辖内**（真机实测两版仍可用）。
        /// </summary>
        public static string BuildInjectionArguments(int rendererPort, string locale)
        {
            return String.Join(" ", new[]
            {
                "--remote-debugging-address=127.0.0.1",
                "--remote-debugging-port=" + rendererPort,
                "--remote-allow-origins=http://127.0.0.1:" + rendererPort,
                "--lang=" + locale
            });
        }
    }
}

