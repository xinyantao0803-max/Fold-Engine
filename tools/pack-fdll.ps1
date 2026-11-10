#requires -Version 5.1
<#
.SYNOPSIS
  Pack manifest.json + timeline.json into a .fdll module (optionally AES-256 encrypted).

.EXAMPLE
  .\tools\pack-fdll.ps1 -Manifest .\manifest.json -Timeline .\timeline.json -Output .\FoldEdge\Modules\demo.fdll

.EXAMPLE
  .\tools\pack-fdll.ps1 -Manifest .\manifest.json -Timeline .\timeline.json -Output .\FoldEdge\Modules\demo.fdll -Encrypt
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Manifest,
    [Parameter(Mandatory = $true)][string]$Timeline,
    [Parameter(Mandatory = $true)][string]$Output,
    [switch]$Encrypt
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Must match FoldEdge/Program.cs and FoldEngine/FoldCrypto.cs exactly.
$Magic = 'FOLDENC1'
$Key = [Text.Encoding]::UTF8.GetBytes('Fold-Engine-Preset-AES-Key-2026!')
$Iv = [Text.Encoding]::UTF8.GetBytes('FoldEngine-IV-01')

function New-ZipBytes {
    param([string[]]$Paths)

    $stream = New-Object System.IO.MemoryStream
    $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    foreach ($path in $Paths) {
        $resolved = (Resolve-Path $path).Path
        $entry = $zip.CreateEntry([System.IO.Path]::GetFileName($resolved))
        $target = $entry.Open()
        $bytes = [System.IO.File]::ReadAllBytes($resolved)
        $target.Write($bytes, 0, $bytes.Length)
        $target.Dispose()
    }
    $zip.Dispose()
    $result = $stream.ToArray()
    $stream.Dispose()
    return $result
}

function Protect-Bytes {
    param([byte[]]$Plain)

    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.Key = $Key
    $aes.IV = $Iv
    $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
    $encryptor = $aes.CreateEncryptor()
    $cipher = $encryptor.TransformFinalBlock($Plain, 0, $Plain.Length)
    $encryptor.Dispose()
    $aes.Dispose()

    $prefix = [Text.Encoding]::ASCII.GetBytes($Magic)
    $output = New-Object byte[] ($prefix.Length + $cipher.Length)
    [Array]::Copy($prefix, 0, $output, 0, $prefix.Length)
    [Array]::Copy($cipher, 0, $output, $prefix.Length, $cipher.Length)
    return $output
}

$zipBytes = New-ZipBytes @($Manifest, $Timeline)
$bytes = if ($Encrypt) { Protect-Bytes $zipBytes } else { $zipBytes }

$targetPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$dir = [System.IO.Path]::GetDirectoryName($targetPath)
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllBytes($targetPath, $bytes)

$mode = if ($Encrypt) { 'AES-256-CBC (FOLDENC1)' } else { 'plain ZIP' }
Write-Host "[pack-fdll] $targetPath  ($($bytes.Length) bytes, $mode)"
