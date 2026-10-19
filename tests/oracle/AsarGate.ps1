param(
    [Parameter(Mandatory)][string]$Mode,
    [string]$Asar,
    [string]$App,
    [string]$RelPath,
    [int]$RuleIndex = 0,
    [string]$Scope = 'webview/assets',
    [string]$Ext = '.js',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

function Resolve-AsarPath {
    if ($Asar) { return (Resolve-Path -LiteralPath $Asar).Path }
    if ($App) { return (Join-Path (Resolve-Path -LiteralPath $App).Path 'resources\app.asar') }
    throw '-Asar or -App is required'
}

function Get-RulePath {
    param($HeaderInfo, $Rule)
    $entries = Get-AsarEntriesInScope -HeaderInfo $HeaderInfo -Scope $Rule.Scope -Ext $Rule.Ext
    foreach ($e in $entries) {
        if (Test-AsarEntryContains -AsarPath $asarPath -DataOffset $HeaderInfo.DataOffset `
                                   -Offset $e.Offset -Size $e.Size -Needle $Rule.From) {
            return $e
        }
    }
    return $null
}

$asarPath = Resolve-AsarPath

switch ($Mode) {
    'inspect' {
        $info = Read-AsarHeaderInfo -AsarPath $asarPath
        $item = Get-Item -LiteralPath $asarPath
        Write-KeyValue 'asar' $asarPath
        Write-KeyValue 'length' $item.Length
        Write-KeyValue 'mtimeUtcTicks' $item.LastWriteTimeUtc.Ticks
        # Re-read the four u32 fields explicitly for the gate.
        $fs = [System.IO.File]::OpenRead($asarPath)
        try {
            $br = New-Object System.IO.BinaryReader($fs)
            Write-KeyValue 'u0' $br.ReadUInt32()
            Write-KeyValue 'u1' $br.ReadUInt32()
            Write-KeyValue 'u2' $br.ReadUInt32()
            Write-KeyValue 'u3' $br.ReadUInt32()
        } finally { $fs.Dispose() }
        Write-KeyValue 'jsonLen' $info.JsonLen
        Write-KeyValue 'dataOffset' $info.DataOffset
        Write-KeyValue 'headerHash' (Get-AsarHeaderHash -AsarPath $asarPath)
        $rules = Get-CodexPatchRules
        Write-KeyValue 'rulesSignature' (Get-RulesSignature -Rules $rules)
        foreach ($rule in $rules) {
            $candidates = Get-AsarEntriesInScope -HeaderInfo $info -Scope $rule.Scope -Ext $rule.Ext
            Write-KeyValue ('candidates.' + $rule.Id) $candidates.Count
        }
    }

    'list-entries' {
        $info = Read-AsarHeaderInfo -AsarPath $asarPath
        $entries = Get-AsarEntriesInScope -HeaderInfo $info -Scope $Scope -Ext $Ext
        foreach ($e in $entries) { Write-Output $e.Path }
    }

    'probe-rules' {
        $info = Read-AsarHeaderInfo -AsarPath $asarPath
        $rules = Get-CodexPatchRules
        foreach ($rule in $rules) {
            $entries = Get-AsarEntriesInScope -HeaderInfo $info -Scope $rule.Scope -Ext $rule.Ext
            Write-KeyValue ('rule.' + $rule.Id + '.candidates') $entries.Count
            foreach ($e in $entries) {
                if (Test-AsarEntryContains -AsarPath $asarPath -DataOffset $info.DataOffset `
                                           -Offset $e.Offset -Size $e.Size -Needle $rule.From) {
                    Write-KeyValue ('rule.' + $rule.Id + '.fromHit') $e.Path
                }
                if (Test-AsarEntryContains -AsarPath $asarPath -DataOffset $info.DataOffset `
                                           -Offset $e.Offset -Size $e.Size -Needle $rule.To) {
                    Write-KeyValue ('rule.' + $rule.Id + '.toHit') $e.Path
                }
                if ($rule.ContainsKey('AlreadyPatchedContext') -and
                    (Test-AsarEntryContains -AsarPath $asarPath -DataOffset $info.DataOffset `
                                            -Offset $e.Offset -Size $e.Size -Needle $rule.AlreadyPatchedContext)) {
                    Write-KeyValue ('rule.' + $rule.Id + '.contextHit') $e.Path
                }
            }
        }
    }

    'entry' {
        $info = Read-AsarHeaderInfo -AsarPath $asarPath
        $e = Get-AsarEntry -HeaderInfo $info -RelPath $RelPath
        if (-not $e) { Write-KeyValue 'found' 'false'; break }
        Write-KeyValue 'found' 'true'
        Write-KeyValue 'offset' ([long]$e.PSObject.Properties['offset'].Value)
        Write-KeyValue 'size' ([long]$e.PSObject.Properties['size'].Value)
        $unpacked = $e.PSObject.Properties['unpacked']
        Write-KeyValue 'unpacked' ([bool]($unpacked -and $unpacked.Value))
        $integrity = $e.PSObject.Properties['integrity']
        if ($integrity) {
            $hash = $integrity.Value.PSObject.Properties['hash']
            $blocks = $integrity.Value.PSObject.Properties['blocks']
            $bs = $integrity.Value.PSObject.Properties['blockSize']
            Write-KeyValue 'integrity.hasHash' ([bool]($hash -and $hash.Value))
            Write-KeyValue 'integrity.hash' $(if ($hash) { [string]$hash.Value } else { '' })
            Write-KeyValue 'integrity.blocks' $(if ($blocks) { @($blocks.Value).Count } else { 0 })
            Write-KeyValue 'integrity.blockSize' $(if ($bs) { [string]$bs.Value } else { '' })
        } else {
            Write-KeyValue 'integrity.hasHash' 'false'
            Write-KeyValue 'integrity.blocks' '0'
        }
    }

    'patch-one' {
        $info = Read-AsarHeaderInfo -AsarPath $asarPath
        $rules = Get-CodexPatchRules
        $rule = $rules[$RuleIndex]
        $e = Get-RulePath -HeaderInfo $info -Rule $rule
        if (-not $e) { Write-KeyValue 'status' 'no-from-hit'; break }
        Write-KeyValue 'path' $e.Path
        $r = Invoke-AsarInPlacePatch -AsarPath $asarPath -RelPath $e.Path `
                                     -Replacements @(@{ From = $rule.From; To = $rule.To; Desc = $rule.Desc }) `
                                     -Apply:$Apply
        Write-KeyValue 'status' $r.Status
        Write-KeyValue 'hits' $r.Hits
        Write-KeyValue 'integrityUpdated' $r.IntegrityUpdated
        Write-KeyValue 'applied' ([bool]$r.Applied)
    }

    'patch-pass' {
        $patch = Invoke-PatchPass -AsarPath $asarPath -Apply:$Apply
        Write-KeyValue 'criticalMiss' $patch.CriticalMiss
        Write-KeyValue 'dataOffset' $patch.DataOffset
        foreach ($res in $patch.Results) {
            Write-KeyValue ('result.' + $res.Id + '.status') $res.Status
            if ($res.ContainsKey('Hits')) { Write-KeyValue ('result.' + $res.Id + '.hits') $res.Hits }
            foreach ($rec in @($res.Entries)) {
                if ($rec) {
                    Write-KeyValue ('entry.' + $rec.id + '.path') $rec.path
                    Write-KeyValue ('entry.' + $rec.id + '.offset') $rec.offset
                    Write-KeyValue ('entry.' + $rec.id + '.size') $rec.size
                    Write-KeyValue ('entry.' + $rec.id + '.needle') $rec.needle
                    Write-KeyValue ('entry.' + $rec.id + '.context') $rec.context
                }
            }
        }
    }

    'embedded-status' {
        if (-not $App) { throw '-App is required' }
        $r = Update-EmbeddedAsarIntegrity -AppDir (Resolve-Path -LiteralPath $App).Path `
                                          -AsarPath $asarPath -Apply:$Apply
        Write-KeyValue 'status' $r.Status
        if ($r.ContainsKey('File')) { Write-KeyValue 'file' (Split-Path $r.File -Leaf) }
        if ($r.ContainsKey('Old')) { Write-KeyValue 'old' $r.Old }
        if ($r.ContainsKey('New')) { Write-KeyValue 'new' $r.New }
        if ($r.ContainsKey('Hash')) { Write-KeyValue 'hash' $r.Hash }
    }

    'header-hash' {
        Write-KeyValue 'headerHash' (Get-AsarHeaderHash -AsarPath $asarPath)
    }

    default { throw "unknown mode: $Mode" }
}
