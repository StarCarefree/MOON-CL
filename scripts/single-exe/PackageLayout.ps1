# Win32 version/icon resource and PE layout checks for the single-exe stub.
# The packaged exe must not grow a tail after the last section. SilverFox loaders
# are detected as a small PE plus a huge overlay.

function Fail-Layout([string]$Message) {
    if (Get-Command Throw-Single -ErrorAction SilentlyContinue) {
        Throw-Single $Message
    }
    throw "Publish-SingleExe: $Message"
}

function New-UnicodeZ([string]$Text) {
    return [System.Text.Encoding]::Unicode.GetBytes($Text + [char]0)
}

function Add-DwordPad([System.Collections.Generic.List[byte]]$Buf) {
    while (($Buf.Count % 4) -ne 0) { $Buf.Add(0) }
}

function Add-Bytes([System.Collections.Generic.List[byte]]$Target, $Source) {
    if ($null -eq $Source) { return }
    foreach ($item in $Source) { $Target.Add([byte]$item) }
}

function New-VerBlock([string]$Key, [int]$ValueLength, [int]$Type, [byte[]]$Value, [byte[]]$Children) {
    $keyBytes = New-UnicodeZ $Key
    $buf = New-Object System.Collections.Generic.List[byte]
    Add-Bytes $buf ([byte[]](0, 0, 0, 0, 0, 0))
    Add-Bytes $buf ($keyBytes)
    Add-DwordPad $buf
    if ($Value -and $Value.Length -gt 0) { Add-Bytes $buf ($Value) }
    Add-DwordPad $buf
    if ($Children -and $Children.Length -gt 0) { Add-Bytes $buf ($Children) }
    $len = [uint16]$buf.Count
    $hdr = [byte[]]::new(6)
    [System.BitConverter]::GetBytes($len).CopyTo($hdr, 0)
    [System.BitConverter]::GetBytes([uint16]$ValueLength).CopyTo($hdr, 2)
    [System.BitConverter]::GetBytes([uint16]$Type).CopyTo($hdr, 4)
    for ($i = 0; $i -lt 6; $i++) { $buf[$i] = $hdr[$i] }
    return $buf.ToArray()
}

function New-VerString([string]$Key, [string]$Value) {
    $chars = $Value + [char]0
    $bytes = [System.Text.Encoding]::Unicode.GetBytes($chars)
    return New-VerBlock $Key $chars.Length 1 $bytes $null
}

function Convert-VersionParts([string]$Version) {
    $parts = $Version.Split('.')
    $nums = @(0, 0, 0, 0)
    for ($i = 0; $i -lt [Math]::Min(4, $parts.Length); $i++) {
        $nums[$i] = [int]$parts[$i]
    }
    return $nums
}

function New-FixedInfo([string]$Version) {
    $n = Convert-VersionParts $Version
    $ms = ([uint32]$n[0] -shl 16) -bor [uint32]$n[1]
    $ls = ([uint32]$n[2] -shl 16) -bor [uint32]$n[3]
    $b = New-Object System.Collections.Generic.List[byte]
    Add-Bytes $b ([byte[]](0xBD, 0x04, 0xEF, 0xFE))
    Add-Bytes $b ([System.BitConverter]::GetBytes([uint32]0x00010000))
    Add-Bytes $b ([System.BitConverter]::GetBytes($ms))
    Add-Bytes $b ([System.BitConverter]::GetBytes($ls))
    Add-Bytes $b ([System.BitConverter]::GetBytes($ms))
    Add-Bytes $b ([System.BitConverter]::GetBytes($ls))
    Add-Bytes $b ([System.BitConverter]::GetBytes([uint32]0x3F))
    Add-Bytes $b ([System.BitConverter]::GetBytes([uint32]0))
    Add-Bytes $b ([System.BitConverter]::GetBytes([uint32]0x00040004))
    Add-Bytes $b ([System.BitConverter]::GetBytes([uint32]1))
    Add-Bytes $b ([System.BitConverter]::GetBytes([uint32]0))
    Add-Bytes $b ([System.BitConverter]::GetBytes([uint32]0))
    Add-Bytes $b ([System.BitConverter]::GetBytes([uint32]0))
    if ($b.Count -ne 52) { Fail-Layout "VS_FIXEDFILEINFO length $($b.Count)" }
    return $b.ToArray()
}

