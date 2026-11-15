using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexZhLauncher
{
    internal enum CopyCleanupState
    {
        ClearWithinScope,
        Referenced,
        Unknown
    }

    internal sealed class CopyCleanupRequest
    {
        public string CopyRoot;
        public string ExpectedRoot;
        public string DefaultConfigDirectory;
        public string CodexHome;
        public string Mode;
        public bool LaunchComplete;
        public bool Approved;
        public Func<string, string> ResolveEnvironment;
    }

    internal sealed class CopyCleanupResult
    {
        public CopyCleanupState State;
        public string Reason;
        public int References;
        public readonly List<string> Files = new List<string>();

        // 不保存配置原文、动态键名或异常 Message；它们可能包含密钥。
        public string Describe()
        {
            return "state=" + State + " reason=" + Reason + " references=" + References +
                " files=" + String.Join(",", Files.ToArray());
        }
    }

    /// <summary>
    /// 删除整个副本前的只读保护。注入成功不代表 MCP/notify 不再依赖副本。
    /// 旧 approved 只代表用户选择，每次真正清理仍必须重新检查。
    /// 不用于固定 app 路径内的升级换新，也不改写用户配置。
    /// </summary>
    internal static class CopyCleanupGuard
    {
        private const int MaxConfigBytes = 8 * 1024 * 1024;
        private static readonly Regex Variable = new Regex(@"%([A-Za-z_][A-Za-z0-9_]*)%");

        public static CopyCleanupResult Check(CopyCleanupRequest request)
        {
            using (var inspection = Inspect(request)) return inspection.Result;
        }

        public static CopyCleanupResult TryCleanup(CopyCleanupRequest request, Action<string> recycle)
        {
            if (request == null || !request.Approved) return Unknown("not-approved");
            var snapshot = new CopyCleanupRequest
            {
                CopyRoot = request.CopyRoot, ExpectedRoot = request.ExpectedRoot,
                DefaultConfigDirectory = request.DefaultConfigDirectory, CodexHome = request.CodexHome,
                Mode = request.Mode, LaunchComplete = request.LaunchComplete, Approved = request.Approved,
                ResolveEnvironment = request.ResolveEnvironment
            };
            using (var inspection = Inspect(snapshot))
            {
                if (inspection.Result.State != CopyCleanupState.ClearWithinScope) return inspection.Result;
                // 再检查目录边界；配置句柄一直持有到清理结束，拒绝并发写入/替换。
                // 这不是对任意恶意并发换链接的句柄级安全保证。
                try { CheckTree(inspection.Root); }
                catch (ScanFailure ex) { return Unknown(ex.Code); }
                catch (Exception) { return Unknown("tree-check-failed"); }
                recycle(inspection.Root);
                return inspection.Result;
            }
        }

        private sealed class Inspection : IDisposable
        {
            public CopyCleanupResult Result = new CopyCleanupResult
            {
                State = CopyCleanupState.ClearWithinScope, Reason = "checked-scope"
            };
            public readonly List<FileStream> Handles = new List<FileStream>();
            public string Root;

            public void Dispose()
            {
                foreach (var handle in Handles) handle.Dispose();
            }
        }

        private static Inspection Inspect(CopyCleanupRequest request)
        {
            var inspection = new Inspection();
            try
            {
                if (request == null || !request.LaunchComplete ||
                    !String.Equals(request.Mode, "inject", StringComparison.Ordinal))
                    throw new ScanFailure("route-not-eligible");
                var root = CanonicalPath(request.CopyRoot);
                inspection.Root = root;
                if (!String.Equals(root, CanonicalPath(request.ExpectedRoot), StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(root, Path.GetPathRoot(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new ScanFailure("unexpected-root");
                CheckTree(root);

                var directories = new List<string>();
                directories.Add(CanonicalPath(request.DefaultConfigDirectory));
                if (!String.IsNullOrWhiteSpace(request.CodexHome))
                {
                    var home = CanonicalPath(Expand(request.CodexHome, request.ResolveEnvironment));
                    if (!String.Equals(home, directories[0], StringComparison.OrdinalIgnoreCase)) directories.Add(home);
                }
                for (int i = 0; i < directories.Count; i++)
                {
                    var prefix = i == 0 ? "default/" : "CODEX_HOME/";
                    ReadConfig(inspection, Path.Combine(directories[i], "config.toml"),
                        prefix + "config.toml", false, root, request.ResolveEnvironment);
                    ReadConfig(inspection, Path.Combine(directories[i], "chrome-native-hosts-v2.json"),
                        prefix + "chrome-native-hosts-v2.json", true, root, request.ResolveEnvironment);
                }
            }
            catch (ScanFailure ex)
            {
                if (inspection.Result.State != CopyCleanupState.Referenced)
                    inspection.Result.State = CopyCleanupState.Unknown;
                inspection.Result.Reason = ex.Code;
            }
            catch (Exception)
            {
                if (inspection.Result.State != CopyCleanupState.Referenced)
                    inspection.Result.State = CopyCleanupState.Unknown;
                inspection.Result.Reason = "inspection-failed";
            }
            return inspection;
        }

        private static void ReadConfig(Inspection inspection, string path, string label, bool json,
            string root, Func<string, string> environment)
        {
            FileStream stream;
            try
            {
                CheckAncestors(Path.GetDirectoryName(path), true);
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new ScanFailure("config-not-regular-file");
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            // 缺失与读取失败原因分开，但都不允许清理：运行中的 Codex 可能
            // 在检查后新建配置，缺失文件没有可持有的句柄来阻止这类并发写入。
            catch (FileNotFoundException) { throw new ScanFailure("config-missing"); }
            catch (DirectoryNotFoundException) { throw new ScanFailure("config-missing"); }
            inspection.Handles.Add(stream);
            inspection.Result.Files.Add(label);
            if (stream.Length > MaxConfigBytes) throw new ScanFailure("config-too-large");
            var bytes = new byte[(int)stream.Length];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = stream.Read(bytes, read, bytes.Length - read);
                if (n == 0) throw new ScanFailure("config-truncated");
                read += n;
            }
            var text = Decode(bytes);
            var values = new List<string>();
            if (json)
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaxConfigBytes, RecursionLimit = 128 };
                try { serializer.DeserializeObject(text); }
                catch (Exception) { throw new ScanFailure("json-invalid"); }
                // 从原文逐个取 token：重复键也要扫描，不能只依赖字典最后一个值。
                for (int i = 0; i < text.Length; i++)
                    if (text[i] == '"') values.Add(ReadString(text, ref i, true));
            }
            else
            {
                var unquoted = new StringBuilder();
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '#')
                    {
                        int end = text.IndexOf('\n', i);
                        if (end < 0) end = text.Length;
                        values.Add(text.Substring(i, end - i));
                        i = end - 1;
                    }
                    else if (text[i] == '\'' || text[i] == '"')
                        values.Add(ReadString(text, ref i, false));
                    else
                        unquoted.Append(text[i]);
                }
                // 合法 TOML 路径应在字符串内。词法器不是通用 TOML 解析器，
                // 发现裸路径/转义不能把它当作“没有字符串引用”而放行。
                if (Regex.IsMatch(unquoted.ToString(), @"[\\/%$]"))
                    throw new ScanFailure("toml-unquoted-expression");
            }
            foreach (var value in values)
            {
                if (ReferencesRoot(value, root, environment))
                {
                    inspection.Result.State = CopyCleanupState.Referenced;
                    inspection.Result.Reason = "config-reference";
                    inspection.Result.References++;
                }
            }
        }

        private static string Decode(byte[] bytes)
        {
            int skip = 0;
            Encoding encoding = new UTF8Encoding(false, true);
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) skip = 3;
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            {
                encoding = new UnicodeEncoding(false, true, true); skip = 2;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            {
                encoding = new UnicodeEncoding(true, true, true); skip = 2;
            }
            try { return encoding.GetString(bytes, skip, bytes.Length - skip); }
            catch (DecoderFallbackException) { throw new ScanFailure("encoding-invalid"); }
        }

        private static string ReadString(string text, ref int index, bool json)
        {
            char quote = text[index];
            if (!json && index + 2 < text.Length && text[index + 1] == quote && text[index + 2] == quote)
                throw new ScanFailure("toml-multiline-unsupported");
            var value = new StringBuilder();
            for (index++; index < text.Length; index++)
            {
                char c = text[index];
                if (c == quote) return value.ToString();
                if (c < ' ') throw new ScanFailure("string-control-character");
                if (c != '\\' || (!json && quote == '\'')) { value.Append(c); continue; }
                if (++index >= text.Length) throw new ScanFailure("string-truncated");
                c = text[index];
                switch (c)
                {
                    case '\\': value.Append('\\'); break;
                    case '"': value.Append('"'); break;
                    case '/': if (!json) throw new ScanFailure("toml-escape-invalid"); value.Append('/'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'u':
                    case 'U':
                        int digits = c == 'u' ? 4 : 8;
                        if (json && digits == 8) throw new ScanFailure("json-escape-invalid");
                        uint code;
                        if (index + digits >= text.Length || !UInt32.TryParse(text.Substring(index + 1, digits),
                            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                            throw new ScanFailure("unicode-escape-invalid");
                        if (digits == 4 && json) value.Append((char)code);
                        else
                        {
                            if (code > 0x10ffff || (code >= 0xd800 && code <= 0xdfff))
                                throw new ScanFailure("unicode-escape-invalid");
                            value.Append(Char.ConvertFromUtf32((int)code));
                        }
                        index += digits;
                        break;
                    default: throw new ScanFailure("string-escape-invalid");
                }
            }
            throw new ScanFailure("string-truncated");
        }

        private static bool ReferencesRoot(string value, string root, Func<string, string> environment)
        {
            var text = Expand(value, environment).Replace('/', '\\')
                .Replace(@"\\?\UNC\", @"\\").Replace(@"\\?\", String.Empty);
            if (text.IndexOf(@"\\.\", StringComparison.Ordinal) >= 0 ||
                text.IndexOf(@"\??\", StringComparison.Ordinal) >= 0 ||
                Regex.IsMatch(text, @"~\d")) throw new ScanFailure("path-alias-unsupported");
            // 先按完整目录段保守匹配，再考虑路径规范化。不能让命令参数中的
            // .. 抹掉前面的可执行路径；撇号、空格也是合法目录名，不能先截断。
            var direct = text.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            while (direct >= 0)
            {
                int after = direct + root.Length;
                if (after == text.Length || "\\;\r\n\t\"'<>|& ()".IndexOf(text[after]) >= 0) return true;
                direct = text.IndexOf(root, direct + 1, StringComparison.OrdinalIgnoreCase);
            }
            bool hit = false;
            foreach (Match match in Regex.Matches(text, @"[A-Za-z]:\\|\\\\(?![?.]\\)[^\\\s]+\\"))
            {
                int end = match.Index;
                while (end < text.Length && ";\r\n\t\"<>|&".IndexOf(text[end]) < 0) end++;
                var candidate = text.Substring(match.Index, end - match.Index).Trim();
                // 空格可能是目录名，也可能是命令参数边界：检查所有可能的前缀，
                // 而非让参数里的 .. 参与可执行路径的归一化。宁可保守拦截。
                for (int cut = candidate.IndexOf(' '); cut >= 0; cut = candidate.IndexOf(' ', cut + 1))
                {
                    var prefix = CanonicalPath(candidate.Substring(0, cut));
                    if (String.Equals(prefix, root, StringComparison.OrdinalIgnoreCase) ||
                        prefix.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) hit = true;
                }
                candidate = CanonicalPath(candidate);
                if (String.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) hit = true;
                // 根目录自身作为命令参数时，后面可能紧跟空格而不是分隔符。
                if (candidate.StartsWith(root + " ", StringComparison.OrdinalIgnoreCase)) hit = true;
            }
            if (!hit && Regex.IsMatch(text, @"(^|[;\s""'=(&])([A-Za-z]:(?!\\)|\\(?!\\)|\.\.?\\|~\\)"))
                throw new ScanFailure("relative-path-unsupported");
            if (!hit)
            {
                foreach (var part in text.Split(new[] { ';', '\r', '\n', '\t' }))
                {
                    if (part.IndexOf('\\') >= 0 && !Regex.IsMatch(part, @"[A-Za-z]:\\|\\\\[^\\\s]+\\"))
                        throw new ScanFailure("relative-path-unsupported");
                }
            }
            return hit;
        }

        private static string Expand(string value, Func<string, string> environment)
        {
            if (String.IsNullOrEmpty(value)) return value;
            if (value.IndexOf("$env:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("${", StringComparison.Ordinal) >= 0 || value.IndexOf("$HOME", StringComparison.Ordinal) >= 0)
                throw new ScanFailure("expression-unsupported");
            for (int pass = 0; pass < 8; pass++)
            {
                if (!Variable.IsMatch(value)) return value;
                value = Variable.Replace(value, delegate(Match match)
                {
                    var expanded = environment == null ? null : environment(match.Groups[1].Value);
                    if (String.IsNullOrEmpty(expanded)) throw new ScanFailure("environment-unresolved");
                    return expanded;
                });
            }
            if (Variable.IsMatch(value)) throw new ScanFailure("environment-cycle");
            return value;
        }

        private static string CanonicalPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ScanFailure("path-empty");
            path = path.Replace('/', '\\');
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path.Substring(8);
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
            if (!(Regex.IsMatch(path, @"^[A-Za-z]:\\") || path.StartsWith(@"\\", StringComparison.Ordinal)))
                throw new ScanFailure("path-not-absolute");
            return Path.GetFullPath(path).TrimEnd('\\');
        }

        private static void CheckAncestors(string path, bool allowMissing)
        {
            var parents = new Stack<string>();
            for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent) parents.Push(dir.FullName);
            foreach (var parent in parents)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(parent); }
                catch (FileNotFoundException) { if (allowMissing) return; throw; }
                catch (DirectoryNotFoundException) { if (allowMissing) return; throw; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ScanFailure("reparse-point");
                if ((attributes & FileAttributes.Directory) == 0) throw new ScanFailure("not-directory");
            }
        }

        private static void CheckTree(string root)
        {
            CheckAncestors(root, false);
            var pending = new Stack<string>();
            pending.Push(root);
            int count = 0;
            while (pending.Count > 0)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    if (++count > 100000) throw new ScanFailure("tree-too-large");
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ScanFailure("reparse-point");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                }
            }
        }

        private static CopyCleanupResult Unknown(string reason)
        {
            return new CopyCleanupResult { State = CopyCleanupState.Unknown, Reason = reason };
        }

        private sealed class ScanFailure : Exception
        {
            public readonly string Code;
            public ScanFailure(string code) { Code = code; }
        }
    }
}
