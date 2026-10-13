using System;

namespace CodexZhLauncher
{
    internal sealed class CodexInstall
    {
        public string Kind;
        public string DisplayName;
        public string Version;
        public string InstallLocation;
        public string ExecutablePath;
        public string AppUserModelId;

        /// <summary>
        /// 源安装（商店包）**已经升级到**的版本号。
        /// 仅当本地化副本落后于源安装时才有值 —— 非空即表示
        /// "你手上的中文版还是旧版 Codex，重打一次才能跟上"。
        /// 两侧版本一律取自 App 自带的 resources\owl-app.ini（见 CodexDiscovery.ReadAppVersion）。
        /// </summary>
        public string StaleSourceVersion;

        public bool IsStorePackage
        {
            get { return !String.IsNullOrWhiteSpace(AppUserModelId); }
        }

        public bool IsValid
        {
            get
            {
                return IsStorePackage ||
                    (!String.IsNullOrWhiteSpace(ExecutablePath) && System.IO.File.Exists(ExecutablePath));
            }
        }
    }

    internal sealed class LaunchReport
    {
        public bool Started;
        public bool LocaleApplied;
        public bool MenuApplied;
        public uint ProcessId;
        public int RendererPort;
        public int InspectorPort;
        public string Message;
        public string LocaleDetail;
        public string MenuDetail;

        /// <summary>本次走的哪条路线：<c>"inject"</c>（运行时注入）/ <c>"patch"</c>（磁盘补丁）。</summary>
        public string Mode;

        /// <summary>注入路线的详情，形如 <c>patchedConfigs=1; target=page; port=54321</c>。</summary>
        public string InjectionDetail;

        /// <summary>注入失败并回退到磁盘补丁时的原因；注入成功时为空。</summary>
        public string FallbackReason;

        /// <summary>语言来源：<c>"SYSTEM"</c>（跟随系统语言）/ <c>"OVERRIDE"</c>（写入 localeOverride）。</summary>
        public string LocaleSource;

        public bool Complete
        {
            get { return Started && LocaleApplied && MenuApplied; }
        }
    }

    internal sealed class ProcessResult
    {
        public int ExitCode;
        public string Output;
        public string Error;

        public bool Success
        {
            get { return ExitCode == 0; }
        }
    }
}

