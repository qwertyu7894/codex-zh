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
        public const string Version = "1.1.0";
        public const string RepositoryUrl = "https://github.com/shibaweidu/codex-desktop-zh";
        public const string FeedbackUrl = RepositoryUrl + "/issues/new/choose";
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
