using System;
using System.Threading.Tasks;

namespace CodexZhLauncher
{
    /// <summary>
    /// 启动路线的**唯一决策点**：先试运行时注入，不成功再回退磁盘补丁。
    ///
    /// 为什么要有这个协调器：两条路的前置条件与副作用完全不同 ——
    ///   注入：不复制、不改磁盘，作用在正在跑的那个版本上（约 20 秒生效）
    ///   补丁：复制 2GB、改副本 asar，汉化被钉死在副本那一版
    /// 让它们各自独立成类、由一个协调器串起来，比在一个方法里塞互斥分支好测得多。
    ///
    /// 界面只认这个类；LocalizationRuntime 从此只被这里和 --launch-zh-patch-only 调用。
    /// </summary>
    internal static class LaunchCoordinator
    {
        /// <summary>注入成功就直接返回；否则关掉刚启动的实例，改走磁盘补丁。</summary>
        public static async Task<LaunchReport> LaunchAsync(
            CodexInstall source,
            string locale,
            Action<string> onProgress)
        {
            LaunchReport injected = null;
            Exception injectError = null;

            try
            {
                injected = await InjectionRuntime.LaunchAsync(source, locale, onProgress);
            }
            catch (Exception ex)
            {
                injectError = ex;
                AppLog.Write("inject.failed " + Describe(ex));
            }

            if (injected != null && injected.Complete)
            {
                AppLog.Write("launch.mode=inject");
                return injected;
            }

            var reason = injectError != null
                ? injectError.Message
                : (injected == null ? "未知原因" : injected.Message);
            AppLog.Write("inject.fallback reason=" + reason);
            Report(onProgress, "注入未成功（" + reason + "），正在关闭刚启动的实例…");

            // 这一步**不能省**：商店版与副本共用同一个 Electron 单实例锁（1.1.4 实测确认）。
            // 不关就去启动副本 → 副本瞬间自退 → 用户看到的是"点了没反应"，
            // 正是 1.1.4 修过的那个 bug。
            try
            {
                var shutdown = await CodexProcessManager.ShutdownAsync(
                    source,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(8),
                    onProgress);
                if (!shutdown.Success)
                {
                    AppLog.Write("inject.fallback.remaining=" + shutdown.RemainingCount);
                    Report(onProgress, "有 " + shutdown.RemainingCount + " 个进程未能关闭，回退可能失败。");
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("inject.fallback.shutdown.failed " + ex.Message);
            }

            // 让系统把单实例锁放掉 —— 与 MainWindow 里关闭重启后的等待同一个道理。
            await Task.Delay(750);

            Report(onProgress, "正在改用磁盘补丁…");

            // 这里刻意用 DetectAsync（副本优先）而不是 DetectSourceInstallAsync：
            // 磁盘补丁要的就是"副本"；没有副本时它返回源安装，脚本会自己复制一份。
            var patchInstall = await CodexDiscovery.DetectAsync();
            var patchReport = await LocalizationRuntime.LaunchAsync(patchInstall, locale, onProgress);
            patchReport.Mode = "patch";
            patchReport.FallbackReason = reason;
            AppLog.Write("launch.mode=patch fallback=" + reason);
            return patchReport;
        }

        /// <summary>
        /// 把异常记成"人看得懂 + 能拿去搜"的形式：类型 + HRESULT + 消息，内层也带上。
        ///
        /// 只记 <c>ex.Message</c> 会丢掉最关键的线索：客户机上那句"拒绝访问。"既可能是
        /// 权限不足，也可能是 AppLocker / Smart App Control / 杀软拦的 —— 三者要看
        /// HRESULT（0x80070005 是权限）和内层异常才分得清。Process.Start 常把真正
        /// 的原因裹在 InnerException 里。
        /// </summary>
        private static string Describe(Exception ex)
        {
            if (ex == null) return "(无异常)";
            var text = ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + ex.Message;
            if (ex.InnerException != null)
            {
                text += "  <-  " + ex.InnerException.GetType().Name +
                    " 0x" + ex.InnerException.HResult.ToString("X8") + " " + ex.InnerException.Message;
            }
            return text;
        }

        private static void Report(Action<string> callback, string message)
        {
            AppLog.Write("coord> " + message);
            if (callback == null) return;
            try { callback(message); }
            catch { }
        }
    }
}
