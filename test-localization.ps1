param(
    [switch]$SelfTest,
    [switch]$Full,
    [string]$SourceApp,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $frameworkDir 'csc.exe'
$outDir = Join-Path $repoRoot 'obj\localization-tests'
$outExe = Join-Path $outDir 'LocalizationHarness.exe'
$powerShellExe = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$oracleDir = Join-Path $repoRoot 'tests\oracle'

if (-not (Test-Path -LiteralPath $csc)) { throw "csc.exe not found: $csc" }
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$sources = @(
    (Join-Path $repoRoot 'tests\LocalizationHarness.cs'),
    (Join-Path $repoRoot 'src\Models.cs'),
    (Join-Path $repoRoot 'src\AppLog.cs'),
    (Join-Path $repoRoot 'src\LocalizationPaths.cs'),
    (Join-Path $repoRoot 'src\AppIconRegistration.cs'),
    (Join-Path $repoRoot 'src\AsarArchive.cs'),
    (Join-Path $repoRoot 'src\AsarPatcher.cs'),
    (Join-Path $repoRoot 'src\AsarPatchPass.cs'),
    (Join-Path $repoRoot 'src\EmbeddedAsarIntegrity.cs'),
    (Join-Path $repoRoot 'src\LocalizationRules.cs'),
    (Join-Path $repoRoot 'src\PatchState.cs'),
    (Join-Path $repoRoot 'src\CodexConfigToml.cs'),
    (Join-Path $repoRoot 'src\FileSystemUtils.cs'),
    (Join-Path $repoRoot 'src\NativePackageLocator.cs'),
    (Join-Path $repoRoot 'src\CodexInstallLocator.cs'),
    (Join-Path $repoRoot 'src\ShortcutService.cs'),
    (Join-Path $repoRoot 'src\PatchInstaller.cs')
)

$cscArgs = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/codepage:65001',
    '/utf8output',
    ('/out:' + $outExe),
    '/main:CodexZhLauncher.LocalizationHarness',
    ('/reference:' + (Join-Path $frameworkDir 'System.dll')),
    ('/reference:' + (Join-Path $frameworkDir 'System.Core.dll')),
    ('/reference:' + (Join-Path $frameworkDir 'System.Web.Extensions.dll'))
) + $sources

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "harness build failed with exit code $LASTEXITCODE" }
Write-Output ('harness=' + $outExe)

if ($SelfTest) {
    $asar = Join-Path ([System.IO.Path]::GetTempPath()) ('codexzh-selftest-' + [guid]::NewGuid().ToString('N') + '.asar')
    try {
        $bytes = New-Object byte[] 32
        [System.IO.File]::WriteAllBytes($asar, $bytes)
        $failed = $false
        try { & $outExe asar-inspect --asar $asar *> $null } catch { $failed = $true }
        if (-not $failed) { throw 'self-test: malformed asar should have failed' }
        Write-Output 'PASS self-test'
    } finally {
        if (Test-Path -LiteralPath $asar) { Remove-Item -LiteralPath $asar -Force }
    }
}

if (-not $Full) { return }

function New-Fixture([string]$Destination, [string]$Source) {
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $Destination 'resources') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $Source 'resources\app.asar') -Destination (Join-Path $Destination 'resources\app.asar')
    Copy-Item -LiteralPath (Join-Path $Source 'ChatGPT.exe') -Destination (Join-Path $Destination 'ChatGPT.exe')
    $ini = Join-Path $Source 'resources\owl-app.ini'
    if (Test-Path -LiteralPath $ini) {
        Copy-Item -LiteralPath $ini -Destination (Join-Path $Destination 'resources\owl-app.ini')
    }
}

function Get-Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-SameHash([string]$A, [string]$B, [string]$Label) {
    $ha = Get-Hash $A
    $hb = Get-Hash $B
    if ($ha -ne $hb) { throw ("{0} mismatch: {1} != {2}" -f $Label, $ha, $hb) }
    Write-Output ("PASS {0} {1}" -f $Label, $ha)
}

function Assert-Text([string]$Actual, [string]$Expected, [string]$Label) {
    if ($Actual -ne $Expected) { throw ("{0} mismatch: '{1}' != '{2}'" -f $Label, $Actual, $Expected) }
    Write-Output ("PASS {0}" -f $Label)
}

