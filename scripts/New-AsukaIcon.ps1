[CmdletBinding()]
param(
    [string] $Source = (Join-Path $PSScriptRoot '..\src\Asuka.App\Assets\Akame.png'),
    [string] $Destination = (Join-Path $PSScriptRoot '..\src\Asuka.App\Assets\Asuka.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$destinationDirectory = Split-Path -Parent $destinationPath
if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) { throw "Destination directory does not exist: $destinationDirectory" }

# A DIB-backed ICO is used deliberately: every entry is an uncompressed 32-bit
# BGRA bitmap, including the small shell sizes where PNG-backed ICO entries are
# inconsistently decoded by older tooling.
$sizes = [int[]](16, 24, 32, 48, 64, 128, 256)
$sourceImage = [System.Drawing.Image]::FromFile($sourcePath)
try {
    $side = [Math]::Min($sourceImage.Width, $sourceImage.Height)
    $sourceX = [int][Math]::Floor(($sourceImage.Width - $side) / 2.0)
    $sourceY = [int][Math]::Floor(($sourceImage.Height - $side) / 2.0)
    $payloads = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($sourceImage, [System.Drawing.Rectangle]::new(0, 0, $size, $size), $sourceX, $sourceY, $side, $side, [System.Drawing.GraphicsUnit]::Pixel)
            } finally { $graphics.Dispose() }
            $stride = $size * 4
            $xor = [byte[]]::new($stride * $size)
            $bits = $bitmap.LockBits([System.Drawing.Rectangle]::new(0, 0, $size, $size), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                for ($row = 0; $row -lt $size; $row++) {
                    [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($bits.Scan0.ToInt64() + (($size - 1 - $row) * $bits.Stride)), $xor, $row * $stride, $stride)
                }
            } finally { $bitmap.UnlockBits($bits) }
            $andMaskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
            $payload = [byte[]]::new(40 + $xor.Length + ($andMaskStride * $size))
            [BitConverter]::GetBytes(40).CopyTo($payload, 0)
            [BitConverter]::GetBytes($size).CopyTo($payload, 4)
            [BitConverter]::GetBytes($size * 2).CopyTo($payload, 8)
            [BitConverter]::GetBytes([int16]1).CopyTo($payload, 12)
            [BitConverter]::GetBytes([int16]32).CopyTo($payload, 14)
            $xor.CopyTo($payload, 40)
            $payloads.Add($payload)
        } finally { $bitmap.Dispose() }
    }
    $stream = [System.IO.File]::Open($destinationPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
            $offset = 6 + (16 * $sizes.Count)
            for ($index = 0; $index -lt $sizes.Count; $index++) {
                $size = $sizes[$index]
                $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size }))); $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size })))
                $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
                $writer.Write([uint32]$payloads[$index].Length); $writer.Write([uint32]$offset); $offset += $payloads[$index].Length
            }
            foreach ($payload in $payloads) { $writer.Write($payload) }
        } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
} finally { $sourceImage.Dispose() }
Write-Output "Created 32-bit multi-size icon: $destinationPath"
