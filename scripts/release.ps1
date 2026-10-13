<#
.SYNOPSIS
    一条命令发版。

.DESCRIPTION
    把"发版"从一串手打命令收敛成一次调用，并在最前面加一道闸门：

      1. 前置检查   工作区干净 / 在当前分支上 / 该版本的标签还不存在
      2. 版本校验   verify-version.ps1 —— 源码与程序集版本必须一致
      3. 编译打包   publish.ps1（两个品牌 + 打包到 release\ + 同步音乐文件夹）
      4. 打标签     git tag -a vX.Y.Z（annotated，带说明与日期）
      5. 发布       只在给了 -Publish 时：推分支和标签 + gh release create，
                    发布说明直接从 CHANGELOG.md 里那一段抽出来

    默认**只做本地部分**（1~4），并把第 5 步的命令打印出来让你自己敲 ——
    往公开仓库推东西这种事，值得多看一眼再动手。

    用法：
        .\scripts\release.ps1                # 用 src/AppInfo.cs 里的版本号
        .\scripts\release.ps1 -Version 1.2.3
        .\scripts\release.ps1 -DryRun        # 只跑到检查为止，不编译也不打标签
        .\scripts\release.ps1 -Publish       # 一条龙：连推送和 Release 一起

    为什么要有这个脚本：v1.2.2 那次，远端标签指着一个自称 1.2.1 的提交，
    附件却是用 1.2.2 的代码编的 —— 手打的每一步都不会检查这件事。
#>
[CmdletBinding()]
param(
    # 要发布的版本号，不带 v 前缀。不给就取 src/AppInfo.cs 里的 AppInfo.Version。
    [string]$Version,
    # 额外执行"推分支和标签 + 建 GitHub Release"。不加则只做本地部分。
    [switch]$Publish,
    # 跳过编译打包（dist 已经是构建好的产物时用）。
    [switch]$SkipBuild,
    # 发布包目录，默认 ..\release（与 package.py 一致）。
    [string]$OutDir,
    # 只做检查，不编译、不打标签、不发布。
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
if (-not $OutDir) { $OutDir = Join-Path (Split-Path $Root -Parent) 'release' }

$stepNo = 0

function Step([string]$Title) {
    $script:stepNo++
    Write-Host ''
    Write-Host ('[{0}] {1}' -f $script:stepNo, $Title) -ForegroundColor Cyan
}

function Note([string]$Text) { Write-Host ('    ' + $Text) -ForegroundColor DarkGray }
function Good([string]$Text) { Write-Host ('    ' + $Text) -ForegroundColor Green }

function Die([string]$Text) {
    Write-Host ''
    Write-Host ('× ' + $Text) -ForegroundColor Red
    exit 1
}

# 调 git 的统一入口。两个坑都在这里处理：
#   1. ErrorActionPreference='Stop' 会让 git 写到 stderr 的普通提示变成终止错误；
#   2. 函数不能叫 Git —— PowerShell 函数名不分大小写，函数内的 `& git` 会
#      解析到这个函数自己，递归爆栈（CallDepthOverflow）。所以写 git.exe。
function Invoke-Git {
    param([string[]]$Arguments)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git.exe -C $Root @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prev }
    return [pscustomobject]@{ Code = $code; Lines = @($out) }
}

function Read-Utf8([string]$Path) {
    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}

# ------------------------------------------------------------------ 版本号
$appInfoPath = Join-Path $Root 'src\AppInfo.cs'
$appInfoText = Read-Utf8 $appInfoPath
$m = [regex]::Match($appInfoText, 'const\s+string\s+Version\s*=\s*"([^"]+)"')
if (-not $m.Success) { Die '读不到 src\AppInfo.cs 里的 AppInfo.Version。' }
$srcVersion = $m.Groups[1].Value

if (-not $Version) { $Version = $srcVersion }

if ($Version -ne $srcVersion) {
    Die ('要发布的版本 ' + $Version + ' 与源码里的 ' + $srcVersion + ' 不一致。' + "`n" +
         "    要么改 src/AppInfo.cs（连带 src/Program.cs 两行程序集版本），" + "`n" +
         '    要么把 -Version 去掉、直接用源码里的版本号。')
}

