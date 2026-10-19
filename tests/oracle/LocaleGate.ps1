param(
    [Parameter(Mandatory)][string]$Path,
    [string]$Locale = 'zh-CN',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

$r = Set-LocaleOverride -Path $Path -Locale $Locale -Apply:$Apply
Write-Output ('changed=' + $(if ($r.Changed) { 'True' } else { 'False' }))
Write-Output ('reason=' + $r.Reason)
if (Test-Path -LiteralPath $Path) {
    Write-Output ('sha256=' + (Get-TextSha256 -Path $Path))
}
