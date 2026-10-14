[CmdletBinding()]
param(
    [string]$PreviewPath = '',
    [string]$OverlayPath = ''
)

$ErrorActionPreference = 'Stop'
$iconRepo = Split-Path -Parent $PSScriptRoot
if (-not $PreviewPath) { $PreviewPath = Join-Path $iconRepo 'build\runtime-preview' }
$iconOutput = Join-Path ([IO.Path]::GetFullPath($PreviewPath)) 'icons'
& (Join-Path $PSScriptRoot 'build-runtime-host.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Runtime icon renderer build failed' }
$iconAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $iconRepo 'src\RoadRuntimeHost.Runtime\bin\Release\RoadRuntimeHost.Runtime.dll'))
$iconRender = $iconAssembly.GetType('RoadRuntimeHost.Runtime.RoadIconRaster', $true).GetMethod('Render')
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies @([Drawing.Bitmap].Assembly.Location, [Drawing.Color].Assembly.Location, 'System.Runtime', 'System.Runtime.InteropServices') -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class RoadIconBitmap {
    public static Bitmap Encode(byte[] rgba) {
        Bitmap image = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        BitmapData data = image.LockBits(new Rectangle(0,0,128,128), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte[] bgra = new byte[rgba.Length];
        for (int i=0; i<rgba.Length; i+=4) {
            bgra[i]=rgba[i+2]; bgra[i+1]=rgba[i+1]; bgra[i+2]=rgba[i]; bgra[i+3]=rgba[i+3];
        }
        Marshal.Copy(bgra,0,data.Scan0,bgra.Length); image.UnlockBits(data); return image;
    }
    public static byte[] Decode(Bitmap image) {
        if (image.Width!=128 || image.Height!=128) throw new ArgumentException("Overlay must be 128x128 PNG");
        byte[] rgba = new byte[128*128*4];
        for (int y=0; y<128; ++y) for (int x=0; x<128; ++x) {
            Color color=image.GetPixel(x,y); int p=(y*128+x)*4;
            rgba[p]=color.R; rgba[p+1]=color.G; rgba[p+2]=color.B; rgba[p+3]=color.A;
        }
        return rgba;
    }
}
'@

if (-not $OverlayPath -and (Test-Path -LiteralPath (Join-Path $iconRepo 'textures\toolbar\overlay.png'))) {
    $OverlayPath = Join-Path $iconRepo 'textures\toolbar\overlay.png'
}
if (-not $OverlayPath -and (Test-Path -LiteralPath (Join-Path $iconOutput 'overlay.png'))) {
    $OverlayPath = Join-Path $iconOutput 'overlay.png'
}
[byte[]]$iconOverlay = $null
if ($OverlayPath) {
    $iconOverlayFile = Get-Item -LiteralPath $OverlayPath
    if ($iconOverlayFile.Length -gt 1MB) { throw 'Overlay PNG exceeds 1 MiB' }
    $iconHeaderCheck = $iconAssembly.GetType('RoadRuntimeHost.Runtime.RoadIconRaster', $true).GetMethod('RequireOverlayPng')
    $iconHeaderCheck.Invoke($null, @(,[IO.File]::ReadAllBytes($iconOverlayFile.FullName))) | Out-Null
    $iconOverlayImage = [Drawing.Bitmap]::new($iconOverlayFile.FullName)
    try {
        if ($iconOverlayImage.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid) { throw 'Overlay must be PNG' }
        $iconOverlay = [RoadIconBitmap]::Decode($iconOverlayImage)
    } finally { $iconOverlayImage.Dispose() }
}
New-Item -ItemType Directory -Force -Path $iconOutput | Out-Null
$iconTemplate = Join-Path $iconOutput 'overlay-template.png'
if (-not (Test-Path -LiteralPath $iconTemplate)) {
    $iconBlank = [RoadIconBitmap]::Encode([byte[]]::new(128*128*4))
    try { $iconBlank.Save($iconTemplate, [Drawing.Imaging.ImageFormat]::Png) } finally { $iconBlank.Dispose() }
}
$iconPairs = @(@(0,1),@(0,2),@(0,3),@(0,4),@(1,1),@(2,2),@(3,3),@(4,4),@(1,2),@(1,3),@(1,4),@(2,3),@(2,4),@(3,4))
$iconSheet = [Drawing.Bitmap]::new(432,432)
$iconGraphics = [Drawing.Graphics]::FromImage($iconSheet)
try {
    $iconGraphics.Clear([Drawing.Color]::FromArgb(255, 13, 18, 24))
    $iconGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    for ($iconIndex=0; $iconIndex -lt $iconPairs.Count; ++$iconIndex) {
        $iconPair = $iconPairs[$iconIndex]
        [byte[]]$iconPixels = $iconRender.Invoke($null, @([int]$iconPair[0], [int]$iconPair[1], $iconOverlay))
        $iconImage = [RoadIconBitmap]::Encode($iconPixels)
        try {
            $iconName = '{0}-{1}.png' -f $iconPair[0], $iconPair[1]
            $iconImage.Save((Join-Path $iconOutput $iconName), [Drawing.Imaging.ImageFormat]::Png)
            $iconGraphics.DrawImage($iconImage, [Drawing.Rectangle]::new(6+108*($iconIndex%4),6+108*[Math]::Floor($iconIndex/4),96,96))
        } finally { $iconImage.Dispose() }
    }
    $iconSheet.Save((Join-Path $iconOutput 'contact-sheet.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally { $iconGraphics.Dispose(); $iconSheet.Dispose() }
if ($OverlayPath -and [IO.Path]::GetFullPath($OverlayPath) -ne (Join-Path $iconOutput 'overlay.png')) {
    Copy-Item -LiteralPath $OverlayPath -Destination (Join-Path $iconOutput 'overlay.png') -Force
}
Write-Host "ROAD_ICONS_EXPORTED count=14 size=128 overlay=$([bool]$iconOverlay) output=$iconOutput"
