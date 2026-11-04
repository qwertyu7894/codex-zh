using System;
using System.Collections.Generic;

namespace CodexZhLauncher
{
    internal sealed class PatchEntryRecord
    {
        public string Id;
        public string Path;
        public long Offset;
        public long Size;
        public string Needle;
        public string Context;
    }

    internal sealed class PatchRuleResult
    {
        public string Id;
        public string Desc;
        public bool Critical;
        public string Status;   // ok | already-patched | miss
        public int Hits;
        public List<PatchEntryRecord> Entries = new List<PatchEntryRecord>();
    }

    internal sealed class PatchPassResult
    {
        public List<PatchRuleResult> Results = new List<PatchRuleResult>();
        public int CriticalMiss;
        public int Errors;
        public long DataOffset;
    }

    /// <summary>一个命中的文件，以及它命中的是哪一种写法。</summary>
    internal sealed class PatchHit
    {
        public AsarEntryRef Entry;
        public PatchNeedle Needle;
    }

    /// <summary>
    /// 与 localize/CodexZh.ps1 的 Invoke-PatchPass 对应。
    /// </summary>
    internal static class AsarPatchPass
    {
        public static PatchPassResult Run(string asarPath, bool apply, Action<string> progress)
        {
            var info = AsarArchive.ReadHeader(asarPath);
            var result = new PatchPassResult { DataOffset = info.DataOffset };
            Report(progress, String.Format("[信息] asar 头部 {0:N0} 字节，数据区起点 {1:N0}", info.JsonLen, info.DataOffset));

            foreach (var rule in LocalizationRules.All)
            {
                var candidates = AsarArchive.EntriesInScope(info, rule.Scope, rule.Ext);
                Report(progress, String.Format("[信息] 规则 [{0}] 在 {1} 下找到 {2} 个候选文件",
                    rule.Id, rule.Scope, candidates.Count));

                // 同一规则可能有多条挑串（不同版本的压缩产物引号不同），
                // 任意一条命中即算这个文件命中。
                var hitFiles = new List<PatchHit>();
                foreach (var entry in candidates)
                {
                    var needle = FindNeedle(asarPath, info.DataOffset, entry, rule);
                    if (needle != null)
                        hitFiles.Add(new PatchHit { Entry = entry, Needle = needle });
                }

                if (hitFiles.Count == 0)
                {
                    AsarEntryRef alreadyEntry = null;
                    PatchNeedle alreadyNeedle = null;
                    string alreadyContext = null;
                    foreach (var entry in candidates)
                    {
                        foreach (var needle in rule.Needles())
                        {
                            if (!AsarArchive.EntryContains(asarPath, info.DataOffset, entry.Offset, entry.Size, needle.To))
                                continue;
                            if (rule.HasAlreadyPatchedContext)
                            {
                                alreadyContext = FindContext(asarPath, info.DataOffset, entry, rule);
                                if (alreadyContext == null) continue;
                            }
                            alreadyEntry = entry;
                            alreadyNeedle = needle;
                            break;
                        }
                        if (alreadyEntry != null) break;
                    }

                    if (alreadyEntry != null)
                    {
                        Report(progress, "[信息]     已打过补丁，跳过：" + rule.Desc);

                        // 崩溃恢复：数据已经是补丁后的状态，但头部 per-file 哈希可能
                        // 停在“写了一半”。只读复查无法发现；这里按当前字节重算，
                        // 不一致就修回去（DryRun 不写）。
                        if (apply)
                        {
                            var patchedNode = AsarArchive.FindEntry(info, alreadyEntry.Path);
                            if (patchedNode != null)
                            {
                                var repaired = AsarPatcher.RepairEntryIntegrity(
                                    asarPath, info.DataOffset, patchedNode);
                                if (repaired > 0)
                                {
                                    Report(progress, "[警告]     修复了 " + alreadyEntry.Path +
                                        " 的完整性哈希（" + repaired + " 处）");
                                }
                            }
                        }
                        var ruleResult = new PatchRuleResult
                        {
                            Id = rule.Id, Desc = rule.Desc, Critical = rule.Critical,
                            Status = "already-patched"
                        };
                        ruleResult.Entries.Add(new PatchEntryRecord
                        {
                            Id = rule.Id,
                            Path = alreadyEntry.Path,
                            Offset = alreadyEntry.Offset,
                            Size = alreadyEntry.Size,
                            Needle = alreadyNeedle.To,
                            Context = alreadyContext ?? String.Empty
                        });
                        result.Results.Add(ruleResult);
                    }
                    else
                    {
                        Report(progress, "[警告]     未命中：" + rule.Desc);
                        if (rule.Critical) result.CriticalMiss++;
                        result.Results.Add(new PatchRuleResult
                        {
                            Id = rule.Id, Desc = rule.Desc, Critical = rule.Critical,
                            Status = "miss"
                        });
                    }
                    continue;
                }

                int totalHits = 0;
                var entryRecords = new List<PatchEntryRecord>();
                foreach (var hit in hitFiles)
                {
                    var entry = hit.Entry;
                    var patch = AsarPatcher.PatchEntry(asarPath, entry.Path, hit.Needle.From, hit.Needle.To, rule.Desc, apply);
                    if (patch.Status == "ok")
                    {
                        totalHits += patch.Hits;
                        var context = rule.HasAlreadyPatchedContext
                            ? (FindContext(asarPath, info.DataOffset, entry, rule) ?? rule.AlreadyPatchedContext)
                            : String.Empty;
                        entryRecords.Add(new PatchEntryRecord
                        {
                            Id = rule.Id,
                            Path = entry.Path,
                            Offset = entry.Offset,
                            Size = entry.Size,
                            Needle = hit.Needle.To,
                            Context = context
                        });
                        Report(progress, String.Format("[信息]       {0} (命中 {1}, 完整性哈希更新 {2})",
                            entry.Path, patch.Hits, patch.IntegrityUpdated));
                    }
                    else if (patch.Status == "length-mismatch")
                    {
                        result.Errors++;
                        Report(progress, "[错误]     拒绝写入：替换前后长度不一致 @ " + entry.Path);
                    }
                    else
                    {
                        Report(progress, "[警告]     " + patch.Status + " @ " + entry.Path);
                    }
                }

                if (totalHits != rule.Expect)
                {
                    Report(progress, String.Format("[警告]     命中 {0} 处，预期 {1} 处 —— App 结构可能已变化，请人工确认后再上线",
                        totalHits, rule.Expect));
                }
                else
                {
                    Report(progress, String.Format("[成功]     命中 {0} 处：{1}", totalHits, rule.Desc));
                }

                result.Results.Add(new PatchRuleResult
                {
                    Id = rule.Id, Desc = rule.Desc, Critical = rule.Critical,
                    Status = "ok", Hits = totalHits, Entries = entryRecords
                });
            }

            return result;
        }

        private static PatchNeedle FindNeedle(string asarPath, long dataOffset, AsarEntryRef entry,
            CodexPatchRule rule)
        {
            foreach (var needle in rule.Needles())
            {
                if (AsarArchive.EntryContains(asarPath, dataOffset, entry.Offset, entry.Size, needle.From))
                    return needle;
            }
            return null;
        }

        private static string FindContext(string asarPath, long dataOffset, AsarEntryRef entry,
            CodexPatchRule rule)
        {
            foreach (var context in rule.Contexts())
            {
                if (AsarArchive.EntryContains(asarPath, dataOffset, entry.Offset, entry.Size, context))
                    return context;
            }
            return null;
        }

        private static void Report(Action<string> progress, string message)
        {
            if (progress != null) progress(message);
        }
    }
}
