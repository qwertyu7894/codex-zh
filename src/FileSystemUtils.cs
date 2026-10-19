using System;
using System.IO;

namespace CodexZhLauncher
{
    internal static class FileSystemUtils
    {
        public static bool IsDirectoryWritable(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, ".wtest-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 逐字节递归复制。不用 CopyFile：WindowsApps 里有重解析点/EFS，
        /// 走字节流最稳。不保留 ACL / 属性 / 时间戳（与 PowerShell 实现一致）。
        /// </summary>
        public static void CopyTree(string source, string destination)
        {
            if (!Directory.Exists(destination)) Directory.CreateDirectory(destination);

            foreach (var dir in Directory.EnumerateDirectories(source))
            {
                CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
            }

            var buffer = new byte[8 * 1024 * 1024];
            foreach (var file in Directory.EnumerateFiles(source))
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    while (true)
                    {
                        int read = input.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        output.Write(buffer, 0, read);
                    }
                }
            }
        }

        /// <summary>等价于 Remove-Item -Recurse -Force：先清只读属性再删。</summary>
        public static void DeleteTreeForce(string path)
        {
            if (!Directory.Exists(path)) return;
            ClearReadOnly(path);
            Directory.Delete(path, true);
        }

        private static void ClearReadOnly(string dir)
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
                catch { }
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                ClearReadOnly(sub);
                try
                {
                    var attributes = File.GetAttributes(sub);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(sub, attributes & ~FileAttributes.ReadOnly);
                }
                catch { }
            }
        }
    }
}
