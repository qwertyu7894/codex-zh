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
            string skipReason = null;

            // ---- 注入前清场 ----
            // Electron 的单实例锁只让**第一个**进程成为主实例，而
            // --remote-debugging-port 只有主实例才会真正绑定端口。已经有一个
            // Codex 在跑时，新进程只会把启动请求转交给它然后自己退出，调试端口
            // 永远不会出现 —— 客户机日志（26.1027）里注入干等 43 秒后报
            // "无法连接到远程服务器"就是这个原因，当时日志还显示有 11 个 Codex
            // 进程在跑。所以注入之前必须先确认没有旧实例：有就关掉再启动，
            // 否则"用户已经开着 Codex"的机器上注入必然失败。
            bool preclosed = false;
            bool skipInject = false;
            var preScan = CodexProcessManager.Scan(source);
            AppLog.Write("inject.preclose.count=" + preScan.TotalPotentialCount +
                " verified=" + preScan.Verified.Count +
                " unverified=" + preScan.UnverifiedCandidateIds.Count);
            if (preScan.TotalPotentialCount > 0)
            {
                AppLog.Write("inject.preclose.begin");
                Report(onProgress, "检测到 Codex 正在运行，先关闭再以调试模式启动…");
                try
                {
                    var preShutdown = await CodexProcessManager.ShutdownAsync(
                        source,
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(8),
                        onProgress);
                    if (preShutdown.Success)
                    {
                        preclosed = true;
                        AppLog.Write("inject.preclose.ok initial=" + preShutdown.InitialCount);
                        // 与 MainWindow 关闭重启后的等待同一个道理：系统要过一会儿
                        // 才真正放掉单实例锁，立刻启动仍会被顶掉。
                        AppLog.Write("inject.preclose.wait=750ms");
                        await Task.Delay(750);
                    }
                    else
                    {
                        // 关不掉就不要再启动一个必然自退的实例（白等 25 秒调试目标
                        // 超时），直接回退磁盘补丁。回退里的关闭逻辑按需执行。
                        skipInject = true;
                        skipReason = "已有 Codex 进程未能关闭（剩余 " + preShutdown.RemainingCount + " 个）";
                        AppLog.Write("inject.preclose.failed remaining=" + preShutdown.RemainingCount);
                        Report(onProgress, "有 " + preShutdown.RemainingCount + " 个进程未能关闭，跳过注入。");
                    }
                }
                catch (Exception ex)
                {
                    skipInject = true;
                    skipReason = "关闭正在运行的 Codex 失败：" + ex.Message;
                    AppLog.Write("inject.preclose.error " + Describe(ex));
                }
            }
            else
            {
                AppLog.Write("inject.preclose.skip");
            }

            // ---- 已有本地化副本时，直接走磁盘补丁 ----
            // 副本已经打好补丁，启动只要 1 秒；而尝试注入在商店版上几乎必然失败
            // （WindowsApps 权限，或调试端口被单实例锁吃掉），每次都要白等 40 多秒。
            // 客户机真实反馈（2026-10-31）：关闭重启后每次仍然先试注入，慢得没道理。
            //
            // 副本落后于新版不用在这里判断：磁盘补丁路线自己会检测版本变化并重拷，
            // 所以"优先副本"不会让用户停在旧版本上。
            //
            // skipInject 为真说明旧进程没关掉，这时启动副本也会被单实例锁顶掉，
            // 不在这种状态下抢跑，交给下面的回退流程处理。
            if (!skipInject && LocalizationRuntime.HasLocalizedCopy)
            {
                AppLog.Write("launch.route.prefer-copy");
                Report(onProgress, "检测到已汉化的本地化副本，直接启动…");

                var copyInstall = await CodexDiscovery.DetectAsync();
                var copyReport = await LocalizationRuntime.LaunchAsync(copyInstall, locale, onProgress);
                copyReport.Mode = "patch";
                AppLog.Write("launch.mode=patch prefer-copy");
                return copyReport;
            }

            if (!skipInject)
            {
                try
                {
                    injected = await InjectionRuntime.LaunchAsync(source, locale, onProgress);
                }
                catch (Exception ex)
                {
                    injectError = ex;
                    AppLog.Write("inject.failed " + Describe(ex));
                }
            }

            if (injected != null && injected.Complete)
            {
                AppLog.Write("launch.mode=inject");
                return injected;
            }

            var reason = injectError != null
                ? injectError.Message
                : (injected == null ? (skipReason == null ? "未知原因" : skipReason) : injected.Message);
            AppLog.Write("inject.fallback reason=" + reason);

            // 什么时候可以省掉这次关闭：
            //   · 注入前已经清场成功（preclosed），并且
            //   · 这次注入压根没启动进程（例如系统语言不是中文被直接跳过）。
            // 此时机器上没有任何 Codex 在跑，再关一遍只是空转。
            // 反过来，只要注入启动过实例 —— 或者抛异常、无法判断有没有启动 ——
            // 就必须把它关掉，否则磁盘补丁启动的副本会被单实例锁顶掉，
            // 表现为"点了没反应"。所以这里不能只看"前面关过"，还要看"有没有新启动"。
            bool needShutdown = !preclosed || injected == null || injected.Started;
            if (needShutdown)
            {
                if (skipInject)
                    Report(onProgress, "注入已跳过（" + reason + "），正在关闭 Codex 进程…");
                else
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
            }
            else
            {
                AppLog.Write("inject.fallback.preclose.reuse");
            }

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
