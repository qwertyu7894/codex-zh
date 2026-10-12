# Codex 中文化工具（自研分支）

基于 [shibaweidu/codex-desktop-zh](https://github.com/shibaweidu/codex-desktop-zh) 的第二个改版，
修复了若干问题。当前版本 **v1.0.0**。

---

## ⚠️ 许可声明（请先读这一段）

**本项目不是开源项目。**

| 范围 | 许可 |
| --- | --- |
| **上游原始文件**（`src/`、`shared/`、`macos/`、`build.ps1` 骨架等） | 沿用上游 **MIT License**，版权归原作者所有，见 [`LICENSE`](LICENSE) |
| **本分支新增与修改的部分** | **保留所有权利（All rights reserved），不对外开源** |

关于 MIT：它允许闭源分发衍生版本，**但要求保留版权声明与许可文本**。
因此本仓库保留 `LICENSE` 与 `THIRD_PARTY_NOTICES.md`，**这不是把项目变成开源，而是履行上游的署名义务**。

本分支自己写的部分（`localize/` 全目录、重写的 `src/LocalizationRuntime.cs`、
`src/CodexDiscovery.cs` 的副本识别、`src/UpdateChecker.cs` 的中和、界面配色等）
**未经许可不得复制、分发或用于商业用途**。

简言之：**上游那部分是 MIT，我们改的部分不是。**

---

## 这个分支改了什么

上游 v0.7.7 的汉化方式，是用 `--inspect` 连上 Electron 主进程注入 JS 改写原生菜单。

新版 Codex 桌面端（`26.1007.*`）关掉了 Electron fuse `EnableNodeCliInspectArguments`，
`--inspect` 被静默忽略，**这条路已永久失效**；同时 MSIX 清单只声明 `en-US`，
把 `app.getLocale()` 顶死成英文。

本分支改为**磁盘补丁**方案，完全不依赖运行时注入：

| 层 | 做法 |
| --- | --- |
| **界面** | 把 `app.asar` 内 `enable_i18n` 的**默认值**由 `false` 改为 `true`。改默认值而非运行时状态，**一次生效、永久有效** |
| **菜单** | 把 App **复制出 MSIX 包**运行。脱离包后 PRI 资源语言限制消失，`app.getLocale()` 跟随系统语言，原生菜单自动加载 `native-menu-locales/zh-CN.json` |
| **完整性** | 改完 asar **必须**同步更新头部里该文件的 per-file 哈希，并把重算后的「头部哈希」写回 `ChatGPT.exe` 里嵌入的记录 |

第三步不能漏。`EnableEmbeddedAsarIntegrityValidation=1` 是真的在执行，
只改 asar 会导致 App 启动瞬间崩溃：

```
FATAL:third_party\electron\shell\common\asar\asar_util.cc:143
Integrity check failed for asar archive
```

三步全部是**等长原地替换**（1 字节 / 64 位十六进制），因此不需要解包重打包，
整个补丁过程是毫秒级的。

---

## 构建

```powershell
.\build.ps1 -Configuration Release
# 产物：dist\CodexZh-Windows-x64.exe
```

**不需要任何 SDK** —— 用 Windows 自带的
`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 编译。

汉化逻辑位于 `localize\`，构建时以**嵌入资源**打进 exe，运行时释放到
`%LOCALAPPDATA%\CodexZhLauncher\scripts\` 并调用。
**逻辑只有一份**：改 `localize\` 下的脚本后重新构建即可，C# 侧无需改动。

---

## 用法

双击 `CodexZh-Windows-x64.exe` → 点「汉化并启动」。

首次约 40 秒（会把 App 复制到 `%USERPROFILE%\.codex\codex-zh\app`，约 2 GB）。

**装完之后不需要再开这个工具** —— 客户端可以直接双击
`%USERPROFILE%\.codex\codex-zh\app\ChatGPT.exe`，中文界面与中文菜单都在，
因为补丁是写在磁盘上的，不是运行时注入。

工具只在三种情况才需要再打开：首次安装、**Codex 升级之后**重打一次、当启动器用。

---

## 项目结构

| 路径 | 说明 |
| --- | --- |
| `localize/` | 汉化逻辑（PowerShell，唯一事实来源） |
| `src/` | Windows 桌面端（C# / WPF，无 XAML，UI 全部代码构建） |
| `macos/` | macOS 端（Swift）—— **本分支未适配，仍是上游原样** |
| `shared/` | 上游遗留的注入脚本（本分支不再使用，保留仅为不破坏上游构建） |

---

## 明确不做的事

不联网、不下载、不写注册表、不装服务 / 计划任务、不读凭据、
**不修改 `C:\Program Files\WindowsApps\` 里的任何原版文件**。

上游的自动更新检查已在本分支**停用**：它指向原作者仓库，
留着会把用户的工具覆盖成上游版本，而那一套在当前 Codex 上已经失效。
