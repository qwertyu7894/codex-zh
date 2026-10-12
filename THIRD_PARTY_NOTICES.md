# Third-party notices

Codex Zh Launcher is an independent community project. It is not affiliated
with or endorsed by OpenAI or Microsoft.

The project launches an existing user-installed copy of Codex Desktop and uses
public Chromium DevTools Protocol and Electron runtime interfaces. It does not
contain or redistribute Codex Desktop binaries.

The implementation in this repository was written independently. No source
code from CodexPlusPlus is included.

---

## 本分支的许可说明

本仓库是 [`shibaweidu/codex-desktop-zh`](https://github.com/shibaweidu/codex-desktop-zh)
的**衍生版本**。**整个仓库（含本分支的全部修改）均以 MIT License 开源**，
见 [`LICENSE`](LICENSE)。

版权分为两段，各自归属：

| 范围 | 版权 |
| --- | --- |
| 上游原始文件（`src/`、`shared/`、`macos/`、`scripts/`、`tests/`、`build.ps1` 骨架等） | © 2026 Codex Zh Launcher contributors |
| 本分支的新增与修改 | © 2026 qwertyu7894 |

本分支改动的主要部分：

- `localize/` 整个目录（`CodexZh.ps1`、`AsarInPlace.ps1`、`Rules.ps1`）
- `src/LocalizationRuntime.cs`（重写为调用本地化脚本）
- `src/CodexDiscovery.cs` 中「本地化副本识别」相关的新增部分
- `src/Program.cs` 的全局异常兜底
- `src/UpdateChecker.cs` 中停用上游自动更新的改动
- `src/MainWindow.cs`、`src/AboutWindow.cs` 中的配色与标题栏改动
- `build.ps1` 的品牌参数与嵌入资源改动
- `scripts/package.py`、`docs/使用说明.template.txt`、`macos/probe.sh`

### 上游 MIT 原文

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

### 分发提醒

按 MIT 第 2 条，**以二进制形式分发构建产物时仍须随附版权声明与许可文本**
（可置于「关于」界面或随附文件中）。

---

## 本分支与上游的技术关系

上游 v0.7.7 依赖 `--inspect` 注入 Electron 主进程改写原生菜单。
该通道在当前 Codex 桌面端上已被 Electron fuse `EnableNodeCliInspectArguments=0` 关闭，
**永久失效**。本分支改用磁盘补丁方案（本地化副本 + `app.asar` 原地改写 + 完整性哈希同步），
不依赖任何运行时注入。详见 `README.md`。

`shared/` 目录下的注入脚本在本分支中**已不再使用**，
保留仅为不破坏上游原有的构建与自检流程。
