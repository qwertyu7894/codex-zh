# 更新记录

本文件记录**本分支**（自研改版）的变更。上游的更新记录见 [`CHANGELOG.upstream.md`](CHANGELOG.upstream.md)。

**版本号唯一来源**：`src/AppInfo.cs` 里的 `AppInfo.Version`。
发布包名由 `scripts/package.py` 自动带上该版本号。

---

## v1.1.2

### 安全修复：原地补丁模式下先备份原 asar

客户端实测发现 Codex 装在 `D:\ChatGPT\`（官网版），走的是 **`mode = 'direct'`**：
目录可写，于是直接改**用户原本的** `app.asar`，不生成副本。

问题：这种改动**没有副本可退**，而此前**没有做任何备份** —— 一旦写坏就不可恢复。
（对比：第三方工具 `codex-localized` 是留了 `app.asar.bak` 的，本分支漏了。）

现在 `-Install` 在 direct 模式下会先把原始 asar 复制为 `app.asar.bak`，
备份失败则**中止而不是继续**。Store 版（`store-copy`）不需要：
`WindowsApps` 里的原件我们从不触碰，原件本身就是最好的备份。

同时修正使用说明：原文写"复制一份到你的用户目录"，对官网版/便携版是**不准确**的，
已改为分别描述两种模式。

---

## v1.1.1

### 修复：可执行文件名必须为纯 ASCII

客户端实机定位（100% 复现，57 条崩溃记录 / ASCII 名 0 次崩溃）。

**现象**：双击 exe 后进程在 1~6 秒内**无声消失** —— 没有窗口、没有错误框、
没有控制台输出。

**根因**：**可执行文件名包含非 ASCII 字符**时（中文汉字、`é` 这类带音标的
拉丁字母都算），程序在"创建窗口 / 初始化图形设备"阶段走到 C 运行库的
`abort()` 路径，即 `ucrtbase.dll` 里的

```
B9 07 00 00 00   mov  ecx, 7        ; FAST_FAIL_FATAL_APP_EXIT
CD 29            int  29h           ; __fastfail
```

事件日志表现为 `ucrtbase.dll` + `0xC0000409` + 异常偏移 `0xA527E`。

**这不是本工具特有的问题** —— 上游官方正版改成中文名同样崩，
现场用系统编译器手写的最小 WPF 程序改成中文名同样崩。
文件夹名含中文**不受影响**，只有可执行文件的**文件名**敏感。

**所以**：

- 成品 exe 与发布包名一律使用**纯 ASCII**（`GPTHanhua.exe`、`CodexZh-Windows-x64.exe`）
- **品牌显示名（窗口标题、程序属性）继续用中文完全没问题** ——
  那是编译进 exe 内部的，不经过文件系统
- `scripts/package.py` 里已加注释固定这条约束

> ⚠️ 不要改回 `GPT汉化.exe` 这类含中文的名字。
> 也**不要**指望异常兜底能拦住它：`0xC0000409` 是 fail-fast，
> 进程级立即终止，托管层的 try/catch 与 UnhandledException 全部无效。

### 改进：自动检测覆盖"随手装在某个盘根目录"的官网版

客户端实测遇到过 Codex 装在 `D:\ChatGPT\`。原有的固定路径探测覆盖不到，
界面会显示「未找到 Codex」，只能手动点「选择 Codex」——
对非技术用户是个障碍。

现在会额外遍历所有固定盘的顶层 `ChatGPT\` / `Codex\` 目录
（`X:\ChatGPT\ChatGPT.exe`、`X:\ChatGPT\app\ChatGPT.exe` 等），
且**必须**同级存在 `resources\app.asar` 才认定，避免把恰好同名的程序误认成 Codex。
只看盘的顶层、不递归，代价极小。

---

## v1.1.0

针对客户端实机反馈修复，并补回一处重写时丢失的能力。

### 修复

- **双击程序没反应** —— ⚠️ 本条的**归因在 v1.1.1 被实机证据推翻，见下**

  v1.1.0 最初把这一现象归因为"`/target:winexe` 的程序启动时抛托管异常、
  没有控制台输出也没有弹框，所以静默消失"，并据此加了 try/catch +
  `AppDomain.UnhandledException` + `DispatcherUnhandledException` 三重兜底。

  **这个归因是错的。** 客户端实机定位给出的是完全不同的机制：
  **可执行文件名包含非 ASCII 字符**（中文、`é` 这类带音标的拉丁字母都算）时，
  进程会在初始化图形设备阶段调用 `ucrtbase.dll` 的
  `__fastfail(FAST_FAIL_FATAL_APP_EXIT)` —— 事件日志表现为
  `ucrtbase.dll` + `0xC0000409` + 异常偏移 `0xA527E`。

  **fail-fast 是进程级立即终止，托管层的三重兜底全部拦不住。**
  该异常兜底代码予以保留（对真正的托管异常仍然有用），
  但它**对本故障无效**，不要据此认为"双击没反应"已被修掉。
  真正的修复是**文件名改用纯 ASCII**，见下方 v1.1.1。

- **客户端没生成快捷方式**
  脚本的每一行输出现在都会落盘到 `launcher.log`（前缀 `ps>` / `ps!`）。
  客户机器上再出问题，直接取那个文件就能定位断在哪一步，不用猜。

### 补回

- **Codex 版本变化检测**（重写时丢失）
  原代码只判断副本"在不在"、**从不比对版本**。后果是商店版一旦升级，
  副本会永远跳过重新复制，用户会一直在跑**旧版 Codex** 且毫无察觉。
  现在 `-Install` 会比对 `state.json` 里记录的版本与当前商店版版本：
  不一致则清理旧副本 → 重新复制 → 重新打补丁 → 更新 state。

### 变更

- `scripts/package.py` 改为从 `src/AppInfo.cs` 读取版本号，发布包名自动带版本。
- 使用说明改为模板渲染（`docs/使用说明.template.txt`），两个品牌自动替换
  品牌名与 exe 名，不再手工维护两份。

### 已知限制

- **macOS 端未适配**，仍是上游原样，依赖已失效的 `--inspect` 注入方案。
- **便携版 / 官网安装版路径未在真机验证过**（逻辑自洽，但没有实测环境）。

---

## v1.0.0

首个自研版本。上游 v0.7.7 依赖 `--inspect` 注入 Electron 主进程，
而新版 Codex 关掉了 fuse `EnableNodeCliInspectArguments`，**该通道已永久失效**；
同时 MSIX 清单只声明 `en-US`，把 `app.getLocale()` 顶死成英文。

本版改用**磁盘补丁**方案，完全不依赖运行时注入：

- **界面**：改 `app.asar` 内 `enable_i18n` 的默认值 `false` → `true`。
  改的是默认值而非运行时状态，**一次生效、永久有效**。
- **菜单**：把 App 复制出 MSIX 包运行。脱离包后 `app.getLocale()` 跟随系统语言，
  原生菜单自动加载 `native-menu-locales/zh-CN.json`。
- **完整性**：改完 asar 必须同步更新头部里该文件的 per-file 哈希，
  并把重算后的头部哈希写回 `ChatGPT.exe` 内嵌的记录。
  漏掉这一步 App 会 `asar_util.cc:143 Integrity check failed` 启动即崩。

其余：

- 新增桌面 + 开始菜单快捷方式（`ChatGPT 中文版`）
- `CodexDiscovery` 增加本地化副本识别，使进程检测与「关闭并重启」流程无需改动即可工作
- 保留上游的自动检测与「选择 Codex」手动指定能力
- 停用指向上游仓库的自动更新检查
- 移除赞助位；界面配色改为 Apple 浅色
- 支持 `-Brand` / `-OutputName` 构建参数，同一份源码可出不同名字的成品
