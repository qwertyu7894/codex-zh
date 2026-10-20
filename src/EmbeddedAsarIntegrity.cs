using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodexZhLauncher
{
    internal sealed class EmbeddedIntegrityResult
    {
        public string Status;   // already-ok | needs-update | updated | not-found
        public string File;
        public string Old;
        public string New;
        public string Hash;
    }

    /// <summary>
    /// Electron 的嵌入式完整性校验（fuse: EnableEmbeddedAsarIntegrityValidation）
    /// 比对的是 asar 头部 JSON 的 SHA-256。该值以 ASCII JSON 记录的形式
    /// 嵌在 app 目录根下某个 exe/dll 里：
    ///   [{"file":"resources\\app.asar","alg":"SHA256","value":"&lt;64 hex&gt;"}]
    /// 本类负责定位并以等长覆盖方式写回。
    /// </summary>
    internal static class EmbeddedAsarIntegrity
    {
        // 不带开头的 '['：这样同一个 JSON 数组里的多条记录（{...},{...}）
        // 也能逐条找到；app.asar.unpacked 会被下面的精确文件名判断排除。
        private static readonly byte[] RecordMarker = Encoding.ASCII.GetBytes("{\"file\":\"");
        private static readonly byte[] ValueMarker = Encoding.ASCII.GetBytes("\"value\":\"");
        private const int MaxRecordSpan = 512;
        private const int HashLength = 64;

        public static EmbeddedIntegrityResult Update(string appDir, string asarPath, bool apply)
        {
            var result = new EmbeddedIntegrityResult();
            string newHash;
            try
            {
                newHash = AsarArchive.HeaderHash(asarPath);
            }
            catch (Exception ex)
            {
                result.Status = "not-found";
                result.New = ex.Message;
                return result;
            }
            result.New = newHash;
            result.Hash = newHash;

            var candidates = GetCandidates(appDir);
            foreach (var candidate in candidates)
            {
                // 一个文件里可能有多条 integrity 记录；app.asar 不一定是第一条。
                // 逐个查找，直到找到一个合法的 app.asar 记录。
                long searchStart = 0;
                while (true)
                {
                    long recordPos;
                    using (var fs = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        recordPos = FindBytes(fs, RecordMarker, searchStart);
                    }
                    if (recordPos < 0) break;

                    long fileLength = new FileInfo(candidate).Length;
                    int windowLength = (int)Math.Min((long)MaxRecordSpan + 128, fileLength - recordPos);
                    if (windowLength > 0)
                    {
                        var window = new byte[windowLength];
                        int read = 0;
                        using (var fs = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            fs.Seek(recordPos, SeekOrigin.Begin);
                            while (read < window.Length)
                            {
                                int n = fs.Read(window, read, window.Length - read);
                                if (n <= 0) break;
                                read += n;
                            }
                        }

                        if (read == window.Length)
                        {
                            // RecordMarker 已经包含 file 值的开头引号；读到下一个引号，
                            // 再做精确文件名判断，避免 app.asar.unpacked 误匹配。
                            int fileEnd = IndexOfByte(window, (byte)'"', RecordMarker.Length);
                            if (fileEnd > RecordMarker.Length)
                            {
                                var fileValue = Encoding.ASCII.GetString(
                                    window, RecordMarker.Length, fileEnd - RecordMarker.Length)
                                    .Replace("\\\\", "\\");
                                if (String.Equals(Path.GetFileName(fileValue), "app.asar",
                                    StringComparison.OrdinalIgnoreCase))
                                {
                                    int valueOffset = IndexOfBytes(window, ValueMarker, fileEnd);
                                    if (valueOffset >= 0 && valueOffset <= MaxRecordSpan)
                                    {
                                        int hashStartInWindow = valueOffset + ValueMarker.Length;
                                        if (hashStartInWindow + HashLength <= window.Length)
                                        {
                                            var oldHash = Encoding.ASCII.GetString(
                                                window, hashStartInWindow, HashLength);
                                            if (IsHex64(oldHash))
                                            {
                                                result.File = candidate;
                                                result.Old = oldHash;

                                                if (String.Equals(oldHash, newHash,
                                                    StringComparison.OrdinalIgnoreCase))
                                                {
                                                    result.Status = "already-ok";
                                                    return result;
                                                }
                                                if (!apply)
                                                {
                                                    result.Status = "needs-update";
                                                    return result;
                                                }

                                                long hashStart = recordPos + hashStartInWindow;
                                                var newBytes = Encoding.ASCII.GetBytes(newHash);
                                                using (var fs = new FileStream(candidate, FileMode.Open,
                                                    FileAccess.ReadWrite, FileShare.Read))
                                                {
                                                    fs.Seek(hashStart, SeekOrigin.Begin);
                                                    fs.Write(newBytes, 0, HashLength);
                                                    fs.Flush();
                                                }
                                                result.Status = "updated";
                                                result.Old = oldHash;
                                                result.New = newHash;
                                                return result;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    searchStart = recordPos + RecordMarker.Length;
                }
            }

            result.Status = "not-found";
            return result;
        }

        private static List<string> GetCandidates(string appDir)
        {
            var result = new List<string>();
            if (String.IsNullOrEmpty(appDir) || !Directory.Exists(appDir)) return result;

            foreach (var file in Directory.GetFiles(appDir))
            {
                var ext = Path.GetExtension(file);
                if (!String.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Get-ChildItem 默认不列出 Hidden/System 文件。
                try
                {
                    var attributes = System.IO.File.GetAttributes(file);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                }
                catch { continue; }
                result.Add(file);
            }

            result.Sort(delegate(string a, string b)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(
                    Path.GetFileName(a), Path.GetFileName(b));
            });
            return result;
        }

        private static long FindBytes(FileStream fs, byte[] needle, long start)
        {
            const int bufferSize = 1024 * 1024;
            var buffer = new byte[bufferSize + needle.Length];
            long absolute = start;
            int carry = 0;

            fs.Seek(start, SeekOrigin.Begin);
            while (true)
            {
                int read = fs.Read(buffer, carry, bufferSize);
                if (read <= 0) return -1;
                int available = carry + read;
                int hit = IndexOfBytes(buffer, needle, 0, available);
                if (hit >= 0) return absolute - carry + hit;

                carry = Math.Min(needle.Length - 1, available);
                if (carry > 0)
                    Array.Copy(buffer, available - carry, buffer, 0, carry);
                absolute += read;
            }
        }

        private static int IndexOfByte(byte[] haystack, byte needle, int start)
        {
            for (int i = start; i < haystack.Length; i++)
            {
                if (haystack[i] == needle) return i;
            }
            return -1;
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle, int start)
        {
            return IndexOfBytes(haystack, needle, start, haystack.Length);
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle, int start, int count)
        {
            int last = count - needle.Length;
            for (int i = start; i <= last; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static bool IsHex64(string value)
        {
            if (value == null || value.Length != HashLength) return false;
            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') ||
                           (c >= 'a' && c <= 'f') ||
                           (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }
}
