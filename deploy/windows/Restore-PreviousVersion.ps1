[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$ServiceName = 'CrptWorkbench',
    [string]$InstallPath = 'C:\Program Files\CrptWorkbench',
    [string]$DataPath = 'C:\ProgramData\CrptWorkbench'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'AccessControl.ps1')

function Assert-SafeTarget([string]$Path, [string]$AllowedRoot) {
    $resolvedRoot = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase) -or $resolvedPath -eq $resolvedRoot.TrimEnd('\')) {
        throw "Unsafe target: $resolvedPath"
    }
    return $resolvedPath
}

$resolvedInstallPath = Assert-SafeTarget -Path $InstallPath -AllowedRoot $env:ProgramFiles
$resolvedDataPath = Assert-SafeTarget -Path $DataPath -AllowedRoot $env:ProgramData
$backupRoot = Join-Path $resolvedDataPath 'backups'
$backup = Get-ChildItem -LiteralPath $backupRoot -Directory -ErrorAction Stop | Sort-Object Name -Descending | Select-Object -First 1
if (-not $backup -or -not (Test-Path -LiteralPath (Join-Path $backup.FullName 'CrptWorkbench.Web.exe'))) {
    throw 'No valid previous binary backup was found.'
}

if ($PSCmdlet.ShouldProcess($backup.FullName, "Restore $ServiceName")) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell session.'
    }
    $validatedBackup = Assert-SafeTarget -Path $backup.FullName -AllowedRoot $backupRoot
    Set-WorkbenchTreeAccess -Path $resolvedDataPath -ProtectedData
    Get-ChildItem -LiteralPath $validatedBackup -Recurse -Force -File | ForEach-Object {
        $stream = [IO.File]::OpenRead($_.FullName)
        $stream.Dispose()
    }
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    $validatedInstall = Assert-SafeTarget -Path $resolvedInstallPath -AllowedRoot $env:ProgramFiles
    if (Test-Path -LiteralPath $validatedInstall) {
        Set-WorkbenchTreeAccess -Path $validatedInstall
        Remove-Item -LiteralPath $validatedInstall -Recurse -Force
    }
    New-Item -ItemType Directory -Path $validatedInstall -Force | Out-Null
    Get-ChildItem -LiteralPath $validatedBackup -Force | Copy-Item -Destination $validatedInstall -Recurse -Force
    Set-WorkbenchTreeAccess -Path $validatedInstall
    Start-Service -Name $ServiceName
    Write-Host "Restored binaries from $($backup.FullName). Runtime data was not changed."
}
