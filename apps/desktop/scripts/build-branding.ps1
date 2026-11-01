# Regenerate Windows assets from the repository's approved logo. No external tools.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$source = Join-Path $repo 'assets/branding/devtunnel-toolkit-logo'
$destination = Join-Path $PSScriptRoot '../packaging/branding'
[IO.Directory]::CreateDirectory($destination) | Out-Null

function New-LogoBitmap([Drawing.Image]$Logo, [int]$Width, [int]$Height, [int]$LogoSize, [bool]$Transparent) {
    $bitmap = New-Object Drawing.Bitmap($Width, $Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        if ($Transparent) { $graphics.Clear([Drawing.Color]::Transparent) }
        else { $graphics.Clear([Drawing.Color]::FromArgb(16, 31, 48)) }
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($Logo, [int](($Width - $LogoSize) / 2), [int](($Height - $LogoSize) / 2), $LogoSize, $LogoSize)
    }
    finally { $graphics.Dispose() }
    return $bitmap
}

$iconLogo = [Drawing.Image]::FromFile((Join-Path $source 'logo-icon-background.png'))
$darkLogo = [Drawing.Image]::FromFile((Join-Path $source 'logo-icon-dark.png'))
try {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = @()
    foreach ($size in $sizes) {
        $bitmap = New-LogoBitmap $iconLogo $size $size $size $true
        $stream = New-Object IO.MemoryStream
        try {
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $images += ,$stream.ToArray()
        }
        finally { $stream.Dispose(); $bitmap.Dispose() }
    }
    $file = [IO.File]::Create((Join-Path $destination 'app.ico'))
    $writer = New-Object IO.BinaryWriter($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
    }
    finally { $writer.Dispose(); $file.Dispose() }
    foreach ($layout in @(@('installer-panel.bmp', 328, 628, 256), @('installer-small.bmp', 110, 110, 94))) {
        $bitmap = New-LogoBitmap $darkLogo $layout[1] $layout[2] $layout[3] $false
        try { $bitmap.Save((Join-Path $destination $layout[0]), [Drawing.Imaging.ImageFormat]::Bmp) }
        finally { $bitmap.Dispose() }
    }
}
finally { $iconLogo.Dispose(); $darkLogo.Dispose() }
Write-Output 'Windows branding assets generated from the approved project logo.'
