using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexZhLauncher
{
    internal static class CopyCleanupTests
    {
        private static int assertions;

        public static int Run()
        {
            var work = Path.Combine(Path.GetTempPath(), "codexzh-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var root = Path.Combine(work, "user with spaces", "codex-zh");
                var config = Path.Combine(work, "config");
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(config);
                File.WriteAllText(Path.Combine(root, "sentinel"), "keep");
                var request = Request(root, config);
                var toml = Path.Combine(config, "config.toml");
                var json = Path.Combine(config, "chrome-native-hosts-v2.json");
                var child = Path.Combine(root, "app", "resources", "node.exe");
                var serializer = new JavaScriptSerializer();
                int deletes = 0;
                Action<string> delete = delegate(string path)
                {
                    Assert(path == root, "only the explicit root reaches deletion");
                    deletes++;
                };

                Assert(CopyCleanupGuard.Check(request).Reason == "config-missing",
                    "missing configs are distinguishable from unreadable configs");
                AssertBlocked(request, delete, ref deletes, "missing configs cannot be locked against new creation");
                File.WriteAllText(toml, "localeOverride = 'zh-CN'", new UTF8Encoding(false));
                File.WriteAllText(json, "{}", new UTF8Encoding(false));
                Assert(CopyCleanupGuard.TryCleanup(request, delete).State == CopyCleanupState.ClearWithinScope && deletes == 1,
                    "approved clean scope calls deletion once");

                foreach (var mode in new[] { "patch", "", "unknown" })
                {
                    request.Mode = mode;
                    AssertBlocked(request, delete, ref deletes, "non-inject route");
                }
                request.Mode = "inject";
                request.LaunchComplete = false;
                AssertBlocked(request, delete, ref deletes, "incomplete launch");
                request.LaunchComplete = true;
                request.Approved = false;
                AssertBlocked(request, delete, ref deletes, "no approval");
                request.Approved = true;
                request.CopyRoot = root + "-other";
                AssertBlocked(request, delete, ref deletes, "foreign target");
                request.CopyRoot = root;

                var values = new[]
                {
                    "command = '" + child + "'",
                    "command = " + serializer.Serialize(child),
                    "notify = [" + serializer.Serialize(child) + ", 'turn-ended']",
                    "paths = 'C:\\other;" + child + ";C:\\third'",
                    "command = '" + child.ToUpperInvariant().Replace('\\', '/') + "'",
                    "command = '" + root + "\\'",
                    "command = '" + Path.Combine(root, "..", "codex-zh", "app", "node.exe") + "'",
                    "command = '" + Path.GetDirectoryName(root) + "\\other\\..\\codex-zh\\node.exe'",
                    "command = '" + child + " --flag'",
                    "command = '\"" + child + "\" --flag'",
                    "command = '%TEST_COPY%\\app\\node.exe'",
                    "command = '\\\\?\\" + child + "'",
                    "command = " + serializer.Serialize(child).Replace("c", "\\u0063").Replace("C:", "\\u0043:"),
                    "# retained reference: " + child + "\nlocaleOverride = 'zh-CN'"
                };
                foreach (var value in values)
                {
                    File.WriteAllText(toml, value, new UTF8Encoding(false));
                    var before = File.ReadAllBytes(toml);
                    Assert(CopyCleanupGuard.Check(request).State == CopyCleanupState.Referenced, "TOML reference " + assertions);
                    AssertBlocked(request, delete, ref deletes, "TOML reference blocks approved retry");
                    Assert(Equal(before, File.ReadAllBytes(toml)), "scan preserves config bytes");
                }

                var escaped = serializer.Serialize(child).Replace("c", "\\u0063").Replace("C:", "\\u0043:");
                File.WriteAllText(toml, "command = " + escaped, new UTF8Encoding(false));
                Assert(CopyCleanupGuard.Check(request).State == CopyCleanupState.Referenced, "TOML Unicode escape");
                File.WriteAllText(toml, "command = 'C:\\unrelated\\node.exe'", new UTF8Encoding(false));
                foreach (var value in new[]
                {
                    "{\"nested\":{\"paths\":[" + serializer.Serialize(child) + "]}}",
                    "{\"path\":" + serializer.Serialize(child.Replace('\\', '/')).Replace("/", "\\/") + "}",
                    "{\"path\":" + escaped + "}",
                    "{\"path\":" + serializer.Serialize(child) + ",\"path\":\"C:/other\"}",
                    "{" + serializer.Serialize(child) + ":true}"
                })
                {
                    File.WriteAllText(json, value, new UTF8Encoding(false));
                    Assert(CopyCleanupGuard.Check(request).State != CopyCleanupState.ClearWithinScope, "JSON nested, escaped, duplicate keys");
                    AssertBlocked(request, delete, ref deletes, "JSON reference blocks deletion");
                }

                File.WriteAllText(json, "{}", new UTF8Encoding(false));
                File.WriteAllText(toml, "command = '" + root + "-backup\\node.exe'", new UTF8Encoding(false));
                Assert(CopyCleanupGuard.Check(request).State == CopyCleanupState.ClearWithinScope, "similar prefix is not target");
                Assert(CopyCleanupGuard.TryCleanup(request, delete).State == CopyCleanupState.ClearWithinScope, "clean retry allowed");
                File.WriteAllText(toml, "command = '" + child + "'", new UTF8Encoding(false));
                AssertBlocked(request, delete, ref deletes, "new reference invalidates previous clean result");

                foreach (var value in new[]
                {
                    "command = \"bad\\q\"",
                    "command = 'unterminated",
                    "command = '''multiline'''",
                    "command = '%UNKNOWN_COPY%\\node.exe'",
                    "command = '${HOME}/node.exe'"
                })
                {
                    File.WriteAllText(toml, value, new UTF8Encoding(false));
                    Assert(CopyCleanupGuard.Check(request).State == CopyCleanupState.Unknown, "unsupported or malformed value");
                    AssertBlocked(request, delete, ref deletes, "unknown cannot delete");
                }
                File.WriteAllText(toml, "localeOverride = 'zh-CN'", new UTF8Encoding(false));
                using (var locked = new FileStream(toml, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert(CopyCleanupGuard.Check(request).State == CopyCleanupState.Unknown, "locked is not missing");
                    AssertBlocked(request, delete, ref deletes, "locked file prevents deletion");
                }
                File.WriteAllBytes(toml, new byte[] { 0xff, 0xfe, 0x00 });
                AssertBlocked(request, delete, ref deletes, "invalid encoding");
                File.WriteAllText(toml, "localeOverride = 'zh-CN'", new UTF8Encoding(false));
                File.WriteAllText(json, "{bad json", new UTF8Encoding(false));
                AssertBlocked(request, delete, ref deletes, "invalid JSON");
                File.WriteAllText(json, "{}", new UTF8Encoding(false));

                request.CodexHome = "relative-home";
                AssertBlocked(request, delete, ref deletes, "relative CODEX_HOME");
                request.CodexHome = "%UNKNOWN_COPY%";
                AssertBlocked(request, delete, ref deletes, "unknown CODEX_HOME");
                var extraConfig = Path.Combine(work, "extra-config");
                Directory.CreateDirectory(extraConfig);
                File.WriteAllText(Path.Combine(extraConfig, "config.toml"), "command = '" + child + "'");
                request.CodexHome = extraConfig;
                AssertBlocked(request, delete, ref deletes, "explicit CODEX_HOME checked too");
                request.CodexHome = null;

                var apostropheRoot = Path.Combine(work, "O'Brien", "codex-zh");
                Directory.CreateDirectory(apostropheRoot);
                var apostropheRequest = Request(apostropheRoot, config);
                File.WriteAllText(json, "{}");
                File.WriteAllText(toml, "command = " + serializer.Serialize(apostropheRoot + "\\app\\node.exe"));
                AssertBlocked(apostropheRequest, delete, ref deletes, "apostrophe in legal Windows path");
                File.WriteAllText(toml, "command = '" + child + " --flag\\..\\..\\..\\..\\outside'");
                AssertBlocked(request, delete, ref deletes, "command arguments cannot normalize away executable");
                File.WriteAllText(toml, "command = '" + Path.GetDirectoryName(root) +
                    "\\other\\..\\codex-zh\\app\\node.exe --flag\\..\\..\\..\\..\\outside'");
                AssertBlocked(request, delete, ref deletes, "normalized executable checked separately from arguments");
                File.WriteAllText(toml, "command = " + child);
                AssertBlocked(request, delete, ref deletes, "unquoted TOML path is not safe input");
                File.WriteAllText(toml, "command = 'cd /d " + root + "&&app\\node.exe'");
                AssertBlocked(request, delete, ref deletes, "shell operator after root");
                foreach (var relative in new[] { "\\user\\codex-zh\\app", "C:codex-zh\\app", "safe;..\\codex-zh\\app" })
                {
                    File.WriteAllText(toml, "command = '" + relative + "'");
                    AssertBlocked(request, delete, ref deletes, "root-relative or list relative path");
                }
                File.WriteAllText(toml, "localeOverride = 'zh-CN'");
                var foreignRoot = Path.Combine(work, "foreign-root");
                Directory.CreateDirectory(foreignRoot);
                request.CodexHome = "%MUTATE%";
                request.ResolveEnvironment = delegate(string key)
                {
                    request.CopyRoot = foreignRoot;
                    return config;
                };
                string recycledRoot = null;
                var mutationResult = CopyCleanupGuard.TryCleanup(request, delegate(string target) { recycledRoot = target; });
                Assert(mutationResult.State == CopyCleanupState.ClearWithinScope && recycledRoot == root,
                    "environment resolver cannot change approved deletion target");
                request.CopyRoot = root;
                request.CodexHome = null;
                request.ResolveEnvironment = delegate(string key) { return key == "TEST_COPY" ? root : null; };

                const string secret = "FAKE_SECRET_DO_NOT_LOG_8675309";
                File.WriteAllText(toml, "token = '" + secret + "'\ncommand = '" + child + "'");
                var diagnostic = CopyCleanupGuard.Check(request).Describe();
                Assert(!diagnostic.Contains(secret) && !diagnostic.Contains(child), "diagnostics contain no config values");

                // 实际配置句柄应保持到删除动作结束，不能仅缓存一次 Safe 结果。
                File.WriteAllText(toml, "localeOverride = 'zh-CN'");
                bool lockedDuringDelete = false;
                CopyCleanupGuard.TryCleanup(request, delegate(string ignored)
                {
                    try
                    {
                        using (var writer = new FileStream(toml, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
                    }
                    catch (IOException) { lockedDuringDelete = true; }
                });
                Assert(lockedDuringDelete, "config cannot be changed between scan and cleanup");
                Assert(File.ReadAllText(Path.Combine(root, "sentinel")) == "keep", "blocked scans never remove copy");
                TestJunction(work, request, delete, ref deletes);
                LegacyCopyDiscoveryTests.Run(work);
                Console.WriteLine("legacy-copy-discovery=ok");
                Console.WriteLine("copy-cleanup assertions=" + assertions);
                return 0;
            }
            finally
            {
                // 夹具也是文件：遵守项目的回收站规则，不做永久递归删除。
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(work,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            }
        }

        private static void TestJunction(string work, CopyCleanupRequest request, Action<string> delete, ref int deletes)
        {
            var outside = Path.Combine(work, "junction-target");
            var link = Path.Combine(request.CopyRoot, "junction");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "sentinel"), "outside");
            var start = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = "/d /c mklink /J \"" + link + "\" \"" + outside + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    Console.WriteLine("SKIP junction: creation not permitted");
                    return;
                }
            }
            try
            {
                AssertBlocked(request, delete, ref deletes, "junction in copy tree");
                Assert(File.ReadAllText(Path.Combine(outside, "sentinel")) == "outside", "junction target untouched");
                var oldExpected = request.ExpectedRoot;
                var oldCopy = request.CopyRoot;
                request.ExpectedRoot = request.CopyRoot = link;
                AssertBlocked(request, delete, ref deletes, "junction as copy root");
                request.CopyRoot = oldCopy;
                request.ExpectedRoot = oldExpected;
                Console.WriteLine("junction-guard=ok");
            }
            finally
            {
                // 这里只移除测试创建的空链接，不跟随、不递归，不删除目标数据。
                Directory.Delete(link);
            }
        }

        private static CopyCleanupRequest Request(string root, string config)
        {
            return new CopyCleanupRequest
            {
                CopyRoot = root, ExpectedRoot = root, DefaultConfigDirectory = config,
                Mode = "inject", LaunchComplete = true, Approved = true,
                ResolveEnvironment = delegate(string key) { return key == "TEST_COPY" ? root : null; }
            };
        }

        private static void AssertBlocked(CopyCleanupRequest request, Action<string> delete, ref int deletes, string label)
        {
            int before = deletes;
            var result = CopyCleanupGuard.TryCleanup(request, delete);
            Assert(result.State != CopyCleanupState.ClearWithinScope, label);
            Assert(deletes == before, label + " never reaches deletion");
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            return Convert.ToBase64String(a) == Convert.ToBase64String(b);
        }

        private static void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("cleanup test failed: " + message);
        }
    }
}
