[CmdletBinding()]
param(
    [string]$PreviewPath = '',
    [string]$OverlayPath = '',
    [string]$OutputPath = '',
    [string]$ConfigPath = ''
)

$ErrorActionPreference = 'Stop'
$iconRepo = Split-Path -Parent $PSScriptRoot
if (-not $PreviewPath) { $PreviewPath = Join-Path $iconRepo 'build\runtime-preview' }
if (-not $OutputPath) { $OutputPath = Join-Path $iconRepo 'build\road-icons' }
if (-not $ConfigPath) { $ConfigPath = Join-Path $iconRepo 'textures\toolbar\icons.json' }
$iconArguments = @((Join-Path $iconRepo 'tools\generate_road_icons.py'), '--output', $OutputPath,
    '--config', $ConfigPath, '--preview-path', $PreviewPath)
if ($OverlayPath) { $iconArguments += @('--overlay', $OverlayPath) }
& python @iconArguments
if ($LASTEXITCODE -ne 0) { throw 'Offline road icon generation failed (Python + Pillow required)' }
