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

# ---------------------------------------------------------------- 快捷方式

function New-AppShortcuts {
    <#
      给本地化副本建立桌面 + 开始菜单快捷方式。
      开始菜单那条很关键：客户端习惯"按 Win 键搜图标打开"，
      有了它才能搜到中文版，而不是只能搜到那个永远英文的商店原版。

      快捷方式带上 --lang，是为了兼容系统语言非中文的机器
      （菜单语言跟随 app.getLocale()，中文系统下不加也对，加了更稳）。
    #>
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$WorkDir,
        [Parameter(Mandatory)][string]$Locale,
        [string]$Name = 'ChatGPT 中文版',
        [switch]$Apply
    )

    $targets = @()
    $desktop = [Environment]::GetFolderPath('Desktop')
    $programs = [Environment]::GetFolderPath('Programs')
    if ($desktop) { $targets += (Join-Path $desktop ($Name + '.lnk')) }
    if ($programs) { $targets += (Join-Path $programs ($Name + '.lnk')) }

    if (-not $Apply) { return @{ Status = 'dry'; Targets = $targets } }

    # 图标：优先用 App 自带的高质量 .ico。
    # 直接用 exe 的内嵌图标资源会发虚（尺寸不全），和商店原版的图块对不上，
    # 用户一眼就看出"这两个图标不一样"。light 版是黑色结，与商店原版一致。
    $iconFile = $ExePath
    foreach ($cand in @(
                (Join-Path $WorkDir 'resources\chatgpt-app-light.ico'),
                (Join-Path $WorkDir 'resources\icon-chatgpt.ico'))) {
        if (Test-Path -LiteralPath $cand) { $iconFile = $cand; break }
    }

    $shell = New-Object -ComObject WScript.Shell
    $made = @()
    foreach ($t in $targets) {
        try {
            $dir = Split-Path $t -Parent
            if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            $sc = $shell.CreateShortcut($t)
            $sc.TargetPath = $ExePath
            $sc.Arguments = '--lang=' + $Locale
            $sc.WorkingDirectory = $WorkDir
            $sc.IconLocation = $iconFile + ',0'
            $sc.Description = 'ChatGPT 中文版（本地化副本，与商店原版共存）'
            $sc.Save()
            $made += $t
        } catch {
            Write-Warn ("快捷方式创建失败：{0} —— {1}" -f $t, $_.Exception.Message)
        }
    }
    return @{ Status = 'ok'; Made = $made; Icon = $iconFile }
}

