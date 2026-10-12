# localize —— 汉化逻辑

Codex / ChatGPT 桌面端中文化工具。**自研，零第三方依赖，不联网。**

> **本目录下的三个 `.ps1` 是逻辑的唯一事实来源。**
> 构建时由仓库根的 `build.ps1` 以嵌入资源打进 exe，运行时释放并调用。
> 改逻辑只改这里、重新构建即可，C# 侧无需任何改动。
>
> 交付形态是带界面的桌面程序（在仓库 `dist\`，由 `build.ps1` 产出，双击即用）。
> 本目录的脚本也可以**脱离界面直接跑**，见下面的「用法」。

---

## 为什么需要它

桌面端从 MSIX 商店版装出来时，有三层各自独立的"永远是英文"：

| 层 | 原因 | 表现 |
|---|---|---|
| 界面 | App 把 `enable_i18n` 默认写死为 `false` | 界面全英文，`localeOverride` 设了也没用 |
| 菜单 | MSIX 清单只声明 `<Resource Language="en-US"/>`，`app.getLocale()` 被顶成 `en-US` | 原生菜单全英文，传 `--lang=zh-CN` 也无效 |
| （旧方案失效） | App 新构建关掉了 Electron fuse `EnableNodeCliInspectArguments` | 上一代工具靠 `--inspect` 注入主进程改菜单，**永久失效** |

本工具用两条干净的路解决前两层，**完全不依赖注入**：

1. **界面** —— 把 `app.asar` 里 `enable_i18n` 的**默认值**由 `false` 改为 `true`。
   改的是默认值而非运行时状态，**一次生效、永久有效**，不需要每次启动重新注入。
2. **菜单** —— 把 App 复制出 MSIX 包运行。脱离包后 PRI 资源语言限制消失，
   `app.getLocale()` 跟随系统语言，原生菜单自动加载 `native-menu-locales/zh-CN.json`。

顺带好处：脱离 MSIX 后 `sparkle` 更新器自动失效（`missing current windows package family`），
**客户端不会再被自动升级打乱版本**。

---

## 用法

```powershell
.\CodexZh.ps1 -Status                 # 看状态
.\CodexZh.ps1 -Install -DryRun        # 只检测，报告会改什么
.\CodexZh.ps1 -Install                # 复制副本 + 打补丁
.\CodexZh.ps1 -Launch                 # 启动本地化副本

.\CodexZh.ps1 -Install -Target "D:\Somewhere\app"   # 对指定目录操作
```

首次 `-Install` 会把 App 复制到 `%USERPROFILE%\.codex\codex-zh\app`（约 2GB，40 秒左右）。
**绝不修改 `C:\Program Files\WindowsApps\` 里的任何文件。**

---

## 关键设计：为什么是"原地改字节"

asar 布局：

```
[u32=4][u32 u1][u32 u2][u32 u3][ JSON 头部 ][ 数据区 ]
                                 ↑ 注意这里
   数据区起点 = 8 + u1，不是 16 + u3 —— JSON 后面有 4 字节对齐填充
```

补丁**不解包、不重打包**，直接在数据区对应偏移改写字节。
前提是**替换前后字节数完全一致**，于是：其他 2 万多个文件的偏移不变、头部长度不变、整个流程只需毫秒级。

> 也正因为这个约束，`` `locale_source`,`IDE` `` → `` `SYSTEM` `` 这类**变长**替换一律不做（3 字节 → 6 字节会顶掉所有后续偏移）。语言改由 `config.toml` 的 `localeOverride` 负责。

### 三步缺一不可

`EnableEmbeddedAsarIntegrityValidation=1` 是**真的在执行**。只改 asar 会导致启动瞬间 FATAL 崩溃：

```
asar_util.cc:143 Integrity check failed for asar archive
```

完整的补丁链是：

```
1. 改写目标 JS 的文件字节
2. 同步更新 asar 头部里该文件的 per-file SHA-256
3. 重算「头部 JSON 区」的 SHA-256，写回 ChatGPT.exe 里的嵌入记录
       [{"file":"resources\\app.asar","alg":"SHA256","value":"<64 hex>"}]
```

三步全部是**等长十六进制/单字节替换**，因此都可以原地完成。

---

## 版本适配

App 每次更新都会让"压缩后的变量名"变化。规则表**只锚定语义不变的那一段**：

```
✅  `enable_i18n`,!1        ← 变量名无关
❌  a?.get(`enable_i18n`,!1) ← 变量名写死，一变就失配（上一代工具的坑）
```

如果提示"关键补丁点未命中"，说明 App 结构变了：
到 `app.asar` 里搜 `enable_i18n`，看新的默认值写法，改 `Rules.ps1` 里对应的 `From`/`To` 即可。
**注意保持 `From` 与 `To` 等长。**

---

## 文件

| 文件 | 作用 |
|---|---|
| `CodexZh.ps1` | 入口：`-Status` / `-Install` / `-Launch` |
| `AsarInPlace.ps1` | asar 原地改写核心 + 嵌入式完整性哈希同步 |
| `Rules.ps1` | 跨版本补丁规则表 |
| `launch.cmd` | 双击启动 |

---

## 明确不做的事

不联网、不下载、不写注册表、不装服务/计划任务、不读凭据、不修改 WindowsApps 原版。
