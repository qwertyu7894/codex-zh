# 版本与发布规范

这份文档回答三件事：**版本号写在哪**、**什么时候进位**、**发一次版要走哪些步**。

规范的目标只有一个：**让"版本号""标签""发布包内容"三者永远对得上。**
v1.2.2 那次就是三者对不上 —— GitHub 上的 `v1.2.2` 标签指着一个自称 `1.2.1`
的提交，而 Release 附件却是用 1.2.2 的代码编出来的。当时没有任何一步会检查
这件事，所以谁也没发现。现在有了 `scripts/verify-version.ps1`。

> 参考了 DeepSeek Harness（`deepseek-ai/deepseek-harness`）的做法：单一版本来源、
> 标签即发布点、发布前强制校验。它那套 `-alpha.N` / `-rc.N` 预发布档**没有采纳** ——
> 见下面第 3 节的理由。

---

## 1. 版本号写在哪（唯一来源）

| 位置 | 内容 | 谁读它 |
| --- | --- | --- |
| **`src/AppInfo.cs` → `AppInfo.Version`** | `"1.2.2"` | **唯一来源。** 打包、闸门、程序标题都从这里取 |
| `src/Program.cs` 两行程序集版本 | `"1.2.2.0"` | Windows 文件属性里显示的版本 |
| `dist/buildinfo.json` | `"1.2.2"` | 不是手写的：`build.ps1` **编译成功后才写**，`package.py` 拿它对账 |

**改版本号 = 改上面第一处，然后同步第二处。** 第三处自动生成，不要手改。

`build.ps1` 只在编译成功后写构建戳，`package.py` 又会拿它跟源码版本比对，
对不上就拒绝打包 —— 这道防线拦的是"编译失败但照样打包、包名已是新版本而
内容还是旧的"，2026-10-12 真的发生过。

---

## 2. 什么时候进哪一位

用语义化版本（`主.次.修订`），在本项目里的具体含义：

| 位 | 什么时候 +1 | 本项目实例 |
| --- | --- | --- |
| **主版本** | 破坏性变化：用户必须改变用法，或旧配置/旧副本不再兼容 | 还没有过（1.0.0 起算） |
| **次版本** | 新增一条路线，**或改变默认行为** | `1.2.0` 新增运行时注入、把磁盘补丁降为回退 |
| **修订** | 修 bug、改文案、改交互细节 | `1.2.1`、`1.2.2` |

几条补充约定：

- **别发太勤。** 这是发给客户的工具，版本号是客户眼里的信用凭证 —— 几小时里冒出
  好几个版本，会让人觉得不靠谱，也没人跟得上"这一版到底改了什么"。
  零碎改动**先攒着**，等合起来够让客户重新下载一次了再发。中间过程用 **commit**
  记录就够了（commit 随便提）。`scripts/release.ps1` 把发版变容易了，
  **不代表应该多发**。
- **纯文档 / 纯脚本改动不单独发版**，攒着跟下一次真实改动一起走。
  版本号描述的是**用户拿到手的东西**变了什么。
- 版本号是**一条线**，不跳号。上一位进位时后面清零（`1.2.9` → `1.3.0`）。
- 仓库里没有第二条产品线，所以**不需要给版本号加项目前缀**。
  DeepSeek Harness 的 `dsh-v0.2.1-alpha.2` 是因为他们同时有 `dsh`、`vendor`、
  `native` 三条各自的版本线，前缀是给那时候用的。

---

## 3. 标签规范

**格式**：`v<主>.<次>.<修订>` —— 例如 `v1.2.2`。

| 约定 | 说明 |
| --- | --- |
| **不带项目前缀** | 本仓库只有一条产品线，`v` 前缀就够，GitHub 也认它是标准版本标签 |
| **用 annotated（`git tag -a`）** | 轻量标签只有一个提交指针；annotated 带创建者、日期和说明，`git describe` 也能用 |
| **一个版本一个标签** | 标签**就是**这个版本的发布点，没有别的含义 |
| **标签必须指向"该版本的代码"** | 标签指向的提交里，`AppInfo.Version` 必须等于标签名 —— 这条由闸门强制 |
| **不引入 `-alpha.N` / `-rc.N` 预发布档** | 本工具的交付物是**单个成品 exe**，直接发给用户，没有内测人群。每个版本号都必须是可直接交付的成品；没验过的改动就留在分支上别打标签。DeepSeek Harness 用那套是因为它有明确的开发者预览人群、且 API 会破坏性变更 |
| **已推送的标签就是公开的** | 要重指必须先删远端标签（`git push origin :refs/tags/vX.Y.Z`），会改变别人下载到的源码包 |

