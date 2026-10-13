using System;
using System.Threading.Tasks;

namespace CodexZhLauncher
{
    /// <summary>
    /// 运行时注入路线：不复制、不改磁盘，作用在**正在跑的那个 Codex 版本**上。
    ///
    /// 为什么要有这条路：磁盘补丁把汉化钉死在"复制那一刻的版本"上，
    /// Codex 一升级，用户跑的还是旧版副本、界面上没有新版本的内容。
    /// 注入则每次启动都作用在当前版本上，换版本自动适配。
    ///
    /// 原理（真机实测 26.1007 / 26.915 两版均通过）：
    ///   1. 启动 Codex 时带 --remote-debugging-port=&lt;随机回环端口&gt;
    ///      （**不能**用 --inspect —— 那条路被 Electron fuse 关掉了）
    ///   2. 等渲染进程的 page target 出现
    ///   3. 注入 i18n-bootstrap.js：它劫持 Statsig 的 getDynamicConfig/getLayer，
    ///      把 configId 72216192 强制成 enable_i18n=true + locale_source='SYSTEM'
    ///   4. App 隔十几秒会重新取一次动态配置 —— 那一刻钩子生效，界面与原生菜单一起变中文
    ///
    /// 成功判据：页内 __codexZhI18nState.patchedConfigs &gt;= 1
    ///   （注入瞬间是 0；变中文那一刻变成 1。实测 16~22 秒。）
    ///
    /// 本类是 macos/Sources/CodexZhLauncherMac/LocalizationRuntime.swift 的 C# 镜像，
    /// 差异只有一处：mac 那边还会走 --inspect 注入主进程菜单，而 Windows 上那条路
    /// 已被 fuse 关闭；这里改用 locale_source='SYSTEM' 让 App 自己重建中文菜单。
    /// </summary>
    internal static class InjectionRuntime
    {
        /// <summary>等渲染进程 page target 出现。冷启动 Electron 首窗口实测可达 20 秒。</summary>
        private static readonly TimeSpan TargetTimeout = TimeSpan.FromSeconds(25);

        /// <summary>等 patchedConfigs 变 1 的窗口。实测 16~22 秒，留约 2 倍余量。</summary>
        private static readonly TimeSpan PatchTimeout = TimeSpan.FromSeconds(50);

        /// <summary>页内状态里"配置已被改写"的计数。</summary>
        private const string PatchedConfigsExpression =
            "String((globalThis.__codexZhI18nState||{}).patchedConfigs||0)";

        /// <summary>多久补一次注入。</summary>
        private static readonly TimeSpan ReinjectInterval = TimeSpan.FromSeconds(3);

