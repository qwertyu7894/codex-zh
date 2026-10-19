param(
    [string]$SourceApp,
    [string]$TargetDir,
    # Deliberately NOT named Root/ConfigToml: dot-sourcing CodexZh.ps1 resets
    # variables with those names to their defaults.
    [Parameter(Mandatory)][string]$StateRoot,
    [Parameter(Mandatory)][string]$ConfigPath,
    [string]$Locale = 'zh-CN',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

# Redirect every write away from the real user profile.
$Root = [System.IO.Path]::GetFullPath($StateRoot)
$configJson = Join-Path $Root 'state.json'
$logFile = Join-Path $Root 'codex-zh.log'
$codexToml = [System.IO.Path]::GetFullPath($ConfigPath)
$Language = $Locale

$found = $null
if ($SourceApp) {
    $full = (Resolve-Path -LiteralPath $SourceApp).Path
    $kind = if ($full -like '*\WindowsApps\*') { 'store' } else { 'writable' }
    $found = @{ AppDir = $full; Kind = $kind }
}

$target = $null
if ($TargetDir) { $target = (Resolve-Path -LiteralPath $TargetDir).Path }

$rc = Invoke-Install -Found $found -TargetDir $target -Apply:$Apply
Write-Output ('exit=' + $rc)