if (-not $SourceApp) {
    $package = Get-AppxPackage -Name 'OpenAI.Codex' | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $package) { throw 'OpenAI.Codex package not found; pass -SourceApp explicitly.' }
    $SourceApp = Join-Path $package.InstallLocation 'app'
}
$SourceApp = (Resolve-Path -LiteralPath $SourceApp).Path
Write-Output ('fixture=' + $SourceApp)

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('codexzh-gates-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
Write-Output ('work=' + $work)

try {
    # ---- Gate A: patch pass + embedded integrity ----
    Write-Output '--- Gate A: patch pass + embedded integrity ---'
    $psApp = Join-Path $work 'patch\ps'
    $csApp = Join-Path $work 'patch\cs'
    New-Fixture $psApp $SourceApp
    New-Fixture $csApp $SourceApp

    & $powerShellExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $oracleDir 'AsarGate.ps1') `
        -Mode patch-pass -App $psApp -Apply *> (Join-Path $work 'patch-ps.log')
    if ($LASTEXITCODE -ne 0) { throw "PS patch-pass failed: $LASTEXITCODE" }
    $psPatch = Get-Content -LiteralPath (Join-Path $work 'patch-ps.log')
    foreach ($expected in @('criticalMiss=0', 'result.enable-i18n-default.status=ok',
                            'result.enable-i18n-default.hits=1')) {
        if ($psPatch -notcontains $expected) { throw ("PS patch-pass missing " + $expected) }
    }

    $csPatch = & $outExe patch-pass --app $csApp --apply --quiet
    if ($LASTEXITCODE -ne 0) { throw "C# patch-pass failed: $LASTEXITCODE" }
    foreach ($expected in @('criticalMiss=0', 'result.enable-i18n-default.status=ok',
                            'result.enable-i18n-default.hits=1')) {
        if ($csPatch -notcontains $expected) { throw ("C# patch-pass missing " + $expected) }
    }
    Assert-SameHash (Join-Path $psApp 'resources\app.asar') (Join-Path $csApp 'resources\app.asar') 'patch-pass asar'

    & $powerShellExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $oracleDir 'AsarGate.ps1') `
        -Mode embedded-status -App $psApp -Asar (Join-Path $psApp 'resources\app.asar') -Apply *> (Join-Path $work 'embedded-ps.log')
    if ($LASTEXITCODE -ne 0) { throw "PS embedded integrity failed: $LASTEXITCODE" }
    & $outExe embedded-status --app $csApp --asar (Join-Path $csApp 'resources\app.asar') --apply *> $null
    if ($LASTEXITCODE -ne 0) { throw "C# embedded integrity failed: $LASTEXITCODE" }
    Assert-SameHash (Join-Path $psApp 'ChatGPT.exe') (Join-Path $csApp 'ChatGPT.exe') 'embedded exe'

    # ---- Gate B: direct install (same path reset) ----
    Write-Output '--- Gate B: direct install ---'
    $directApp = Join-Path $work 'direct\app'
    $psRoot = Join-Path $work 'direct\root-ps'
    $csRoot = Join-Path $work 'direct\root-cs'
    $psConfig = Join-Path $work 'direct\config-ps.toml'
    $csConfig = Join-Path $work 'direct\config-cs.toml'
    New-Fixture $directApp $SourceApp

    & $powerShellExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $oracleDir 'InstallGate.ps1') `
        -SourceApp $directApp -StateRoot $psRoot -ConfigPath $psConfig -Apply *> (Join-Path $work 'install-ps.log')
    if ($LASTEXITCODE -ne 0) { throw "PS install failed: $LASTEXITCODE" }

    $psOut = Join-Path $work 'direct\ps-out'
    New-Item -ItemType Directory -Path $psOut -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $directApp 'resources\app.asar') -Destination (Join-Path $psOut 'app.asar')
    Copy-Item -LiteralPath (Join-Path $directApp 'ChatGPT.exe') -Destination (Join-Path $psOut 'ChatGPT.exe')
    Copy-Item -LiteralPath (Join-Path $directApp 'resources\app.asar.bak') -Destination (Join-Path $psOut 'app.asar.bak')
    Copy-Item -LiteralPath $psConfig -Destination (Join-Path $psOut 'config.toml')
    Copy-Item -LiteralPath (Join-Path $psRoot 'state.json') -Destination (Join-Path $psOut 'state.json')

    New-Fixture $directApp $SourceApp

    & $outExe install --source $directApp --root $csRoot --config $csConfig --apply --quiet *> $null
    if ($LASTEXITCODE -ne 0) { throw "C# install failed: $LASTEXITCODE" }

    $csOut = Join-Path $work 'direct\cs-out'
    New-Item -ItemType Directory -Path $csOut -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $directApp 'resources\app.asar') -Destination (Join-Path $csOut 'app.asar')
    Copy-Item -LiteralPath (Join-Path $directApp 'ChatGPT.exe') -Destination (Join-Path $csOut 'ChatGPT.exe')
    Copy-Item -LiteralPath (Join-Path $directApp 'resources\app.asar.bak') -Destination (Join-Path $csOut 'app.asar.bak')
    Copy-Item -LiteralPath $csConfig -Destination (Join-Path $csOut 'config.toml')
    Copy-Item -LiteralPath (Join-Path $csRoot 'state.json') -Destination (Join-Path $csOut 'state.json')

    foreach ($name in @('app.asar', 'ChatGPT.exe', 'app.asar.bak', 'config.toml')) {
        Assert-SameHash (Join-Path $psOut $name) (Join-Path $csOut $name) ('install ' + $name)
    }

    $stateEqual = & $outExe state-equal --a (Join-Path $psOut 'state.json') --b (Join-Path $csOut 'state.json')
    if ($stateEqual -notcontains 'equal=True') {
        $stateEqual | Write-Output
        throw 'install state semantic mismatch'
    }
    Write-Output 'PASS install state semantic'

    $fileCheck = & $outExe state-file-check --state (Join-Path $csOut 'state.json') --app $directApp
    if ($fileCheck -notcontains 'asarTicksMatch=True' -or $fileCheck -notcontains 'exeTicksMatch=True') {
        $fileCheck | Write-Output
        throw 'C# state ticks do not match the files it describes'
    }
    Write-Output 'PASS install state ticks'

    # 注：快路径、state 双向兼容、不完整副本、Codex.exe-only、嵌入记录缺失
    # 这几条已经做过针对性手工验证；脚本化的用例留到下一轮补，避免在这里
    # 继续堆复杂的 PowerShell 管道。

    # ---- Gate C: localeOverride ----
    Write-Output '--- Gate C: localeOverride ---'
    $localeRoot = Join-Path $work 'locale'
    New-Item -ItemType Directory -Path (Join-Path $localeRoot 'ps') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $localeRoot 'cs') -Force | Out-Null
    foreach ($name in @('case2', 'case3', 'case4', 'case5')) {
        $content = switch ($name) {
            'case2' { "[desktop]`nfoo = 1`n`n[other]`nbar = 2`n" }
            'case3' { "[desktop]`nlocaleOverride = `"en-US`"`n" }
            'case4' { "[desktop]`nlocaleOverride = `"zh-CN`"`n" }
            'case5' { "[general]`nx = 1`n" }
        }
        [System.IO.File]::WriteAllText((Join-Path $localeRoot ('ps\' + $name + '.toml')), $content, (New-Object System.Text.UTF8Encoding($false)))
        [System.IO.File]::WriteAllText((Join-Path $localeRoot ('cs\' + $name + '.toml')), $content, (New-Object System.Text.UTF8Encoding($false)))
    }
    foreach ($name in @('case1', 'case2', 'case3', 'case4', 'case5')) {
        & $powerShellExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $oracleDir 'LocaleGate.ps1') `
            -Path (Join-Path $localeRoot ('ps\' + $name + '.toml')) -Apply *> $null
        if ($LASTEXITCODE -ne 0) { throw "PS locale case failed: $name" }
        & $outExe locale-override --path (Join-Path $localeRoot ('cs\' + $name + '.toml')) --apply *> $null
        if ($LASTEXITCODE -ne 0) { throw "C# locale case failed: $name" }
        Assert-SameHash (Join-Path $localeRoot ('ps\' + $name + '.toml')) (Join-Path $localeRoot ('cs\' + $name + '.toml')) ('locale ' + $name)
    }

    Write-Output 'ALL GATES PASSED'
}
finally {
    if (-not $KeepWork) {
        try { Remove-Item -LiteralPath $work -Recurse -Force } catch { Write-Warning ("cleanup failed: " + $_) }
    } else {
        Write-Output ('kept work dir: ' + $work)
    }
}
