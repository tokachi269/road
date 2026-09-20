[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$BuildDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile (Join-Path $repoRoot 'config\toolchain.psd1')
$sourceRoot = (Resolve-Path -LiteralPath $BuildDirectory).Path
$importsFile = Join-Path $sourceRoot 'imports.txt'
$sourceImport = Join-Path $sourceRoot 'import'
$sourceTextures = Join-Path $sourceRoot 'textures'

foreach ($required in @($importsFile, $sourceImport, $sourceTextures)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Missing generated input: $required"
    }
}

$destinationRoot = Join-Path $config.CitiesSkylinesDataDir 'RoadImporter'
$destinationImport = Join-Path $destinationRoot 'import'
$destinationTextures = Join-Path $destinationRoot 'textures'

if ($PSCmdlet.ShouldProcess($destinationRoot, "Stage RoadImporter input from $sourceRoot")) {
    New-Item -ItemType Directory -Force -Path $destinationImport | Out-Null
    New-Item -ItemType Directory -Force -Path $destinationTextures | Out-Null
    Copy-Item -LiteralPath $importsFile -Destination (Join-Path $destinationRoot 'imports.txt') -Force
    Get-ChildItem -LiteralPath $sourceImport -File | Copy-Item -Destination $destinationImport -Force
    Get-ChildItem -LiteralPath $sourceTextures -File | Copy-Item -Destination $destinationTextures -Force
    Write-Host "Staged: $destinationRoot"
}

