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

& (Join-Path $root 'build.ps1') -Configuration Release `
    -Brand 'Codex 汉化增强工具' -OutputName 'CodexZh-Windows-x64.exe'

& (Join-Path $root 'build.ps1') -Configuration Release `
    -Brand 'GPT汉化' -OutputName 'GPTHanhua.exe'

python (Join-Path $root 'scripts\package.py') --handoff $HandoffDir
if ($LASTEXITCODE -ne 0) { throw "package.py 失败，退出码 $LASTEXITCODE" }

Write-Host ''
Write-Host '发布完成。' -ForegroundColor Green
