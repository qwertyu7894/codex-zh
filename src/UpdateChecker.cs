using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Linq;

namespace CodexZhLauncher
{
    internal sealed class UpdateCheckResult
    {
        public bool UpdateAvailable { get; set; }
        public string LatestVersion { get; set; }
        public string ReleaseUrl { get; set; }
        public string AssetName { get; set; }
        public string AssetUrl { get; set; }
        public long AssetSize { get; set; }
        public string ChecksumUrl { get; set; }
        public string Message { get; set; }
    }

    internal static class UpdateChecker
    {
        // 指向本分支自己的仓库，从 AppInfo.RepositorySlug 派生 —— 仓库改名时不用改这里。
        //
        // 注意：GitHub 的 releases/latest **不返回** draft 和 prerelease，
        // 所以"最新版"天然就是正式版，和 docs/VERSIONING.md 里"不打预发布档"的约定一致。
        private const string LatestReleaseApi =
            "https://api.github.com/repos/" + AppInfo.RepositorySlug + "/releases/latest";

        // 8 秒。api.github.com 在部分网络下根本连不上（国内是常态），
        // 卡住不如早点回来让用户看到"没查成、去页面手动下"。
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);

        // 两个品牌在 Release 里各自对应一个裸 exe 附件，名字与 scripts/package.py 的
        // BUILDS 一致。自更新只下载"自己这个品牌"的那一个。
        private const string DefaultAssetName = "CodexZh-Windows-x64.exe";
        private const string HandoffAssetName = "GPTHanhua.exe";

        private sealed class GitHubRelease
        {
            public string tag_name { get; set; }
            public string html_url { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public GitHubAsset[] assets { get; set; }
        }

        private sealed class GitHubAsset
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
            public long size { get; set; }
        }

        /// <summary>
        /// 查一次最新版本。**只有用户在「关于」窗口里点「检查更新」才会走到这里** ——
        /// 工具启动时不再联网（见 MainWindow：启动时的自动检查已拆掉）。
        ///
        /// 查不成（网络不通、被墙、还没发过 Release）时**抛异常**而不是假装"已是最新"：
        /// 假装没问题会让用户以为真没问题，从此再不来看。
        /// </summary>
        public static async Task<UpdateCheckResult> CheckAsync(string currentVersion)
        {
            string json;
            try
            {
                json = await FetchLatestReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Write("update.check.unreachable " + ex.Message);
                throw new InvalidOperationException(
                    "拿不到 GitHub 上的版本信息（检查网络或代理后重试）；" +
                    "也可以直接到 GitHub Releases 页面手动下载。");
            }

            var result = Parse(json, currentVersion, ExpectedAssetName());
            AppLog.Write("update.check " + result.Message);
            return result;
        }

