using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace CodexZhLauncher
{
    internal sealed class LegacyCopyCandidate
    {
        public string Name;
        public string Kind;
    }

    /// <summary>
    /// 只读探测旧版副本目录。仅检查固定名称和少量固定标记，不枚举目录、
    /// 不读取配置内容，也不跟随任何重解析点。
    /// </summary>
    internal static class LegacyCopyDiscovery
    {
        private const string CandidateName = "codex-localized";

        private enum PathState
        {
            Missing,
            Accessible,
            Inaccessible,
            ReparsePoint
        }

        public static List<LegacyCopyCandidate> Find(string configDirectory)
        {
            string configPath;
            string candidatePath;
            List<string> ancestors;

            try
            {
                configPath = Path.GetFullPath(configDirectory);
                candidatePath = Path.Combine(configPath, CandidateName);
                ancestors = AncestorsFromRoot(configPath);
            }
            catch (ArgumentException)
            {
                return One("inaccessible");
            }
            catch (NotSupportedException)
            {
                return One("inaccessible");
            }
            catch (SecurityException)
            {
                return One("inaccessible");
            }
            catch (IOException)
            {
                return One("inaccessible");
            }

            foreach (var ancestor in ancestors)
            {
                FileAttributes attributes;
                var state = GetState(ancestor, out attributes);
                if (state == PathState.Missing) return None();
                if (state == PathState.Inaccessible) return One("inaccessible");
                if (state == PathState.ReparsePoint) return One("reparse-point");
                if ((attributes & FileAttributes.Directory) == 0) return None();
            }

            FileAttributes candidateAttributes;
            var candidateState = GetState(candidatePath, out candidateAttributes);
            if (candidateState == PathState.Missing) return None();
            if (candidateState == PathState.Inaccessible) return One("inaccessible");
            if (candidateState == PathState.ReparsePoint) return One("reparse-point");
            if ((candidateAttributes & FileAttributes.Directory) == 0) return One("unknown");

            var appState = GetRelativeState(candidatePath,
                new[] { "app", "resources", "app.asar" });
            if (appState == PathState.Accessible) return One("app");
            if (appState == PathState.Inaccessible) return One("inaccessible");
            if (appState == PathState.ReparsePoint) return One("reparse-point");

            var installState = GetRelativeState(candidatePath, new[] { "install.json" });
            if (installState == PathState.Accessible) return One("metadata");
            if (installState == PathState.Inaccessible) return One("inaccessible");
            if (installState == PathState.ReparsePoint) return One("reparse-point");

            var stateState = GetRelativeState(candidatePath, new[] { "state.json" });
            if (stateState == PathState.Accessible) return One("metadata");
            if (stateState == PathState.Inaccessible) return One("inaccessible");
            if (stateState == PathState.ReparsePoint) return One("reparse-point");

            return One("unknown");
        }

        private static List<string> AncestorsFromRoot(string path)
        {
            var stack = new Stack<string>();
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                stack.Push(current.FullName);
                current = current.Parent;
            }

            var result = new List<string>();
            while (stack.Count > 0) result.Add(stack.Pop());
            return result;
        }

        private static PathState GetRelativeState(string root, string[] parts)
        {
            var current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);

                FileAttributes attributes;
                var state = GetState(current, out attributes);
                if (state != PathState.Accessible) return state;

                bool isLeaf = i == parts.Length - 1;
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((!isLeaf && !isDirectory) || (isLeaf && isDirectory))
                    return PathState.Missing;
            }
            return PathState.Accessible;
        }

        private static PathState GetState(string path, out FileAttributes attributes)
        {
            attributes = 0;
            try
            {
                attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return PathState.ReparsePoint;
                return PathState.Accessible;
            }
            catch (FileNotFoundException)
            {
                return PathState.Missing;
            }
            catch (DirectoryNotFoundException)
            {
                return PathState.Missing;
            }
            catch (UnauthorizedAccessException)
            {
                return PathState.Inaccessible;
            }
            catch (SecurityException)
            {
                return PathState.Inaccessible;
            }
            catch (ArgumentException)
            {
                return PathState.Inaccessible;
            }
            catch (NotSupportedException)
            {
                return PathState.Inaccessible;
            }
            catch (IOException)
            {
                return PathState.Inaccessible;
            }
        }

        private static List<LegacyCopyCandidate> None()
        {
            return new List<LegacyCopyCandidate>();
        }

        private static List<LegacyCopyCandidate> One(string kind)
        {
            return new List<LegacyCopyCandidate>
            {
                new LegacyCopyCandidate { Name = CandidateName, Kind = kind }
            };
        }
    }
}
