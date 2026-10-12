#Requires -Version 5.1
<#
  asar 原地改写核心。

  asar 文件布局：
      [u32 = 4][u32 jsonLen+8][u32 jsonLen+4][u32 jsonLen][ JSON 头部 ][ 数据区 ]
      数据区中各文件按各自 offset 顺序紧密排列。

  本模块只做一件事：把 asar 内某个文件的字节**原地**换掉。
  前提是替换前后字节数完全一致 —— 这样：
      · 数据区里其他文件的 offset 不变
      · 头部 JSON 长度不变（dataOffset 不变）
      · 无需解包、无需重打包
  替换后会同步刷新该文件在头部里的 SHA-256（同样 64 位十六进制，长度不变）。

  设计约束：纯 .NET，PowerShell 5.1 兼容，零第三方依赖，不联网。
#>

function Read-AsarHeaderInfo {
    param([Parameter(Mandatory)][string]$AsarPath)

    $fs = [System.IO.File]::OpenRead($AsarPath)
    try {
        if ($fs.Length -lt 16) { throw "asar 文件过小: $AsarPath" }
        $br = New-Object System.IO.BinaryReader($fs)
        $u0 = $br.ReadUInt32()   # 恒为 4
        $u1 = $br.ReadUInt32()   # jsonLen + 8
        $u2 = $br.ReadUInt32()   # jsonLen + 4
        $u3 = $br.ReadUInt32()   # jsonLen

        # 头部布局：
        #   [0..3]   = 恒为 4
        #   [4..7]   = u1 = 头部 pickle 总长度   -> 数据区起点 = 8 + u1
        #   [8..11]  = u2 = pickle 负载长度（JSON + 4 字节串长度字段 + 对齐填充）
        #   [12..15] = u3 = JSON 字节数
        #   [16..]   = JSON
        # 注意数据区不是紧跟在 JSON 后面：中间有 4 字节对齐填充，
        # 所以必须用 8+u1，不能想当然用 16+u3。
        if ($u0 -ne 4) { throw "asar 头起始字段异常：u0=$u0" }
        if ($u1 -ne ($u2 + 4)) { throw "asar 头部长度字段不自洽：u1=$u1 u2=$u2" }
        if ($u3 -le 0 -or $u3 -gt 512MB -or $u3 -gt $u2) { throw "asar JSON 长度异常：u3=$u3 u2=$u2" }

        $fs.Seek(16, [System.IO.SeekOrigin]::Begin) | Out-Null
        $jsonBytes = $br.ReadBytes([int]$u3)
        $json = [System.Text.Encoding]::UTF8.GetString($jsonBytes)

        return @{
            AsarPath    = $AsarPath
            Json        = $json | ConvertFrom-Json
            JsonLen     = [int]$u3
            JsonOffset  = 16
            DataOffset  = [long]8 + [long]$u1
        }
    } finally { $fs.Dispose() }
}

function Get-AsarEntry {
    <#
      按 asar 内的相对路径取条目节点，例如 'webview/assets/index-abc.js'。
      路径分隔符统一用 '/'（asar 头部的键就是 '/' 分隔的层级）。
    #>
    param(
        [Parameter(Mandatory)]$HeaderInfo,
        [Parameter(Mandatory)][string]$RelPath
    )
    $parts = $RelPath -split '[\\/]' | Where-Object { $_ -ne '' }
    $node = $HeaderInfo.Json
    foreach ($p in $parts) {
        $files = $node.PSObject.Properties['files']
        if (-not $files) { return $null }
        $next = $files.Value.PSObject.Properties[$p]
        if (-not $next) { return $null }
        $node = $next.Value
    }
    return $node
}

function Get-AsarAllEntries {
    <# 返回全部叶子条目：[{ Path; Node }]，Path 用 '/' 分隔 #>
    param([Parameter(Mandatory)]$HeaderInfo)

    $out = New-Object System.Collections.ArrayList
    $walk = {
        param($Node, [string]$Rel)
        $files = $Node.PSObject.Properties['files']
        if ($files) {
            foreach ($p in $files.Value.PSObject.Properties) {
                $child = if ([string]::IsNullOrEmpty($Rel)) { $p.Name } else { "$Rel/$($p.Name)" }
                & $walk $p.Value $child
            }
            return
        }
        [void]$script:_asarEntries.Add(@{ Path = $Rel; Node = $Node })
    }
    $script:_asarEntries = $out
    & $walk $HeaderInfo.Json ''
    return $out
}

