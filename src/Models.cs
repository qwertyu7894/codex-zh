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

