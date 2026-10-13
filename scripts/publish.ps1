# 一次跑完发布：编译两个品牌 -> 打包 -> 把交付用的 exe 同步到音乐文件夹。
#
# 为什么最后一步不能省：客户端拿到的是用户**直接从音乐文件夹**发出去的单个 exe，
# 不是 release\ 里的压缩包。少了同步就会出现"源码已是新版、发出去的还是旧版"。
#
# 用法：
#   .\scripts\publish.ps1                      # 同步到 %USERPROFILE%\Music
#   .\scripts\publish.ps1 -HandoffDir D:\x     # 或指定别的交付目录
[CmdletBinding()]
param(
    [string]$HandoffDir = (Join-Path $env:USERPROFILE 'Music')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# 编译前先过版本一致性闸门（版本号唯一来源 vs Program.cs 的两行程序集版本）。
# 不一致就别编译 —— 免得打出一个"名字是 vX、内容是 vY"的包。
# 构建戳那一项要等编译完才有意义，所以这里跳过；scripts/package.py 会在
# 打包前再对一次账，两次都不放水。
& (Join-Path $PSScriptRoot 'verify-version.ps1') -SkipStamp
if ($LASTEXITCODE -ne 0) { throw '版本号对不上，已中止发布。' }

& (Join-Path $root 'build.ps1') -Configuration Release `
    -Brand 'Codex 汉化增强工具' -OutputName 'CodexZh-Windows-x64.exe'

& (Join-Path $root 'build.ps1') -Configuration Release `
    -Brand 'GPT汉化' -OutputName 'GPTHanhua.exe'

python (Join-Path $root 'scripts\package.py') --handoff $HandoffDir
if ($LASTEXITCODE -ne 0) { throw "package.py 失败，退出码 $LASTEXITCODE" }

Write-Host ''
Write-Host '发布完成。' -ForegroundColor Green
