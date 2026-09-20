[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Script,

    [string[]]$ScriptArguments = @()
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile (Join-Path $repoRoot 'config\toolchain.psd1')
$scriptPath = (Resolve-Path -LiteralPath $Script).Path

& $config.BlenderExe --background --factory-startup --python $scriptPath -- @ScriptArguments
if ($LASTEXITCODE -ne 0) {
    throw "Blender failed with exit code $LASTEXITCODE"
}

