[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # 成品显示名（窗口标题 / 关于窗 / 文件属性）。同一份源码可构建出不同名字的成品。
    [string]$Brand = 'Codex 汉化增强工具',

    # 输出文件名
    [string]$OutputName = 'CodexZh-Windows-x64.exe',

    # 编译期开关：这个成品在汉化成功后，是否往桌面/开始菜单放一个快捷方式。
    #
    # 默认**关**。主版本从 v1.2.2 起刻意不建任何快捷方式：指向"本地化副本"的旧图标
    # 双击后启动的是一份没经过注入的 Codex（界面仍英文），比任何提示都更容易让人
    # 以为"汉化没生效"。只有明确要"跑一次→桌面出现图标→以后双击图标进中文版"
    # 那种用法的客户，才专门构建一个开了这个开关的变体。
    #
    # 开了之后图标指向的是**本工具自身 + --launch-zh**，不是副本 —— 见 CodexZh.ps1
    # 里 New-AppShortcuts 的调用处。
    [switch]$DesktopShortcut,

    # 快捷方式的显示名。留空则用 $Brand。
    [string]$ShortcutName = '',

    # 交付副本目录（默认 %USERPROFILE%\Music）。见文件末尾"交付副本"那段。
    [string]$HandoffDir = '',

    # 跳过交付副本同步（构建别人要的成品、或临时试验时用）。
    [switch]$NoHandoff
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$sourceDir = Join-Path $projectRoot 'src'
$sharedDir = Join-Path $projectRoot 'shared'
$manifestFile = Join-Path $sourceDir 'app.manifest'
$outputDir = Join-Path $projectRoot 'dist'
$intermediateDir = Join-Path $projectRoot 'obj'
$outputFile = Join-Path $outputDir $OutputName

# 汉化逻辑的唯一事实来源：仓库内的 localize\ 下的三个脚本。
# 它们以嵌入资源形式打进 exe，运行时由 LocalizationRuntime 释放并调用。
# 改逻辑只改那三个脚本、重新 build 即可，C# 侧无需任何改动。
$localizeDir = Join-Path $projectRoot 'localize'
$iconFile = Join-Path $intermediateDir 'AppIcon.ico'
$iconGenerator = Join-Path $projectRoot 'scripts\generate-windows-icon.ps1'
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "System C# compiler not found: $compiler"
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
New-Item -ItemType Directory -Force -Path $intermediateDir | Out-Null

if (-not (Test-Path -LiteralPath $iconGenerator)) {
    throw "Missing Windows icon generator: $iconGenerator"
}
& $iconGenerator -OutputPath $iconFile

$references = @(
    (Join-Path $frameworkRoot 'WPF\PresentationFramework.dll'),
    (Join-Path $frameworkRoot 'WPF\PresentationCore.dll'),
    (Join-Path $frameworkRoot 'WPF\WindowsBase.dll'),
    (Join-Path $frameworkRoot 'System.dll'),
    (Join-Path $frameworkRoot 'System.Core.dll'),
    (Join-Path $frameworkRoot 'System.Net.Http.dll'),
    (Join-Path $frameworkRoot 'System.Web.Extensions.dll'),
    (Join-Path $frameworkRoot 'System.Xml.dll'),
    (Join-Path $frameworkRoot 'System.Xml.Linq.dll'),
    (Join-Path $frameworkRoot 'System.Xaml.dll'),
    (Join-Path $frameworkRoot 'System.Windows.Forms.dll')
)

foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) {
        throw "Missing compiler reference: $reference"
    }
}

