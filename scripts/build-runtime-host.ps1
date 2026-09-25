[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile (Join-Path $repoRoot 'config\toolchain.psd1')
$managedDir = Join-Path $config.CitiesSkylinesDir 'Cities_Data\Managed'
$projects = @(
    (Join-Path $repoRoot 'src\RoadRuntimeHost.Loader\RoadRuntimeHost.Loader.csproj'),
    (Join-Path $repoRoot 'src\RoadRuntimeHost.Runtime\RoadRuntimeHost.Runtime.csproj')
)

foreach ($project in $projects) {
    & $config.MSBuildExe $project /t:Build "/p:Configuration=$Configuration" "/p:CitiesSkylinesManagedDir=$managedDir" /m /nologo /v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Runtime Host build failed for $project with exit code $LASTEXITCODE"
    }
}

$loader = Join-Path $repoRoot "src\RoadRuntimeHost.Loader\bin\$Configuration\RoadRuntimeHost.Loader.dll"
$runtime = Join-Path $repoRoot "src\RoadRuntimeHost.Runtime\bin\$Configuration\RoadRuntimeHost.Runtime.dll"
if (-not (Test-Path -LiteralPath $loader) -or -not (Test-Path -LiteralPath $runtime)) {
    throw 'Build completed without expected Runtime Host DLLs'
}
Write-Host "Built: $loader"
Write-Host "Built: $runtime"
