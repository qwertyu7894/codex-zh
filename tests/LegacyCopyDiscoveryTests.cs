using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodexZhLauncher
{
    internal static class LegacyCopyDiscoveryTests
    {
        private sealed class FileSnapshot
        {
            public byte[] Bytes;
            public long LastWriteUtcTicks;
            public FileAttributes Attributes;
        }

        public static void Run(string work)
        {
            if (String.IsNullOrWhiteSpace(work))
                throw new ArgumentException("test work directory is required", "work");

            var legacyWork = Path.Combine(work, "legacy-copy-discovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(legacyWork);

            TestMissing(legacyWork);
            TestCurrentOnly(legacyWork);
            TestMetadata(legacyWork, "install.json");
            TestMetadata(legacyWork, "state.json");
            TestUnknown(legacyWork);
            TestFullApp(legacyWork);
        }

        private static void TestMissing(string work)
        {
            var config = CreateConfig(work, "missing");
            var sentinel = WriteFixture(config, "sentinel.txt", "missing-case-sentinel");
            var before = Capture(sentinel);

            var found = LegacyCopyDiscovery.Find(config);

            Assert(found.Count == 0, "missing target must not produce a candidate");
            AssertUnchanged(sentinel, before, "missing sentinel");
        }

        private static void TestCurrentOnly(string work)
        {
            var config = CreateConfig(work, "current-only");
            var current = Path.Combine(config, "codex-zh");
            var asar = WriteFixture(current, Path.Combine("app", "resources", "app.asar"),
                "current-copy-fixture");
            var state = WriteFixture(current, "state.json", "{\"fixture\":\"current-only\"}");
            var sentinel = WriteFixture(current, "sentinel.txt", "current-copy-sentinel");
            var asarBefore = Capture(asar);
            var stateBefore = Capture(state);
            var sentinelBefore = Capture(sentinel);

            var found = LegacyCopyDiscovery.Find(config);

            Assert(found.Count == 0, "current codex-zh must be ignored");
            AssertUnchanged(asar, asarBefore, "current app.asar");
            AssertUnchanged(state, stateBefore, "current state");
            AssertUnchanged(sentinel, sentinelBefore, "current sentinel");
        }

        private static void TestMetadata(string work, string metadataName)
        {
            var label = "metadata-" + Path.GetFileNameWithoutExtension(metadataName);
            var config = CreateConfig(work, label);
            var candidate = Path.Combine(config, "codex-localized");
            var metadata = WriteFixture(candidate, metadataName,
                "{\"fixture\":\"metadata-only\"}");
            var sentinel = WriteFixture(candidate, "sentinel.txt", label + "-sentinel");
            var metadataBefore = Capture(metadata);
            var sentinelBefore = Capture(sentinel);

            List<LegacyCopyCandidate> found;
            using (var locked = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                found = LegacyCopyDiscovery.Find(config);
            }

            AssertCandidate(found, "metadata", label);
            AssertUnchanged(metadata, metadataBefore, label + " file");
            AssertUnchanged(sentinel, sentinelBefore, label + " sentinel");
        }

        private static void TestUnknown(string work)
        {
            var config = CreateConfig(work, "unknown");
            var candidate = Path.Combine(config, "codex-localized");
            var sentinel = WriteFixture(candidate, "sentinel.txt", "unknown-case-sentinel");
            var before = Capture(sentinel);

            var found = LegacyCopyDiscovery.Find(config);

            AssertCandidate(found, "unknown", "unknown");
            AssertUnchanged(sentinel, before, "unknown sentinel");
        }

        private static void TestFullApp(string work)
        {
            var config = CreateConfig(work, "full-app");
            var candidate = Path.Combine(config, "codex-localized");
            var asar = WriteFixture(candidate, Path.Combine("app", "resources", "app.asar"),
                "full-app-fixture");
            var metadata = WriteFixture(candidate, "install.json",
                "{\"fixture\":\"app-with-metadata\"}");
            var sentinel = WriteFixture(candidate, "sentinel.txt", "full-app-sentinel");
            var asarBefore = Capture(asar);
            var metadataBefore = Capture(metadata);
            var sentinelBefore = Capture(sentinel);

            List<LegacyCopyCandidate> found;
            using (var locked = new FileStream(asar, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                found = LegacyCopyDiscovery.Find(config);
            }

            AssertCandidate(found, "app", "full app");
            AssertUnchanged(asar, asarBefore, "full app.asar");
            AssertUnchanged(metadata, metadataBefore, "full app metadata");
            AssertUnchanged(sentinel, sentinelBefore, "full app sentinel");
        }

        private static string CreateConfig(string work, string name)
        {
            var path = Path.Combine(work, name, "config");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string WriteFixture(string root, string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath);
            var parent = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        private static FileSnapshot Capture(string path)
        {
            return new FileSnapshot
            {
                Bytes = File.ReadAllBytes(path),
                LastWriteUtcTicks = File.GetLastWriteTimeUtc(path).Ticks,
                Attributes = File.GetAttributes(path)
            };
        }

        private static void AssertUnchanged(string path, FileSnapshot before, string label)
        {
            Assert(File.Exists(path), label + " must not be deleted");
            Assert(Equal(before.Bytes, File.ReadAllBytes(path)), label + " bytes must not change");
            Assert(File.GetLastWriteTimeUtc(path).Ticks == before.LastWriteUtcTicks,
                label + " timestamp must not change");
            Assert(File.GetAttributes(path) == before.Attributes,
                label + " attributes must not change");
        }

        private static void AssertCandidate(List<LegacyCopyCandidate> found, string kind, string label)
        {
            Assert(found != null, label + " result must not be null");
            Assert(found.Count == 1, label + " must produce exactly one candidate");
            Assert(found[0] != null, label + " candidate must not be null");
            Assert(found[0].Name == "codex-localized", label + " candidate name");
            Assert(found[0].Kind == kind, label + " candidate kind");
        }

        private static bool Equal(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(
                "legacy copy discovery test failed: " + message);
        }
    }
}
