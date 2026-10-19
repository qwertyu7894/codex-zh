using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CodexZhLauncher
{
    internal sealed class DiscoveredInstall
    {
        public string AppDir;
        public string Kind;   // store | writable
        public CodexPackageInfo Package;
    }

    /// <summary>
    /// 与 CodexZh.ps1 的 Find-CodexInstall / Get-CodexVersion 对应。
    /// 探测顺序：显式指定 → Store 包 → 注册表 Uninstall 三根键。
    /// </summary>
    internal static class CodexInstallLocator
    {
        private static readonly Regex StorePackageName = new Regex(
            "OpenAI.*Codex|^Codex$|^ChatGPT$", RegexOptions.IgnoreCase);
        private static readonly Regex UninstallDisplayName = new Regex(
            "Codex|ChatGPT", RegexOptions.IgnoreCase);
        private static readonly Regex VersionFromDirectory = new Regex(
            @"OpenAI\.Codex_([0-9][0-9.]*)", RegexOptions.IgnoreCase);
        private static readonly Regex IniAppVersion = new Regex(
            @"^\s*AppVersion\s*=\s*(.+)$", RegexOptions.IgnoreCase);

        public static DiscoveredInstall Find(string overridePath)
        {
            if (!String.IsNullOrWhiteSpace(overridePath))
            {
                foreach (var path in new[] { overridePath, Path.Combine(overridePath, "app") })
                {
                    var appDir = TryAppDir(path, true);
                    if (appDir == null) continue;
                    return new DiscoveredInstall
                    {
                        AppDir = appDir,
                        Kind = IsWindowsAppsPath(appDir) ? "store" : "writable"
                    };
                }
                return null;
            }

            // Microsoft Store / MSIX：原生注册表，不再调用 PowerShell 包查询。
            try
            {
                CodexPackageInfo best = null;
                foreach (var package in NativePackageLocator.FindAll())
                {
                    if (!StorePackageName.IsMatch(package.Name)) continue;
                    if (best == null ||
                        NativePackageLocator.CompareVersions(package.Version, best.Version) > 0)
                        best = package;
                }
                if (best != null && !String.IsNullOrWhiteSpace(best.InstallLocation))
                {
                    foreach (var path in new[]
                    {
                        Path.Combine(best.InstallLocation, "app"),
                        best.InstallLocation
                    })
                    {
                        var appDir = TryAppDir(path, false);
                        if (appDir == null) continue;
                        return new DiscoveredInstall { AppDir = appDir, Kind = "store", Package = best };
                    }
                }
            }
            catch { }

            // 官网安装版（注册表 Uninstall）
            foreach (var root in GetUninstallRoots())
            {
                foreach (var appDir in FindUninstallInstalls(root))
                {
                    return new DiscoveredInstall { AppDir = appDir, Kind = "writable" };
                }
            }

            return null;
        }

        private static IEnumerable<RegistryKey> GetUninstallRoots()
        {
            var roots = new List<RegistryKey>();
            try { roots.Add(Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")); }
            catch { }
            try { roots.Add(Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")); }
            catch { }
            try { roots.Add(Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")); }
            catch { }
            return roots;
        }

        private static IEnumerable<string> FindUninstallInstalls(RegistryKey root)
        {
            var result = new List<string>();
            if (root == null) return result;
            try
            {
                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    using (var sub = root.OpenSubKey(subKeyName))
                    {
                        if (sub == null) continue;
                        var displayName = sub.GetValue("DisplayName") as string;
                        var installLocation = sub.GetValue("InstallLocation") as string;
                        if (String.IsNullOrWhiteSpace(displayName) || String.IsNullOrWhiteSpace(installLocation))
                            continue;
                        if (!UninstallDisplayName.IsMatch(displayName)) continue;

                        foreach (var path in new[] { installLocation, Path.Combine(installLocation, "app") })
                        {
                            var appDir = TryAppDir(path, false);
                            if (appDir != null) { result.Add(appDir); break; }
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private static string TryAppDir(string path, bool acceptCodexExe)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path)) return null;
                var asar = Path.Combine(path, "resources", "app.asar");
                var chatGpt = Path.Combine(path, "ChatGPT.exe");
                var codex = Path.Combine(path, "Codex.exe");
                if (!File.Exists(asar)) return null;
                if (!File.Exists(chatGpt) && !(acceptCodexExe && File.Exists(codex))) return null;
                return Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsWindowsAppsPath(string path)
        {
            return path != null &&
                path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static DiscoveredInstall FromHint(CodexInstall hint, string localizedAppDir)
        {
            if (hint == null || String.IsNullOrWhiteSpace(hint.InstallLocation)) return null;
            // 副本优先的 DetectAsync 会把本地化副本当成"安装"；那不是源安装。
            if (!String.IsNullOrWhiteSpace(localizedAppDir) &&
                String.Equals(Path.GetFullPath(hint.InstallLocation).TrimEnd('\\'),
                              Path.GetFullPath(localizedAppDir).TrimEnd('\\'),
                              StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (var path in new[]
            {
                hint.InstallLocation,
                Path.Combine(hint.InstallLocation, "app")
            })
            {
                var appDir = TryAppDir(path, true);
                if (appDir == null) continue;
                return new DiscoveredInstall
                {
                    AppDir = appDir,
                    Kind = IsWindowsAppsPath(appDir) ? "store" : "writable"
                };
            }
            return null;
        }

        public static string GetVersion(string appDir)
        {
            try
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(appDir));
                if (!String.IsNullOrEmpty(parent))
                {
                    var match = VersionFromDirectory.Match(parent);
                    if (match.Success) return match.Groups[1].Value;
                }

                var ini = Path.Combine(appDir, "resources", "owl-app.ini");
                if (File.Exists(ini))
                {
                    foreach (var line in File.ReadAllLines(ini))
                    {
                        var match = IniAppVersion.Match(line);
                        if (match.Success) return match.Groups[1].Value.Trim();
                    }
                }
            }
            catch { }
            return "unknown";
        }
    }
}