function Get-Sha256Hex {
    param([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($Bytes)) -replace '-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

function Update-AsarEntryIntegrity {
    <#
      头部区域内的完整性哈希是 64 位十六进制，与原值等长，
      所以可以安全地"原地替换字节"，不动 JSON 结构、不改头部长度。
      返回替换发生的次数。
    #>
    param(
        [Parameter(Mandatory)][string]$AsarPath,
        [Parameter(Mandatory)][byte[]]$NewBytes,
        $Entry,
        [Parameter(Mandatory)][byte[]]$OldHashInFile
    )
    $integrity = $Entry.PSObject.Properties['integrity']
    if (-not $integrity) { return 0 }

    $node = $integrity.Value
    $oldHashes = New-Object System.Collections.Generic.List[string]
    $wholeHash = $node.PSObject.Properties['hash']
    if ($wholeHash -and $wholeHash.Value) { $oldHashes.Add([string]$wholeHash.Value) }
    $blocks = $node.PSObject.Properties['blocks']
    if ($blocks -and $blocks.Value) {
        foreach ($b in $blocks.Value) { if ($b) { $oldHashes.Add([string]$b) } }
    }
    if ($oldHashes.Count -eq 0) { return 0 }

    # 按 blockSize 计算新哈希（blockSize 缺省视为整文件一块）
    $blockSize = 4194304
    $bsProp = $node.PSObject.Properties['blockSize']
    if ($bsProp -and $bsProp.Value) { $blockSize = [int]$bsProp.Value }

    $newWhole = Get-Sha256Hex -Bytes $NewBytes
    $newBlocks = New-Object System.Collections.Generic.List[string]
    $pos = 0
    while ($pos -lt $NewBytes.Length) {
        $len = [Math]::Min($blockSize, $NewBytes.Length - $pos)
        $chunk = New-Object byte[] $len
        [Array]::Copy($NewBytes, $pos, $chunk, 0, $len)
        $newBlocks.Add((Get-Sha256Hex -Bytes $chunk))
        $pos += $len
    }
    if ($newBlocks.Count -eq 0) { $newBlocks.Add($newWhole) }

    # 头部字节区间
    $fs = [System.IO.File]::Open($AsarPath, 'Open', 'ReadWrite')
    try {
        $headerStart = 16
        $headerEnd   = 16 + $OldHashInFile.Length   # 占位，下面用真实头部长度覆盖
        $headerEnd   = $null
        $headerLen = 0
        $fs.Seek(12, [System.IO.SeekOrigin]::Begin) | Out-Null
        $tmp = New-Object byte[] 4
        [void]$fs.Read($tmp, 0, 4)
        $headerLen = [BitConverter]::ToUInt32($tmp, 0)
        $fs.Seek(16, [System.IO.SeekOrigin]::Begin) | Out-Null
        $head = New-Object byte[] $headerLen
        [void]$fs.Read($head, 0, $headerLen)

        $replaced = 0
        # 整文件哈希：把旧串换成新串（头部内所有出现处一并替换）
        $i = 0
        while ($i -lt $oldHashes.Count) {
            $oldHex = $oldHashes[$i]
            $newHex = if ($i -eq 0) { $newWhole } else { $newBlocks[[Math]::Min($i - 1, $newBlocks.Count - 1)] }
            if ($oldHex.Length -eq 64 -and $newHex -and $oldHex -ne $newHex) {
                $oldB = [System.Text.Encoding]::ASCII.GetBytes($oldHex)
                $newB = [System.Text.Encoding]::ASCII.GetBytes($newHex)
                $idx = 0
                while ($true) {
                    $hit = -1
                    for ($k = $idx; $k -le ($head.Length - $oldB.Length); $k++) {
                        $same = $true
                        for ($m = 0; $m -lt $oldB.Length; $m++) {
                            if ($head[$k + $m] -ne $oldB[$m]) { $same = $false; break }
                        }
                        if ($same) { $hit = $k; break }
                    }
                    if ($hit -lt 0) { break }
                    [Array]::Copy($newB, 0, $head, $hit, $newB.Length)
                    $replaced++
                    $idx = $hit + $newB.Length
                }
            }
            $i++
        }

        if ($replaced -gt 0) {
            $fs.Seek(16, [System.IO.SeekOrigin]::Begin) | Out-Null
            $fs.Write($head, 0, $head.Length)
            $fs.Flush()
        }
        return $replaced
    } finally { $fs.Dispose() }
}

function Invoke-AsarInPlacePatch {
    <#
      对 asar 内单个文件做同长度字节替换。
        $AsarPath   目标 asar
        $RelPath    asar 内相对路径（'/' 分隔）
        $Replacements  @( @{ From = '...'; To = '...'; Desc = '...' } )
        $Apply      $false 时只检测不写入
      返回 @{ Status; Hits; BytesRewritten; IntegrityUpdated; Details }
        Status: 'ok' | 'miss' | 'length-mismatch' | 'not-found' | 'unpacked'
    #>
    param(
        [Parameter(Mandatory)][string]$AsarPath,
        [Parameter(Mandatory)][string]$RelPath,
        [Parameter(Mandatory)][array]$Replacements,
        [switch]$Apply
    )

    $info = Read-AsarHeaderInfo -AsarPath $AsarPath
    $entry = Get-AsarEntry -HeaderInfo $info -RelPath $RelPath
    if (-not $entry) { return @{ Status = 'not-found'; Hits = 0; BytesRewritten = 0; IntegrityUpdated = 0 } }

    if ($entry.PSObject.Properties['unpacked'] -and $entry.PSObject.Properties['unpacked'].Value) {
        return @{ Status = 'unpacked'; Hits = 0; BytesRewritten = 0; IntegrityUpdated = 0 }
    }

    $offset = [long]$entry.PSObject.Properties['offset'].Value
    $size   = [long]$entry.PSObject.Properties['size'].Value

    # 只读取字节：用只读句柄。DryRun 时目标常见于只读目录（WindowsApps），
    # 用 ReadWrite 打开会直接 UnauthorizedAccessException。
    $fs = [System.IO.File]::Open($AsarPath, 'Open', 'Read')
    try {
        $fs.Seek($info.DataOffset + $offset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $bytes = New-Object byte[] $size
        $read = 0
        while ($read -lt $size) {
            $r = $fs.Read($bytes, $read, [int]($size - $read))
            if ($r -le 0) { throw "读取 asar 数据失败 @ $RelPath" }
            $read += $r
        }
    } finally { $fs.Dispose() }

    $text  = [System.Text.Encoding]::UTF8.GetString($bytes)
    $total = 0
    $details = New-Object System.Collections.ArrayList

    foreach ($rule in $Replacements) {
        $from = [string]$rule.From
        $to   = [string]$rule.To
        if ([string]::IsNullOrEmpty($from)) { continue }

        $n = 0
        $idx = 0
        while ($true) {
            $j = $text.IndexOf($from, $idx, [System.StringComparison]::Ordinal)
            if ($j -lt 0) { break }
            $n++
            $idx = $j + $from.Length
        }

        [void]$details.Add(@{ Pattern = $from; Desc = $rule.Desc; Hits = $n })

        if ($n -gt 0) {
            if ($to.Length -ne $from.Length) {
                return @{ Status = 'length-mismatch'; Hits = 0; BytesRewritten = 0; IntegrityUpdated = 0; Details = $details }
            }
            $text = $text.Replace($from, $to)
            $total += $n
        }
    }

    if ($total -eq 0) {
        return @{ Status = 'miss'; Hits = 0; BytesRewritten = 0; IntegrityUpdated = 0; Details = $details }
    }

    $newBytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    if ($newBytes.Length -ne $size) {
        return @{ Status = 'length-mismatch'; Hits = $total; BytesRewritten = 0; IntegrityUpdated = 0; Details = $details }
    }

    if (-not $Apply) {
        return @{ Status = 'ok'; Hits = $total; BytesRewritten = 0; IntegrityUpdated = 0; Applied = $false; Details = $details }
    }

    $fs = [System.IO.File]::Open($AsarPath, 'Open', 'ReadWrite')
    try {
        $fs.Seek($info.DataOffset + $offset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $fs.Write($newBytes, 0, $newBytes.Length)
        $fs.Flush()
    } finally { $fs.Dispose() }

    $intUpdated = Update-AsarEntryIntegrity -AsarPath $AsarPath -NewBytes $newBytes -Entry $entry -OldHashInFile $bytes

    return @{
        Status           = 'ok'
        Hits             = $total
        BytesRewritten   = $total
        IntegrityUpdated = $intUpdated
        Applied          = $true
        Details          = $details
    }
}

function Get-AsarHeaderHash {
    <#
      Electron 的嵌入式完整性校验（fuse: EnableEmbeddedAsarIntegrityValidation）
      比对的是 **asar 头部 JSON 区** 的 SHA-256，不是整个文件。
      头部里装着每个文件的 per-file 哈希，所以头部一改，这个值就必须跟着改。
    #>
    param([Parameter(Mandatory)][string]$AsarPath)

    $info = Read-AsarHeaderInfo -AsarPath $AsarPath
    $fs = [System.IO.File]::Open($AsarPath, 'Open', 'Read')
    try {
        $fs.Seek(16, [System.IO.SeekOrigin]::Begin) | Out-Null
        $buf = New-Object byte[] $info.JsonLen
        $read = 0
        while ($read -lt $buf.Length) {
            $r = $fs.Read($buf, $read, $buf.Length - $read)
            if ($r -le 0) { throw "读取 asar 头部失败" }
            $read += $r
        }
        return Get-Sha256Hex -Bytes $buf
    } finally { $fs.Dispose() }
}

function Update-EmbeddedAsarIntegrity {
    <#
      把 $AsarPath 当前的头部哈希，同步写进 app 目录下二进制的嵌入记录里。

      二进制里的形态（ASCII，位于自定义区段，紧跟 IEND 之后）：
        [{"file":"resources\\app.asar","alg":"SHA256","value":"<64 hex>"}]

      新旧值都是 64 位十六进制 -> 等长 -> 原地替换。
      本函数是**幂等**的：值已经正确时不做任何写入。
    #>
    param(
        [Parameter(Mandatory)][string]$AppDir,
        [Parameter(Mandatory)][string]$AsarPath,
        [switch]$Apply
    )

    $newHash = Get-AsarHeaderHash -AsarPath $AsarPath
    # 二进制里是 ASCII 的 JSON 记录，形如：
    #   [{"file":"resources\\app.asar","alg":"SHA256","value":"<64 hex>"}]
    # 注意 JSON 里路径是**两个**反斜杠。这里不锚定具体路径，
    # 只认记录开头 + value 字段，用 app.asar 关键字做校验，避免路径写法变化导致失配。
    $recordMarker = '[{"file":'
    $valueMarker  = '"value":"'

    # 只扫 app 目录根下的 exe/dll。按名字排序，ChatGPT.exe 排在最前，
    # 通常第一个就命中，不必去读 329MB 的 chrome.dll。
    $candidates = @(Get-ChildItem -LiteralPath $AppDir -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Extension -in @('.exe', '.dll') } |
                    Sort-Object Name)

    foreach ($c in $candidates) {
        $bytes = [System.IO.File]::ReadAllBytes($c.FullName)
        $text = [System.Text.Encoding]::ASCII.GetString($bytes)

        $recPos = $text.IndexOf($recordMarker, [System.StringComparison]::Ordinal)
        if ($recPos -lt 0) { continue }

        $valPos = $text.IndexOf($valueMarker, $recPos, [System.StringComparison]::Ordinal)
        if ($valPos -lt 0 -or ($valPos - $recPos) -gt 512) { continue }

        # 必须是 app.asar 的记录，别误改别的文件
        if ($text.IndexOf('app.asar', $recPos, [System.StringComparison]::Ordinal) -lt 0 -or
            $text.IndexOf('app.asar', $recPos, [System.StringComparison]::Ordinal) -gt $valPos) { continue }

        $hashStart = $valPos + $valueMarker.Length
        if ($hashStart + 64 -gt $text.Length) { continue }
        $oldHash = $text.Substring($hashStart, 64)
        if (-not ($oldHash -match '^[0-9a-fA-F]{64}$')) { continue }

        if ($oldHash -eq $newHash) {
            return @{ Status = 'already-ok'; File = $c.FullName; Hash = $newHash }
        }
        if (-not $Apply) {
            return @{ Status = 'needs-update'; File = $c.FullName; Old = $oldHash; New = $newHash }
        }

        $newBytes = [System.Text.Encoding]::ASCII.GetBytes($newHash)
        [Array]::Copy($newBytes, 0, $bytes, $hashStart, 64)
        [System.IO.File]::WriteAllBytes($c.FullName, $bytes)
        return @{ Status = 'updated'; File = $c.FullName; Old = $oldHash; New = $newHash }
    }

    return @{ Status = 'not-found' }
}
