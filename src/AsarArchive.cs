using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexZhLauncher
{
    /// <summary>
    /// asar 头部解析结果。只读用途；任何写操作都必须在原始字节上做等长覆盖，
    /// 绝不重新序列化头部 JSON。
    /// </summary>
    internal sealed class AsarHeaderInfo
    {
        public string AsarPath;
        public AsarNode Root;
        public int JsonLen;
        public long DataOffset;
        public long FileLength;
    }

    /// <summary>asar 头部里的一个节点：目录（Files/Children 非空）或叶子文件。</summary>
    internal sealed class AsarNode
    {
        public string Name;

        // 目录节点
        public Dictionary<string, AsarNode> Files;
        public List<AsarNode> Children;

        // 叶子节点
        public long Offset;
        public long Size;
        public bool Unpacked;
        public AsarIntegrity Integrity;

        public bool IsDirectory
        {
            get { return Files != null; }
        }
    }

    internal sealed class AsarIntegrity
    {
        public string Hash;              // 可能为空
        public List<string> Blocks;      // 可能为空
        public int BlockSize = 4194304;  // 与 PowerShell 实现的默认值一致
    }

    internal sealed class AsarEntryRef
    {
        public string Path;
        public long Offset;
        public long Size;
        public bool Unpacked;
    }

    internal static class AsarArchive
    {
        private const int HeaderPrefixLength = 16;
        private const int MaxJsonLength = 512 * 1024 * 1024;

        public static AsarHeaderInfo ReadHeader(string asarPath)
        {
            using (var fs = new FileStream(asarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < HeaderPrefixLength)
                    throw new InvalidDataException("asar 文件过小: " + asarPath);

                uint u0, u1, u2, u3;
                using (var br = new BinaryReader(fs, Encoding.UTF8, true))
                {
                    u0 = br.ReadUInt32();
                    u1 = br.ReadUInt32();
                    u2 = br.ReadUInt32();
                    u3 = br.ReadUInt32();

                    if (u0 != 4)
                        throw new InvalidDataException("asar 头起始字段异常：u0=" + u0);
                    if (u1 != u2 + 4)
                        throw new InvalidDataException("asar 头部长度字段不自洽：u1=" + u1 + " u2=" + u2);
                    if (u3 == 0 || u3 > MaxJsonLength || u3 > u2)
                        throw new InvalidDataException("asar JSON 长度异常：u3=" + u3 + " u2=" + u2);

                    fs.Seek(HeaderPrefixLength, SeekOrigin.Begin);
                    var jsonBytes = br.ReadBytes((int)u3);
                    if (jsonBytes.Length != (int)u3)
                        throw new InvalidDataException("读取 asar 头部失败: " + asarPath);

                    var json = Encoding.UTF8.GetString(jsonBytes);
                    var serializer = CreateSerializer();
                    var raw = serializer.Deserialize<Dictionary<string, object>>(json);
                    if (raw == null)
                        throw new InvalidDataException("asar 头部 JSON 解析为空: " + asarPath);

                    return new AsarHeaderInfo
                    {
                        AsarPath = asarPath,
                        Root = BuildNode(String.Empty, raw),
                        JsonLen = (int)u3,
                        DataOffset = 8L + (long)u1,
                        FileLength = fs.Length
                    };
                }
            }
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer();
            // 默认 2 MB / 递归 100 对 5.8 MB、层级很深的 asar 头部不够用。
            serializer.MaxJsonLength = Int32.MaxValue;
            serializer.RecursionLimit = 512;
            return serializer;
        }

        private static AsarNode BuildNode(string name, Dictionary<string, object> raw)
        {
            var node = new AsarNode { Name = name };

            object filesValue;
            if (TryGetValueIgnoreCase(raw, "files", out filesValue))
            {
                var files = filesValue as Dictionary<string, object>;
                if (files != null)
                {
                    node.Files = new Dictionary<string, AsarNode>(StringComparer.OrdinalIgnoreCase);
                    node.Children = new List<AsarNode>();
                    foreach (var pair in files)
                    {
                        var childRaw = pair.Value as Dictionary<string, object>;
                        if (childRaw == null) continue;
                        var child = BuildNode(pair.Key, childRaw);
                        // 大小写不同的同名键在 asar 里不应出现；真出现时保留第一个，
                        // 与 PowerShell 按属性名查找的语义一致。
                        if (!node.Files.ContainsKey(pair.Key))
                        {
                            node.Files[pair.Key] = child;
                            node.Children.Add(child);
                        }
                    }
                    return node;
                }
            }

            node.Offset = GetInt64(raw, "offset");
            node.Size = GetInt64(raw, "size");

            object unpackedValue;
            if (TryGetValueIgnoreCase(raw, "unpacked", out unpackedValue) && unpackedValue != null)
            {
                try { node.Unpacked = Convert.ToBoolean(unpackedValue, CultureInfo.InvariantCulture); }
                catch { node.Unpacked = false; }
            }

            object integrityValue;
            if (TryGetValueIgnoreCase(raw, "integrity", out integrityValue))
            {
                var integrityRaw = integrityValue as Dictionary<string, object>;
                if (integrityRaw != null) node.Integrity = BuildIntegrity(integrityRaw);
            }
            return node;
        }

        private static AsarIntegrity BuildIntegrity(Dictionary<string, object> raw)
        {
            var integrity = new AsarIntegrity();
            object value;
            if (TryGetValueIgnoreCase(raw, "hash", out value) && value != null)
                integrity.Hash = Convert.ToString(value, CultureInfo.InvariantCulture);

            if (TryGetValueIgnoreCase(raw, "blocks", out value) && value != null)
            {
                // JavaScriptSerializer 返回 object[]；某些运行时会给出 ArrayList，
                // 所以按 IList 处理，避免 blocks 被静默忽略。
                var items = value as System.Collections.IList;
                if (items != null)
                {
                    integrity.Blocks = new List<string>();
                    foreach (var item in items)
                    {
                        if (item != null) integrity.Blocks.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
                    }
                }
            }

            if (TryGetValueIgnoreCase(raw, "blockSize", out value) && value != null)
            {
                try
                {
                    var blockSize = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    if (blockSize > 0 && blockSize <= Int32.MaxValue) integrity.BlockSize = (int)blockSize;
                }
                catch { }
            }
            return integrity;
        }

        private static bool TryGetValueIgnoreCase(Dictionary<string, object> dict, string key, out object value)
        {
            if (dict.TryGetValue(key, out value)) return true;
            foreach (var pair in dict)
            {
                if (String.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        private static long GetInt64(Dictionary<string, object> dict, string key)
        {
            object value;
            if (!TryGetValueIgnoreCase(dict, key, out value) || value == null) return 0;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public static AsarNode FindEntry(AsarHeaderInfo info, string relPath)
        {
            if (info == null || info.Root == null) return null;
            var parts = relPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var node = info.Root;
            foreach (var part in parts)
            {
                if (node.Files == null) return null;
                AsarNode next;
                if (!node.Files.TryGetValue(part, out next)) return null;
                node = next;
            }
            return node;
        }

        public static List<AsarEntryRef> EnumerateLeaves(AsarHeaderInfo info)
        {
            var result = new List<AsarEntryRef>();
            if (info == null || info.Root == null) return result;
            WalkLeaves(info.Root, String.Empty, result);
            return result;
        }

        private static void WalkLeaves(AsarNode node, string rel, List<AsarEntryRef> output)
        {
            if (node.IsDirectory)
            {
                foreach (var child in node.Children)
                {
                    var childRel = String.IsNullOrEmpty(rel) ? child.Name : rel + "/" + child.Name;
                    WalkLeaves(child, childRel, output);
                }
                return;
            }
            output.Add(new AsarEntryRef
            {
                Path = rel,
                Offset = node.Offset,
                Size = node.Size,
                Unpacked = node.Unpacked
            });
        }

        public static List<AsarEntryRef> EntriesInScope(AsarHeaderInfo info, string scope, string ext)
        {
            string prefix = NormalizeScope(scope);
            var result = new List<AsarEntryRef>();
            foreach (var entry in EnumerateLeaves(info))
            {
                if (String.IsNullOrEmpty(entry.Path)) continue;
                if (!String.IsNullOrEmpty(prefix) &&
                    !entry.Path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!String.IsNullOrEmpty(ext) &&
                    !entry.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.Unpacked) continue;
                result.Add(entry);
            }
            return result;
        }

        private static string NormalizeScope(string scope)
        {
            if (scope == null) return String.Empty;
            return scope.Replace('\\', '/').Trim('/');
        }

        public static byte[] ReadEntryBytes(string asarPath, long dataOffset, long offset, long size)
        {
            if (size < 0 || size > Int32.MaxValue)
                throw new InvalidDataException("asar 条目尺寸异常: " + size);

            using (var fs = new FileStream(asarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(dataOffset + offset, SeekOrigin.Begin);
                var buffer = new byte[(int)size];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = fs.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) throw new InvalidDataException("读取 asar 数据失败: offset=" + offset);
                    read += n;
                }
                return buffer;
            }
        }

        public static bool EntryContains(string asarPath, long dataOffset, long offset, long size, string needle)
        {
            if (String.IsNullOrEmpty(needle)) return false;
            var needleBytes = Encoding.UTF8.GetBytes(needle);
            if (needleBytes.Length == 0 || size < needleBytes.Length) return false;

            var bytes = ReadEntryBytes(asarPath, dataOffset, offset, size);
            var text = Encoding.UTF8.GetString(bytes);
            return text.IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", String.Empty).ToLowerInvariant();
            }
        }

        public static string HeaderHash(string asarPath)
        {
            var info = ReadHeader(asarPath);
            using (var fs = new FileStream(asarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(HeaderPrefixLength, SeekOrigin.Begin);
                var buffer = new byte[info.JsonLen];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = fs.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) throw new InvalidDataException("读取 asar 头部失败");
                    read += n;
                }
                return Sha256Hex(buffer);
            }
        }
    }
}
