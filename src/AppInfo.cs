using System.Diagnostics;

namespace CodexZhLauncher
{
    // partial：BrandName / BrandTagline 由 build.ps1 生成到 obj\Brand.g.cs，
    // 因此同一份源码可以构建出不同名字的成品（-Brand 参数）。
    internal static partial class AppInfo
    {
        // 自研分支版本。上游最新为 0.7.7；本分支改用磁盘补丁方案
        // （本地化副本 + asar 原地改写），与原版不是同一条线，故从 1.0.0 重新起算。
        //
        // ⚠️ 这是版本号的**唯一来源**：Program.cs 的程序集版本、
        //    scripts/package.py 的发布包名都从这里读/对齐。
        //    改版本号只需改这一处，然后同步 Program.cs 的两行程序集版本。
        public const string Version = "1.1.4";
        // 指向本分支自己的仓库（二次开发版，不指向上游 shibaweidu/codex-desktop-zh）。
        // 「反馈支持」「GitHub 仓库」两个按钮，以及 UpdateChecker 的 URL 白名单，
        // 全部从 RepositorySlug 派生 —— 仓库改名时只需改这一处。
        public const string RepositorySlug = "qwertyu7894/codex-zh";
        public const string RepositoryUrl = "https://github.com/" + RepositorySlug;
        public const string FeedbackUrl = RepositoryUrl + "/issues/new";
        public const string LatestReleaseUrl = RepositoryUrl + "/releases/latest";

        public static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
    }
}