$sharedResources = @(
    @{ Path = (Join-Path $sharedDir 'i18n-bootstrap.js'); Name = 'CodexZhLauncher.Shared.i18n-bootstrap.js' },
    @{ Path = (Join-Path $sharedDir 'locale-script.js'); Name = 'CodexZhLauncher.Shared.locale-script.js' },
    @{ Path = (Join-Path $sharedDir 'menu-script.js'); Name = 'CodexZhLauncher.Shared.menu-script.js' },
    @{ Path = (Join-Path $sharedDir 'menu-translations.json'); Name = 'CodexZhLauncher.Shared.menu-translations.json' }
)
foreach ($resource in $sharedResources) {
    if (-not (Test-Path -LiteralPath $resource.Path)) {
        throw "Missing shared resource: $($resource.Path)"
    }
}

# 汉化脚本（嵌入资源）。资源名必须与 LocalizationRuntime.ScriptNames 完全一致。
$localizeResources = @(
    @{ Path = (Join-Path $localizeDir 'CodexZh.ps1');     Name = 'CodexZh.ps1' },
    @{ Path = (Join-Path $localizeDir 'AsarInPlace.ps1'); Name = 'AsarInPlace.ps1' },
    @{ Path = (Join-Path $localizeDir 'Rules.ps1');       Name = 'Rules.ps1' }
)
foreach ($resource in $localizeResources) {
    if (-not (Test-Path -LiteralPath $resource.Path)) {
        throw "Missing localize script: $($resource.Path)（应位于 ..\codex-zh\ 下）"
    }
}

# 生成品牌常量。必须带 BOM —— csc 没有 BOM 时按 ANSI 解码，中文会乱。
$brandFile = Join-Path $intermediateDir 'Brand.g.cs'
if ([string]::IsNullOrWhiteSpace($ShortcutName)) { $ShortcutName = $Brand }
$shortcutFlag = if ($DesktopShortcut) { 'true' } else { 'false' }
$brandSource = @"
// 本文件由 build.ps1 自动生成，请勿手工编辑。
namespace CodexZhLauncher
{
    internal static partial class AppInfo
    {
        public const string BrandName = "$Brand";
        public const string BrandTagline = "$Brand —— Codex Desktop 中文化（运行时注入，跟随最新版）";

        // 编译期决定：汉化成功后要不要放桌面/开始菜单快捷方式。
        // 只有 build.ps1 -DesktopShortcut 构建出来的变体才是 true。
        public const bool CreateDesktopShortcut = $shortcutFlag;
        public const string ShortcutName = "$ShortcutName";
    }
}
"@
[System.IO.File]::WriteAllText($brandFile, $brandSource, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "Brand: $Brand  ->  $OutputName"

$sourceFiles = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' | Sort-Object Name | Select-Object -ExpandProperty FullName)
if (-not $sourceFiles) {
    throw 'No C# source files found.'
}
$sourceFiles += $brandFile

$compilerArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/utf8output',
    '/codepage:65001',
    "/win32manifest:$manifestFile",
    "/win32icon:$iconFile",
    "/out:$outputFile"
)

if ($Configuration -eq 'Release') {
    $compilerArgs += '/optimize+'
    $compilerArgs += '/debug-'
} else {
    $compilerArgs += '/optimize-'
    $compilerArgs += '/debug:full'
}

foreach ($reference in $references) {
    $compilerArgs += "/reference:$reference"
}
foreach ($resource in $sharedResources) {
    $compilerArgs += "/resource:$($resource.Path),$($resource.Name)"
}
foreach ($resource in $localizeResources) {
    $compilerArgs += "/resource:$($resource.Path),$($resource.Name)"
}
$compilerArgs += "/resource:$iconFile,CodexZhLauncher.AppIcon.ico"
$compilerArgs += $sourceFiles

& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed. csc exit code: $LASTEXITCODE"
}

# 构建戳。package.py 会拿它和 AppInfo.Version 对账 ——
# 万一本脚本编译失败，dist 里还是上一次的旧 exe，打包脚本就能靠这个发现
# "包名版本号与包内实际版本对不上"，而不是静默打出一个名不副实的包。
$appInfoText = [System.IO.File]::ReadAllText(
    (Join-Path $sourceDir 'AppInfo.cs'), [System.Text.Encoding]::UTF8)
