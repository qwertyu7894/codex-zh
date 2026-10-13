<#
.SYNOPSIS
    发布前的版本一致性闸门。

.DESCRIPTION
    版本号散落在四处，一旦不同步就会出"包名是 vX、内容是 vY"，
    或者更糟 —— GitHub 上的标签指着一个自称别的版本的提交。

    v1.2.2 就是这么翻的车：远端 v1.2.2 标签指向 b5c2738，
    而那个提交里的 AppInfo.Version 还是 "1.2.1"；release 附件却是用
    1.2.2 的代码编出来的。两边对不上，当时没有任何东西拦得住。

    本脚本把四处对一遍，任何一处对不上就退出码 1：

      1. src/AppInfo.cs        AppInfo.Version        <- 唯一来源
      2. src/Program.cs        两行程序集版本（应为 <版本>.0）
      3. dist/buildinfo.json   构建戳（文件存在时才查）
      4. git tag               剥掉 v 前缀后必须等于版本号

    用法：
        .\scripts\verify-version.ps1               # 查 1~3，外加 HEAD 上恰好有的 tag
        .\scripts\verify-version.ps1 -Tag v1.2.3   # 额外要求该 tag 与版本号一致
        .\scripts\verify-version.ps1 -Quiet        # 不打印，只看退出码

    退出码：0 = 一致；1 = 有对不上的地方。
#>
[CmdletBinding()]
param(
    # 要校验的标签，例如 v1.2.3。不给就只看 HEAD 上恰好指着的那个标签。
    [string]$Tag,
    # 仓库根目录，默认取本脚本的上一级。
    [string]$Root,
    # 跳过分发构建戳的检查。编译**前**用它：那时 dist 里还是上一版的戳。
    [switch]$SkipStamp,
    # 只回退出码，不打印。
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path $PSScriptRoot -Parent }

$rows     = @()
$problems = @()

# 结果只有三态：OK / 不一致 / 跳过。跳过不等于通过，所以单独标出来。
function Add-Row {
    param([string]$Where, [string]$Value, [string]$Status, [string]$Note)

    $script:rows += [pscustomobject]@{
        Where  = $Where
        Value  = $Value
        Status = $Status
        Note   = $Note
    }
    if ($Status -eq '不一致') {
        $script:problems += ('{0} — {1}' -f $Where, $Note)
    }
}

function Read-Utf8([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}

function Get-Match([string]$Text, [string]$Pattern) {
    if ([string]::IsNullOrEmpty($Text)) { return $null }
    $m = [regex]::Match($Text, $Pattern)
    if (-not $m.Success) { return $null }
    return $m.Groups[1].Value
}

# 读某个提交（或标签）里的文件内容。取不到返回 $null。
# 注意 git 会往 stderr 写 fatal，所以临时把 ErrorActionPreference 降下来，
# 否则在 'Stop' 下它会变成终止错误 —— 本脚本最初就是这么被绊倒的。
function Read-AtCommit([string]$Rev, [string]$Path) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = & git -C $Root show ($Rev + ':' + $Path) 2>$null
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prev }
    if ($code -ne 0) { return $null }
    return ($text -join "`n")
}

# ---------------------------------------------------------------- 1. 唯一来源
$version = Get-Match (Read-Utf8 (Join-Path $Root 'src\AppInfo.cs')) `
                     'const\s+string\s+Version\s*=\s*"([^"]+)"'
if (-not $version) {
    Write-Host '找不到 src\AppInfo.cs 里的 AppInfo.Version —— 版本号唯一来源失效了。' -ForegroundColor Red
    exit 1
}
Add-Row 'src\AppInfo.cs' $version 'OK' '版本号唯一来源'

# ------------------------------------------------------- 2. 程序集版本（两行）
$programText = Read-Utf8 (Join-Path $Root 'src\Program.cs')
$expect      = $version + '.0'   # "1.2.3" -> "1.2.3.0"
foreach ($attr in @('AssemblyVersion', 'AssemblyFileVersion')) {
    $got = Get-Match $programText ($attr + '\("([^"]+)"\)')
    if (-not $got) {
        Add-Row ('src\Program.cs ' + $attr) '(缺失)' '不一致' ('没找到 ' + $attr + ' 这一行')
    }
    elseif ($got -ne $expect) {
        Add-Row ('src\Program.cs ' + $attr) $got '不一致' ('应为 ' + $expect + '（版本号 + ".0"）')
    }
    else {
        Add-Row ('src\Program.cs ' + $attr) $got 'OK' ''
    }
}

# ------------------------------------------------------------ 3. dist 构建戳
$stampPath = Join-Path $Root 'dist\buildinfo.json'
if ($SkipStamp) {
    Add-Row 'dist\buildinfo.json' '(本次跳过)' '跳过' '编译前检查 —— 构建戳要等编译完才更新'
}
elseif (Test-Path -LiteralPath $stampPath) {
    $stamp = Get-Content -LiteralPath $stampPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($stamp.version -eq $version) {
        Add-Row 'dist\buildinfo.json' $stamp.version 'OK' ('构建于 ' + $stamp.builtAt)
    }
    else {
        Add-Row 'dist\buildinfo.json' $stamp.version '不一致' `
            ('dist 里是上一次的旧产物，先重新构建（期望 ' + $version + '）')
    }
}
else {
    Add-Row 'dist\buildinfo.json' '(不存在)' '跳过' '还没构建过，发布前要跑 build.ps1'
}

