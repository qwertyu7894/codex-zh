# 发布指南（GitHub 仓库侧）

**版本号怎么定、标签怎么打、发一次版走哪些步** —— 全部在
[`VERSIONING.md`](VERSIONING.md)。本文只讲仓库本身怎么摆。

> 本文原先是从上游搬来的，里面描述的 `.github/workflows/build.yml` /
> `release.yml` 在本仓库**并不存在**（本仓库没有 CI），产物的文件名也和现在不同。
> 2026-10-13 按实际状况重写。

---

## 仓库信息

| 项 | 值 |
| --- | --- |
| 仓库 | `qwertyu7894/codex-zh` |
| 网址 | https://github.com/qwertyu7894/codex-zh |
| 许可 | MIT（`LICENSE`） |
| 默认分支 | `main` |

**Description**：

```text
Windows Codex / ChatGPT 桌面端中文化工具：运行时注入中文界面与原生菜单，跟随 Codex 最新版，无需重打补丁。
```

**Topics**：

```text
openai-codex
codex-desktop
codex-chinese
chinese-localization
localization
i18n
electron
windows
wpf
dotnet
chinese
```

README 首段已自然覆盖「Codex 汉化」「Codex 中文」「Codex Desktop 中文版」
「Codex Desktop 汉化工具」这些搜索词。不要在标题或正文里重复堆砌关键词，
也不要使用「破解」「官方中文版」「去签名」这类容易误导的描述。

---

## 上传范围

上传 `src/`、`tests/`、`shared/`、`scripts/`、`docs/`、构建脚本、文档、
许可证与第三方声明。

**不要上传** `dist/`、`obj/`、`macos/.build/`、`macos/.artifacts/`、
本地日志、Codex 安装文件、账号数据、用户项目内容。

`macos/` 是上游原样保留、**本分支未适配**，可以上传，但不要为它建发布产物。

提交前先确认：

```powershell
git status --short
git check-ignore -v dist obj
```

---

## About 与 Social Preview

在仓库右侧 **About** 填 Description、Topics 与项目网站。

Social Preview 用 `1280 x 640` 的 PNG，在
**Settings > General > Social preview** 上传。截图里不能出现用户名、项目名、
路径、任务内容或其他个人信息，建议至少 1200 像素宽。

---

## 发布产物

**本仓库没有 CI。** 构建、打包、打标签、发 Release 全在本地完成
（`scripts/release.ps1`，见 [`VERSIONING.md`](VERSIONING.md)）。

`scripts/publish.ps1` 会编出两个品牌、打包到 `..\release\`：

| 品牌 | exe 文件名 | 发布包目录 |
| --- | --- | --- |
| Codex 汉化增强工具 | `CodexZh-Windows-x64.exe` | `CodexZh-v<版本>-Windows-x64\` |
| GPT汉化 | `GPTHanhua.exe` | `GPTHanhua-v<版本>-Windows-x64\` |

每个发布包里有四样东西，都打进同名 `.zip`：

```text
<exe>
使用说明.txt          ← 由 docs/使用说明.template.txt 渲染，带 UTF-8 BOM
LICENSE.txt           ← MIT 第 2 条：分发二进制必须随附许可文本
SHA256SUMS.txt        ← 该 exe 的 SHA-256
```

**exe 文件名必须是纯 ASCII。** 含中文的 exe 名在部分机器上会触发
`ucrtbase.dll` 的 fail-fast，进程无声消失（详见 README）。中文只保留在
品牌显示名里。

Release 页面上传的就是这两个 zip。交付给单个客户时用的是
`C:\Users\jojo\Music\GPTHanhua.exe`（`publish.ps1` 自动同步）。

---

## 发布后复核

在 Windows 10/11 x64 上：

1. 下载 Release 里的 zip，核对包内 `SHA256SUMS.txt` 与 exe 实际哈希
2. 双击 exe → 点「汉化并启动」→ 约 20~40 秒后界面与原生菜单均为中文
3. 在 Codex 里点一次菜单，确认原生菜单也是中文（不只是网页部分）

---

## 明确不做的事

不联网（注入只连本机 `127.0.0.1` 的调试端口）、不下载、不写注册表、
不装服务或计划任务、不读凭据、**不修改 `C:\Program Files\WindowsApps\`
里的任何原版文件**。

本仓库**不发布 macOS 产物**：`macos/` 是上游原样保留、未适配。