        /// <summary>
        /// 自更新要下载**当前这个品牌**的 exe。
        ///
        /// 两个品牌共用同一份源码，只在编译时换了程序集标题（窗口标题、程序属性）。
        /// 如果一律拉 CodexZh-Windows-x64.exe，那么把 GPTHanhua.exe 发给客户后，
        /// 客户一更新，窗口标题就会**变成另一个牌子** —— 看着像被换了程序。
        /// 所以按当前运行的 exe 文件名挑对应附件。
        /// </summary>
        private static string ExpectedAssetName()
        {
            var fileName = String.Empty;
            try { fileName = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName); }
            catch (Exception ex) { AppLog.Write("update.assetname.failed " + ex.Message); }
            return String.Equals(fileName, HandoffAssetName, StringComparison.OrdinalIgnoreCase)
                ? HandoffAssetName
                : DefaultAssetName;
        }

        private static async Task<string> FetchLatestReleaseAsync()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new HttpClient())
            {
                client.Timeout = CheckTimeout;
                // GitHub API 不带 User-Agent 会直接 403。
                client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexZhLauncher/" + AppInfo.Version);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                return await client.GetStringAsync(LatestReleaseApi).ConfigureAwait(false);
            }
        }

        internal static UpdateCheckResult Parse(string json, string currentVersion, string assetName)
        {
            var release = new JavaScriptSerializer().Deserialize<GitHubRelease>(json);
            if (release == null || release.draft || release.prerelease)
                throw new InvalidOperationException("GitHub 返回的最新版本无效。");

            var latest = ParseVersion(release.tag_name);
            var current = ParseVersion(currentVersion);
            var releaseUri = ValidateReleaseUrl(release.html_url);
            var available = latest > current;
            GitHubAsset executable = null;
            GitHubAsset checksums = null;
            if (available)
            {
                var assets = release.assets ?? new GitHubAsset[0];
                executable = assets.FirstOrDefault(item =>
                    String.Equals(item.name, assetName, StringComparison.Ordinal));
                checksums = assets.FirstOrDefault(item =>
                    String.Equals(item.name, "SHA256SUMS.txt", StringComparison.Ordinal));
                if (executable == null || checksums == null || executable.size <= 0)
                    throw new InvalidOperationException(
                        "GitHub Release 里缺少 " + assetName + " 或 SHA256SUMS.txt；" +
                        "发布时要用 scripts/release.ps1 -Publish，它会一起传上去。");
            }
            return new UpdateCheckResult
            {
                UpdateAvailable = available,
                LatestVersion = latest.ToString(3),
                ReleaseUrl = releaseUri.AbsoluteUri,
                AssetName = executable == null ? null : executable.name,
                AssetUrl = executable == null ? null : ValidateAssetUrl(executable.browser_download_url, executable.name).AbsoluteUri,
                AssetSize = executable == null ? 0 : executable.size,
                ChecksumUrl = checksums == null ? null : ValidateAssetUrl(checksums.browser_download_url, checksums.name).AbsoluteUri,
                Message = available
                    ? "发现新版本 v" + latest.ToString(3)
                    : "当前已是最新版本 v" + current.ToString(3)
            };
        }

        internal static string SelfTest()
        {
            const string newer =
                "{\"tag_name\":\"v0.6.1\",\"html_url\":\"https://github.com/qwertyu7894/codex-zh/releases/tag/v0.6.1\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"CodexZh-Windows-x64.exe\",\"browser_download_url\":\"https://github.com/qwertyu7894/codex-zh/releases/download/v0.6.1/CodexZh-Windows-x64.exe\",\"size\":1000},{\"name\":\"GPTHanhua.exe\",\"browser_download_url\":\"https://github.com/qwertyu7894/codex-zh/releases/download/v0.6.1/GPTHanhua.exe\",\"size\":1000},{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://github.com/qwertyu7894/codex-zh/releases/download/v0.6.1/SHA256SUMS.txt\",\"size\":298}]}";
            const string current =
                "{\"tag_name\":\"v0.6.0\",\"html_url\":\"https://github.com/qwertyu7894/codex-zh/releases/tag/v0.6.0\",\"draft\":false,\"prerelease\":false}";
            var newerResult = Parse(newer, "0.6.0", DefaultAssetName);
            var currentResult = Parse(current, "0.6.0", DefaultAssetName);
            if (!newerResult.UpdateAvailable || currentResult.UpdateAvailable)
                throw new InvalidOperationException("更新版本比较自检失败。");
            if (String.IsNullOrWhiteSpace(newerResult.AssetUrl) || String.IsNullOrWhiteSpace(newerResult.ChecksumUrl))
                throw new InvalidOperationException("更新文件解析自检失败。");

            // 两个品牌必须各取各的附件 —— 取错了会悄悄把用户的牌子换掉。
            var handoffResult = Parse(newer, "0.6.0", HandoffAssetName);
            if (!String.Equals(handoffResult.AssetName, HandoffAssetName, StringComparison.Ordinal))
                throw new InvalidOperationException("按品牌挑选更新文件自检失败。");

            try
            {
                Parse(
                    "{\"tag_name\":\"v9.0.0\",\"html_url\":\"https://example.com/releases/tag/v9.0.0\",\"draft\":false,\"prerelease\":false}",
                    "0.6.0",
                    DefaultAssetName);
                throw new InvalidOperationException("外部更新地址未被拒绝。");
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message == "外部更新地址未被拒绝。") throw;
            }
            return "update-check=ok";
        }

        private static Version ParseVersion(string value)
        {
            var normalized = (value ?? String.Empty).Trim().TrimStart('v', 'V');
            var suffix = normalized.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0) normalized = normalized.Substring(0, suffix);
            Version version;
            if (!Version.TryParse(normalized, out version))
                throw new InvalidOperationException("无法识别版本号：" + value);
            return new Version(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);
        }

        private static Uri ValidateReleaseUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith(
                    "/" + AppInfo.RepositorySlug + "/releases/",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("更新地址不是本项目的 GitHub Release。");
            }
            return uri;
        }

        private static Uri ValidateAssetUrl(string value, string assetName)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith(
                    "/" + AppInfo.RepositorySlug + "/releases/download/",
                    StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.EndsWith("/" + assetName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("更新文件地址不是本项目的 GitHub Release。");
            }
            return uri;
        }
    }
}
