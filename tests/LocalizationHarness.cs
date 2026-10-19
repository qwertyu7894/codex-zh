using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CodexZhLauncher;

namespace CodexZhLauncher
{
    /// <summary>
    /// Standalone console harness used by test-localization.ps1. It links only
    /// the engine classes, never WPF/AppInfo/Program.
    /// </summary>
    internal static class LocalizationHarness
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: LocalizationHarness <command> [--key value]");
                return 2;
            }

            try
            {
                switch (args[0])
                {
                    case "asar-inspect": return AsarInspect(args);
                    case "asar-list": return AsarList(args);
                    case "asar-entry": return AsarEntry(args);
                    case "probe-rules": return ProbeRules(args);
                    case "patch-one": return PatchOne(args);
                    case "patch-pass": return PatchPass(args);
                    case "embedded-status": return EmbeddedStatus(args);
                    case "header-hash": return HeaderHash(args);
                    case "hash": return Hash(args);
                    case "compare": return Compare(args);
                    case "state-read": return StateRead(args);
                    case "state-validate": return StateValidate(args);
                    case "state-equal": return StateEqual(args);
                    case "state-file-check": return StateFileCheck(args);
                    case "locale-override": return LocaleOverride(args);
                    case "install": return Install(args);
                    case "detect-store": return DetectStore(args);
                    case "shortcut-plan": return ShortcutPlan(args);
                    case "shortcut-create": return ShortcutCreate(args);
                    case "launch": return Launch(args);
                    default:
                        Console.Error.WriteLine("unknown command: " + args[0]);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static string Opt(string[] args, string name, string fallback)
        {
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return fallback;
        }

        private static bool Flag(string[] args, string name)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void KV(string key, object value)
        {
            Console.WriteLine(key + "=" + Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static string ResolveAsar(string[] args)
        {
            var asar = Opt(args, "--asar", null);
            if (!String.IsNullOrEmpty(asar)) return Path.GetFullPath(asar);
            var app = Opt(args, "--app", null);
            if (!String.IsNullOrEmpty(app)) return Path.Combine(Path.GetFullPath(app), "resources", "app.asar");
            throw new ArgumentException("--asar or --app is required");
        }

        private static int AsarInspect(string[] args)
        {
            var asarPath = ResolveAsar(args);
            var info = AsarArchive.ReadHeader(asarPath);
            var file = new FileInfo(asarPath);
            KV("asar", asarPath);
            KV("length", file.Length);
            KV("mtimeUtcTicks", file.LastWriteTimeUtc.Ticks);

            using (var fs = new FileStream(asarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                KV("u0", br.ReadUInt32());
                KV("u1", br.ReadUInt32());
                KV("u2", br.ReadUInt32());
                KV("u3", br.ReadUInt32());
            }

            KV("jsonLen", info.JsonLen);
            KV("dataOffset", info.DataOffset);
            KV("headerHash", AsarArchive.HeaderHash(asarPath));
            KV("rulesSignature", LocalizationRules.Signature());
            foreach (var rule in LocalizationRules.All)
            {
                KV("candidates." + rule.Id, AsarArchive.EntriesInScope(info, rule.Scope, rule.Ext).Count);
            }
            return 0;
        }

        private static int AsarList(string[] args)
        {
            var asarPath = ResolveAsar(args);
            var scope = Opt(args, "--scope", "webview/assets");
            var ext = Opt(args, "--ext", ".js");
            var info = AsarArchive.ReadHeader(asarPath);
            foreach (var entry in AsarArchive.EntriesInScope(info, scope, ext))
                Console.WriteLine(entry.Path);
            return 0;
        }

        private static int AsarEntry(string[] args)
        {
            var asarPath = ResolveAsar(args);
            var rel = Opt(args, "--relpath", null);
            if (String.IsNullOrEmpty(rel)) throw new ArgumentException("--relpath is required");
            var info = AsarArchive.ReadHeader(asarPath);
            var entry = AsarArchive.FindEntry(info, rel);
            if (entry == null)
            {
                KV("found", "false");
                return 0;
            }
            KV("found", "true");
            KV("offset", entry.Offset);
            KV("size", entry.Size);
            KV("unpacked", entry.Unpacked ? "True" : "False");
            if (entry.Integrity != null)
            {
                KV("integrity.hasHash", String.IsNullOrEmpty(entry.Integrity.Hash) ? "False" : "True");
                KV("integrity.hash", entry.Integrity.Hash ?? String.Empty);
                KV("integrity.blocks", entry.Integrity.Blocks == null ? 0 : entry.Integrity.Blocks.Count);
                KV("integrity.blockSize", entry.Integrity.BlockSize);
            }
            else
            {
                KV("integrity.hasHash", "False");
                KV("integrity.blocks", 0);
            }
            return 0;
        }

        private static int ProbeRules(string[] args)
        {
            var asarPath = ResolveAsar(args);
            var info = AsarArchive.ReadHeader(asarPath);
            foreach (var rule in LocalizationRules.All)
            {
                var entries = AsarArchive.EntriesInScope(info, rule.Scope, rule.Ext);
                KV("rule." + rule.Id + ".candidates", entries.Count);
                foreach (var entry in entries)
                {
                    if (AsarArchive.EntryContains(asarPath, info.DataOffset, entry.Offset, entry.Size, rule.From))
                        KV("rule." + rule.Id + ".fromHit", entry.Path);
                    if (AsarArchive.EntryContains(asarPath, info.DataOffset, entry.Offset, entry.Size, rule.To))
                        KV("rule." + rule.Id + ".toHit", entry.Path);
                    if (rule.HasAlreadyPatchedContext &&
                        AsarArchive.EntryContains(asarPath, info.DataOffset, entry.Offset, entry.Size, rule.AlreadyPatchedContext))
                        KV("rule." + rule.Id + ".contextHit", entry.Path);
                }
            }
            return 0;
        }

        private static int PatchOne(string[] args)
        {
            var asarPath = ResolveAsar(args);
            int ruleIndex = Int32.Parse(Opt(args, "--rule", "0"), CultureInfo.InvariantCulture);
            bool apply = Flag(args, "--apply");
            var info = AsarArchive.ReadHeader(asarPath);
            var rule = LocalizationRules.All[ruleIndex];
            AsarEntryRef hit = null;
            foreach (var entry in AsarArchive.EntriesInScope(info, rule.Scope, rule.Ext))
            {
                if (AsarArchive.EntryContains(asarPath, info.DataOffset, entry.Offset, entry.Size, rule.From))
                {
                    hit = entry;
                    break;
                }
            }
            if (hit == null)
            {
                KV("status", "no-from-hit");
                return 0;
            }
            KV("path", hit.Path);
            var result = AsarPatcher.PatchEntry(asarPath, hit.Path, rule.From, rule.To, rule.Desc, apply);
            KV("status", result.Status);
            KV("hits", result.Hits);
            KV("integrityUpdated", result.IntegrityUpdated);
            KV("applied", result.Applied ? "True" : "False");
            return 0;
        }

        private static int PatchPass(string[] args)
        {
            var asarPath = ResolveAsar(args);
            bool apply = Flag(args, "--apply");
            bool quiet = Flag(args, "--quiet");
            Action<string> progress = null;
            if (!quiet)
            {
                progress = delegate(string message) { Console.Error.WriteLine(message); };
            }
            var result = AsarPatchPass.Run(asarPath, apply, progress);
            KV("criticalMiss", result.CriticalMiss);
            KV("dataOffset", result.DataOffset);
            foreach (var ruleResult in result.Results)
            {
                KV("result." + ruleResult.Id + ".status", ruleResult.Status);
                KV("result." + ruleResult.Id + ".hits", ruleResult.Hits);
                foreach (var record in ruleResult.Entries)
                {
                    KV("entry." + record.Id + ".path", record.Path);
                    KV("entry." + record.Id + ".offset", record.Offset);
                    KV("entry." + record.Id + ".size", record.Size);
                    KV("entry." + record.Id + ".needle", record.Needle);
                    KV("entry." + record.Id + ".context", record.Context);
                }
            }
            return 0;
        }

        private static int EmbeddedStatus(string[] args)
        {
            var asarPath = ResolveAsar(args);
            var app = Opt(args, "--app", null);
            if (String.IsNullOrEmpty(app)) throw new ArgumentException("--app is required");
            bool apply = Flag(args, "--apply");
            var result = EmbeddedAsarIntegrity.Update(Path.GetFullPath(app), asarPath, apply);
            KV("status", result.Status);
            if (!String.IsNullOrEmpty(result.File)) KV("file", Path.GetFileName(result.File));
            if (!String.IsNullOrEmpty(result.Old)) KV("old", result.Old);
            if (!String.IsNullOrEmpty(result.New)) KV("new", result.New);
            if (!String.IsNullOrEmpty(result.Hash)) KV("hash", result.Hash);
            return 0;
        }

        private static int HeaderHash(string[] args)
        {
            KV("headerHash", AsarArchive.HeaderHash(ResolveAsar(args)));
            return 0;
        }

        private static int Hash(string[] args)
        {
            var path = Opt(args, "--path", null);
            if (String.IsNullOrEmpty(path)) throw new ArgumentException("--path is required");
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(fs);
                KV("sha256", BitConverter.ToString(hash).Replace("-", String.Empty).ToLowerInvariant());
                KV("length", fs.Length);
            }
            return 0;
        }

        private static int Compare(string[] args)
        {
            var a = Opt(args, "--a", null);
            var b = Opt(args, "--b", null);
            if (String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b)) throw new ArgumentException("--a and --b are required");

            using (var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                KV("lengthA", fa.Length);
                KV("lengthB", fb.Length);
                long min = Math.Min(fa.Length, fb.Length);
                var ba = new byte[1024 * 1024];
                var bb = new byte[1024 * 1024];
                long pos = 0;
                long firstDiff = -1;
                while (pos < min)
                {
                    int want = (int)Math.Min(ba.Length, min - pos);
                    int ra = ReadFull(fa, ba, want);
                    int rb = ReadFull(fb, bb, want);
                    if (ra != rb) { firstDiff = pos + Math.Min(ra, rb); break; }
                    for (int i = 0; i < ra; i++)
                    {
                        if (ba[i] != bb[i]) { firstDiff = pos + i; break; }
                    }
                    if (firstDiff >= 0) break;
                    pos += ra;
                }
                if (firstDiff < 0 && fa.Length != fb.Length) firstDiff = min;
                KV("equal", firstDiff < 0 ? "True" : "False");
                if (firstDiff >= 0) KV("firstDifference", firstDiff);
            }
            return 0;
        }

        private static int StateRead(string[] args)
        {
            var path = Opt(args, "--path", null);
            if (String.IsNullOrEmpty(path)) throw new ArgumentException("--path is required");
            var state = PatchState.Load(path);
            if (state == null)
            {
                KV("loaded", "False");
                return 0;
            }
            KV("loaded", "True");
            KV("schemaVersion", state.SchemaVersion);
            KV("mode", state.Mode);
            KV("sourcePath", state.SourcePath);
            KV("installPath", state.InstallPath);
            KV("version", state.Version);
            KV("language", state.Language);
            KV("patchedAt", state.PatchedAt);
            if (state.Patch != null)
            {
                KV("patch.installPath", state.Patch.InstallPath);
                KV("patch.rulesSignature", state.Patch.RulesSignature);
                KV("patch.criticalMiss", state.Patch.CriticalMiss);
                KV("patch.dataOffset", state.Patch.DataOffset);
                KV("patch.asarSize", state.Patch.AsarSize);
                KV("patch.asarTicks", state.Patch.AsarTicks);
                KV("patch.exeSize", state.Patch.ExeSize);
                KV("patch.exeTicks", state.Patch.ExeTicks);
                KV("patch.entries", state.Patch.Entries.Count);
                foreach (var entry in state.Patch.Entries)
                {
                    KV("entry." + entry.Id + ".path", entry.Path);
                    KV("entry." + entry.Id + ".offset", entry.Offset);
                    KV("entry." + entry.Id + ".size", entry.Size);
                    KV("entry." + entry.Id + ".needle", entry.Needle);
                    KV("entry." + entry.Id + ".context", entry.Context);
                }
            }
            return 0;
        }

        private static int StateValidate(string[] args)
        {
            var path = Opt(args, "--state", null);
            var app = Opt(args, "--app", null);
            var version = Opt(args, "--version", "unknown");
            var signature = Opt(args, "--signature", LocalizationRules.Signature());
            if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(app))
                throw new ArgumentException("--state and --app are required");
            var state = PatchState.Load(path);
            KV("valid", PatchState.IsStillValid(state, Path.GetFullPath(app), version, signature) ? "True" : "False");
            return 0;
        }

        private static int StateEqual(string[] args)
        {
            var a = Opt(args, "--a", null);
            var b = Opt(args, "--b", null);
            if (String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b))
                throw new ArgumentException("--a and --b are required");
            var sa = PatchState.Load(a);
            var sb = PatchState.Load(b);
            var differences = new List<string>();
            if (sa == null || sb == null)
            {
                if (sa == null && sb == null) { KV("equal", "True"); return 0; }
                KV("equal", "False");
                KV("difference", sa == null ? "a-not-loaded" : "b-not-loaded");
                return 0;
            }

            Diff(differences, "schemaVersion", sa.SchemaVersion, sb.SchemaVersion);
            Diff(differences, "mode", sa.Mode, sb.Mode);
            Diff(differences, "sourcePath", sa.SourcePath, sb.SourcePath);
            Diff(differences, "installPath", sa.InstallPath, sb.InstallPath);
            Diff(differences, "version", sa.Version, sb.Version);
            Diff(differences, "language", sa.Language, sb.Language);

            if (sa.Patch == null || sb.Patch == null)
            {
                Diff(differences, "patch.null", sa.Patch == null, sb.Patch == null);
            }
            else
            {
                Diff(differences, "patch.installPath", sa.Patch.InstallPath, sb.Patch.InstallPath);
                Diff(differences, "patch.rulesSignature", sa.Patch.RulesSignature, sb.Patch.RulesSignature);
                Diff(differences, "patch.criticalMiss", sa.Patch.CriticalMiss, sb.Patch.CriticalMiss);
                Diff(differences, "patch.dataOffset", sa.Patch.DataOffset, sb.Patch.DataOffset);
                Diff(differences, "patch.asarSize", sa.Patch.AsarSize, sb.Patch.AsarSize);
                Diff(differences, "patch.exeSize", sa.Patch.ExeSize, sb.Patch.ExeSize);
                if (sa.Patch.Entries.Count != sb.Patch.Entries.Count)
                {
                    differences.Add("patch.entries.count " + sa.Patch.Entries.Count + " != " + sb.Patch.Entries.Count);
                }
                else
                {
                    for (int i = 0; i < sa.Patch.Entries.Count; i++)
                    {
                        var ea = sa.Patch.Entries[i];
                        var eb = sb.Patch.Entries[i];
                        Diff(differences, "entry[" + i + "].id", ea.Id, eb.Id);
                        Diff(differences, "entry[" + i + "].path", ea.Path, eb.Path);
                        Diff(differences, "entry[" + i + "].offset", ea.Offset, eb.Offset);
                        Diff(differences, "entry[" + i + "].size", ea.Size, eb.Size);
                        Diff(differences, "entry[" + i + "].needle", ea.Needle, eb.Needle);
                        Diff(differences, "entry[" + i + "].context", ea.Context, eb.Context);
                    }
                }
            }

            KV("equal", differences.Count == 0 ? "True" : "False");
            foreach (var difference in differences) KV("difference", difference);
            return 0;
        }

        private static void Diff(List<string> differences, string name, object a, object b)
        {
            var sa = Convert.ToString(a, CultureInfo.InvariantCulture);
            var sb = Convert.ToString(b, CultureInfo.InvariantCulture);
            if (!String.Equals(sa, sb, StringComparison.Ordinal)) differences.Add(name + " " + sa + " != " + sb);
        }

        private static int StateFileCheck(string[] args)
        {
            var path = Opt(args, "--state", null);
            var app = Opt(args, "--app", null);
            if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(app))
                throw new ArgumentException("--state and --app are required");
            var state = PatchState.Load(path);
            if (state == null || state.Patch == null)
            {
                KV("valid", "False");
                return 0;
            }
            var asar = new FileInfo(Path.Combine(app, "resources", "app.asar"));
            var exe = new FileInfo(Path.Combine(app, "ChatGPT.exe"));
            KV("asarSizeMatch", asar.Length == state.Patch.AsarSize ? "True" : "False");
            KV("asarTicksMatch", asar.LastWriteTimeUtc.Ticks == state.Patch.AsarTicks ? "True" : "False");
            KV("exeSizeMatch", exe.Length == state.Patch.ExeSize ? "True" : "False");
            KV("exeTicksMatch", exe.LastWriteTimeUtc.Ticks == state.Patch.ExeTicks ? "True" : "False");
            return 0;
        }

        private static int LocaleOverride(string[] args)
        {
            var path = Opt(args, "--path", null);
            var locale = Opt(args, "--locale", "zh-CN");
            if (String.IsNullOrEmpty(path)) throw new ArgumentException("--path is required");
            bool apply = Flag(args, "--apply");
            var result = CodexConfigToml.SetLocaleOverride(path, locale, apply);
            KV("changed", result.Changed ? "True" : "False");
            KV("reason", result.Reason);
            if (File.Exists(path))
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha = SHA256.Create())
                {
                    KV("sha256", BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", String.Empty).ToLowerInvariant());
                }
            }
            return 0;
        }

        private static int Install(string[] args)
        {
            var source = Opt(args, "--source", null);
            var target = Opt(args, "--target", null);
            var root = Opt(args, "--root", null);
            var config = Opt(args, "--config", null);
            var locale = Opt(args, "--locale", "zh-CN");
            bool apply = Flag(args, "--apply");
            bool quiet = Flag(args, "--quiet");

            CodexInstall hint = null;
            if (!String.IsNullOrEmpty(source))
            {
                source = Path.GetFullPath(source);
                hint = new CodexInstall
                {
                    Kind = "harness",
                    DisplayName = "harness",
                    Version = "-",
                    InstallLocation = source,
                    ExecutablePath = Path.Combine(source, "ChatGPT.exe")
                };
            }

            Action<string> progress = null;
            if (!quiet)
            {
                progress = delegate(string message) { Console.Error.WriteLine(message); };
            }
            var request = new InstallRequest
            {
                TargetDir = target,
                Locale = locale,
                Apply = apply,
                RootDir = root,
                ConfigTomlPath = config,
                SourceHint = hint,
                Progress = progress
            };
            var outcome = PatchInstaller.Install(request);
            KV("exitCode", outcome.ExitCode);
            KV("mode", outcome.Mode);
            KV("targetApp", outcome.TargetApp);
            KV("criticalMiss", outcome.CriticalMiss ? "True" : "False");
            KV("hadError", outcome.HadError ? "True" : "False");
            KV("fastSkip", outcome.FastSkip ? "True" : "False");
            if (!String.IsNullOrEmpty(outcome.Message)) KV("message", outcome.Message);
            return outcome.ExitCode;
        }

        private static int DetectStore(string[] args)
        {
            var package = NativePackageLocator.FindNewest("OpenAI.Codex");
            if (package == null)
            {
                KV("found", "False");
                return 0;
            }
            KV("found", "True");
            KV("name", package.Name);
            KV("version", package.Version);
            KV("packageFamilyName", package.PackageFamilyName);
            KV("installLocation", package.InstallLocation);
            KV("displayName", package.DisplayName);
            return 0;
        }

        private static int ShortcutPlan(string[] args)
        {
            var launcher = Opt(args, "--launcher", null);
            var workDir = Opt(args, "--workdir", null);
            var name = Opt(args, "--name", "GPT中文汉化");
            var iconDir = Opt(args, "--icon-dir", null);
            if (String.IsNullOrEmpty(launcher)) throw new ArgumentException("--launcher is required");
            if (String.IsNullOrEmpty(workDir)) workDir = Path.GetDirectoryName(Path.GetFullPath(launcher));
            var result = ShortcutService.Create(Path.GetFullPath(launcher), workDir, name,
                "--launch-zh", "打开中文版 Codex（运行时注入，跟随最新版）", iconDir, false, null);
            KV("status", result.Status);
            KV("icon", result.Icon);
            foreach (var target in result.Targets) KV("target", target);
            return 0;
        }

        private static int ShortcutCreate(string[] args)
        {
            var launcher = Opt(args, "--launcher", null);
            var workDir = Opt(args, "--workdir", null);
            var name = Opt(args, "--name", "GPT中文汉化-Test");
            var iconDir = Opt(args, "--icon-dir", null);
            var targetDir = Opt(args, "--target-dir", null);
            if (String.IsNullOrEmpty(launcher)) throw new ArgumentException("--launcher is required");
            if (String.IsNullOrEmpty(targetDir)) throw new ArgumentException("--target-dir is required");
            if (String.IsNullOrEmpty(workDir)) workDir = Path.GetDirectoryName(Path.GetFullPath(launcher));
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            var targets = new List<string> { Path.Combine(targetDir, name + ".lnk") };
            var result = ShortcutService.CreateAtTargets(Path.GetFullPath(launcher), workDir, name,
                "--launch-zh", "打开中文版 Codex（运行时注入，跟随最新版）", iconDir, true,
                delegate(string message) { Console.Error.WriteLine(message); }, targets);
            KV("status", result.Status);
            KV("icon", result.Icon);
            foreach (var made in result.Made) KV("made", made);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用");
            var shell = Activator.CreateInstance(shellType);
            foreach (var lnk in result.Made)
            {
                var shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var shortcutType = shortcut.GetType();
                KV("lnk.targetPath", GetComProperty(shortcutType, shortcut, "TargetPath"));
                KV("lnk.arguments", GetComProperty(shortcutType, shortcut, "Arguments"));
                KV("lnk.workingDirectory", GetComProperty(shortcutType, shortcut, "WorkingDirectory"));
                KV("lnk.iconLocation", GetComProperty(shortcutType, shortcut, "IconLocation"));
                KV("lnk.description", GetComProperty(shortcutType, shortcut, "Description"));
            }
            return 0;
        }

        private static string GetComProperty(Type type, object instance, string name)
        {
            var value = type.InvokeMember(name, BindingFlags.GetProperty, null, instance, null);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int Launch(string[] args)
        {
            var app = Opt(args, "--app", null);
            var locale = Opt(args, "--locale", "zh-CN");
            if (String.IsNullOrEmpty(app)) throw new ArgumentException("--app is required");
            var exit = PatchInstaller.Launch(Path.GetFullPath(app), locale,
                delegate(string message) { Console.Error.WriteLine(message); });
            KV("launchExit", exit);
            return exit;
        }

        private static int ReadFull(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) break;
                read += n;
            }
            return read;
        }
    }
}
