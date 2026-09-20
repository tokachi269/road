[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile (Join-Path $repoRoot 'config\toolchain.psd1')
$project = Join-Path $repoRoot 'src\RoadImporter\RoadImporter.csproj'
$managedDir = Join-Path $config.CitiesSkylinesDir 'Cities_Data\Managed'

& $config.MSBuildExe $project /t:Build "/p:Configuration=$Configuration" "/p:CitiesSkylinesManagedDir=$managedDir" /m /nologo /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "RoadImporter build failed with exit code $LASTEXITCODE"
}

$dll = Join-Path $repoRoot "src\RoadImporter\bin\$Configuration\RoadImporter.dll"
if (-not (Test-Path -LiteralPath $dll)) {
    throw "Build completed without expected DLL: $dll"
}
Write-Host "Built: $dll"

