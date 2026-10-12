[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # 成品显示名（窗口标题 / 关于窗 / 文件属性）。同一份源码可构建出不同名字的成品。
    [string]$Brand = 'Codex 汉化增强工具',

    # 输出文件名
    [string]$OutputName = 'CodexZh-Windows-x64.exe'
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
# （早期版本把它们放在仓库外的 ..\codex-zh\，这里保留回退以兼容旧布局。）
$localizeDir = Join-Path $projectRoot 'localize'
if (-not (Test-Path -LiteralPath (Join-Path $localizeDir 'CodexZh.ps1'))) {
    $localizeDir = Join-Path (Split-Path $projectRoot -Parent) 'codex-zh'
}
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
$brandSource = @"
// 本文件由 build.ps1 自动生成，请勿手工编辑。
namespace CodexZhLauncher
{
    internal static partial class AppInfo
    {
        public const string BrandName = "$Brand";
        public const string BrandTagline = "$Brand —— Codex Desktop 中文化（本地化副本 + asar 补丁）";
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
