[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('find_net', 'inspect_net')]
    [string]$Command,
    [string]$Filter = '',
    [string]$PrefabName = '',
    [ValidateSet('summary', 'lanes', 'lane_props', 'segments', 'nodes')]
    [string]$Section = 'summary',
    [ValidateRange(0, 2147483647)]
    [int]$LaneIndex = 0,
    [ValidateRange(0, 2147483647)]
    [int]$Offset = 0,
    [ValidateRange(1, 20)]
    [int]$Limit = 20,
    [string]$PreviewPath = '',
    [ValidateRange(1, 60)]
    [int]$TimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PreviewPath) { $PreviewPath = Join-Path $repoRoot 'build\runtime-preview' }
$PreviewPath = [IO.Path]::GetFullPath($PreviewPath)
New-Item -ItemType Directory -Force -Path $PreviewPath | Out-Null

if ($Command -eq 'inspect_net' -and -not $PrefabName) {
    throw 'PrefabName is required for inspect_net.'
}

$requestId = [Guid]::NewGuid().ToString('N')
$request = [ordered]@{
    schema_version = 1
    request_id = $requestId
    command = $Command
    name_contains = $Filter
    prefab_name = $PrefabName
    section = $Section
    lane_index = $LaneIndex
    offset = $Offset
    limit = $Limit
}
$requestPath = Join-Path $PreviewPath 'inspect.request.json'
$responsePath = Join-Path $PreviewPath 'inspect.response.json'
$tempPath = "$requestPath.tmp"
$request | ConvertTo-Json -Compress | Set-Content -LiteralPath $tempPath -Encoding UTF8
Move-Item -LiteralPath $tempPath -Destination $requestPath -Force

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
do {
    if (Test-Path -LiteralPath $responsePath) {
        try {
            $responseText = Get-Content -LiteralPath $responsePath -Raw
            $response = $responseText | ConvertFrom-Json
            if ($response.request_id -eq $requestId) {
                $responseText
                if ($response.status -ne 'ok') { exit 1 }
                exit 0
            }
        } catch {
            # Runtime response replacement can overlap this read; retry briefly.
        }
    }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $deadline)

throw "Timed out waiting for RoadRuntimeHost inspection response after $TimeoutSeconds seconds."
