#Requires -Version 5.1
<#
.SYNOPSIS
  Codex 桌面版本地化（自研，零依赖）

.DESCRIPTION
  做两件事，仅此两件：

    1) 让界面变中文 —— 把 app.asar 内 `enable_i18n`,!1 这个**默认值**
       原地改成 `!0`（等长 1 字节）。改的是默认值，不是运行时状态，
       所以一次生效、永久有效，不需要注入、不需要每次重启重打。

    2) 让菜单变中文 —— 保证 %USERPROFILE%\.codex\config.toml 里
       [desktop] localeOverride = "zh-CN" 存在。
       MSIX 包只声明 en-US 资源语言，会把 app.getLocale() 顶成 en-US，
       导致原生菜单永远英文；脱离 MSIX 运行（复制副本）后此限制消失。

  为什么要复制副本：Microsoft Store 版装在
  C:\Program Files\WindowsApps\ 下，受 ACL 保护且包签名校验，
  无法原地改写。复制到用户可写目录后打补丁，绝不动原件。

  明确不做的事：不联网、不下载、不写注册表、不装服务/计划任务、
  不读凭据、不修改 WindowsApps 里的任何文件。

.PARAMETER Target
  直接指定一个 app 目录（含 resources\app.asar 与 ChatGPT.exe）。
  用于对已有的本地化副本打补丁，或对官网可写版直接打补丁。

.PARAMETER DryRun
  只检测并报告会改什么，不写入。

.EXAMPLE
  .\CodexZh.ps1 -Status
  .\CodexZh.ps1 -Install -DryRun
  .\CodexZh.ps1 -Install
  .\CodexZh.ps1 -Launch
#>
[CmdletBinding()]
param(
    [switch]$Status,
    [switch]$Install,
    [switch]$Launch,
    [switch]$DryRun,
    [string]$Language = 'zh-CN',
    [string]$Target,
    [string]$Root,
    [switch]$Quiet          # GUI 调用时用：抑制"用法"帮助块，只留状态与结果
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot

. (Join-Path $scriptDir 'AsarInPlace.ps1')
. (Join-Path $scriptDir 'Rules.ps1')

function Write-Info { param([string]$m) Write-Host "[信息] $m" -ForegroundColor Cyan }
function Write-Ok   { param([string]$m) Write-Host "[成功] $m" -ForegroundColor Green }
function Write-Warn { param([string]$m) Write-Host "[警告] $m" -ForegroundColor Yellow }
function Write-Err  { param([string]$m) Write-Host "[错误] $m" -ForegroundColor Red }

$userProfile = [Environment]::GetFolderPath('UserProfile')
if ([string]::IsNullOrWhiteSpace($userProfile)) { $userProfile = $env:USERPROFILE }

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Join-Path $userProfile '.codex\codex-zh'
}
$Root         = [System.IO.Path]::GetFullPath($Root)
$configJson   = Join-Path $Root 'state.json'
$logFile      = Join-Path $Root 'codex-zh.log'
$codexToml    = Join-Path $userProfile '.codex\config.toml'

function Write-Log {
    param([string]$m)
    try {
        $dir = Split-Path $logFile -Parent
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Add-Content -LiteralPath $logFile -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m) -Encoding UTF8
    } catch {}
}

function Read-StateFile {
    param([string]$Path)
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return $null }
        return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
    } catch { return $null }
}

function Get-RulesSignature {
    <#
      规则表的"指纹"。改了任何一条规则的挑串/替换串/预期命中数，签名就变 ——
      那样就不能再信任上次的打补丁结果，必须重新全量扫描。
      只用语义字段，不含 Desc，免得改个说明文字就白白重扫一遍。
    #>
    param($Rules)
    $parts = @()
    foreach ($r in $Rules) {
        $parts += ('{0}:{1}>{2}:{3}' -f $r.Id, $r.From, $r.To, $r.Expect)
    }
    return ($parts -join '|')
}

# ---------------------------------------------------------------- 安装探测

