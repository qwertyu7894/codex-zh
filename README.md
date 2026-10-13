# Codex 中文化工具

基于 [shibaweidu/codex-desktop-zh](https://github.com/shibaweidu/codex-desktop-zh) 的改版。

汉化默认走**运行时注入**：启动 Codex 时连上它的渲染进程，把中文界面注入进去 ——
作用在**正在跑的那个版本**上，所以 **Codex 升级后不需要重打**。
注入失败会**自动回退**到**磁盘补丁**（复制一份 Codex、改副本的 `app.asar`）。

当前版本见 [`CHANGELOG.md`](CHANGELOG.md)，
版本号唯一来源是 `src/AppInfo.cs` 的 `AppInfo.Version`。

---

## ⚠️ 构建产物文件名必须为纯 ASCII

**不要**把 exe 命名为 `GPT汉化.exe` 这类含中文的名字。

实机定位结论：可执行文件**文件名**含非 ASCII 字符时，这类 .NET Framework WPF
程序会在初始化图形设备阶段触发 `ucrtbase.dll` 的
`__fastfail(FAST_FAIL_FATAL_APP_EXIT)`，事件日志表现为
`ucrtbase.dll` + `0xC0000409`，进程在 1~6 秒内**无声消失** ——
没有窗口、没有错误框、没有控制台输出，用户看到的就是"双击没反应"。

- **这不是本工具特有的**：上游官方正版、乃至现场手写的最小 WPF 程序，
  改成中文名同样崩；改成 ASCII 名同样好。
- **fail-fast 拦不住**：它绕过全部托管异常处理，
  `try/catch` / `AppDomain.UnhandledException` / `DispatcherUnhandledException` 全部无效。
- **只有文件名敏感**：文件夹名、品牌显示名（窗口标题、程序属性）用中文都不受影响，
  因为那些不经过文件系统。

`scripts/package.py` 里的 `BUILDS` 已固定这条约束并附注释。

---

## 许可

**本项目开源，采用 [MIT License](LICENSE)。**

| 范围 | 版权 |
| --- | --- |
| 上游原始文件（`src/`、`shared/`、`macos/`、`build.ps1` 骨架等） | © 2026 Codex Zh Launcher contributors |
| 本分支的新增与修改（`localize/` 全目录、重写的 `LocalizationRuntime`、快捷方式、配色等） | © 2026 qwertyu7894 |

两者都以 MIT 授权：**可自由使用、修改、分发、商用、闭源再发布，只需保留版权声明与许可文本。**

上游项目的署名义务另见 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)。

---

## 这个分支改了什么

上游 v0.7.7 用 `--inspect` 连 Electron **主进程**注入 JS 改写原生菜单。
**新版** Codex（`26.1007.*` 起）关掉了 Electron fuse `EnableNodeCliInspectArguments`，
`--inspect` 被静默忽略 —— 那条路在新版上不可用。

> 它不是"永久失效"：fuse 是**编译期**烧进二进制的属性，绑的是 **Codex 的版本**而不是时间。
> 仍跑旧版 Codex 的机器上照常工作。

本分支改用两条路，**互为保险**：

### 默认：运行时注入（`src/InjectionRuntime.cs`）

启动 Codex 时带 `--remote-debugging-port=<随机回环端口>`，连上**渲染进程**注入
`shared/i18n-bootstrap.js`：

| 层 | 做法 |
| --- | --- |
| **界面** | 劫持 Statsig 的 `getDynamicConfig` / `getLayer`，把 configId `72216192` 强制成 `enable_i18n=true` |
| **菜单** | 同时强制 `locale_source='SYSTEM'` —— App 自己按系统语言重建原生菜单 |

成功判据是页内 `__codexZhI18nState.patchedConfigs >= 1`（实测 16~40 秒生效，
因为要等 App 自己去重取一次动态配置）。

> **`--remote-debugging-port` 是 Chromium 的开关，不受 Electron fuse 管辖** ——
> 实测在新版上仍然可用。而 `--inspect` 那条路已经死了，所以**不要**把它加回来
> （`--self-test` 里有断言拦着）。