$tagName = 'v' + $Version

$slugMatch = [regex]::Match($appInfoText, 'RepositorySlug\s*=\s*"([^"]+)"')
$slug = if ($slugMatch.Success) { $slugMatch.Groups[1].Value } else { '' }

Write-Host ''
Write-Host ('发布 ' + $tagName) -ForegroundColor White
if ($DryRun) { Write-Host '（-DryRun：只检查，不编译也不打标签）' -ForegroundColor Yellow }

# ------------------------------------------------------------ 1. 前置检查
Step '前置检查'

$inside = Invoke-Git @('rev-parse', '--is-inside-work-tree')
if ($inside.Code -ne 0) { Die '这里不是 git 仓库。' }
Good '在 git 仓库内'

$dirty = Invoke-Git @('status', '--porcelain')
if ($dirty.Lines.Count -gt 0 -and $dirty.Lines[0]) {
    Write-Host '    工作区有未提交的改动，标签会指向不含这些改动的提交：' -ForegroundColor Red
    foreach ($line in $dirty.Lines) { if ($line) { Write-Host ('      ' + $line) -ForegroundColor Red } }
    Die '先提交（或 stash）再发版。'
}
Good '工作区干净'

$branch = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')).Lines -join ''
if ($branch -eq 'HEAD') { Die '当前处于游离 HEAD，先切回分支。' }
Good ('当前分支 ' + $branch)

$existing = Invoke-Git @('rev-parse', '--verify', '--quiet', ('refs/tags/' + $tagName))
if ($existing.Code -eq 0) {
    $where = (Invoke-Git @('rev-parse', '--short', ($tagName + '^{commit}'))).Lines -join ''
    Die ('标签 ' + $tagName + ' 已经存在（指向 ' + $where + '）。' + "`n" +
         '    要重发就先把旧的删掉：' + "`n" +
         ('      git tag -d ' + $tagName + '    # 本地') + "`n" +
         ('      git push origin :refs/tags/' + $tagName + '    # 远端（慎用）'))
}
Good ('标签 ' + $tagName + ' 尚未使用')

# ------------------------------------------------------------ 2. 版本校验
Step '版本一致性校验'
& (Join-Path $PSScriptRoot 'verify-version.ps1') -Tag $tagName
if ($LASTEXITCODE -ne 0) { Die '版本号对不上，停下来。' }
Good '版本号一致'

# ------------------------------------------------ 从 CHANGELOG 抽发布说明
$changelogPath = Join-Path $Root 'CHANGELOG.md'
$notes = $null
$releaseTitle = $null
if (Test-Path -LiteralPath $changelogPath) {
    $changelog = Read-Utf8 $changelogPath
    $pattern = '(?ms)^##[ \t]+(v' + [regex]::Escape($Version) + '[^\r\n]*)\r?\n(.*?)(?=^##[ \t]+v|\Z)'
    $sec = [regex]::Match($changelog, $pattern)
    if ($sec.Success) {
        $body = $sec.Groups[2].Value.Trim()
        $notes = '# ' + $sec.Groups[1].Value.Trim() + "`n`n" + $body
        $firstHead = [regex]::Match($body, '^###[ \t]+(.+)$', 'Multiline')
        if ($firstHead.Success) { $releaseTitle = $firstHead.Groups[1].Value.Trim() }
    }
}
if (-not $notes) {
    Die ('CHANGELOG.md 里没有 ## v' + $Version + ' 这一段。' + "`n" +
         '    发布说明是从那里抽的，先补上再发。')
}

Step '发布说明（取自 CHANGELOG.md）'
if ($releaseTitle) { Good $releaseTitle }
Note ($notes.Length.ToString() + ' 个字符')

if ($DryRun) {
    Write-Host ''
    Write-Host '检查全部通过（-DryRun 到此为止）。' -ForegroundColor Yellow
    exit 0
}

# ------------------------------------------------------------ 3. 编译打包
Step '编译打包'
if ($SkipBuild) {
    Note '跳过（-SkipBuild）'
}
else {
    & (Join-Path $PSScriptRoot 'publish.ps1')
    if ($LASTEXITCODE -ne 0) { Die ('publish.ps1 失败，退出码 ' + $LASTEXITCODE) }
}
Good '产物已在 dist\ 与 ' + $OutDir