# --------------------------------------------------------------- 4. git tag
$tagToCheck = $Tag
$tagFromHead = $false
if (-not $tagToCheck) {
    # 没指定就看看 HEAD 上是不是恰好有个 v* 标签 —— 有就顺手查掉，
    # 这正是"打完 tag 又往上堆提交"最容易出事的地方。
    #
    # 用 tag --points-at 而不是 describe --exact-match：没有标签时前者
    # 静默返回空，后者会往 stderr 写 fatal，在 ErrorActionPreference='Stop'
    # 下会变成终止错误（本脚本最初就是这么被绊倒的）。
    $headTags = @(& git -C $Root tag --points-at HEAD 2>$null | Where-Object { $_ -match '^v\d+\.\d+\.\d+' })
    if ($headTags.Count -gt 0) {
        $tagToCheck = $headTags[0].Trim()
        $tagFromHead = $true
    }
}

if ($tagToCheck) {
    $tagName    = $tagToCheck.Trim()
    $tagVersion = $tagName -replace '^v', ''
    $why        = if ($tagFromHead) { 'HEAD 上恰好有这个标签' } else { '-Tag 指定' }

    # 标签已经存在时，真正要查的是**它指向的那个提交自称什么版本** ——
    # v1.2.2 的坑正在这里：名字叫 v1.2.2，指向的提交却写着 1.2.1。
    # 只比名字的话这种错根本抓不到。
    $atTag = Read-AtCommit ($tagName + '^{commit}') 'src/AppInfo.cs'
    $atTagVersion = Get-Match $atTag 'const\s+string\s+Version\s*=\s*"([^"]+)"'

    if ($atTagVersion) {
        # 标签已存在：查它指向的提交自称什么版本。这样**旧标签也能审**
        # （不要求它等于当前工作区的版本）。
        if ($atTagVersion -eq $tagVersion) {
            $commit = (& git -C $Root rev-parse --short ($tagName + '^{commit}') 2>$null)
            Add-Row ('git tag ' + $tagName) $atTagVersion 'OK' ($why + '；指向 ' + $commit)
        }
        else {
            Add-Row ('git tag ' + $tagName) $atTagVersion '不一致' `
                ('标签名是 ' + $tagVersion + '，但它指向的提交自称 ' + $atTagVersion +
                 ' —— 附件与标签对不上，得把标签指到正确的提交上')
        }
    }
    elseif ($tagVersion -eq $version) {
        # 标签还没建：这是发版前的预检，比的是"名字 vs 当前源码"。
        Add-Row ('git tag ' + $tagName) $tagVersion 'OK' ($why + '；标签尚未创建，比的是当前源码')
    }
    else {
        Add-Row ('git tag ' + $tagName) $tagVersion '不一致' `
            ('标签名与当前源码版本 ' + $version + ' 不符')
    }
}
else {
    Add-Row 'git tag' '(未检查)' '跳过' 'HEAD 上没有 v* 标签，也没给 -Tag'
}

# ------------------------------------------------------------------- 输出
if (-not $Quiet) {
    Write-Host ''
    Write-Host '版本一致性检查' -ForegroundColor Cyan
    Write-Host ('-' * 62)
    foreach ($r in $rows) {
        $color = 'Gray'
        if ($r.Status -eq 'OK')     { $color = 'Green' }
        if ($r.Status -eq '不一致') { $color = 'Red' }
        if ($r.Status -eq '跳过')   { $color = 'DarkGray' }
        Write-Host ('  {0,-28} {1,-14} ' -f $r.Where, $r.Value) -NoNewline
        Write-Host $r.Status -ForegroundColor $color -NoNewline
        if ($r.Note) { Write-Host ('  ' + $r.Note) -ForegroundColor DarkGray } else { Write-Host '' }
    }
    Write-Host ('-' * 62)
}

if ($problems.Count -gt 0) {
    if (-not $Quiet) {
        Write-Host ''
        Write-Host '版本号对不上，别往下走：' -ForegroundColor Red
        foreach ($p in $problems) { Write-Host ('  · ' + $p) -ForegroundColor Red }
        Write-Host ''
        Write-Host '修法：改 src/AppInfo.cs 的 AppInfo.Version，同步 src/Program.cs 的两行程序集版本，' -ForegroundColor DarkGray
        Write-Host '      重新 build.ps1，再打标签。细节见 docs/VERSIONING.md。' -ForegroundColor DarkGray
    }
    exit 1
}

if (-not $Quiet) {
    Write-Host ''
    Write-Host ('版本号一致：' + $version) -ForegroundColor Green
}
exit 0
