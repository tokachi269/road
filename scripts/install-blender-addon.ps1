[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile (Join-Path $repoRoot 'config\toolchain.psd1')
$source = Join-Path $repoRoot 'blender_addon\road_builder'
$addonsDirectory = Join-Path $env:APPDATA 'Blender Foundation\Blender\5.1\scripts\addons'
$destination = Join-Path $addonsDirectory 'road_builder'

if (-not (Test-Path -LiteralPath $source)) {
    throw "Add-on source not found: $source"
}

New-Item -ItemType Directory -Force -Path $addonsDirectory | Out-Null
if (Test-Path -LiteralPath $destination) {
    $item = Get-Item -LiteralPath $destination -Force
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Destination already exists and is not a development link: $destination"
    }
} else {
    New-Item -ItemType Junction -Path $destination -Target $source | Out-Null
}

& $config.BlenderExe --background --factory-startup --python-expr "import bpy, addon_utils; bpy.ops.preferences.addon_enable(module='road_builder'); assert addon_utils.check('road_builder')[1], 'road_builder did not register'; bpy.ops.wm.save_userpref()"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to enable Blender add-on (exit $LASTEXITCODE)"
}
Write-Host "Enabled Blender add-on: $destination"