# --------------------------------------------------------------- 4. 打标签
Step ('打标签 ' + $tagName)
$tagArgs = @('tag', '-a', $tagName, '-m', $tagName)
if ($releaseTitle) { $tagArgs += @('-m', $releaseTitle) }
$tagResult = Invoke-Git $tagArgs
if ($tagResult.Code -ne 0) {
    foreach ($l in $tagResult.Lines) { Write-Host ('    ' + $l) -ForegroundColor Red }
    Die '打标签失败。'
}
Good ('已创建 annotated 标签 ' + $tagName)

# 打完再自查一遍：确认标签指向的提交自称的就是这个版本。
& (Join-Path $PSScriptRoot 'verify-version.ps1') -Tag $tagName -Quiet
if ($LASTEXITCODE -ne 0) {
    Invoke-Git @('tag', '-d', $tagName) | Out-Null
    Die '标签指向的提交版本对不上，已回滚该标签。'
}
Good '标签指向的提交自称同一版本'

# ------------------------------------------------------------- 5. 发布
$assets = @()
if (Test-Path -LiteralPath $OutDir) {
    $assets = @(Get-ChildItem -LiteralPath $OutDir -Filter ('*-v' + $Version + '-*.zip') -ErrorAction SilentlyContinue)
}

Write-Host ''
if (-not $Publish) {
    Write-Host '本地部分做完了。要发到 GitHub，就执行下面这三条（或加 -Publish 重跑）：' -ForegroundColor Yellow
    Write-Host ('  git -C "' + $Root + '" push origin ' + $branch)
    Write-Host ('  git -C "' + $Root + '" push origin ' + $tagName)
    $assetArg = if ($assets.Count -gt 0) { ($assets | ForEach-Object { '"' + $_.FullName + '"' }) -join ' ' } else { '<release 里的 zip>' }
    Write-Host ('  gh release create ' + $tagName + ' ' + $assetArg + ' --title "' + $tagName + '" --notes-file <发布说明>')
    exit 0
}

Step '推送到 GitHub'
$pushBranch = Invoke-Git @('push', 'origin', $branch)
foreach ($l in $pushBranch.Lines) { Write-Host ('    ' + $l) -ForegroundColor DarkGray }
if ($pushBranch.Code -ne 0) { Die '推送分支失败。（代理端口变了？见 docs/VERSIONING.md 末尾）' }
Good ('已推送 ' + $branch)

$pushTag = Invoke-Git @('push', 'origin', $tagName)
foreach ($l in $pushTag.Lines) { Write-Host ('    ' + $l) -ForegroundColor DarkGray }
if ($pushTag.Code -ne 0) { Die '推送标签失败。' }
Good ('已推送 ' + $tagName)

Step '创建 GitHub Release'
$notesFile = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($notesFile, $notes, (New-Object System.Text.UTF8Encoding($false)))
    $ghArgs = @('release', 'create', $tagName, '--title', ($tagName + $(if ($releaseTitle) { ' —— ' + $releaseTitle } else { '' })), '--notes-file', $notesFile)
    if ($slug) { $ghArgs += @('--repo', $slug) }
    foreach ($a in $assets) { $ghArgs += $a.FullName }

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $ghOut = & gh @ghArgs 2>&1
        $ghCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prev }

    foreach ($l in @($ghOut)) { Write-Host ('    ' + $l) -ForegroundColor DarkGray }
    if ($ghCode -ne 0) { Die 'gh release create 失败。（gh 没登录？代理？）' }
}
finally { Remove-Item -LiteralPath $notesFile -ErrorAction SilentlyContinue }

Good ('Release ' + $tagName + ' 已创建，附件 ' + $assets.Count + ' 个')

Write-Host ''
Write-Host ('发布完成：' + $tagName) -ForegroundColor Green
if ($slug) { Write-Host ('  https://github.com/' + $slug + '/releases/tag/' + $tagName) -ForegroundColor DarkGray }