function New-VersionResourceBytes([string]$Version, [string]$Product) {
    $strings = New-Object System.Collections.Generic.List[byte]
    foreach ($pair in @(
            @('FileDescription', $Product),
            @('FileVersion', $Version),
            @('InternalName', 'PCL-MOON.exe'),
            @('LegalCopyright', 'Copyright'),
            @('OriginalFilename', 'PCL-MOON.exe'),
            @('ProductName', $Product),
            @('ProductVersion', $Version),
            @('CompanyName', $Product),
            @('Comments', 'Minecraft launcher')
        )) {
        Add-Bytes $strings ((New-VerString $pair[0] $pair[1]))
    }
    $table = New-VerBlock '040904B0' 0 1 $null $strings.ToArray()
    $stringFile = New-VerBlock 'StringFileInfo' 0 1 $null $table
    $translation = [byte[]](0x09, 0x04, 0xB0, 0x04)
    $var = New-VerBlock 'Translation' 4 0 $translation $null
    $varFile = New-VerBlock 'VarFileInfo' 0 1 $null $var
    $kids = New-Object System.Collections.Generic.List[byte]
    Add-Bytes $kids ($stringFile)
    Add-Bytes $kids ($varFile)
    return New-VerBlock 'VS_VERSION_INFO' 52 0 (New-FixedInfo $Version) $kids.ToArray()
}

function New-ResEntry([uint16]$Type, [uint16]$Name, [uint16]$Language, [byte[]]$Data) {
    $buf = New-Object System.Collections.Generic.List[byte]
    Add-Bytes $buf ([System.BitConverter]::GetBytes([uint32]$Data.Length))
    Add-Bytes $buf ([System.BitConverter]::GetBytes([uint32]32))
    Add-Bytes $buf ([byte[]](0xFF, 0xFF))
    Add-Bytes $buf ([System.BitConverter]::GetBytes($Type))
    Add-Bytes $buf ([byte[]](0xFF, 0xFF))
    Add-Bytes $buf ([System.BitConverter]::GetBytes($Name))
    Add-Bytes $buf ([System.BitConverter]::GetBytes([uint32]0))
    Add-Bytes $buf ([System.BitConverter]::GetBytes([uint16]0x0030))
    Add-Bytes $buf ([System.BitConverter]::GetBytes($Language))
    Add-Bytes $buf ([System.BitConverter]::GetBytes([uint32]0))
    Add-Bytes $buf ([System.BitConverter]::GetBytes([uint32]0))
    if ($buf.Count -ne 32) { Fail-Layout "resource header length $($buf.Count)" }
    Add-Bytes $buf ($Data)
    Add-DwordPad $buf
    return $buf.ToArray()
}

function Add-IconResources([System.Collections.Generic.List[byte]]$Out, [string]$IconPath) {
    if ([string]::IsNullOrWhiteSpace($IconPath) -or -not (Test-Path -LiteralPath $IconPath -PathType Leaf)) { return }
    $ico = [System.IO.File]::ReadAllBytes($IconPath)
    if ($ico.Length -lt 6) { return }
    if ([System.BitConverter]::ToUInt16($ico, 0) -ne 0 -or [System.BitConverter]::ToUInt16($ico, 2) -ne 1) { return }
    $count = [System.BitConverter]::ToUInt16($ico, 4)
    $group = New-Object System.Collections.Generic.List[byte]
    Add-Bytes $group ([byte[]](0, 0, 1, 0))
    Add-Bytes $group ([System.BitConverter]::GetBytes([uint16]$count))
    for ($i = 0; $i -lt $count; $i++) {
        $entry = 6 + ($i * 16)
        if (($entry + 16) -gt $ico.Length) { Fail-Layout "icon entry out of range" }
        $width = $ico[$entry]
        $height = $ico[$entry + 1]
        $colors = $ico[$entry + 2]
        $reserved = $ico[$entry + 3]
        $planes = [System.BitConverter]::ToUInt16($ico, $entry + 4)
        $bitCount = [System.BitConverter]::ToUInt16($ico, $entry + 6)
        $bytesInRes = [System.BitConverter]::ToUInt32($ico, $entry + 8)
        $imageOffset = [System.BitConverter]::ToInt32($ico, $entry + 12)
        if ($imageOffset -lt 0 -or ($imageOffset + $bytesInRes) -gt $ico.Length) { Fail-Layout "icon image out of range" }
        $image = New-Object byte[] $bytesInRes
        [System.Array]::Copy($ico, $imageOffset, $image, 0, $bytesInRes)
        $id = [uint16]($i + 1)
        Add-Bytes $Out ((New-ResEntry 3 $id 0x0409 $image))
        $group.Add($width)
        $group.Add($height)
        $group.Add($colors)
        $group.Add($reserved)
        Add-Bytes $group ([System.BitConverter]::GetBytes($planes))
        Add-Bytes $group ([System.BitConverter]::GetBytes($bitCount))
        Add-Bytes $group ([System.BitConverter]::GetBytes($bytesInRes))
        Add-Bytes $group ([System.BitConverter]::GetBytes($id))
    }
    Add-Bytes $Out ((New-ResEntry 14 1 0x0409 $group.ToArray()))
}