        public static async Task<LaunchReport> LaunchAsync(
            CodexInstall source,
            string locale,
            Action<string> onProgress)
        {
            if (source == null || !source.IsValid)
                throw new InvalidOperationException("未检测到可用的 Codex Desktop。");

            var report = new LaunchReport();
            report.Mode = "inject";

            // locale_source='SYSTEM' 让 App 跟随**系统语言** —— 中文 Windows 上界面与原生菜单
            // 会一起中文化（真机实测）。但英文 Windows 上 'SYSTEM' 会解析成 en-US、界面回英文。
            //
            // 把 locale_source 设成 null（不碰它、交给 localeOverride 兜底）那条路
            // **还没真机验证过**，万一不灵就会变成"工具报成功、界面还是英文"的静默失败。
            // 所以这种机器上干脆不试注入，让 LaunchCoordinator 直接走已验证的磁盘补丁：
            // 慢一点（要复制），但确定可用。
            if (!IsChineseSystemUi())
            {
                report.Message = "系统界面语言不是中文，跳过运行时注入（'SYSTEM' 会解析成 en-US）。";
                AppLog.Write("inject.skip.locale=" + System.Globalization.CultureInfo.InstalledUICulture.Name);
                return report;
            }
            report.LocaleSource = "SYSTEM";

            // 随机回环端口，不用固定值 —— 免得和本机其它程序的调试端口撞上。
            var rendererPort = AppActivation.ReserveLoopbackPort();
            report.RendererPort = rendererPort;

            var arguments = AppActivation.BuildInjectionArguments(rendererPort, locale);
            Report(onProgress, "正在启动 Codex…");
            AppLog.Write("inject.begin path=" + source.ExecutablePath +
                " port=" + rendererPort + " locale=" + locale);

            // 刻意用 LaunchUnpackaged：商店版也要走 Process.Start 直接跑 exe（"未打包运行"），
            // 只有这条路实测能让 --remote-debugging-port 生效。
            report.ProcessId = AppActivation.LaunchUnpackaged(source, arguments);
            report.Started = true;

            Report(onProgress, "Codex 已启动，正在连接汉化接口…");

            DevToolsTarget target;
            try
            {
                target = await DevToolsClient.WaitForTargetAsync(rendererPort, "page", TargetTimeout);
            }
            catch (Exception ex)
            {
                report.Message = "等待调试目标超时（127.0.0.1:" + rendererPort + "）。";
                AppLog.Write("inject.target.timeout " + ex.Message);
                return report;
            }

            if (target == null || String.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
            {
                report.Message = "调试目标没有可用的 WebSocket 地址。";
                AppLog.Write("inject.target.nosocket");
                return report;
            }

            Report(onProgress, "正在注入中文（约 20 秒，请稍候）…");

            var bootstrap = LocalizationScripts.BuildI18nBootstrap(report.LocaleSource);
            var patched = await InjectUntilPatchedAsync(rendererPort, target, bootstrap, onProgress);
            report.InjectionDetail = "patchedConfigs=" + patched +
                "; target=" + (target.Type ?? "?") + "; port=" + rendererPort;

            if (patched < 1)
            {
                report.Message = "注入的钩子没有生效（等 " +
                    (int)PatchTimeout.TotalSeconds + " 秒未见 patchedConfigs>=1）。";
                AppLog.Write("inject.patch.timeout " + report.InjectionDetail);
                // 刻意**不**把 LocaleApplied/MenuApplied 置真 ——
                // Complete 保持 false，让 LaunchCoordinator 去走回退。
                return report;
            }

            report.LocaleApplied = true;
            report.MenuApplied = true;
            report.LocaleDetail = "运行时注入：enable_i18n 已强制为 true，locale_source = SYSTEM";
            report.MenuDetail = "原生菜单随 locale_source 走系统语言，已中文化";
            report.Message = "注入成功：界面与原生菜单均已中文化。";
            AppLog.Write("inject.ok " + report.InjectionDetail);
            return report;
        }

        /// <summary>
        /// 反复注入 + 轮询，直到 patchedConfigs &gt;= 1 或超时。
        ///
        /// **为什么要反复注入，而不是"等界面就绪再注一次"** —— 这是踩过坑换来的：
        ///   · 一开始"一看到 page target 就注入"：注入进了空页面，随后 App 导航到真正的
        ///     界面时把脚本冲掉，patchedConfigs 永远停在 0。
        ///   · 改成"等界面有文字再注入"：探针选错了元素 —— 这个 App 用**影子 DOM** 渲染，
        ///     #root 的 textContent 恒为 0（innerText 才有内容，但 innerText 依赖布局，
        ///     窗口没真正绘制时又是空串）。
        /// 与其继续猜"什么时候算就绪"，不如承认某个时间点是猜不准的：
        /// 周期性补注入，页面晚就绪、中途导航、整个渲染进程被换掉，都能自愈。
        /// 钩子是幂等的（patchClient 用 __codexZhI18nClient 去重）。
        /// </summary>
        private static async Task<int> InjectUntilPatchedAsync(
            int port,
            DevToolsTarget first,
            string bootstrap,
            Action<string> onProgress)
        {
            var started = DateTime.UtcNow;
            var deadline = started.Add(PatchTimeout);
            var current = first;
            var lastInjectedAt = DateTime.MinValue;
            var last = 0;
            var nudged = false;

            while (DateTime.UtcNow < deadline)
            {
                // 页面导航会换掉 webContents，target 也跟着换 —— 每次都重新列一次。
                try
                {
                    var targets = await DevToolsClient.ListTargetsAsync(port);
                    foreach (var candidate in targets)
                    {
                        if (candidate == null) continue;
                        if (!String.Equals(candidate.Type, "page", StringComparison.OrdinalIgnoreCase)) continue;
                        if (String.IsNullOrWhiteSpace(candidate.WebSocketDebuggerUrl)) continue;
                        current = candidate;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("inject.list.error " + ex.Message);
                }

                if (current == null || String.IsNullOrWhiteSpace(current.WebSocketDebuggerUrl))
                {
                    await Task.Delay(500);
                    continue;
                }

                try
                {
                    last = ParseInt(await DevToolsClient.EvaluateAsync(
                        current.WebSocketDebuggerUrl, PatchedConfigsExpression, false));
                    if (last >= 1) return last;
                }
                catch (Exception ex)
                {
                    // 连接抖动 / 页面正在导航 —— 正常，下个周期再来。
                    AppLog.Write("inject.poll.error " + ex.Message);
                }

                if ((DateTime.UtcNow - lastInjectedAt) >= ReinjectInterval)
                {
                    lastInjectedAt = DateTime.UtcNow;
                    try
                    {
                        await DevToolsClient.EvaluateAsync(current.WebSocketDebuggerUrl, bootstrap, false);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("inject.reinject.error " + ex.Message);
                    }
                }

                if (!nudged && DateTime.UtcNow >= started.AddSeconds(15))
                {
                    nudged = true;
                    Report(onProgress, "仍在等 App 重新读取配置（通常 15~25 秒）…");
                }

                await Task.Delay(700);
            }

            return last;
        }

        /// <summary>
        /// 系统界面语言是不是中文。只有是中文时才敢用 <c>locale_source='SYSTEM'</c>。
        /// 用 InstalledUICulture（安装时的系统 UI 语言）而不是 CurrentUICulture ——
        /// 后者会被本进程的 .NET 语言设置影响，不是系统真实语言。
        /// </summary>
        private static bool IsChineseSystemUi()
        {
            try
            {
                var ui = System.Globalization.CultureInfo.InstalledUICulture;
                return ui != null &&
                       String.Equals(ui.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;   // 读不到就当作"不是中文" —— 保守，走已验证的补丁路线
            }
        }

        /// <summary>构建期自检：启动参数串与轮询表达式。挂到 <c>--self-test</c>。</summary>
        public static string SelfTest()
        {
            var args = AppActivation.BuildInjectionArguments(9333, "zh-CN");
            if (args.IndexOf("--remote-debugging-port=9333", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("注入参数缺少 --remote-debugging-port。 ");
            if (args.IndexOf("--remote-allow-origins=http://127.0.0.1:9333", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("注入参数缺少 --remote-allow-origins。 ");
            // 这条最要紧：--inspect 那条路已被 Electron fuse 关闭，带上只会被静默忽略，
            // 让人误以为"注入了主进程菜单"。
            if (args.IndexOf("--inspect", StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("注入参数不该带 --inspect（该通道已被 fuse 关闭）。 ");
            if (args.IndexOf("  ", StringComparison.Ordinal) >= 0 || !String.Equals(args, args.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("注入参数串有多余空白。 ");
            if (PatchedConfigsExpression.IndexOf("patchedConfigs", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("轮询表达式与引导脚本的状态名对不上。 ");
            return "injection-runtime=ok; args=" + args;
        }

        private static int ParseInt(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return 0;
            int parsed;
            return Int32.TryParse(value.Trim(), out parsed) ? parsed : 0;
        }

        private static void Report(Action<string> callback, string message)
        {
            AppLog.Write("inject> " + message);
            if (callback == null) return;
            try { callback(message); }
            catch { }
        }
    }
}