> `v1.2.0` 与 `v1.2.1` 是这套规范定下**之前**由 `gh release create` 顺带建的
> **轻量标签**（没有创建者/日期/说明）。它们指向的提交是对的，所以保留原样、
> 不追溯重建 —— 规范从 `v1.1.0` 这一批补建的历史标签起生效。
> 判断办法：`git cat-file -t v1.2.0` 返回 `commit` 是轻量，返回 `tag` 是 annotated。

`v1.1.3` **没有**标签：那一版和 `v1.1.4` 的改动被合进了同一个提交
（`b33f271`），没有任何提交自称过 `1.1.3`。按上面的规矩，**不给一个没有
对应代码的版本号打标签** —— 硬打上去正是本项目要消灭的那种不一致。

---

## 4. 发布前置校验（闸门）

```powershell
.\scripts\verify-version.ps1               # 查内部一致性（含 HEAD 上恰好有的标签）
.\scripts\verify-version.ps1 -Tag v1.2.3   # 额外要求这个标签与版本号一致
.\scripts\verify-version.ps1 -SkipStamp    # 编译前用：dist 里还是上一版的戳
.\scripts\verify-version.ps1 -Quiet        # 只看退出码
```

它把四处对一遍，**任何一处对不上就退出码 1**：

| 查什么 | 期望 |
| --- | --- |
| `src/AppInfo.cs` | 版本号唯一来源，读不到就直接失败 |
| `src/Program.cs` 两行程序集版本 | 必须等于 `<版本>.0` |
| `dist/buildinfo.json` | 存在时必须等于源码版本（编译前用 `-SkipStamp` 跳过） |
| git 标签 | 标签名必须等于**它指向的那个提交**自称的版本 |

最后一条是关键。只比"标签名 vs 当前源码"是不够的 —— v1.2.2 的错恰恰是
标签名对、但它指向的提交不对，只有去读那个提交里的 `AppInfo.cs` 才能抓到。
（副作用：旧标签也能审，不要求它等于当前工作区的版本。）

**它会在三个地方自动跑**，你不用记得手动执行：

- `scripts/publish.ps1` —— 编译前（`-SkipStamp`）
- `scripts/package.py` —— 打包前（对构建戳，原有逻辑）
- `scripts/release.ps1` —— 发版时两遍（打标签前、打完标签再复查一遍）

---

## 5. 发版流程（一条命令）

```powershell
# 1) 改版本号：src/AppInfo.cs 的 AppInfo.Version，同步 src/Program.cs 两行
# 2) 在 CHANGELOG.md 顶部加 "## v1.2.3" 一段  ← 发布说明就从这里抽
# 3) 提交

.\scripts\release.ps1              # 本地：校验 -> 编译打包 -> 打标签
.\scripts\release.ps1 -Publish     # 再加上：推分支和标签 + 建 GitHub Release
.\scripts\release.ps1 -DryRun      # 只跑到检查为止，看它认不认
```

`release.ps1` 依次做：

1. **前置检查** —— 工作区干净（有未提交改动就拒绝，否则标签会指向不含这些改动的提交）、
   不在游离 HEAD、该版本的标签还没被占用
2. **版本校验** —— 调 `verify-version.ps1`
3. **抽发布说明** —— 从 `CHANGELOG.md` 里 `## vX.Y.Z` 那一段
4. **编译打包** —— 调 `publish.ps1`（两个品牌 + 打包到 `release\` + 同步音乐文件夹）
5. **打标签** —— `git tag -a`，打完再自查一遍标签指向的提交版本
6. **发布** —— 仅当给了 `-Publish`

**默认不发到 GitHub**：跑完本地部分会把该敲的三条命令打印出来。往公开仓库
推东西这件事值得多看一眼再动手。

---

## 6. 手工流程（脚本哪天坏了，照这个走）

```powershell
# 版本号：src/AppInfo.cs 改 AppInfo.Version；src/Program.cs 两行改成 "<版本>.0"
# CHANGELOG.md 加 "## v1.2.3" 一段

.\scripts\verify-version.ps1 -SkipStamp        # 必须 EXIT=0
.\scripts\publish.ps1                          # 编译 + 打包 + 同步音乐文件夹

git add -A
git commit -m "v1.2.3：<一句话>"

git tag -a v1.2.3 -m "v1.2.3"
.\scripts\verify-version.ps1 -Tag v1.2.3       # 再查一遍，必须 EXIT=0
git push origin main
git push origin v1.2.3

