using System;
using System.Collections.Generic;

namespace CodexZhLauncher
{
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

        public bool HasAlreadyPatchedContext
        {
            get { return !String.IsNullOrEmpty(AlreadyPatchedContext); }
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
                parts.Add(rule.Id + ":" + rule.From + ">" + rule.To + ":" +
                          rule.Expect.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return String.Join("|", parts.ToArray());
        }
    }
}
