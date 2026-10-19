using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace CodexZhLauncher
{
    internal sealed class CodexPackageInfo
    {
        public string Name;
        public string Version;
        public string PackageFamilyName;
        public string InstallLocation;
        public string DisplayName;
        public string PackageId;
    }

    /// <summary>
    /// 原生读取当前用户的 MSIX 包信息，替代旧的 PowerShell 包查询。
    ///
    /// 数据源：HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\
    ///         CurrentVersion\AppModel\Repository\Packages
    /// 子键名就是 PackageID：Name_Version_Arch_ResourceId_PublisherId。
    /// </summary>
    internal static class NativePackageLocator
    {
        private const string RepositoryKey =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        public static List<CodexPackageInfo> FindAll()
        {
            var result = new List<CodexPackageInfo>();
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(RepositoryKey))
                {
                    if (root == null) return result;
                    foreach (var subKeyName in root.GetSubKeyNames())
                    {
                        var info = ParsePackageId(subKeyName);
                        if (info == null) continue;

                        using (var sub = root.OpenSubKey(subKeyName))
                        {
                            if (sub == null) continue;
                            info.InstallLocation = sub.GetValue("PackageRootFolder") as string;
                            info.DisplayName = sub.GetValue("DisplayName") as string;
                        }
                        if (String.IsNullOrWhiteSpace(info.InstallLocation)) continue;
                        result.Add(info);
                    }
                }
            }
            catch
            {
                // 与旧的包查询一致：读不到就返回空。
            }
            return result;
        }

        public static CodexPackageInfo FindNewest(string packageName)
        {
            CodexPackageInfo best = null;
            foreach (var info in FindAll())
            {
                if (!String.Equals(info.Name, packageName, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || CompareVersions(info.Version, best.Version) > 0) best = info;
            }
            return best;
        }

        private static CodexPackageInfo ParsePackageId(string packageId)
        {
            if (String.IsNullOrEmpty(packageId)) return null;
            var parts = packageId.Split('_');
            if (parts.Length < 5) return null;

            var name = parts[0];
            var version = parts[1];
            var publisherId = parts[parts.Length - 1];
            if (String.IsNullOrEmpty(name) || String.IsNullOrEmpty(version) || String.IsNullOrEmpty(publisherId))
                return null;

            return new CodexPackageInfo
            {
                Name = name,
                Version = version,
                PackageFamilyName = name + "_" + publisherId,
                PackageId = packageId
            };
        }

        public static int CompareVersions(string a, string b)
        {
            Version va, vb;
            if (Version.TryParse(a, out va) && Version.TryParse(b, out vb))
                return va.CompareTo(vb);
            return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        }
    }
}
