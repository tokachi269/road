[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile (Join-Path $repoRoot 'config\toolchain.psd1')
$source = Join-Path $repoRoot "src\RoadImporter\bin\$Configuration\RoadImporter.dll"
$destinationDirectory = Join-Path $config.CitiesSkylinesDataDir 'Addons\Mods\RoadImporter'
$destination = Join-Path $destinationDirectory 'RoadImporter.dll'

if (-not (Test-Path -LiteralPath $source)) {
    throw "Build first: $source"
}

if ($PSCmdlet.ShouldProcess($destination, 'Install RoadImporter')) {
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
    Write-Host "Installed: $destination"
}

