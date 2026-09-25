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
$runtimeIdentity = [Reflection.AssemblyName]::GetAssemblyName($runtime).FullName
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtime).Hash.ToLowerInvariant().Substring(0, 16)
$runtimeName = "RoadRuntimeHost.Runtime.$hash.dll"
New-Item -ItemType Directory -Force -Path $runtimeTarget | Out-Null
Copy-Item -LiteralPath $runtime -Destination (Join-Path $runtimeTarget $runtimeName) -Force

$pointer = Join-Path $target 'runtime.current'
$currentToken = if (Test-Path -LiteralPath $pointer) { (Get-Content -LiteralPath $pointer -Raw).Trim() } else { '' }
if ($currentToken -match '^runtime\\RoadRuntimeHost\.Runtime\.[0-9a-f]{16}\.dll$') {
    $currentDll = Join-Path $target $currentToken
    if (Test-Path -LiteralPath $currentDll) {
        $currentIdentity = [Reflection.AssemblyName]::GetAssemblyName($currentDll).FullName
        if ($currentIdentity -eq $runtimeIdentity -and $currentToken -ne "runtime\$runtimeName") {
            throw "Hot Runtime assembly identity did not change: $runtimeIdentity"
        }
    }
}
$temporary = Join-Path $target 'runtime.current.tmp'
[IO.File]::WriteAllText($temporary, "runtime\$runtimeName`n", [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $pointer -Force
Get-ChildItem -LiteralPath $runtimeTarget -Filter 'RoadRuntimeHost.Runtime.*.dll' -File |
    Where-Object Name -ne $runtimeName |
    ForEach-Object {
        try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop }
        catch { Write-Warning "Obsolete Runtime could not be removed until CS1 exits: $($_.FullName): $($_.Exception.Message)" }
    }
Write-Host "Published hot Runtime: $runtimeName ($runtimeIdentity)"
