[CmdletBinding()]
param([string]$PreviewPath = '')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile (Join-Path $repoRoot 'config\toolchain.psd1')
$managedDir = Join-Path $config.CitiesSkylinesDir 'Cities_Data\Managed'
if (-not $PreviewPath) { $PreviewPath = Join-Path $repoRoot 'build\smoke\runtime-preview' }
$project = Join-Path $repoRoot 'tests\RoadRuntimeHost.ContractSmoke\RoadRuntimeHost.ContractSmoke.csproj'

& $config.MSBuildExe $project /t:Build /p:Configuration=Release "/p:CitiesSkylinesManagedDir=$managedDir" /m /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Runtime contract smoke build failed with exit code $LASTEXITCODE" }
$exe = Join-Path $repoRoot 'tests\RoadRuntimeHost.ContractSmoke\bin\Release\RoadRuntimeHost.ContractSmoke.exe'
& $exe ([IO.Path]::GetFullPath($PreviewPath))
if ($LASTEXITCODE -ne 0) { throw "Runtime contract smoke failed with exit code $LASTEXITCODE" }
