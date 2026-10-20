using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodexZhLauncher
{
    internal sealed class AsarReplacementDetail
    {
        public string Pattern;
        public string Desc;
        public int Hits;
    }

    internal sealed class AsarPatchResult
    {
        public string Status;            // ok | miss | length-mismatch | not-found | unpacked
        public int Hits;
        public int IntegrityUpdated;
        public bool Applied;
        public List<AsarReplacementDetail> Details = new List<AsarReplacementDetail>();
    }

    /// <summary>
    /// asar 原地等长替换。与 localize/AsarInPlace.ps1 的
    /// Invoke-AsarInPlacePatch / Update-AsarEntryIntegrity 对应。
    /// </summary>
    internal static class AsarPatcher
    {
        public static AsarPatchResult PatchEntry(
            string asarPath, string relPath, string from, string to, string desc, bool apply)
        {
            var result = new AsarPatchResult();
            var info = AsarArchive.ReadHeader(asarPath);
            var entry = AsarArchive.FindEntry(info, relPath);
            if (entry == null)
            {
                result.Status = "not-found";
                return result;
            }
            if (entry.Unpacked)
            {
                result.Status = "unpacked";
                return result;
            }
            if (entry.IsDirectory)
            {
                // 与 PowerShell 一致：目录节点没有 offset/size，读 0 字节后判 miss。
                result.Status = "miss";
                return result;
            }

            var bytes = AsarArchive.ReadEntryBytes(asarPath, info.DataOffset, entry.Offset, entry.Size);
            var text = Encoding.UTF8.GetString(bytes);

            int total = 0;
            if (!String.IsNullOrEmpty(from))
            {
                int n = CountOccurrences(text, from);
                var detail = new AsarReplacementDetail { Pattern = from, Desc = desc, Hits = n };
                result.Details.Add(detail);

                if (n > 0)
                {
                    if (to.Length != from.Length)
                    {
                        result.Status = "length-mismatch";
                        result.Hits = 0;
                        return result;
                    }
                    text = text.Replace(from, to);
                    total += n;
                }
            }

            if (total == 0)
            {
                result.Status = "miss";
                return result;
            }

            var newBytes = Encoding.UTF8.GetBytes(text);
            if (newBytes.Length != entry.Size)
            {
                result.Status = "length-mismatch";
                result.Hits = total;
                return result;
            }

            if (!apply)
            {
                result.Status = "ok";
                result.Hits = total;
                result.Applied = false;
                return result;
            }

            using (var fs = new FileStream(asarPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fs.Seek(info.DataOffset + entry.Offset, SeekOrigin.Begin);
                fs.Write(newBytes, 0, newBytes.Length);
                fs.Flush();
            }

            result.IntegrityUpdated = UpdateEntryIntegrity(asarPath, newBytes, entry);
            result.Status = "ok";
            result.Hits = total;
            result.Applied = true;
            return result;
        }

        /// <summary>
        /// 不改数据，只按条目当前字节重算并写回 per-file 完整性哈希。
        /// 用于 already-patched 分支：如果上次在“数据已写、头部 hash 未写”之间崩溃，
        /// 重跑时必须能把坏掉的 hash 修回来，否则 App 会一直 FATAL。
        /// </summary>
        public static int RepairEntryIntegrity(string asarPath, long dataOffset, AsarNode entry)
        {
            if (entry == null || entry.Integrity == null) return 0;
            var bytes = AsarArchive.ReadEntryBytes(asarPath, dataOffset, entry.Offset, entry.Size);
            return UpdateEntryIntegrity(asarPath, bytes, entry);
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while (true)
            {
                int hit = text.IndexOf(needle, index, StringComparison.Ordinal);
                if (hit < 0) break;
                count++;
                index = hit + needle.Length;
            }
            return count;
        }

        /// <summary>
        /// 只替换头部 JSON 字节里的 64 位十六进制串，不重新序列化。
        /// 返回替换次数。
        /// </summary>
        private static int UpdateEntryIntegrity(string asarPath, byte[] newBytes, AsarNode entry)
        {
            if (entry == null || entry.Integrity == null) return 0;

            var oldHashes = new List<string>();
            bool hasWholeHash = !String.IsNullOrEmpty(entry.Integrity.Hash);
            if (hasWholeHash) oldHashes.Add(entry.Integrity.Hash);
            if (entry.Integrity.Blocks != null)
            {
                foreach (var block in entry.Integrity.Blocks)
                {
                    if (!String.IsNullOrEmpty(block)) oldHashes.Add(block);
                }
            }
            if (oldHashes.Count == 0) return 0;

            string newWhole = AsarArchive.Sha256Hex(newBytes);
            var newBlocks = new List<string>();
            int blockSize = entry.Integrity.BlockSize;
            if (blockSize <= 0) blockSize = 4194304;
            int pos = 0;
            while (pos < newBytes.Length)
            {
                int len = Math.Min(blockSize, newBytes.Length - pos);
                var chunk = new byte[len];
                Array.Copy(newBytes, pos, chunk, 0, len);
                newBlocks.Add(AsarArchive.Sha256Hex(chunk));
                pos += len;
            }
            if (newBlocks.Count == 0) newBlocks.Add(newWhole);

            int headerLen;
            byte[] head;
            using (var fs = new FileStream(asarPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var lenBytes = new byte[4];
                fs.Seek(12, SeekOrigin.Begin);
                if (fs.Read(lenBytes, 0, 4) != 4) throw new InvalidDataException("读取 asar 头部长度失败");
                headerLen = (int)BitConverter.ToUInt32(lenBytes, 0);
                if (headerLen <= 0) throw new InvalidDataException("asar 头部长度异常: " + headerLen);

                fs.Seek(16, SeekOrigin.Begin);
                head = new byte[headerLen];
                int read = 0;
                while (read < head.Length)
                {
                    int n = fs.Read(head, read, head.Length - read);
                    if (n <= 0) throw new InvalidDataException("读取 asar 头部失败");
                    read += n;
                }

                int replaced = 0;
                for (int i = 0; i < oldHashes.Count; i++)
                {
                    string oldHex = oldHashes[i];
                    string newHex;
                    if (hasWholeHash)
                    {
                        newHex = (i == 0)
                            ? newWhole
                            : newBlocks[Math.Min(i - 1, newBlocks.Count - 1)];
                    }
                    else
                    {
                        // 只有 blocks、没有整文件 hash 时，PS 的索引假设会把
                        // blocks[0] 当成整文件哈希。这里显式修正为一一对应。
                        newHex = newBlocks[Math.Min(i, newBlocks.Count - 1)];
                    }

                    if (oldHex.Length != 64 || String.IsNullOrEmpty(newHex) ||
                        String.Equals(oldHex, newHex, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var oldByteArray = Encoding.ASCII.GetBytes(oldHex);
                    var newByteArray = Encoding.ASCII.GetBytes(newHex);
                    int index = 0;
                    while (true)
                    {
                        int hit = IndexOfBytes(head, oldByteArray, index);
                        if (hit < 0) break;
                        Array.Copy(newByteArray, 0, head, hit, newByteArray.Length);
                        replaced++;
                        index = hit + newByteArray.Length;
                    }
                }

                if (replaced > 0)
                {
                    fs.Seek(16, SeekOrigin.Begin);
                    fs.Write(head, 0, head.Length);
                    fs.Flush();
                }
                return replaced;
            }
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle, int start)
        {
            int last = haystack.Length - needle.Length;
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
    }
}
