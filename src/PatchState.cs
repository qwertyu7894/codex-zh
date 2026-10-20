using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexZhLauncher
{
    internal sealed class PatchRecord
    {
        public string InstallPath;
        public string RulesSignature;
        public int CriticalMiss;
        public long DataOffset;
        public long AsarSize;
        public long AsarTicks;
        public long ExeSize;
        public long ExeTicks;
        public List<PatchEntryRecord> Entries = new List<PatchEntryRecord>();
    }

    internal sealed class LocalizationState
    {
        public int SchemaVersion = 2;
        public string Mode;
        public string SourcePath;
        public string InstallPath;
        public string Version;
        public string Language;
        public string PatchedAt;
        public PatchRecord Patch;
    }

    /// <summary>
    /// 读取/写入 %USERPROFILE%\.codex\codex-zh\state.json，并实现快路径判定。
    /// 字段名与语义必须与 localize/CodexZh.ps1 完全一致：
    /// CodexDiscovery.CopyCameFromStorePackage 依赖顶层的 mode/sourcePath。
    /// </summary>
    internal static class PatchState
    {
        public static LocalizationState Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path, Encoding.UTF8);
                var serializer = CreateSerializer();
                var raw = serializer.Deserialize<Dictionary<string, object>>(text);
                if (raw == null) return null;
                return FromRaw(raw);
            }
            catch
            {
                return null;
            }
        }

        private static LocalizationState FromRaw(Dictionary<string, object> raw)
        {
            var state = new LocalizationState();
            state.SchemaVersion = (int)GetInt64(raw, "schemaVersion");
            state.Mode = GetString(raw, "mode");
            state.SourcePath = GetString(raw, "sourcePath");
            state.InstallPath = GetString(raw, "installPath");
            state.Version = GetString(raw, "version");
            state.Language = GetString(raw, "language");
            state.PatchedAt = GetString(raw, "patchedAt");

            object patchValue;
            if (!TryGetValueIgnoreCase(raw, "patch", out patchValue)) return state;
            var patchRaw = patchValue as Dictionary<string, object>;
            if (patchRaw == null) return state;

            var patch = new PatchRecord();
            patch.InstallPath = GetString(patchRaw, "installPath");
            patch.RulesSignature = GetString(patchRaw, "rulesSignature");
            patch.CriticalMiss = (int)GetInt64(patchRaw, "criticalMiss");
            patch.DataOffset = GetInt64(patchRaw, "dataOffset");
            patch.AsarSize = GetInt64(patchRaw, "asarSize");
            patch.AsarTicks = GetInt64(patchRaw, "asarTicks");
            patch.ExeSize = GetInt64(patchRaw, "exeSize");
            patch.ExeTicks = GetInt64(patchRaw, "exeTicks");

            object entriesValue;
            if (TryGetValueIgnoreCase(patchRaw, "entries", out entriesValue) && entriesValue != null)
            {
                var list = entriesValue as IList;
                if (list != null && !(entriesValue is string))
                {
                    foreach (var item in list)
                    {
                        var entry = ParseEntry(item as Dictionary<string, object>);
                        if (entry != null) patch.Entries.Add(entry);
                    }
                }
                else
                {
                    // PowerShell 的 @($object) 把单个对象也当成一条记录。
                    var entry = ParseEntry(entriesValue as Dictionary<string, object>);
                    if (entry != null) patch.Entries.Add(entry);
                }
            }

            state.Patch = patch;
            return state;
        }

        private static PatchEntryRecord ParseEntry(Dictionary<string, object> raw)
        {
            if (raw == null) return null;
            return new PatchEntryRecord
            {
                Id = GetString(raw, "id"),
                Path = GetString(raw, "path"),
                Offset = GetInt64(raw, "offset"),
                Size = GetInt64(raw, "size"),
                Needle = GetString(raw, "needle"),
                Context = GetString(raw, "context")
            };
        }

        public static void Save(string path, LocalizationState state)
        {
            var dir = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var patch = new Dictionary<string, object>();
            patch["installPath"] = state.Patch.InstallPath;
            patch["rulesSignature"] = state.Patch.RulesSignature;
            patch["criticalMiss"] = state.Patch.CriticalMiss;
            patch["dataOffset"] = state.Patch.DataOffset;
            patch["asarSize"] = state.Patch.AsarSize;
            patch["asarTicks"] = state.Patch.AsarTicks;
            patch["exeSize"] = state.Patch.ExeSize;
            patch["exeTicks"] = state.Patch.ExeTicks;

            var entries = new List<Dictionary<string, object>>();
            foreach (var entry in state.Patch.Entries)
            {
                var item = new Dictionary<string, object>();
                item["id"] = entry.Id;
                item["path"] = entry.Path;
                item["offset"] = entry.Offset;
                item["size"] = entry.Size;
                item["needle"] = entry.Needle;
                item["context"] = entry.Context;
                entries.Add(item);
            }
            patch["entries"] = entries;

            var root = new Dictionary<string, object>();
            root["schemaVersion"] = state.SchemaVersion;
            root["mode"] = state.Mode;
            root["sourcePath"] = state.SourcePath;
            root["installPath"] = state.InstallPath;
            root["version"] = state.Version;
            root["language"] = state.Language;
            root["patchedAt"] = state.PatchedAt;
            root["patch"] = patch;

            var serializer = CreateSerializer();
            File.WriteAllText(path, serializer.Serialize(root), new UTF8Encoding(false));
        }

        /// <summary>
        /// 与 CodexZh.ps1 的 Test-PatchRecordStillValid 对应。
        /// 任何一项不成立或任何异常都返回 false（宁可慢也不能错）。
        /// </summary>
        public static bool IsStillValid(LocalizationState state, string targetApp,
            string nowVersion, string signature)
        {
            try
            {
                if (state == null || state.Patch == null) return false;
                var patch = state.Patch;

                if (patch.CriticalMiss > 0) return false;
                if (!String.Equals(patch.RulesSignature, signature, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (String.IsNullOrWhiteSpace(nowVersion) ||
                    String.Equals(nowVersion, "unknown", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!String.Equals(state.Version, nowVersion, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (String.IsNullOrWhiteSpace(patch.InstallPath) ||
                    !String.Equals(patch.InstallPath, targetApp, StringComparison.OrdinalIgnoreCase))
                    return false;

                var asarPath = Path.Combine(targetApp, "resources", "app.asar");
                var exePath = LocalizationPaths.ResolveExecutable(targetApp);
                if (!File.Exists(asarPath) || !File.Exists(exePath)) return false;

                var asar = new FileInfo(asarPath);
                var exe = new FileInfo(exePath);
                if (asar.Length != patch.AsarSize) return false;
                if (asar.LastWriteTimeUtc.Ticks != patch.AsarTicks) return false;
                if (exe.Length != patch.ExeSize) return false;
                if (exe.LastWriteTimeUtc.Ticks != patch.ExeTicks) return false;

                if (patch.Entries == null || patch.Entries.Count == 0) return false;

                foreach (var record in patch.Entries)
                {
                    if (record == null) return false;
                    if (String.IsNullOrEmpty(record.Needle)) return false;
                    if (!AsarArchive.EntryContains(asarPath, patch.DataOffset,
                            record.Offset, record.Size, record.Needle))
                        return false;
                    if (!String.IsNullOrEmpty(record.Context) &&
                        !AsarArchive.EntryContains(asarPath, patch.DataOffset,
                            record.Offset, record.Size, record.Context))
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = Int32.MaxValue;
            serializer.RecursionLimit = 512;
            return serializer;
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

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object value;
            if (!TryGetValueIgnoreCase(dict, key, out value) || value == null) return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static long GetInt64(Dictionary<string, object> dict, string key)
        {
            object value;
            if (!TryGetValueIgnoreCase(dict, key, out value) || value == null) return 0;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
    }
}
