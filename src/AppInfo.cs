using System.Diagnostics;

namespace CodexZhLauncher
{
    internal static class AppInfo
    {
        // 自研分支版本。上游最新为 0.7.7；本分支改用磁盘补丁方案
        // （本地化副本 + asar 原地改写），与原版不是同一条线，故从 1.0.0 重新起算。
        public const string Version = "1.0.0";
        public const string RepositoryUrl = "https://github.com/shibaweidu/codex-desktop-zh";
        public const string FeedbackUrl = RepositoryUrl + "/issues/new/choose";
        public const string LatestReleaseUrl = RepositoryUrl + "/releases/latest";
        public const string SponsorUrl = "https://www.appkaola.com";

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