# 建 Release（附件用 release\ 里那两个 zip；说明文件从 CHANGELOG 抽）
gh release create v1.2.3 "..\release\GPTHanhua-v1.2.3-Windows-x64.zip" `
                          "..\release\CodexZh-v1.2.3-Windows-x64.zip" `
                          --title "v1.2.3" --notes-file notes.md
```

**顺序不能变**：先编译打包，再 `git add` / `commit`，最后打标签。
标签指向的是**提交**，而提交里必须已经是最终代码。

---

## 7. 发布检查清单

- [ ] `src/AppInfo.cs` 的 `AppInfo.Version` 已改，`src/Program.cs` 两行同步
- [ ] `CHANGELOG.md` 有该版本段落（发布说明的唯一来源）
- [ ] `verify-version.ps1 -SkipStamp` 退出码 0
- [ ] `publish.ps1` 跑完，`C:\Users\jojo\Music\GPTHanhua.exe` 已更新
- [ ] **真机跑一次**：双击工具 → 中文界面 + 中文原生菜单
- [ ] `verify-version.ps1 -Tag vX.Y.Z` 退出码 0（标签指向的提交自称同一版本）
- [ ] Release 附件里 `SHA256SUMS.txt` 与 zip 内 exe 的实际 SHA-256 一致
- [ ] 使用说明 / README 里描述的行为与这一版实际行为一致

---

## 8. 已发布版本 ↔ 提交对照表

标签指向的就是"这一版的代码"：

| 版本 | 标签 | 提交 | 一句话 |
| --- | --- | --- | --- |
| v1.0.0 | `v1.0.0` | `e9e14cd` | 改版起点：改用磁盘补丁方案，修复新版 Codex 上汉化失效 |
| v1.1.0 | `v1.1.0` | `6479ed0` | 修复"双击没反应"、增强可诊断性、建立版本管理 |
| v1.1.1 | `v1.1.1` | `64d6141` | 修正错误归因；自动检测覆盖各固定盘顶层目录 |
| v1.1.2 | `v1.1.2` | `117b653` | 原地补丁前先备份原 asar |
| v1.1.3 | *（无标签）* | — | 改动合进了 `b33f271`，没有单独提交 |
| v1.1.4 | `v1.1.4` | `b33f271` | 副本过期可见、商店版进程识别、上游地址全改本仓库 |
| v1.1.5 | `v1.1.5` | `47f86d7` | 「已打过补丁」判据收紧 + 版本变化措辞 |
| v1.2.0 | `v1.2.0` | `b625ef9` | 新增运行时注入路线（默认），磁盘补丁降为回退 |
| v1.2.1 | `v1.2.1` | `8ab4bab` | 修界面日志里的字面星号 |
| v1.2.2 | `v1.2.2` | `a926bd0` | 不再创建任何快捷方式 |

> `v1.2.2` 的标签原先错指在 `b5c2738`（那个提交自称 `1.2.1`）。2026-10-13
> 用闸门查出来并重指到 `a926bd0`。

---

## 9. 踩过的坑

**PowerShell 脚本带中文就必须有 UTF-8 BOM。**
没有 BOM，PowerShell 5.1 会按 ANSI（本机 CP936）解码，中文变成乱码甚至解析报错。
`scripts/` 下的 `.ps1` 一律用 `utf-8-sig` 保存。

**`$ErrorActionPreference = 'Stop'` 会把 git 的 stderr 变成终止错误。**
git 往 stderr 写提示是常态（`fatal:` 之类）。调 git 时临时把它降成 `'Continue'`，
或者用一个不产生 stderr 的问法 —— 比如用 `git tag --points-at HEAD` 代替
`git describe --exact-match`（后者在没标签时会写 `fatal:` 然后炸掉脚本）。

**PowerShell 函数不能叫 `Git`。**
函数名不分大小写，函数体内的 `& git` 会解析到函数自己，递归爆栈
（`CallDepthOverflow`）。函数叫 `Invoke-Git`，并且显式写 `git.exe`。

**`git push` 失败先看代理端口。**
本机代理（FlClash 之类）端口**会变**，而 `git config --global http.proxy`
不会跟着变。用一次性覆盖，别改全局配置：

```bash
git -c http.proxy=http://127.0.0.1:17890 -c https.proxy=http://127.0.0.1:17890 push
```

`gh` 不受 `git` 的代理配置影响，所以会出现"`gh` 能用、`git` 推不上去"。

---

## 相关文档

- [`PUBLISHING.md`](PUBLISHING.md) —— 仓库的 About / Topics / Release 操作细节
- [`../CHANGELOG.md`](../CHANGELOG.md) —— 每个版本改了什么（发布说明的来源）
- [`../CONTRIBUTING.md`](../CONTRIBUTING.md) —— 本地检查与提交约定