function Write-Win32Resource([string]$Path, [string]$Version, [string]$Product, [string]$IconPath, [string]$ManifestPath) {
    $out = New-Object System.Collections.Generic.List[byte]
    Add-IconResources $out $IconPath
    Add-Bytes $out ((New-ResEntry 16 1 0x0409 (New-VersionResourceBytes $Version $Product)))
    if (-not [string]::IsNullOrWhiteSpace($ManifestPath) -and (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        Add-Bytes $out ((New-ResEntry 24 1 0x0409 ([System.IO.File]::ReadAllBytes($ManifestPath))))
    }
    [System.IO.File]::WriteAllBytes($Path, $out.ToArray())
}

function Get-PeOverlayLength([string]$Path) {
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $br = [System.IO.BinaryReader]::new($fs)
        if ($br.ReadUInt16() -ne 0x5A4D) { Fail-Layout "不是 PE: $Path" }
        $null = $fs.Seek(0x3C, [System.IO.SeekOrigin]::Begin)
        $pe = $br.ReadInt32()
        $null = $fs.Seek($pe, [System.IO.SeekOrigin]::Begin)
        if ($br.ReadUInt32() -ne 0x4550) { Fail-Layout "不是 PE: $Path" }
        $null = $br.ReadUInt16()
        $sections = $br.ReadUInt16()
        $null = $fs.Seek($pe + 20, [System.IO.SeekOrigin]::Begin)
        $optSize = $br.ReadUInt16()
        $sectionStart = $pe + 24 + $optSize
        $end = [int64]0
        for ($i = 0; $i -lt $sections; $i++) {
            $null = $fs.Seek($sectionStart + ($i * 40) + 16, [System.IO.SeekOrigin]::Begin)
            $rawSize = $br.ReadUInt32()
            $rawPtr = $br.ReadUInt32()
            $sectionEnd = [int64]$rawPtr + [int64]$rawSize
            if ($sectionEnd -gt $end) { $end = $sectionEnd }
        }
        $overlay = $fs.Length - $end
        if ($overlay -lt 0) { return [int64]0 }
        return [int64]$overlay
    }
    finally { $fs.Dispose() }
}

function Assert-NoPeOverlay([string]$Path) {
    $overlay = Get-PeOverlayLength $Path
    if ($overlay -gt 512) {
        Fail-Layout "拒绝尾部附加载荷 overlay=$overlay。银狐规则会把小壳加大段尾部数据判成 Trojan.Win64.SilverFox。"
    }
    return $overlay
}

function Set-PeChecksum([string]$Path) {
    if (-not ('ImageHlpChecksum' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ImageHlpChecksum {
    [DllImport("imagehlp.dll", CharSet = CharSet.Unicode, EntryPoint = "MapFileAndCheckSumW")]
    public static extern uint MapFileAndCheckSum(string filename, out uint headerSum, out uint checkSum);
}
"@
    }
    $header = [uint32]0
    $sum = [uint32]0
    $rc = [ImageHlpChecksum]::MapFileAndCheckSum($Path, [ref]$header, [ref]$sum)
    if ($rc -ne 0) { Fail-Layout "MapFileAndCheckSum 失败: $rc" }
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $br = [System.IO.BinaryReader]::new($fs)
        $null = $fs.Seek(0x3C, [System.IO.SeekOrigin]::Begin)
        $pe = $br.ReadInt32()
        $checksumOffset = $pe + 24 + 64
    }
    finally { $fs.Dispose() }
    $patch = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $null = $patch.Seek($checksumOffset, [System.IO.SeekOrigin]::Begin)
        $bytes = [System.BitConverter]::GetBytes($sum)
        $patch.Write($bytes, 0, 4)
    }
    finally { $patch.Dispose() }
    $header2 = [uint32]0
    $sum2 = [uint32]0
    $rc2 = [ImageHlpChecksum]::MapFileAndCheckSum($Path, [ref]$header2, [ref]$sum2)
    if ($rc2 -ne 0 -or $header2 -ne $sum2) {
        Fail-Layout "PE checksum 写入后不一致 header=$header2 sum=$sum2"
    }
}
