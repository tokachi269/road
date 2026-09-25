[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile (Join-Path $repoRoot 'config\toolchain.psd1')
$target = Join-Path $config.CitiesSkylinesDataDir 'Addons\Mods\RoadRuntimeHost'
$runtimeTarget = Join-Path $target 'runtime'
if (-not (Test-Path -LiteralPath (Join-Path $target 'RoadRuntimeHost.Loader.dll'))) {
    throw "Road Runtime Host is not installed. Run install-runtime-host.ps1 once while the game is stopped."
}

& (Join-Path $PSScriptRoot 'build-runtime-host.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Runtime Host build failed with exit code $LASTEXITCODE" }
$runtime = Join-Path $repoRoot "src\RoadRuntimeHost.Runtime\bin\$Configuration\RoadRuntimeHost.Runtime.dll"
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtime).Hash.ToLowerInvariant().Substring(0, 16)
$runtimeName = "RoadRuntimeHost.Runtime.$hash.dll"
New-Item -ItemType Directory -Force -Path $runtimeTarget | Out-Null
Copy-Item -LiteralPath $runtime -Destination (Join-Path $runtimeTarget $runtimeName) -Force

$pointer = Join-Path $target 'runtime.current'
$temporary = Join-Path $target 'runtime.current.tmp'
[IO.File]::WriteAllText($temporary, "runtime\$runtimeName`n", [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $pointer -Force
Write-Host "Published hot Runtime: $runtimeName"
