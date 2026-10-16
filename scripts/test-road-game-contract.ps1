[CmdletBinding()]
param([string]$PlacementPreviewPath = '')

$ErrorActionPreference = 'Stop'
$gameRepo = Split-Path -Parent $PSScriptRoot
$gameConfig = Import-PowerShellDataFile (Join-Path $gameRepo 'config\toolchain.psd1')
$gameManaged = Join-Path $gameConfig.CitiesSkylinesDir 'Cities_Data\Managed'
$gameProject = Join-Path $gameRepo 'tests\RoadGame.ContractSmoke\RoadGame.ContractSmoke.csproj'
& $gameConfig.MSBuildExe $gameProject /t:Build /p:Configuration=Release "/p:CitiesSkylinesManagedDir=$gameManaged" /m:1 /nr:false /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Game-only source build failed; preview Host must not be a dependency' }
$gameExe = Join-Path $gameRepo 'tests\RoadGame.ContractSmoke\bin\Release\RoadGame.ContractSmoke.exe'
if ($PlacementPreviewPath) { & $gameExe ([IO.Path]::GetFullPath($PlacementPreviewPath)) }
else { & $gameExe }
if ($LASTEXITCODE -ne 0) { throw 'Game-only contract failed' }
