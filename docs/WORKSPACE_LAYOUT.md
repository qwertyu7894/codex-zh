# 项目目录布局

## 当前源码

- `src/`：Windows C# 产品源码；构建脚本只从这里编译顶层 `.cs`。
- `shared/`：运行时注入所需的共享脚本资源。
- `macos/`：macOS 端源码和构建配置。
- `localize/`：历史 PowerShell 参考实现；默认产品链路不释放、不执行其中的脚本。

## 测试与验证

- `tests/`：C# harness、旧实现 oracle 和测试夹具逻辑。
- `test-localization.ps1`：asar、完整性哈希、安装和 localeOverride 门禁。
- `test-shutdown.ps1`：进程关闭相关测试。

## 文档与发布

- `README.md`：用户和维护者说明。
- `CLAUDE.md`：项目开发规则；修改启动流程前必须遵守“有本地化副本时优先走副本”。
  该文件与其它 Agent 规则/交接文档只在本地保留，不纳入版本控制。
- `CHANGELOG.md`：当前分支更新记录。
- `docs/PUBLISHING.md`、`docs/VERSIONING.md`：发布流程和版本规则。
- `scripts/`：构建辅助、打包和发布脚本。

## 生成目录（不提交）

- `dist/`：本地构建出的 exe。
- `obj/`：编译中间文件和测试 harness。

这些目录可以随时由构建脚本重建；不要把其中的本地日志、客户路径或临时夹具复制回源码。

## 发布边界

- 项目发布包由 `scripts/release.ps1` 生成到仓库上一级的 `release/` 目录。
- 客户交付品牌的 exe 由构建脚本同步到用户的 Music 目录；它不是源码，也不纳入项目仓库。
- 发布前检查文档、注释、日志样例和压缩包内容，不得包含真实用户路径、客户用户名、API 密钥、令牌或本地日志。