### 回退：磁盘补丁（`localize/`，PowerShell）

注入不成功时自动回退（回退前必须先关掉刚启动的实例 —— 与副本共用单实例锁）。
改 `app.asar` 内 `enable_i18n` 的**默认值** `false` → `true`，
改完**必须**同步两处哈希，否则 App 启动瞬间崩溃：

```
FATAL:third_party\electron\shell\common\asar\asar_util.cc:143
Integrity check failed for asar archive
```

| 步骤 | 内容 |
| --- | --- |
| 1 | 改写目标 JS 的文件字节（**必须等长**，1 字节） |
| 2 | 同步更新 asar 头部里该文件的 per-file SHA-256 |
| 3 | 重算「头部 JSON 区」的 SHA-256，写回 `ChatGPT.exe` 里嵌入的记录 |

商店版无法原地改（`WindowsApps` 受 ACL 保护），所以先复制一份再改副本。

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

默认走运行时注入，约 **20~40 秒**（期间界面先英文、后中文）；
若注入失败会自动回退磁盘补丁（会多花一两分钟复制副本）。日志里会写明走的哪条路。

启动成功后工具会把「ChatGPT 中文版」快捷方式**指向工具自身**（带 `--launch-zh`）——
双击它 = 每次走一遍注入，对用户仍然只是一次双击。

> 为什么必须重指：注入时代之前那个快捷方式直接指向副本 exe，
> 双击它会启动**没有经过注入**的副本 —— 用户看到的还是英文，然后以为注入没用。

机器上若还留着磁盘补丁时代的本地化副本（`%USERPROFILE%\.codex\codex-zh\app`，约 2 GB），
启动成功后会弹框询问是否删除。**只删 `codex-zh\` 这一个目录，
绝不碰 `.codex\config.toml`** —— 那是 `localeOverride` 所在，删了原生菜单就回英文。

### 调试用的命令行开关

```
--launch-zh                 默认路线（注入 → 失败回退补丁）
--launch-zh-inject-only     只走注入，不回落
--launch-zh-patch-only      只走磁盘补丁
--self-test                 构建期自检
--diagnostics               打印探测到的安装信息
```

---

## 更新后汉化还在吗

**默认路线（运行时注入）：永远在，而且永远是最新版。**

汉化是在 Codex 跑起来之后注入的，所以它作用在**当前正在跑的那个版本**上 ——
Codex 什么时候更新，下次打开就是最新版 + 中文。**不需要任何"重打"。**

**回退路线（磁盘补丁）：汉化打在"复制那一刻的版本"上**，所以：

| | 界面汉化（改 asar） | 菜单汉化 |
| --- | --- | --- |
| **商店版**（`store-copy`） | 还在（副本是独立目录，商店更新碰不到它），**但停在旧版本** | 不受影响 |
| **官网版 / 便携版**（`direct`） | 更新会覆盖 `app.asar` → **失效** | 不受影响 |

重跑一次工具即可跟上（`-Install` 会比对 `state.json` 记录的版本与当前源安装版本，
不一致就重做副本 + 重打补丁）。

> 这正是引入运行时注入的原因：磁盘补丁那条路会在 Codex 升级后
> **让用户停在旧版而毫无察觉** —— 界面上没有新版本的内容。

---

## 项目结构

| 路径 | 说明 |
| --- | --- |
| `localize/` | 汉化逻辑（PowerShell，唯一事实来源） |
| `src/` | Windows 桌面端（C# / WPF，无 XAML，UI 全部代码构建） |
| `macos/` | macOS 端（Swift）—— **本分支未适配，仍是上游原样** |
| `shared/` | 注入载荷（`i18n-bootstrap.js`、`locale-script.js` 等）—— **运行时注入路线正在使用** |

---

## 明确不做的事

不联网、不下载、不写注册表、不装服务 / 计划任务、不读凭据、
**不修改 `C:\Program Files\WindowsApps\` 里的任何原版文件**。

上游的自动更新检查已在本分支**停用**：它指向原作者仓库，
留着会把用户的工具覆盖成上游版本，而那一套在新版 Codex 上已经用不了。
