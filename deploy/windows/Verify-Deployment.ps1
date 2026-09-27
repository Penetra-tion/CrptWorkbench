[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$ServiceName = 'CrptWorkbench',
    [ValidateRange(1024, 65535)]
    [int]$Port = 5080,
    [switch]$RequireReady
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$service = Get-Service -Name $ServiceName -ErrorAction Stop
if ($service.Status -ne 'Running') { throw "Service $ServiceName is not running." }

$listeners = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop
$unsafeListeners = $listeners | Where-Object { $_.LocalAddress -notin '127.0.0.1', '::1' }
if ($unsafeListeners) {
    throw "Port $Port is exposed outside loopback: $($unsafeListeners.LocalAddress -join ', ')"
}

$baseUrl = "http://127.0.0.1:$Port"
$live = Invoke-RestMethod -Uri "$baseUrl/health/live" -TimeoutSec 5
if ($live.status -ne 'live') { throw 'Unexpected liveness response.' }

try {
    $ready = Invoke-RestMethod -Uri "$baseUrl/health/ready" -TimeoutSec 10
    Write-Host "Ready: $($ready.ready)"
    $ready.checks | ForEach-Object { Write-Host "  - $_" }
}
catch {
    $body = $_.ErrorDetails.Message
    if ([string]::IsNullOrWhiteSpace($body)) { $body = $_.Exception.Message }
    Write-Warning "Readiness returned an error: $body"
    if ($RequireReady) { throw }
}

Write-Host "Service: $ServiceName ($($service.Status))"
Write-Host "UI:      $baseUrl"
Write-Host 'Loopback binding and liveness are valid.'
