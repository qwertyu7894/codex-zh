param(
    [Parameter(Mandatory)][string]$State,
    [Parameter(Mandatory)][string]$App,
    [Parameter(Mandatory)][string]$Version,
    [string]$Signature
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

if (-not $Signature) {
    $Signature = Get-RulesSignature -Rules (Get-CodexPatchRules)
}

$st = Read-StateFile -Path $State
$ok = Test-PatchRecordStillValid -State $st -TargetApp (Resolve-Path -LiteralPath $App).Path `
                                -NowVersion $Version -Signature $Signature
Write-Output ('valid=' + $(if ($ok) { 'True' } else { 'False' }))