$versionMatch = [regex]::Match($appInfoText, 'Version\s*=\s*"([^"]+)"')
$appVersion = if ($versionMatch.Success) { $versionMatch.Groups[1].Value } else { 'unknown' }
Write-Host "Assembly version: $appVersion"

$buildInfo = @{
    version = $appVersion
    output  = [System.IO.Path]::GetFileName($outputFile)
    brand   = $Brand
    builtAt = (Get-Date).ToString('s')
} | ConvertTo-Json -Compress
[System.IO.File]::WriteAllText(
    (Join-Path $outputDir 'buildinfo.json'),
    $buildInfo,
    (New-Object System.Text.UTF8Encoding($false))
)

$hash = Get-FileHash -LiteralPath $outputFile -Algorithm SHA256
$checksumFile = Join-Path $outputDir 'SHA256SUMS.txt'
[System.IO.File]::WriteAllText(
    $checksumFile,
    "$($hash.Hash)  $([System.IO.Path]::GetFileName($outputFile))`r`n",
    [System.Text.Encoding]::ASCII
)
Write-Host "Build completed: $outputFile"
Write-Host "SHA-256: $($hash.Hash)"

# ---------------------------------------------------------------- 交付副本
#
# 用户是**直接从音乐文件夹**把单个 exe 发给客户的。这个目录必须始终是最新构建，
# **跟发不发 GitHub Release 完全是两回事**（Release 见 scripts/release.ps1）。
#
# 为什么做进 build.ps1 而不是只靠 publish.ps1：只跑 build.ps1 就忘了同步的情况
# 真实发生过（2026-10-16），结果是源码已经是新的、发出去的还是旧的。
# 做进构建这一步，就不依赖"记得跑另一条命令"了。
#
# 只同步交付品牌那一个：构建 Codex 汉化增强工具、或构建变体版时都不动它。
# ⚠️ 这个常量与 scripts/package.py 的 HANDOFF_EXE 必须一致 —— 改一处要改两处。
$handoffExe = 'GPTHanhua.exe'
if (-not $NoHandoff -and [System.IO.Path]::GetFileName($outputFile) -eq $handoffExe) {
    $targetDir = if ([string]::IsNullOrWhiteSpace($HandoffDir)) {
        Join-Path $env:USERPROFILE 'Music'
    } else { $HandoffDir }
    $target = Join-Path $targetDir $handoffExe
    try {
        if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }

        # 先清掉上次"改名腾位置"留下的旧文件（那时它还被运行中的进程锁着）。
        Get-ChildItem -LiteralPath $targetDir -Filter ($handoffExe + '.old-*') -ErrorAction SilentlyContinue |
            ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop } catch { } }

        try {
            Copy-Item -LiteralPath $outputFile -Destination $target -Force -ErrorAction Stop
            Write-Host "交付副本已同步: $target"
        }
        catch {
            # 目标正在运行（被锁）时不能直接覆盖 —— 但 **正在运行的 exe 允许改名**。
            # 把旧的挪开，新的放进去：正在跑的那个进程继续用改名后的文件，
            # 下次启动就是新版。这样"工具开着"也不会让音乐文件夹落后。
            $stale = $target + '.old-' + (Get-Date -Format 'HHmmss')
            Move-Item -LiteralPath $target -Destination $stale -Force -ErrorAction Stop
            Copy-Item -LiteralPath $outputFile -Destination $target -Force -ErrorAction Stop
            Write-Host "交付副本已同步（旧的那份正在运行，已改名为 $(Split-Path $stale -Leaf)）: $target"
            try { Remove-Item -LiteralPath $stale -Force -ErrorAction Stop } catch { }
        }
    } catch {
        Write-Warning "交付副本同步失败（不影响本次构建）：$($_.Exception.Message)"
    }
}
