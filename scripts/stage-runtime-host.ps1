[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$PreviewPath = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PreviewPath) { $PreviewPath = Join-Path $repoRoot 'build\runtime-preview' }
$PreviewPath = [IO.Path]::GetFullPath($PreviewPath)
$stage = Join-Path $repoRoot 'build\runtime-host'
$runtimeStage = Join-Path $stage 'runtime'

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build-runtime-host.ps1') -Configuration $Configuration }
if ($LASTEXITCODE -ne 0) { throw "Runtime Host build failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Force -Path $PreviewPath, $runtimeStage | Out-Null
& python (Join-Path $repoRoot 'generator\runtime_catalog.py') (Join-Path $repoRoot 'catalog') $PreviewPath
if ($LASTEXITCODE -ne 0) { throw "Catalog compile failed with exit code $LASTEXITCODE" }

$loader = Join-Path $repoRoot "src\RoadRuntimeHost.Loader\bin\$Configuration\RoadRuntimeHost.Loader.dll"
$runtime = Join-Path $repoRoot "src\RoadRuntimeHost.Runtime\bin\$Configuration\RoadRuntimeHost.Runtime.dll"
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtime).Hash.ToLowerInvariant().Substring(0, 16)
$runtimeName = "RoadRuntimeHost.Runtime.$hash.dll"
Copy-Item -LiteralPath $loader -Destination (Join-Path $stage 'RoadRuntimeHost.Loader.dll') -Force
Copy-Item -LiteralPath $runtime -Destination (Join-Path $runtimeStage $runtimeName) -Force
[IO.File]::WriteAllText((Join-Path $stage 'runtime.current'), "runtime\$runtimeName`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $stage 'preview.path'), "$PreviewPath`n", [Text.UTF8Encoding]::new($false))

Write-Host "Staged Runtime Host: $stage"
Write-Host "Preview input: $PreviewPath"
Write-Host "Runtime implementation: $runtimeName"
