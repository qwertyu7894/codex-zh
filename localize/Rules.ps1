#Requires -Version 5.1
<#
  跨版本补丁规则表。

  设计原则（这是本项目与其它汉化工具最大的区别）：

  1. **不写死压缩后的变量名。** 上一代工具用的是
         a?.get(`enable_i18n`,!1)
     而 v26.1007 里压缩变量名从 a 变成了 s，规则直接未命中。
     这里只锚定**语义不变的那一段**：`enable_i18n`,!1

  2. **必须等长替换。** 原地改写的前提是字节数不变。任何长度会变的
     规则（例如 `IDE` -> `SYSTEM`）一律不准进这张表，改由配置文件解决。

  3. **命中数要报出来。** 规则是宽松匹配，一旦命中数不是预期的 1，
     说明 App 结构变了，必须让人看见，而不是默默多改几处。
#>

function Get-CodexPatchRules {
    return @(
        @{
            Id       = 'enable-i18n-default'
            Scope    = 'webview/assets'
            Ext      = '.js'
            From     = '`enable_i18n`,!1'
            To       = '`enable_i18n`,!0'
            Expect   = 1
            Critical = $true
            Desc     = '界面多语言总开关：默认 false -> true（不开则界面永远是英文）'
        },
        @{
            Id       = 'enable-i18n-default-legacy'
            Scope    = '.vite/build'
            Ext      = '.js'
            From     = '`enable_i18n`,!1'
            To       = '`enable_i18n`,!0'
            Expect   = 1
            Critical = $false
            Desc     = '界面多语言总开关（旧版打包布局 .vite/build）'
        }
    )
}

function Get-AsarEntriesInScope {
    <#
      列出 asar 内某个目录下、指定扩展名的全部叶子条目。
      返回 [{ Path; Size; Offset }]
    #>
    param(
        [Parameter(Mandatory)]$HeaderInfo,
        [Parameter(Mandatory)][string]$Scope,
        [string]$Ext = '.js'
    )
    $prefix = ($Scope -replace '\\', '/').Trim('/')
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
        if ([string]::IsNullOrEmpty($Rel)) { return }
        if ($prefix -and -not ($Rel -like "$prefix/*")) { return }
        if ($Ext -and -not $Rel.EndsWith($Ext, [System.StringComparison]::OrdinalIgnoreCase)) { return }
        $unpacked = $Node.PSObject.Properties['unpacked']
        if ($unpacked -and $unpacked.Value) { return }
        [void]$script:_scopeEntries.Add(@{
            Path   = $Rel
            Size   = [long]$Node.PSObject.Properties['size'].Value
            Offset = [long]$Node.PSObject.Properties['offset'].Value
        })
    }
    $script:_scopeEntries = $out
    & $walk $HeaderInfo.Json ''
    return $out
}

function Test-AsarEntryContains {
    <# 在 asar 数据区里按字节查找 needle，命中即返回 $true（用于快速筛选候选文件） #>
    param(
        [Parameter(Mandatory)][string]$AsarPath,
        [Parameter(Mandatory)][long]$DataOffset,
        [Parameter(Mandatory)][long]$Offset,
        [Parameter(Mandatory)][long]$Size,
        [Parameter(Mandatory)][string]$Needle
    )
    $needleBytes = [System.Text.Encoding]::UTF8.GetBytes($Needle)
    if ($needleBytes.Length -eq 0 -or $Size -lt $needleBytes.Length) { return $false }

    $fs = [System.IO.File]::OpenRead($AsarPath)
    try {
        $fs.Seek($DataOffset + $Offset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $buf = New-Object byte[] $Size
        $read = 0
        while ($read -lt $Size) {
            $r = $fs.Read($buf, $read, [int]($Size - $read))
            if ($r -le 0) { break }
            $read += $r
        }
        $text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
        return $text.Contains($Needle)
    } finally { $fs.Dispose() }
}
