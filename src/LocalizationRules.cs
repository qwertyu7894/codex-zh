using System;
using System.Collections.Generic;

namespace CodexZhLauncher
{
    /// <summary>一对「挑串 → 替换串」。</summary>
    internal sealed class PatchNeedle
    {
        public string From;
        public string To;
    }

    internal sealed class CodexPatchRule
    {
        public string Id;
        public string Scope;
        public string Ext;
        public string From;
        public string To;
        public int Expect;
        public bool Critical;
        public string AlreadyPatchedContext;
        public string Desc;

        /// <summary>
        /// 同一语义在别的打包产物里的其他写法。
        ///
        /// 为什么需要：Codex 26.1029 把
        /// <c>`enable_i18n`,!1</c>（模板字符串）改成了
        /// <c>"enable_i18n",!1</c>（双引号），语义没变、字符变了。
        /// 只维护一条挑串的话，每换一次引号就得发版；而且新版命中时旧版会变成
        /// “关键点未命中”。这里把不同写法并列，任意一条命中即算这条规则命中。
        /// </summary>
        public PatchNeedle[] Alternatives;

        /// <summary>
        /// 上下文串的其他写法。与 <see cref="Alternatives"/> 同理：
        /// 26.1029 里 <c>`locale_source`</c> 也换成了 <c>"locale_source"</c>，
        /// 只认一种写法会导致“已打过补丁”判定失效、每次全量扫描。
        /// </summary>
        public string[] ContextAlternatives;

        public bool HasAlreadyPatchedContext
        {
            get { return !String.IsNullOrEmpty(AlreadyPatchedContext); }
        }

        /// <summary>主上下文 + 备选上下文；没有配置时返回空序列。</summary>
        public IEnumerable<string> Contexts()
        {
            if (String.IsNullOrEmpty(AlreadyPatchedContext)) yield break;
            yield return AlreadyPatchedContext;
            if (ContextAlternatives == null) yield break;
            foreach (var alternative in ContextAlternatives)
            {
                if (!String.IsNullOrEmpty(alternative)) yield return alternative;
            }
        }

        /// <summary>主写法 + 备选写法，主写法优先。</summary>
        public IEnumerable<PatchNeedle> Needles()
        {
            yield return new PatchNeedle { From = From, To = To };
            if (Alternatives == null) yield break;
            foreach (var alternative in Alternatives)
            {
                if (alternative != null) yield return alternative;
            }
        }
    }

    /// <summary>
    /// 跨版本补丁规则表。与 localize/Rules.ps1 保持逐字段一致。
    ///
    /// 设计原则：
    ///   1. 不锚压缩后的变量名，只锚语义串 `enable_i18n`,!1；
    ///   2. 只允许等长替换；
    ///   3. 命中数必须报告，不等于 Expect 只警告，不改变成败。
    /// </summary>
    internal static class LocalizationRules
    {
        private const string From = "`enable_i18n`,!1";
        private const string To = "`enable_i18n`,!0";
        private const string PatchedContext = "`locale_source`";

        // 26.1029 起压缩产物的引号从模板字符串换成了双引号。
        // 挑串/替换串都只差引号，长度同为 16 字节，仍然是等长替换。
        private const string QuotedFrom = "\"enable_i18n\",!1";
        private const string QuotedTo = "\"enable_i18n\",!0";

        private static readonly PatchNeedle[] QuotedVariants =
        {
            new PatchNeedle { From = QuotedFrom, To = QuotedTo }
        };

        private static readonly CodexPatchRule[] rules =
        {
            new CodexPatchRule
            {
                Id = "enable-i18n-default",
                Scope = "webview/assets",
                Ext = ".js",
                From = From,
                To = To,
                Expect = 1,
                Critical = true,
                AlreadyPatchedContext = PatchedContext,
                ContextAlternatives = new[] { "\"locale_source\"" },
                Alternatives = QuotedVariants,
                Desc = "界面多语言总开关：默认 false -> true（不开则界面永远是英文）"
            },
            new CodexPatchRule
            {
                Id = "enable-i18n-default-legacy",
                Scope = ".vite/build",
                Ext = ".js",
                From = From,
                To = To,
                Expect = 1,
                Critical = false,
                AlreadyPatchedContext = null,
                Alternatives = QuotedVariants,
                Desc = "界面多语言总开关（旧版打包布局 .vite/build）"
            }
        };

        public static CodexPatchRule[] All
        {
            get { return rules; }
        }

        /// <summary>
        /// 规则表指纹：改任何一条规则的挑串/替换串/预期命中数，签名就变。
        /// 只用语义字段，不含 Desc。
        /// </summary>
        public static string Signature()
        {
            var parts = new List<string>();
            foreach (var rule in rules)
            {
                var text = rule.Id + ":" + rule.From + ">" + rule.To + ":" +
                    rule.Expect.ToString(System.Globalization.CultureInfo.InvariantCulture);
                // 备选写法也算规则的一部分：改它同样要让快路径失效。
                foreach (var needle in rule.Needles())
                {
                    text += ";" + needle.From + ">" + needle.To;
                }
                foreach (var context in rule.Contexts())
                {
                    text += "@" + context;
                }
                parts.Add(text);
            }
            return String.Join("|", parts.ToArray());
        }
    }
}
