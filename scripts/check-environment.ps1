[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile (Join-Path $repoRoot 'config\toolchain.psd1')

$required = @(
    $config.BlenderExe,
    $config.CitiesSkylinesDir,
    (Join-Path $config.CitiesSkylinesDir 'Cities_Data\Managed\Assembly-CSharp.dll'),
    (Join-Path $config.CitiesSkylinesDir 'Cities_Data\Managed\ColossalManaged.dll'),
    (Join-Path $config.CitiesSkylinesDir 'Cities_Data\Managed\ICities.dll'),
    (Join-Path $config.CitiesSkylinesDir 'Cities_Data\Managed\UnityEngine.dll'),
    $config.CitiesSkylinesDataDir,
    $config.MSBuildExe,
    (Join-Path $repoRoot 'references\RoadImporter\RoadImporter\Environment.cs'),
    (Join-Path $repoRoot 'references\CSUR\prefab\assetmaker.py')
)

$missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing.Count -gt 0) {
    $missing | ForEach-Object { Write-Error "Missing: $_" }
    exit 1
}

Write-Host 'Blender:'
& $config.BlenderExe --version | Select-Object -First 1
Write-Host 'MSBuild:'
& $config.MSBuildExe -version -nologo | Select-Object -First 1
Write-Host 'Cities: Skylines managed DLLs: OK'
Write-Host 'Git submodules: OK'

