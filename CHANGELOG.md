# 更新记录

本文件记录**本分支**（自研改版）的变更。上游的更新记录见 [`CHANGELOG.upstream.md`](CHANGELOG.upstream.md)。

**版本号唯一来源**：`src/AppInfo.cs` 里的 `AppInfo.Version`。
发布包名由 `scripts/package.py` 自动带上该版本号。

---

## v1.1.0

针对客户端实机反馈修复，并补回一处重写时丢失的能力。

### 修复

- **双击程序没反应**
  `/target:winexe` 编译出来的程序，在启动阶段抛异常时**既没有控制台输出、
  也没有任何弹框**，进程直接静默消失 —— 用户看到的就是"双击没反应"。
  现在 `Program.Main` 外面套了 try/catch，并同时挂上
  `AppDomain.UnhandledException` 与 `DispatcherUnhandledException`，
  任何未处理异常都会**弹出错误框**并写入 `launcher.log`。

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
