using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexZhLauncher
{
    /// <summary>
    /// 目录操作工具。
    ///
    /// 这里刻意**不走 System.IO 的递归 API**，而是直接调 Win32：
    /// Codex 26.1016 起 app 里多了
    /// <c>resources\cua_node\bin\node_modules\@oai\...\leveldb\...</c> 这类深层目录，
    /// 单条相对路径就有 168 个字符。加上 <c>%USERPROFILE%\.codex\codex-zh\app</c>
    /// 或 <c>C:\Program Files\WindowsApps\OpenAI.Codex_&lt;版本&gt;_x64__...</c> 前缀，
    /// 轻松超过 260 字符的 MAX_PATH —— 客户机真实案例（2026-10-31，26.1027）：
    /// 补丁跑 5 秒后直接失败，报"指定的路径或文件名太长"。
    ///
    /// 只加 <c>\\?\</c> 前缀不够：本项目用 legacy csc 编译、没有 app.config，
    /// .NET Framework 走的是旧路径处理，<c>\\?\</c> 会被判成"非法字符"。
    /// 所以文件/目录的枚举、创建、读写、删除全部落到 Win32 API 上，
    /// 由扩展长度前缀把上限抬到 32767。
    /// </summary>
    internal static class FileSystemUtils
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint OPEN_EXISTING = 3;
        private const uint CREATE_ALWAYS = 2;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindData
        {
            public uint FileAttributes;
            public FileTime CreationTime;
            public FileTime LastAccessTime;
            public FileTime LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint Reserved0;
            public uint Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string AlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string pattern, out Win32FindData data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr handle, out Win32FindData data);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint attributes, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryW(string path, IntPtr security);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveDirectoryW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetFileAttributesW(string path, uint attributes);

        private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

        /// <summary>
        /// 转成 Win32 的扩展长度路径（<c>\\?\C:\...</c>）。
        /// 扩展前缀要求绝对路径、只用反斜杠、不能含 <c>.</c>/<c>..</c> 段 ——
        /// 本类拼出来的路径都满足。
        /// </summary>
        public static string ExtendedPath(string path)
        {
            if (String.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;

            var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + full.Substring(2);
            return @"\\?\" + full;
        }

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
        /// 逐字节递归复制。不保留 ACL / 属性 / 时间戳（与 PowerShell 实现一致），
        /// 不跟随重解析点进死循环以外的特殊处理 —— WindowsApps 里的目录都是普通目录。
        /// </summary>
        public static void CopyTree(string source, string destination)
        {
            CopyTreeCore(ToExtended(source), ToExtended(destination));
        }

        private static void CopyTreeCore(string source, string destination)
        {
            CreateDirectoryTree(destination);

            var buffer = new byte[8 * 1024 * 1024];
            foreach (var entry in Enumerate(source))
            {
                var sourceChild = source + @"\" + entry.Name;
                var targetChild = destination + @"\" + entry.Name;

                if ((entry.Attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    CopyTreeCore(sourceChild, targetChild);
                    continue;
                }

                try
                {
                    using (var input = OpenRead(sourceChild))
                    using (var output = OpenCreate(targetChild))
                    {
                        while (true)
                        {
                            int read = input.Read(buffer, 0, buffer.Length);
                            if (read <= 0) break;
                            output.Write(buffer, 0, read);
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new IOException("复制失败：" + sourceChild + " -> " + targetChild +
                        "（" + ex.Message + "）", ex);
                }
            }
        }

        /// <summary>
        /// 创建目录，并自动补建缺失的父目录。
        ///
        /// Win32 的 <c>CreateDirectoryW</c> 只创建最后一级 —— 父目录不存在时返回
        /// ERROR_PATH_NOT_FOUND；而 System.IO 的 <c>Directory.CreateDirectory</c> 会一路补建。
        /// 换成 Win32 之后必须自己把这个语义补回来，否则全新机器上
        /// <c>%USERPROFILE%\.codex\codex-zh</c> 还不存在时首次安装会直接失败。
        /// 客户机真实案例（2026-11-02，用户 HP）：报"系统找不到指定的路径"。
        /// </summary>
        private static void CreateDirectoryTree(string extendedPath)
        {
            // 盘符根（\\?\C:）必然存在，也不该再往上补。
            if (extendedPath.EndsWith(":", StringComparison.Ordinal)) return;

            if (CreateDirectoryW(extendedPath, IntPtr.Zero)) return;
            int error = Marshal.GetLastWin32Error();
            if (error == 183) return; // ERROR_ALREADY_EXISTS

            var trimmed = extendedPath.TrimEnd('\\');
            int split = trimmed.LastIndexOf('\\');
            if (split > 0)
            {
                CreateDirectoryTree(trimmed.Substring(0, split));
                if (CreateDirectoryW(extendedPath, IntPtr.Zero)) return;
                error = Marshal.GetLastWin32Error();
                if (error == 183) return;
            }

            throw new IOException("无法创建目录：" + extendedPath + "（" +
                new Win32Exception(error).Message + "）");
        }

        /// <summary>等价于 Remove-Item -Recurse -Force：先清只读属性再删。</summary>
        public static void DeleteTreeForce(string path)
        {
            var extended = ToExtended(path);
            if (GetFileAttributesW(extended) == INVALID_FILE_ATTRIBUTES) return;
            DeleteTreeCore(extended);
        }

        private static void DeleteTreeCore(string directory)
        {
            foreach (var entry in Enumerate(directory))
            {
                var child = directory + @"\" + entry.Name;
                // 只读属性会让 DeleteFile/RemoveDirectory 直接失败，先清掉。
                SetFileAttributesW(child, FILE_ATTRIBUTE_NORMAL);
                if ((entry.Attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    DeleteTreeCore(child);
                    continue;
                }
                if (!DeleteFileW(child))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException("删除失败：" + child + "（" +
                        new Win32Exception(error).Message + "）");
                }
            }

            if (!RemoveDirectoryW(directory))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("删除目录失败：" + directory + "（" +
                    new Win32Exception(error).Message + "）");
            }
        }

        private static string ToExtended(string path)
        {
            var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
            return ExtendedPath(full);
        }

        private sealed class Entry
        {
            public string Name;
            public uint Attributes;
        }

        /// <summary>列出目录下的条目（不含 <c>.</c> / <c>..</c>）。</summary>
        private static System.Collections.Generic.List<Entry> Enumerate(string extendedDirectory)
        {
            var result = new System.Collections.Generic.List<Entry>();
            Win32FindData data;
            var handle = FindFirstFileW(extendedDirectory + @"\*", out data);
            if (handle == InvalidHandle)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 2 || error == 3) return result; // 不存在
                throw new IOException("无法枚举目录：" + extendedDirectory + "（" +
                    new Win32Exception(error).Message + "）");
            }

            try
            {
                do
                {
                    if (data.FileName == "." || data.FileName == "..") continue;
                    var entry = new Entry();
                    entry.Name = data.FileName;
                    entry.Attributes = data.FileAttributes;
                    result.Add(entry);
                }
                while (FindNextFileW(handle, out data));
            }
            finally
            {
                FindClose(handle);
            }
            return result;
        }

        private static FileStream OpenRead(string path)
        {
            var handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero,
                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (handle == InvalidHandle)
                throw new IOException("无法打开文件（" +
                    new Win32Exception(Marshal.GetLastWin32Error()).Message + "）");
            return new FileStream(new SafeFileHandle(handle, true), FileAccess.Read, 1 << 20);
        }

        private static FileStream OpenCreate(string path)
        {
            var handle = CreateFileW(path, GENERIC_WRITE, 0, IntPtr.Zero,
                CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (handle == InvalidHandle)
                throw new IOException("无法创建文件（" +
                    new Win32Exception(Marshal.GetLastWin32Error()).Message + "）");
            return new FileStream(new SafeFileHandle(handle, true), FileAccess.Write, 1 << 20);
        }
    }
}
