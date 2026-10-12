# Third-party notices

Codex Zh Launcher is an independent community project. It is not affiliated
with or endorsed by OpenAI or Microsoft.

The project launches an existing user-installed copy of Codex Desktop and uses
public Chromium DevTools Protocol and Electron runtime interfaces. It does not
contain or redistribute Codex Desktop binaries.

The implementation in this repository was written independently. No source
code from CodexPlusPlus is included.

---

## 本分支（自研改版）的许可说明

本仓库是 [`shibaweidu/codex-desktop-zh`](https://github.com/shibaweidu/codex-desktop-zh)
的**衍生版本**。许可证按来源分层，请注意区分：

### 1. 上游部分 —— MIT License

`src/`、`shared/`、`macos/`、`scripts/`、`tests/`、`build.ps1` 的骨架，
以及 `LICENSE`、本文件中原有的段落在内的**上游原始内容**，
均沿用上游的 MIT License，版权归上游作者所有：

```
MIT License

Copyright (c) 2026 Codex Zh Launcher contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
```

> MIT 允许闭源分发衍生版本。**保留 `LICENSE` 与本文件，是在履行 MIT 要求的
> 署名义务，并不意味着本项目整体开源。**

### 2. 本分支新增与修改的部分 —— 保留所有权利

以下内容由本分支作者编写或修改，**保留所有权利（All rights reserved），
不对外开源**，未经书面许可不得复制、分发、再许可或用于商业用途：

- `localize/` 整个目录（`CodexZh.ps1`、`AsarInPlace.ps1`、`Rules.ps1`）
- `src/LocalizationRuntime.cs`（重写为调用本地化脚本）
- `src/CodexDiscovery.cs` 中「本地化副本识别」相关的新增部分
- `src/UpdateChecker.cs` 中停用上游自动更新的改动
- `src/MainWindow.cs`、`src/AboutWindow.cs` 中的配色与标题栏改动
- `build.ps1` 中嵌入 `localize/` 资源的相关改动
- `README.md`（本文件顶部的上游段落除外）

### 3. 关于分发的提醒

若以二进制形式分发本项目的构建产物，按 MIT 第 2 条，
**仍须随附上游的版权声明与许可文本**（可置于关于界面或随附文件中）。

---

## 本分支与上游的技术关系

上游 v0.7.7 依赖 `--inspect` 注入 Electron 主进程改写原生菜单。
该通道在当前 Codex 桌面端上已被 Electron fuse `EnableNodeCliInspectArguments=0` 关闭，
**永久失效**。本分支改用磁盘补丁方案（本地化副本 + `app.asar` 原地改写 + 完整性哈希同步），
不依赖任何运行时注入。详见 `README.md`。

`shared/` 目录下的注入脚本在本分支中**已不再使用**，
保留仅为不破坏上游原有的构建与自检流程。