# ---------------------------------------------------------------- 打补丁

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
            $alreadyPatched = $false
            foreach ($e in $entries) {
                if (Test-AsarEntryContains -AsarPath $AsarPath -DataOffset $info.DataOffset `
                                           -Offset $e.Offset -Size $e.Size -Needle $rule.To) {
                    $alreadyPatched = $true
                    break
                }
            }
            if ($alreadyPatched) {
                Write-Info ("    已打过补丁，跳过：{0}" -f $rule.Desc)
                [void]$results.Add(@{ Id = $rule.Id; Desc = $rule.Desc; Critical = $rule.Critical; Status = 'already-patched'; Files = @() })
            } else {
                Write-Warn ("    未命中：{0}" -f $rule.Desc)
                if ($rule.Critical) { $criticalMiss++ }
                [void]$results.Add(@{ Id = $rule.Id; Desc = $rule.Desc; Critical = $rule.Critical; Status = 'miss'; Files = @() })
            }
            continue
        }

        $totalHits = 0
        $done = New-Object System.Collections.ArrayList
        foreach ($e in $hitFiles) {
            $r = Invoke-AsarInPlacePatch -AsarPath $AsarPath -RelPath $e.Path `
                                         -Replacements @(@{ From = $rule.From; To = $rule.To; Desc = $rule.Desc }) `
                                         -Apply:$Apply
            if ($r.Status -eq 'ok') {
                $totalHits += $r.Hits
                [void]$done.Add(("{0} (命中 {1}, 完整性哈希更新 {2})" -f $e.Path, $r.Hits, $r.IntegrityUpdated))
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

        [void]$results.Add(@{ Id = $rule.Id; Desc = $rule.Desc; Critical = $rule.Critical; Status = 'ok'; Hits = $totalHits; Files = @($done) })
    }

    return @{ Results = $results; CriticalMiss = $criticalMiss }
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
        Write-Warn ("  副本已过期  : 源安装已升级到 {0}，本地化副本仍是 {1}" -f $sourceVersion, $state.version)
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
        $stDetail = ('源安装已升级到 {0}，本地化副本仍是 {1} —— 重打一次即可跟上' -f $sourceVersion, $state.version)
    } else {
        $stDetail = 'Codex Desktop ' + $version + ' · 中文界面与原生菜单'
    }
    Write-Host ("STATE|{0}|{1}|{2}" -f $level, $stTitle, $stDetail)
}

function Invoke-Install {
    param($Found, [string]$TargetDir, [switch]$Apply)

    if (-not $Found -and -not $TargetDir) {
        Write-Err '未检测到 Codex。用 -Target 手动指定 app 目录。'
        return 1
    }

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
                $prevVersion = $null
                if (Test-Path -LiteralPath $configJson) {
                    try {
                        $st = Get-Content -LiteralPath $configJson -Raw -Encoding UTF8 | ConvertFrom-Json
                        $prevVersion = $st.version
                    } catch {}
                }
                $nowVersion = Get-CodexVersion -AppDir $Found.AppDir
                if ($prevVersion -and $nowVersion -and $prevVersion -ne $nowVersion) {
                    Write-Warn ("检测到 Codex 已升级：{0} -> {1}" -f $prevVersion, $nowVersion)
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
    $patch = Invoke-PatchPass -AsarPath $asarPath -Apply:$Apply
    if ($patch.CriticalMiss -gt 0) {
        Write-Warn '关键补丁点未命中：界面很可能仍是英文。App 版本结构已变化，需要更新规则表。'
    }

    # 2b) 同步嵌入式 asar 完整性哈希。
    #     漏掉这步的后果是：App 启动瞬间 FATAL 崩溃
    #     （asar_util.cc:143 Integrity check failed），而不是"汉化没生效"。
    Write-Info ''
    Write-Info '--- 嵌入式完整性哈希 ---'
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

    # 3) localeOverride
    Write-Info ''
    Write-Info '--- 语言设置 ---'
    $r = Set-LocaleOverride -Path $codexToml -Locale $Language -Apply:$Apply
    if ($r.Changed) { Write-Ok ("localeOverride -> {0}（{1}）" -f $Language, $r.Reason) }
    else            { Write-Info ("localeOverride 已是 {0}" -f $Language) }

    # 4) 状态落盘
    if ($Apply) {
        if (-not (Test-Path -LiteralPath $Root)) { New-Item -ItemType Directory -Path $Root -Force | Out-Null }
        $state = @{
            schemaVersion = 1
            mode          = $mode
            sourcePath    = if ($Found) { $Found.AppDir } else { $null }
            installPath   = $targetApp
            version       = if ($Found) { Get-CodexVersion -AppDir $Found.AppDir } else { 'unknown' }
            language      = $Language
            patchedAt     = (Get-Date).ToString('s')
        }
        [System.IO.File]::WriteAllText($configJson, ($state | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
        Write-Log ("install mode=$mode target=$targetApp version=$($state.version)")
    }

    # 5) 桌面 / 开始菜单快捷方式
    #    没有这条，客户端只能从开始菜单搜到「商店原版」——而那个永远是英文，
    #    用户会以为汉化没生效。
    Write-Info ''
    Write-Info '--- 快捷方式 ---'
    $exeForShortcut = Join-Path $targetApp 'ChatGPT.exe'
    if (-not (Test-Path -LiteralPath $exeForShortcut)) { $exeForShortcut = Join-Path $targetApp 'Codex.exe' }
    if (Test-Path -LiteralPath $exeForShortcut) {
        $sc = New-AppShortcuts -ExePath $exeForShortcut -WorkDir $targetApp -Locale $Language -Apply:$Apply
        if ($sc.Status -eq 'ok') {
            foreach ($m in $sc.Made) { Write-Ok ("已创建快捷方式：{0}" -f $m) }
            Write-Info ("使用图标：{0}" -f $sc.Icon)
        } elseif ($sc.Status -eq 'dry') {
            foreach ($m in $sc.Targets) { Write-Info ("将创建快捷方式：{0}" -f $m) }
        }
    } else {
        Write-Warn '没找到可执行文件，跳过快捷方式创建。'
    }

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
