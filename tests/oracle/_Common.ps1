# Shared oracle bootstrap.
#
# Dot-source this file from a gate script. It copies the three legacy
# PowerShell files to a temp directory, truncates CodexZh.ps1 right before
# its top-level main flow, then dot-sources the result so every legacy
# function becomes available in the caller's scope.
#
# CodexZh.ps1 MUST NOT be dot-sourced directly: lines 868+ run Install/Launch
# on load.

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$oracleTempDir = Join-Path ([System.IO.Path]::GetTempPath()) ('codexzh-oracle-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $oracleTempDir -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $repoRoot 'localize\AsarInPlace.ps1') -Destination $oracleTempDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'localize\Rules.ps1')       -Destination $oracleTempDir -Force

$codexZhPath = Join-Path $repoRoot 'localize\CodexZh.ps1'
$content = [System.IO.File]::ReadAllText($codexZhPath)
# The main-flow marker is the LAST section separator in the file. Search for
# the ASCII separator only so this bootstrap stays ASCII-only (PowerShell 5.1
# reads BOM-less .ps1 files using the ANSI code page).
$marker = '# ----------------------------------------------------------------'
$markerIndex = $content.LastIndexOf($marker)
if ($markerIndex -lt 0) { throw 'section marker not found in CodexZh.ps1' }
[System.IO.File]::WriteAllText(
    (Join-Path $oracleTempDir 'CodexZh.ps1'),
    $content.Substring(0, $markerIndex),
    (New-Object System.Text.UTF8Encoding($true)))

. (Join-Path $oracleTempDir 'CodexZh.ps1')

# Codex 26.1029 changed the minifier's quote style: the i18n default is now
# written as "enable_i18n",!1 instead of `enable_i18n`,!1. The legacy table is
# kept frozen as a historical reference, so append the quoted variant here
# (non-critical) to keep this oracle able to produce a byte-identical patch
# for whichever version is installed.
$script:LegacyGetCodexPatchRules = ${function:Get-CodexPatchRules}
function Get-CodexPatchRules {
    $rules = @(& $script:LegacyGetCodexPatchRules)

    # Primary rule targets the quote style used by current builds (26.1029+).
    $rules[0].From = '"enable_i18n",!1'
    $rules[0].To   = '"enable_i18n",!0'
    $rules[0].AlreadyPatchedContext = '"locale_source"'

    # Keep the backtick form as a non-critical fallback so an older fixture
    # still patches (the gate asserts on the primary rule id).
    $rules += @(
        @{
            Id       = 'enable-i18n-default-backtick'
            Scope    = 'webview/assets'
            Ext      = '.js'
            From     = '`enable_i18n`,!1'
            To       = '`enable_i18n`,!0'
            Expect   = 0
            Critical = $false
            AlreadyPatchedContext = '`locale_source`'
            Desc     = 'backtick variant (pre-26.1029)'
        },
        @{
            Id       = 'enable-i18n-default-legacy-quoted'
            Scope    = '.vite/build'
            Ext      = '.js'
            From     = '"enable_i18n",!1'
            To       = '"enable_i18n",!0'
            Expect   = 0
            Critical = $false
            Desc     = 'quoted variant (26.1029+)'
        }
    )
    return $rules
}

# Keep every caller away from the real user profile. Gate scripts may override
# these after dot-sourcing this file.
$Root = Join-Path $oracleTempDir 'root'
$configJson = Join-Path $Root 'state.json'
$logFile = Join-Path $Root 'codex-zh.log'
$codexToml = Join-Path $oracleTempDir 'config.toml'

function Get-TextSha256 {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

function Write-KeyValue {
    param([string]$Key, $Value)
    Write-Output ('{0}={1}' -f $Key, $Value)
}
