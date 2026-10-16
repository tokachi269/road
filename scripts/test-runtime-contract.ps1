[CmdletBinding()]
param([string]$PreviewPath = '')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile (Join-Path $repoRoot 'config\toolchain.psd1')
$managedDir = Join-Path $config.CitiesSkylinesDir 'Cities_Data\Managed'
if (-not $PreviewPath) { $PreviewPath = Join-Path $repoRoot 'build\smoke\runtime-preview' }
$project = Join-Path $repoRoot 'tests\RoadRuntimeHost.ContractSmoke\RoadRuntimeHost.ContractSmoke.csproj'

# The placement reader must consume real Blender output, not only a DTO fixture.
& (Join-Path $PSScriptRoot 'run-blender.ps1') -Script (Join-Path $repoRoot 'tests\blender_placement_smoke.py')
if ($LASTEXITCODE -ne 0) { throw 'Blender placement fixtures failed' }
$placementPreview = Join-Path $repoRoot 'build\smoke\placement-runtime'
& (Join-Path $PSScriptRoot 'test-road-game-contract.ps1') -PlacementPreviewPath $placementPreview
if ($LASTEXITCODE -ne 0) { throw 'Game-only separation contract failed' }

# Runtime reads real offline PNG output, never a second icon renderer.
& (Join-Path $PSScriptRoot 'generate-road-icons.ps1') -PreviewPath $PreviewPath -OutputPath (Join-Path $repoRoot 'build\smoke\road-icons')
if ($LASTEXITCODE -ne 0) { throw 'Offline road icon fixtures failed' }

& $config.MSBuildExe $project /t:Build /p:Configuration=Release "/p:CitiesSkylinesManagedDir=$managedDir" /m /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Runtime contract smoke build failed with exit code $LASTEXITCODE" }
$runtimeDll = Join-Path $repoRoot 'src\RoadRuntimeHost.Runtime\bin\Release\RoadRuntimeHost.Runtime.dll'
$runtimeReferences = ([Reflection.Assembly]::LoadFile($runtimeDll)).GetReferencedAssemblies() | ForEach-Object Name
$unsupported = @('System.ServiceModel.Web', 'System.Runtime.Serialization') | Where-Object { $runtimeReferences -contains $_ }
if ($unsupported) { throw "Runtime references assemblies unavailable or unusable in CS1 Mono: $($unsupported -join ', ')" }
$exe = Join-Path $repoRoot 'tests\RoadRuntimeHost.ContractSmoke\bin\Release\RoadRuntimeHost.ContractSmoke.exe'
& $exe ([IO.Path]::GetFullPath($PreviewPath)) $placementPreview
if ($LASTEXITCODE -ne 0) { throw "Runtime contract smoke failed with exit code $LASTEXITCODE" }