function Find-CodexInstall {
    param([string]$Override)

    if ($Override) {
        foreach ($p in @($Override, (Join-Path $Override 'app'))) {
            if ((Test-Path -LiteralPath (Join-Path $p 'resources\app.asar')) -and
                ((Test-Path -LiteralPath (Join-Path $p 'ChatGPT.exe')) -or (Test-Path -LiteralPath (Join-Path $p 'Codex.exe')))) {
                $full = (Get-Item -LiteralPath $p).FullName
                $kind = if ($full -like '*\WindowsApps\*') { 'store' } else { 'writable' }
                return @{ AppDir = $full; Kind = $kind }
            }
        }
        return $null
    }

    # Microsoft Store / MSIX
    try {
        $pkg = Get-AppxPackage -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -match 'OpenAI.*Codex|^Codex$|^ChatGPT$' } |
               Select-Object -First 1
        if ($pkg -and $pkg.InstallLocation) {
            foreach ($p in @((Join-Path $pkg.InstallLocation 'app'), $pkg.InstallLocation)) {
                if ((Test-Path -LiteralPath (Join-Path $p 'resources\app.asar')) -and (Test-Path -LiteralPath (Join-Path $p 'ChatGPT.exe'))) {
                    return @{ AppDir = (Get-Item -LiteralPath $p).FullName; Kind = 'store'; Package = $pkg }
                }
            }
        }
    } catch {}

    # 官网安装版（注册表）
    try {
        $roots = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                   'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
                   'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')
        foreach ($r in $roots) {
            Get-ItemProperty $r -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -match 'Codex|ChatGPT' -and $_.InstallLocation } |
                ForEach-Object {
                    foreach ($p in @($_.InstallLocation, (Join-Path $_.InstallLocation 'app'))) {
                        if ((Test-Path -LiteralPath (Join-Path $p 'resources\app.asar')) -and (Test-Path -LiteralPath (Join-Path $p 'ChatGPT.exe'))) {
                            return @{ AppDir = (Get-Item -LiteralPath $p).FullName; Kind = 'writable' }
                        }
                    }
                }
        }
    } catch {}

    return $null
}

function Get-CodexVersion {
    param([string]$AppDir)
    $leaf = Split-Path (Split-Path $AppDir -Parent) -Leaf
    if ($leaf -match 'OpenAI\.Codex_([0-9][0-9.]*)') { return $Matches[1] }
    $ini = Join-Path $AppDir 'resources\owl-app.ini'
    if (Test-Path -LiteralPath $ini) {
        $line = Select-String -LiteralPath $ini -Pattern '^\s*AppVersion\s*=\s*(.+)$' -ErrorAction SilentlyContinue
        if ($line) { return $line.Matches[0].Groups[1].Value.Trim() }
    }
    return 'unknown'
}

function Test-DirWritable {
    param([string]$Dir)
    try {
        $probe = Join-Path $Dir ('.wtest-' + [guid]::NewGuid().ToString('N'))
        [System.IO.File]::WriteAllText($probe, 'x')
        Remove-Item -LiteralPath $probe -Force
        return $true
    } catch { return $false }
}

function Copy-TreeBytes {
    <# 逐字节递归复制。不用 Copy-Item：WindowsApps 里有重解析点/EFS，字节流最稳。 #>
    param([string]$Src, [string]$Dst)
    if (-not (Test-Path -LiteralPath $Dst)) { New-Item -ItemType Directory -Path $Dst -Force | Out-Null }
    foreach ($dir in [System.IO.Directory]::EnumerateDirectories($Src)) {
        Copy-TreeBytes -Src $dir -Dst (Join-Path $Dst ([System.IO.Path]::GetFileName($dir)))
    }
    foreach ($file in [System.IO.Directory]::EnumerateFiles($Src)) {
        $target = Join-Path $Dst ([System.IO.Path]::GetFileName($file))
        $in = [System.IO.File]::OpenRead($file)
        try {
            $out = [System.IO.File]::Create($target)
            try {
                $buf = New-Object byte[] (8MB)
                while (($r = $in.Read($buf, 0, $buf.Length)) -gt 0) { $out.Write($buf, 0, $r) }
            } finally { $out.Dispose() }
        } finally { $in.Dispose() }
    }
}

# ---------------------------------------------------------------- 配置写入

function Set-LocaleOverride {
    <#
      确保 config.toml 的 [desktop] 段里有 localeOverride。
      这是唯一一处"长度可以变化"的改动 —— 因为改的是配置文件，不是 asar。
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Locale,
        [switch]$Apply
    )

    $line = 'localeOverride = "' + $Locale + '"'
    $lines = @()
    if (Test-Path -LiteralPath $Path) {
        $lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
    }

    $sectionStart = -1
    $sectionEnd   = $lines.Count
    $existing     = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\[desktop\]\s*$') {
            $sectionStart = $i
            for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                if ($lines[$j] -match '^\s*\[') { $sectionEnd = $j; break }
            }
            break
        }
    }
    if ($sectionStart -ge 0) {
        for ($i = $sectionStart + 1; $i -lt $sectionEnd; $i++) {
            if ($lines[$i] -match '^\s*localeOverride\s*=') { $existing = $i; break }
        }
    }

    if ($existing -ge 0) {
        if ($lines[$existing] -eq $line) { return @{ Changed = $false; Reason = 'already-set' } }
        if ($Apply) {
            $lines[$existing] = $line
            [System.IO.File]::WriteAllLines($Path, $lines, (New-Object System.Text.UTF8Encoding($false)))
        }
        return @{ Changed = $true; Reason = 'updated' }
    }

    if ($sectionStart -ge 0) {
        if ($Apply) {
            $out = @()
            $out += $lines[0..$sectionStart]
            $out += $line
            if ($sectionStart + 1 -le $lines.Count - 1) { $out += $lines[($sectionStart + 1)..($lines.Count - 1)] }
            [System.IO.File]::WriteAllLines($Path, $out, (New-Object System.Text.UTF8Encoding($false)))
        }
        return @{ Changed = $true; Reason = 'inserted-into-desktop' }
    }

    if ($Apply) {
        $out = @($lines) + @('', '[desktop]', $line)
        [System.IO.File]::WriteAllLines($Path, $out, (New-Object System.Text.UTF8Encoding($false)))
    }
    return @{ Changed = $true; Reason = 'appended-desktop-section' }
}

