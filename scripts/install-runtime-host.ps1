[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$PreviewPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile (Join-Path $repoRoot 'config\toolchain.psd1')
$stageScript = Join-Path $PSScriptRoot 'stage-runtime-host.ps1'
& $stageScript -Configuration $Configuration -PreviewPath $PreviewPath
if ($LASTEXITCODE -ne 0) { throw "Runtime Host staging failed with exit code $LASTEXITCODE" }

$source = Join-Path $repoRoot 'build\runtime-host'
$target = Join-Path $config.CitiesSkylinesDataDir 'Addons\Mods\RoadRuntimeHost'
$dataRoot = [IO.Path]::GetFullPath($config.CitiesSkylinesDataDir).TrimEnd('\') + '\'
$target = [IO.Path]::GetFullPath($target)
if (-not ($target + '\').StartsWith($dataRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install outside CitiesSkylinesDataDir: $target"
}
$runtimeTarget = Join-Path $target 'runtime'
New-Item -ItemType Directory -Force -Path $runtimeTarget | Out-Null
$runtimeRelative = (Get-Content -LiteralPath (Join-Path $source 'runtime.current') -Raw).Trim()
if ($runtimeRelative -notmatch '^runtime\\RoadRuntimeHost\.Runtime\.[0-9a-f]{16}\.dll$') {
    throw "Invalid staged runtime pointer: $runtimeRelative"
}
$runtimeName = Split-Path -Leaf $runtimeRelative
Copy-Item -LiteralPath (Join-Path $source 'RoadRuntimeHost.Loader.dll') -Destination (Join-Path $target 'RoadRuntimeHost.Loader.dll') -Force
Copy-Item -LiteralPath (Join-Path $source 'preview.path') -Destination (Join-Path $target 'preview.path') -Force
Copy-Item -LiteralPath (Join-Path $source $runtimeRelative) -Destination (Join-Path $runtimeTarget $runtimeName) -Force
Copy-Item -LiteralPath (Join-Path $source 'runtime.current') -Destination (Join-Path $target 'runtime.current') -Force
Get-ChildItem -LiteralPath $runtimeTarget -Filter 'RoadRuntimeHost.Runtime.*.dll' -File |
    Where-Object Name -ne $runtimeName |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
Write-Host "Installed Runtime Host: $target"
