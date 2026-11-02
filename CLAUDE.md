# 项目规则

## 启动路线：检测到本地化副本时，必须优先走副本

这是用户明确定下的规则，改 `src/LaunchCoordinator.cs` 前先读完这一段。

**固定顺序：**

1. 有 Codex 在跑 → 先关掉（否则新实例被 Electron 单实例锁顶掉）
2. **`%USERPROFILE%\.codex\codex-zh\app` 里已有本地化副本 → 直接走磁盘补丁 / 启动副本，
   不尝试运行时注入**
3. 没有副本 → 才尝试注入（只有全新安装会到这里）
4. 注入失败 → 回退创建副本

**为什么：** 注入实测要 ~43 秒（等 App 自己重取一次动态配置），副本启动只要 ~2 秒；
商店版在客户机上还经常因 `WindowsApps` 权限或单实例锁直接失败。副本落后于新版时，
磁盘补丁路线会自动重拷，所以优先副本不会让用户停留在旧版本。

**代码位置：** `src/LaunchCoordinator.cs` 里的 `launch.route.prefer-copy` 分支。

**回归判据（改坏了会这样）：** 日志里出现 `inject.begin`，却没有
`launch.route.prefer-copy` / `launch.mode=patch prefer-copy`。

详细说明见 `README.md` 的「路线选择：有本地化副本时优先副本」。