# ---------------------------------------------------------------- 打补丁
# 注：本脚本此前还会创建「ChatGPT 中文版」桌面/开始菜单快捷方式。
# 从 v1.2.2 起**不再创建任何快捷方式** —— 用户直接启动本工具即可。
# 旧的快捷方式指向的是本地化副本，双击它会启动一份**没有经过注入**的 Codex
# （界面仍是英文），留着只会让人以为"汉化没生效"，所以一并去掉了。

function Invoke-PatchPass {
    param(
        [Parameter(Mandatory)][string]$AsarPath,
        [switch]$Apply
    )

    $info  = Read-AsarHeaderInfo -AsarPath $AsarPath
    $rules = Get-CodexPatchRules
    $results = New-Object System.Collections.ArrayList
    $criticalMiss = 0

    Write-Info ("asar 头部 {0:N0} 字节，数据区起点 {1:N0}" -f $info.JsonLen, $info.DataOffset)

    foreach ($rule in $rules) {
        $entries = Get-AsarEntriesInScope -HeaderInfo $info -Scope $rule.Scope -Ext $rule.Ext
        Write-Info ("规则 [{0}] 在 {1} 下找到 {2} 个候选文件" -f $rule.Id, $rule.Scope, $entries.Count)

        $hitFiles = New-Object System.Collections.ArrayList
        foreach ($e in $entries) {
            if (Test-AsarEntryContains -AsarPath $AsarPath -DataOffset $info.DataOffset `
                                       -Offset $e.Offset -Size $e.Size -Needle $rule.From) {
                [void]$hitFiles.Add($e)
            }
        }

        if ($hitFiles.Count -eq 0) {
            # 源串不存在，可能是"已经打过补丁"（重复运行 -Install 的正常情形），
            # 而不是"App 结构变了"。两者必须区分，否则会误报关键补丁失败。
            #
            # ⚠️ 光在 scope 里找到 To 是不够的：有的文件**天然**就含 To
            #    （实测 26.915 / 26.1007 里 general-settings-*.js 都有
            #     `enable_i18n`,!0 —— 那是设置页自己的读取默认值）。
            #    拿它当"已打过补丁"，就会把"结构变了"静默判成"已打好"：
            #    界面还是英文，工具却报成功。
            #    所以要求 To 与规则声明的 AlreadyPatchedContext 出现在**同一个文件**里
            #    —— 那才是我们改过的那个动态配置默认值块。
            $alreadyPatched = $false
            $alreadyEntry   = $null
            foreach ($e in $entries) {
                if (-not (Test-AsarEntryContains -AsarPath $AsarPath -DataOffset $info.DataOffset `
                                                 -Offset $e.Offset -Size $e.Size -Needle $rule.To)) {
                    continue
                }
                if ($rule.ContainsKey('AlreadyPatchedContext')) {
                    $ctx = $rule.AlreadyPatchedContext
                    if (-not (Test-AsarEntryContains -AsarPath $AsarPath -DataOffset $info.DataOffset `
                                                     -Offset $e.Offset -Size $e.Size -Needle $ctx)) {
                        continue
                    }
                }
                $alreadyPatched = $true
                $alreadyEntry   = $e
                break
            }
            if ($alreadyPatched) {
                Write-Info ("    已打过补丁，跳过：{0}" -f $rule.Desc)
                # 把"凭什么判它已打过"的那一条也记下来：下次就能只复查这一个条目。
                $alrRecords = New-Object System.Collections.ArrayList
                if ($alreadyEntry) {
                    [void]$alrRecords.Add(@{
                        id      = $rule.Id
                        path    = $alreadyEntry.Path
                        offset  = [long]$alreadyEntry.Offset
                        size    = [long]$alreadyEntry.Size
                        needle  = $rule.To
                        context = $(if ($rule.ContainsKey('AlreadyPatchedContext')) { $rule.AlreadyPatchedContext } else { '' })
                    })
                }
                [void]$results.Add(@{ Id = $rule.Id; Desc = $rule.Desc; Critical = $rule.Critical; Status = 'already-patched'; Files = @(); Entries = @($alrRecords) })
            } else {
                Write-Warn ("    未命中：{0}" -f $rule.Desc)
                if ($rule.Critical) { $criticalMiss++ }
                [void]$results.Add(@{ Id = $rule.Id; Desc = $rule.Desc; Critical = $rule.Critical; Status = 'miss'; Files = @(); Entries = @() })
            }
            continue
        }

        $totalHits = 0
        $done = New-Object System.Collections.ArrayList
        # 结构化地记下"改的是哪个文件、在数据区的哪个偏移"，写进 state.json，
        # 供下次运行时免去全量扫描（见 Test-PatchRecordStillValid）。
        # 只有路径不够用：定位条目要解析整个 5.8 MB 头部（实测 19 秒），
        # 记下 offset/size 之后，复查就退化成"seek + 读那几百 KB"。
        $entryRecords = New-Object System.Collections.ArrayList
        foreach ($e in $hitFiles) {
            $r = Invoke-AsarInPlacePatch -AsarPath $AsarPath -RelPath $e.Path `
                                         -Replacements @(@{ From = $rule.From; To = $rule.To; Desc = $rule.Desc }) `
                                         -Apply:$Apply
            if ($r.Status -eq 'ok') {
                $totalHits += $r.Hits
                [void]$done.Add(("{0} (命中 {1}, 完整性哈希更新 {2})" -f $e.Path, $r.Hits, $r.IntegrityUpdated))
                [void]$entryRecords.Add(@{
                    id      = $rule.Id
                    path    = $e.Path
                    offset  = [long]$e.Offset
                    size    = [long]$e.Size
                    needle  = $rule.To
                    context = $(if ($rule.ContainsKey('AlreadyPatchedContext')) { $rule.AlreadyPatchedContext } else { '' })
                })
            } elseif ($r.Status -eq 'length-mismatch') {
                Write-Err ("    拒绝写入：替换前后长度不一致 @ {0}" -f $e.Path)
            } else {
                Write-Warn ("    {0} @ {1}" -f $r.Status, $e.Path)
            }
        }

        if ($totalHits -ne $rule.Expect) {
            Write-Warn ("    命中 {0} 处，预期 {1} 处 —— App 结构可能已变化，请人工确认后再上线" -f $totalHits, $rule.Expect)
        } else {
            Write-Ok ("    命中 {0} 处：{1}" -f $totalHits, $rule.Desc)
        }
        foreach ($d in $done) { Write-Info ("      " + $d) }

        [void]$results.Add(@{ Id = $rule.Id; Desc = $rule.Desc; Critical = $rule.Critical; Status = 'ok'; Hits = $totalHits; Files = @($done); Entries = @($entryRecords) })
    }

    # DataOffset 一并带出去：它是"数据区起点"，下次复查要拿它换算 seek 位置。
    # asar 没变它就一定没变，所以可以安全地记进 state.json。
    return @{ Results = $results; CriticalMiss = $criticalMiss; DataOffset = [long]$info.DataOffset }
}

# ---------------------------------------------------------------- 子命令

function Show-Status {
    param($Found, [string]$TargetApp)

    $state = $null
    if (Test-Path -LiteralPath $configJson) {
        try { $state = Get-Content -LiteralPath $configJson -Raw -Encoding UTF8 | ConvertFrom-Json } catch {}
    }

    Write-Host ''
    Write-Host '=== Codex 本地化状态 ===' -ForegroundColor White
    if ($Found) {
        Write-Host ("  源安装      : {0}" -f $Found.AppDir)
        Write-Host ("  形态        : {0}" -f $Found.Kind)
        Write-Host ("  版本        : {0}" -f (Get-CodexVersion -AppDir $Found.AppDir))
    } else {
        Write-Host '  源安装      : 未检测到' -ForegroundColor Yellow
    }
    if ($TargetApp) {
        $patched = Test-Path -LiteralPath (Join-Path $TargetApp 'resources\app.asar')
        Write-Host ("  本地化副本  : {0}" -f $TargetApp)
        Write-Host ("  副本可用    : {0}" -f $patched)
    }
    if ($state) {
        Write-Host ("  上次补丁    : {0} (版本 {1})" -f $state.patchedAt, $state.version)
    }
    if (Test-Path -LiteralPath $codexToml) {
        $hit = Select-String -LiteralPath $codexToml -Pattern '^\s*localeOverride\s*=\s*"?([^"\s]+)"?' -ErrorAction SilentlyContinue
        if ($hit) { Write-Host ("  localeOverride: {0}" -f $hit.Matches[0].Groups[1].Value) }
        else { Write-Host '  localeOverride: 未设置' -ForegroundColor Yellow }
    } else {
        Write-Host ("  config.toml : 不存在（{0}）" -f $codexToml) -ForegroundColor Yellow
    }

    # 副本是否落后于源安装。
    # 比对用的是**同一套版本号**：$state.version 记的是打补丁那一刻
    # Get-CodexVersion($Found.AppDir) 的值，与现在的值同源，可以直接比。
    # ⚠️ 不要改成 Get-CodexVersion($TargetApp)：商店副本的目录名不含 OpenAI.Codex_ 前缀，
    #    会退化成读 owl-app.ini，那是另一套编号（副本 26.1007.21434 vs 商店包 26.1007.2314.0），
    #    永远对不上，必然误报"副本已过期"。
    $sourceVersion = if ($Found) { Get-CodexVersion -AppDir $Found.AppDir } else { 'unknown' }
    $stale = ($state -and $state.version -and $sourceVersion -ne 'unknown' -and $state.version -ne $sourceVersion)
    if ($stale) {
        Write-Warn ("  副本已过期  : 源安装已是 {0}，本地化副本仍是 {1}" -f $sourceVersion, $state.version)
        Write-Warn  '                重打一次即可跟上：.\CodexZh.ps1 -Install'
    }
    Write-Host ''

    # ---- 结构化状态行 ----
    # 注意：C# 侧（src/）目前**不消费**这两行 —— 界面状态走 CodexDiscovery。
    # 保留是给"直接跑脚本"的场合看的。（v1.1.3 之前这里的注释误称"供 GUI 解析"。）
    # DETECT|<可执行路径>|<形态>|<版本>
    # STATE|<ok|warn|bad>|<标题>|<说明>
    $version = if ($Found) { Get-CodexVersion -AppDir $Found.AppDir } else { 'unknown' }
    $detectPath = if ($Found) { $Found.AppDir } else { '' }
    $detectKind = if ($Found) { $Found.Kind } else { '' }
    Write-Host ("DETECT|{0}|{1}|{2}" -f $detectPath, $detectKind, $version)

    $level = 'ok'
    $stTitle = '已就绪'
    $stDetail = '中文界面与原生菜单均已生效'
    if (-not $Found) {
        $level = 'bad'
        $stTitle = '未检测到 Codex'
        $stDetail = '请先安装 Codex / ChatGPT 桌面版，或用 -Target 指定目录'
    } elseif (-not $TargetApp) {
        $level = 'warn'
        $stTitle = '尚未汉化'
        $stDetail = '点下方按钮：复制副本 + 打补丁，约 40 秒'
    } elseif ($stale) {
        $level = 'warn'
        $stTitle = '副本已过期'
        $stDetail = ('源安装已是 {0}，本地化副本仍是 {1} —— 重打一次即可跟上' -f $sourceVersion, $state.version)
    } else {
        $stDetail = 'Codex Desktop ' + $version + ' · 中文界面与原生菜单'
    }
    Write-Host ("STATE|{0}|{1}|{2}" -f $level, $stTitle, $stDetail)
}

function Test-PatchRecordStillValid {
    <#
      判断 state.json 里记的"上次打补丁结果"**现在是否依然成立**。
      只有返回 $true 才允许跳过全量扫描。

      依据就一条：**目标文件一个字节都没变**。
        · asar 与 exe 的体积 + 最后写入时间都对得上；且
        · 当时改动过的（或据以判定"已打过"的）那几条，现在仍然含 To（+上下文）

      任何一处对不上、任何异常，一律 $false → 退回全量扫描。

      ⚠️ 这里宁可慢也不能错：把"其实没打上"判成"已打好"，用户会看到英文界面而
         工具报成功 —— 正是 Rules.ps1 里反复警告的那个坑。
    #>
    param(
        # 刻意**不**标 Mandatory：首次运行时 state.json 还不存在，这里收到 $null
        # 是正常情况（下面第一行就返回 $false，退回全量扫描）。标了 Mandatory
        # 反而会在参数绑定阶段直接抛异常，一次安装都跑不起来。
        $State,
        [Parameter(Mandatory)][string]$TargetApp,
        [Parameter(Mandatory)][string]$NowVersion,
        [Parameter(Mandatory)][string]$Signature
    )

    try {
        if (-not $State) { return $false }
        $p = $State.patch
        if (-not $p) { return $false }

        # 上次有"关键补丁没命中"，说明 App 结构已变 —— 那种状态必须每次全量复核。
        if ([int]$p.criticalMiss -gt 0) { return $false }

        # 规则表改过（挑串/替换串/预期命中数变了）就不能再信任旧结论。
        if ([string]$p.rulesSignature -ne $Signature) { return $false }

        # 版本变了 → 副本可能已重拷 → 记下的偏移全不可信。
        if ([string]::IsNullOrWhiteSpace($NowVersion) -or $NowVersion -eq 'unknown') { return $false }
        if ([string]$State.version -ne $NowVersion) { return $false }

        # 上次打的不是这个目录 → 不适用。
        if ([string]::IsNullOrWhiteSpace($p.installPath) -or [string]$p.installPath -ne $TargetApp) { return $false }

        $asarPath = Join-Path $TargetApp 'resources\app.asar'
        $exePath  = Join-Path $TargetApp 'ChatGPT.exe'
        if (-not (Test-Path -LiteralPath $asarPath)) { return $false }
        if (-not (Test-Path -LiteralPath $exePath)) { return $false }

        $asarItem = Get-Item -LiteralPath $asarPath
        $exeItem  = Get-Item -LiteralPath $exePath
        if ([long]$asarItem.Length -ne [long]$p.asarSize) { return $false }
        if ([long]$asarItem.LastWriteTimeUtc.Ticks -ne [long]$p.asarTicks) { return $false }
        if ([long]$exeItem.Length -ne [long]$p.exeSize) { return $false }
        if ([long]$exeItem.LastWriteTimeUtc.Ticks -ne [long]$p.exeTicks) { return $false }

        $records = @($p.entries)
        if ($records.Count -eq 0) { return $false }

        $dataOffset = [long]$p.dataOffset
        foreach ($rec in $records) {
            if (-not $rec) { return $false }
            $needle = [string]$rec.needle
            if ([string]::IsNullOrEmpty($needle)) { return $false }
            if (-not (Test-AsarEntryContains -AsarPath $asarPath -DataOffset $dataOffset `
                        -Offset ([long]$rec.offset) -Size ([long]$rec.size) -Needle $needle)) {
                return $false
            }
            $ctx = [string]$rec.context
            if (-not [string]::IsNullOrEmpty($ctx)) {
                if (-not (Test-AsarEntryContains -AsarPath $asarPath -DataOffset $dataOffset `
                            -Offset ([long]$rec.offset) -Size ([long]$rec.size) -Needle $ctx)) {
                    return $false
                }
            }
        }
        return $true
    } catch {
        return $false
    }
}

function Invoke-Install {
    param($Found, [string]$TargetDir, [switch]$Apply)

    if (-not $Found -and -not $TargetDir) {
        Write-Err '未检测到 Codex。用 -Target 手动指定 app 目录。'
        return 1
    }

    # 这几样在下面要用到多处（复制决策、快路径判定、状态落盘），先取一次。
    $statePrev  = Read-StateFile -Path $configJson
    $patchRules = Get-CodexPatchRules
    $ruleSig    = Get-RulesSignature -Rules $patchRules
    $nowVersion = if ($Found) { Get-CodexVersion -AppDir $Found.AppDir } else { 'unknown' }

    # 1) 决定补丁目标目录
    if ($TargetDir) {
        $targetApp = (Get-Item -LiteralPath $TargetDir).FullName
        $mode = 'explicit'
    } elseif ($Found.Kind -eq 'store' -or -not (Test-DirWritable -Dir $Found.AppDir)) {
        $targetApp = Join-Path $Root 'app'
        $mode = 'store-copy'
        if (-not $Apply) {
            Write-Info ("DryRun：将把 {0} 复制到 {1}" -f $Found.AppDir, $targetApp)
        } else {
            $copyAsar = Join-Path $targetApp 'resources\app.asar'
            $needCopy = $false

            if (-not (Test-Path -LiteralPath $copyAsar)) {
                $needCopy = $true
                Write-Info '首次安装，需要复制副本。'
            } else {
                # 版本变化检测 —— 这一步不能少。
                # 商店版升级后，副本会停留在旧版本；只看"副本在不在"的话会永远跳过
                # 重新复制，用户就一直在跑旧版 Codex 且毫无察觉。
                $prevVersion = if ($statePrev) { $statePrev.version } else { $null }
                if ($prevVersion -and $nowVersion -and $prevVersion -ne $nowVersion) {
                    # 措辞刻意不判方向：这里只知道"变了"。之前写死"已升级"，
                    # 装旧版（降级）时会说反。
                    Write-Warn ("检测到 Codex 版本变化：{0} -> {1}" -f $prevVersion, $nowVersion)
                    Write-Info '旧的本地化副本已过期，需要重新复制。'
                    $needCopy = $true
                } else {
                    Write-Info ("副本已是最新（{0}），跳过复制。" -f $nowVersion)
                }
            }

            if ($needCopy) {
                if (Test-Path -LiteralPath $targetApp) {
                    Write-Info '清理旧副本…'
                    try {
                        Remove-Item -LiteralPath $targetApp -Recurse -Force -ErrorAction Stop
                    } catch {
                        throw ("无法删除旧副本：{0}。请确认 Codex 已完全退出后重试。" -f $_.Exception.Message)
                    }
                }
                Write-Info ("复制 Codex 到 {0}（约 1-2 GB，可能需要一会）..." -f $targetApp)
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                Copy-TreeBytes -Src $Found.AppDir -Dst $targetApp
                $sw.Stop()
                Write-Ok ("复制完成，耗时 {0:N0} 秒" -f $sw.Elapsed.TotalSeconds)
            }
        }
    } else {
        $targetApp = $Found.AppDir
        $mode = 'direct'
    }

    if (-not $Apply -and $mode -eq 'store-copy') {
        Write-Info '（DryRun 下副本尚未复制，补丁环节改为对源 asar 做只读检测）'
        $asarForCheck = Join-Path $Found.AppDir 'resources\app.asar'
        $r = Invoke-PatchPass -AsarPath $asarForCheck -Apply:$false
        return 0
    }

    $asarPath = Join-Path $targetApp 'resources\app.asar'
    if (-not (Test-Path -LiteralPath $asarPath)) {
        Write-Err ("目标 asar 不存在：{0}" -f $asarPath)
        return 1
    }

    # 2a) 原地补丁模式下，先给原安装目录留一份原始 asar。
    #     官网版 / 便携版目录可写，我们直接改的就是**用户原本的** app.asar，
    #     没有副本可退 —— 不留备份等于不可恢复。Store 版则不必：
    #     WindowsApps 里的原件我们从不触碰，原件本身就是最好的备份。
    if ($mode -eq 'direct' -and $Apply) {
        $asarBackup = $asarPath + '.bak'
        if (-not (Test-Path -LiteralPath $asarBackup)) {
            Write-Info ('为原安装目录留一份备份：{0}' -f $asarBackup)
            try {
                Copy-Item -LiteralPath $asarPath -Destination $asarBackup -Force
            } catch {
                Write-Err ("备份失败，为安全起见中止：{0}" -f $_.Exception.Message)
                return 1
            }
        }
    }

    # 2) 打补丁
    Write-Info ''
    Write-Info '--- 应用补丁 ---'

    # ---- 快路径 ----
    # 全量扫描 = 遍历 5.8 MB 头部（实测约 19 秒）+ 把 14770 个候选文件逐个读一遍
    # （约 27 秒）；而重复运行时它其实什么都不会改（结果恒为"已打过补丁，跳过"）。
    # 只要上次的记录**逐项复查仍然成立**（副本一个字节没变），就能整段跳过。
    # 复查只读当时动过的那 1~2 个条目，约 1 毫秒。
    # 任何一处对不上 → 照旧全量扫描。判定逻辑见 Test-PatchRecordStillValid。
    $fastSkip = $false
    if ($Apply -and (Test-PatchRecordStillValid -State $statePrev -TargetApp $targetApp `
                        -NowVersion $nowVersion -Signature $ruleSig)) {
        $fastSkip = $true
        Write-Ok ('副本自上次打补丁后未变化（版本 {0}），跳过全量扫描。' -f $nowVersion)
        $recorded = @($statePrev.patch.entries)
        foreach ($rule in $patchRules) {
            $hit = @($recorded | Where-Object { $_ -and $_.id -eq $rule.Id })
            if ($hit.Count -gt 0) {
                Write-Info ("    复查通过（已打过补丁）：{0}" -f $rule.Desc)
            } else {
                Write-Info ("    与上次相同（未命中）：{0}" -f $rule.Desc)
            }
        }
    }

    if (-not $fastSkip) {
        $patch = Invoke-PatchPass -AsarPath $asarPath -Apply:$Apply
        if ($patch.CriticalMiss -gt 0) {
            Write-Warn '关键补丁点未命中：界面很可能仍是英文。App 版本结构已变化，需要更新规则表。'
        }
    }

    # 2b) 同步嵌入式 asar 完整性哈希。
    #     漏掉这步的后果是：App 启动瞬间 FATAL 崩溃
    #     （asar_util.cc:143 Integrity check failed），而不是"汉化没生效"。
    Write-Info ''
    Write-Info '--- 嵌入式完整性哈希 ---'
    if ($fastSkip) {
        # asar 与 exe 都一个字节没变（上面的复查刚验过），说明头部没动、
        # 嵌在 exe 里的那份哈希自然也还是对的 —— 不用重算。
        Write-Info '已是最新 ChatGPT.exe（副本未变化，无需重算）'
    } else {
        $emb = Update-EmbeddedAsarIntegrity -AppDir $targetApp -AsarPath $asarPath -Apply:$Apply
        if ($emb.Status -eq 'updated') {
            Write-Ok ("已同步 {0}" -f (Split-Path $emb.File -Leaf))
            Write-Info ("  {0}" -f $emb.Old)
            Write-Info ("  -> {0}" -f $emb.New)
        } elseif ($emb.Status -eq 'already-ok') {
            Write-Info ("已是最新 {0}" -f (Split-Path $emb.File -Leaf))
        } elseif ($emb.Status -eq 'needs-update') {
            Write-Warn ("待更新 {0}（DryRun 未写入）" -f (Split-Path $emb.File -Leaf))
        } else {
            Write-Warn '未在二进制中找到嵌入式 asar 完整性记录（该构建可能未开启此校验）'
        }
    }

    # 3) localeOverride
    Write-Info ''
    Write-Info '--- 语言设置 ---'
    $r = Set-LocaleOverride -Path $codexToml -Locale $Language -Apply:$Apply
    if ($r.Changed) { Write-Ok ("localeOverride -> {0}（{1}）" -f $Language, $r.Reason) }
    else            { Write-Info ("localeOverride 已是 {0}" -f $Language) }

    # 4) 状态落盘
    #
    # 快路径时不重写：什么都没变，重写只会把 patchedAt 改成"这次运行的时间"，
    # 那个时间应该如实指向真正打补丁的那一刻。
    if ($Apply -and -not $fastSkip) {
        if (-not (Test-Path -LiteralPath $Root)) { New-Item -ItemType Directory -Path $Root -Force | Out-Null }

        # 汇总"这次到底动了哪几个条目"，连同改完之后的文件指纹一起记下。
        # 指纹用体积 + 最后写入时间：对 2 GB 的 asar 算哈希要好几秒，
        # 而这两样已经足以判断"文件有没有被碰过"。
        $allEntries = New-Object System.Collections.ArrayList
        foreach ($res in @($patch.Results)) {
            foreach ($rec in @($res.Entries)) {
                if ($rec) { [void]$allEntries.Add($rec) }
            }
        }
        $asarItem = Get-Item -LiteralPath $asarPath
        $exeItem  = Get-Item -LiteralPath (Join-Path $targetApp 'ChatGPT.exe')

        $state = @{
            schemaVersion = 2
            mode          = $mode
            sourcePath    = if ($Found) { $Found.AppDir } else { $null }
            installPath   = $targetApp
            version       = $nowVersion
            language      = $Language
            patchedAt     = (Get-Date).ToString('s')
            patch         = @{
                installPath    = $targetApp
                rulesSignature = $ruleSig
                criticalMiss   = [int]$patch.CriticalMiss
                dataOffset     = [long]$patch.DataOffset
                asarSize       = [long]$asarItem.Length
                asarTicks      = [long]$asarItem.LastWriteTimeUtc.Ticks
                exeSize        = [long]$exeItem.Length
                exeTicks       = [long]$exeItem.LastWriteTimeUtc.Ticks
                entries        = @($allEntries)
            }
        }
        [System.IO.File]::WriteAllText($configJson, ($state | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
        Write-Log ("install mode=$mode target=$targetApp version=$nowVersion entries=$($allEntries.Count)")
    }

    # 注：这里此前会创建「ChatGPT 中文版」快捷方式。v1.2.2 起**不再创建** ——
    # 用户直接启动本工具即可；而指向本地化副本的快捷方式会启动一份没有注入的
    # Codex（英文），留着反而误导。
    return 0
}

function Invoke-Launch {
    param([string]$TargetApp, [string]$Lang)

    if (-not $TargetApp) {
        Write-Err '没有可启动的本地化副本。先运行 -Install，或用 -Target 指定目录。'
        return 1
    }
    $exe = Join-Path $TargetApp 'ChatGPT.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        $exe = Join-Path $TargetApp 'Codex.exe'
    }
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Err ("找不到可执行文件：{0}" -f $TargetApp)
        return 1
    }

    $args = @()
    if ($Lang) { $args += "--lang=$Lang" }
    Write-Info ("启动：{0} {1}" -f $exe, ($args -join ' '))
    Start-Process -FilePath $exe -ArgumentList $args
    Write-Ok '已启动。'
    return 0
}

# ---------------------------------------------------------------- 主流程

Write-Host ''
Write-Host 'Codex 本地化工具（codex-zh）' -ForegroundColor White -NoNewline
Write-Host '  零依赖 / 不联网 / 不碰原版' -ForegroundColor DarkGray

$found = Find-CodexInstall -Override $Target

# 解析「要启动哪个 app 目录」。三种来源按优先级：
#   1. 显式 -Target
#   2. state.json 里 -Install 记下的 installPath
#      —— 这一条是为【便携版 / 官网安装版】准备的：它们目录可写，
#         -Install 是「原地补丁」不生成副本，所以启动目标不是副本目录而是它本身。
#   3. 默认的本地化副本目录（Store 版走这条）
$targetApp = $null
if ($Target -and (Test-Path -LiteralPath (Join-Path $Target 'resources\app.asar'))) {
    $targetApp = (Get-Item -LiteralPath $Target).FullName
}
if (-not $targetApp -and (Test-Path -LiteralPath $configJson)) {
    try {
        $st = Get-Content -LiteralPath $configJson -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($st.installPath -and (Test-Path -LiteralPath (Join-Path $st.installPath 'resources\app.asar'))) {
            $targetApp = $st.installPath
        }
    } catch {}
}
if (-not $targetApp) {
    $copyDir = Join-Path $Root 'app'
    if (Test-Path -LiteralPath (Join-Path $copyDir 'resources\app.asar')) {
        $targetApp = $copyDir
    }
}

# 注：此前这里有个 -Shortcuts 模式（只创建快捷方式然后退出）。
# v1.2.2 起不再创建任何快捷方式，该模式随之删除。

if ($Status -or (-not $Install -and -not $Launch)) {
    Show-Status -Found $found -TargetApp $targetApp
    if (-not $Install -and -not $Launch -and -not $Quiet) {
        Write-Host '用法：' -ForegroundColor DarkGray
        Write-Host '  -Status            查看状态' -ForegroundColor DarkGray
        Write-Host '  -Install [-DryRun] 打补丁（Store 版会先复制副本）' -ForegroundColor DarkGray
        Write-Host '  -Launch            启动本地化副本' -ForegroundColor DarkGray
        Write-Host '  -Target <目录>     直接对指定 app 目录操作' -ForegroundColor DarkGray
        Write-Host ''
    }
    if (-not $Install -and -not $Launch) { exit 0 }
}

$exit = 0
if ($Install) {
    $exit = Invoke-Install -Found $found -TargetDir $(if ($Target) { $Target } else { $null }) -Apply:(-not $DryRun)
}
if ($Launch) {
    $e = Invoke-Launch -TargetApp $targetApp -Lang $Language
    if ($e -ne 0) { $exit = $e }
}
exit $exit
