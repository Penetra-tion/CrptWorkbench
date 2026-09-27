[CmdletBinding(SupportsShouldProcess)]
param(
    [PSCredential]$ServiceCredential,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$ServiceName = 'CrptWorkbench',
    [string]$InstallPath = 'C:\Program Files\CrptWorkbench',
    [string]$DataPath = 'C:\ProgramData\CrptWorkbench',
    # Accepted for backward-compatible command lines. SignedCms signing no longer shells out to csptest.exe.
    [string]$CryptoProExecutablePath = 'C:\Program Files\Crypto Pro\CSP\csptest.exe',
    [ValidateRange(1024, 65535)]
    [int]$Port = 5080
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'AccessControl.ps1')

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell session.'
    }
}

function Assert-SafeTarget([string]$Path, [string]$AllowedRoot) {
    $resolvedRoot = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase) -or $resolvedPath -eq $resolvedRoot.TrimEnd('\')) {
        throw "Target must be a child of ${AllowedRoot}: $resolvedPath"
    }
    return $resolvedPath
}

function Resolve-AccountName([string]$AccountName) {
    if ($AccountName.StartsWith('.\', [StringComparison]::Ordinal)) {
        return "$env:COMPUTERNAME\$($AccountName.Substring(2))"
    }
    return $AccountName
}

function Get-ServiceStartName {
    param([string]$Name)

    return (Get-CimInstance Win32_Service -Filter "Name='$Name'").StartName
}

function Set-ExistingServiceConfiguration {
    param([string]$Name, [string]$Executable)

    $instance = Get-CimInstance Win32_Service -Filter "Name='$Name'"
    $result = Invoke-CimMethod -InputObject $instance -MethodName Change -Arguments @{
        PathName = "`"$Executable`""
        StartMode = 'Automatic'
    }
    if ($result.ReturnValue -ne 0) {
        throw "Failed to update the service executable path. Win32_Service.Change returned $($result.ReturnValue)."
    }
}

function Set-ProtectedDirectoryAccess {
    param([string]$Path, [string]$AdditionalModifyAccount)

    Set-WorkbenchTreeAccess -Path $Path -ProtectedData -AdditionalAccount $AdditionalModifyAccount
}

function Get-WorkbenchServiceEnvironment {
    param([string[]]$Existing, [int]$Port, [string]$DataPath)

    # Retain unrelated operator settings; remove the legacy mode override.
    foreach ($entry in $Existing) {
        if ($entry -notmatch '^(Crpt__Mode|Crpt:Mode|ASPNETCORE_ENVIRONMENT|ASPNETCORE_URLS|Crpt__DataDirectory)=') {
            $entry
        }
    }
    'ASPNETCORE_ENVIRONMENT=Production'
    "ASPNETCORE_URLS=http://127.0.0.1:$Port"
    "Crpt__DataDirectory=$DataPath"
}

function ConvertFrom-WorkbenchJson {
    param([string]$Json)

    # Windows PowerShell 5.1 rejects JSON comments. Match strings first so URLs,
    # escaped quotes and comment-like text inside values remain untouched.
    $withoutComments = [regex]::Replace($Json, '"(?:\\.|[^"\\])*"|//[^\r\n]*|/\*[\s\S]*?\*/', {
        param($match)
        if ($match.Value.StartsWith('"')) { return $match.Value }
        return ' '
    })
    return ($withoutComments | ConvertFrom-Json)
}

function Restore-InstalledSettings {
    param([string]$BackupPath, [string]$InstallPath)

    foreach ($file in Get-ChildItem -LiteralPath $BackupPath -Filter 'appsettings*.json' -File) {
        Copy-Item -LiteralPath $file.FullName -Destination $InstallPath -Force
    }
}

function Repair-ExistingInstallationAccess {
    param([string]$Path)

    Write-Host "Repairing permissions on the existing application directory: $Path"
    Set-WorkbenchTreeAccess -Path $Path
}

function Set-ApplicationDirectoryAccess {
    param(
        [string]$Path,
        [string]$AdditionalReadExecuteAccount
    )

    Set-WorkbenchTreeAccess -Path $Path -AdditionalAccount $AdditionalReadExecuteAccount -AdditionalRights ReadAndExecute
}

function Assert-ExecutableAccessible {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Installed executable was not found: $Path"
    }

    try {
        $stream = [IO.File]::Open(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        $stream.Dispose()
        Get-Acl -LiteralPath $Path -ErrorAction Stop | Out-Null
    }
    catch {
        throw "Installed executable cannot be read with its current Windows permissions: $Path. $($_.Exception.Message)"
    }
}

function Assert-BackupReadable {
    param([string]$Path)

    Assert-ExecutableAccessible -Path (Join-Path $Path 'CrptWorkbench.Web.exe')
    Get-ChildItem -LiteralPath $Path -Recurse -Force -File | ForEach-Object {
        $stream = [IO.File]::OpenRead($_.FullName)
        $stream.Dispose()
    }
}

function Write-ServiceDiagnostics {
    param([string]$Name)

    Write-Host "Service diagnostics for ${Name}:"
    Get-CimInstance Win32_Service -Filter "Name='$Name'" |
        Select-Object Name, State, StartName, ExitCode, ProcessId, PathName |
        Format-List |
        Out-String |
        Write-Host

    try {
        $settingsPath = Join-Path $InstallPath 'appsettings.json'
        if (-not (Test-Path -LiteralPath $settingsPath)) {
            Write-Host 'No installed appsettings.json was found.'
            return
        }
        $settings = ConvertFrom-WorkbenchJson ([IO.File]::ReadAllText($settingsPath))
        function Find-LogFiles($Node) {
            if ($Node -is [pscustomobject]) {
                $sinkName = $Node.PSObject.Properties['Name']
                $sinkArgs = $Node.PSObject.Properties['Args']
                $sinkPath = if ($sinkArgs) { $sinkArgs.Value.PSObject.Properties['path'] } else { $null }
                if ($sinkName -and $sinkName.Value -eq 'File' -and $sinkPath) { [string]$sinkPath.Value }
                foreach ($property in $Node.PSObject.Properties) { Find-LogFiles $property.Value }
            }
            elseif ($Node -is [array]) { foreach ($value in $Node) { Find-LogFiles $value } }
        }
        foreach ($configuredPath in (Find-LogFiles $settings)) {
            $logPath = if ([IO.Path]::IsPathRooted($configuredPath)) { $configuredPath } else { Join-Path $DataPath $configuredPath }
            $logDirectory = Split-Path -Path $logPath -Parent
            $prefix = [IO.Path]::GetFileNameWithoutExtension($logPath)
            Write-Host "Log directory: $logDirectory"
            if (Test-Path -LiteralPath $logDirectory) {
                $latest = Get-ChildItem -LiteralPath $logDirectory -File | Where-Object { $_.Name.StartsWith($prefix) -and $_.Extension -eq '.log' } |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($latest) { Get-Content -LiteralPath $latest.FullName -Tail 20 | Write-Host }
            }
        }
    }
    catch { Write-Host 'File diagnostics could not be read; inspect the configured log directory manually.' }
}

Assert-Administrator
$sourcePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'app'))
$sourceExecutable = Join-Path $sourcePath 'CrptWorkbench.Web.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw "Application package is incomplete: $sourceExecutable"
}
$resolvedInstallPath = Assert-SafeTarget -Path $InstallPath -AllowedRoot $env:ProgramFiles
$resolvedDataPath = Assert-SafeTarget -Path $DataPath -AllowedRoot $env:ProgramData
$backupRoot = Join-Path $resolvedDataPath 'backups'
$backupPath = Join-Path $backupRoot (Get-Date -Format 'yyyyMMdd-HHmmssfff')
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$serviceAccount = if ($ServiceCredential) { Resolve-AccountName $ServiceCredential.UserName } else { 'LocalSystem' }
$serviceWasRunning = $service -and $service.Status -eq 'Running'
$backupReady = $false
$replacementStarted = $false

if ($PSCmdlet.ShouldProcess($resolvedInstallPath, "Install or update $ServiceName")) {
    try {
        New-Item -ItemType Directory -Path $resolvedDataPath -Force | Out-Null
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        Set-ProtectedDirectoryAccess -Path $resolvedDataPath `
            -AdditionalModifyAccount $(if ($ServiceCredential) { $serviceAccount } else { $null })

        Get-ChildItem -LiteralPath $sourcePath -Recurse -Force -File | Unblock-File -ErrorAction Stop

        if ($service -and $service.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force
            (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }

        if (Test-Path -LiteralPath $resolvedInstallPath) {
            Repair-ExistingInstallationAccess -Path $resolvedInstallPath
            New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
            Get-ChildItem -LiteralPath $resolvedInstallPath -Force | Copy-Item -Destination $backupPath -Recurse -Force
            Assert-BackupReadable -Path $backupPath
            $backupReady = $true
            $replacementStarted = $true
            $validatedInstall = Assert-SafeTarget -Path $resolvedInstallPath -AllowedRoot $env:ProgramFiles
            Remove-Item -LiteralPath $validatedInstall -Recurse -Force
        }

        New-Item -ItemType Directory -Path $resolvedInstallPath -Force | Out-Null
        Get-ChildItem -LiteralPath $sourcePath -Force | Copy-Item -Destination $resolvedInstallPath -Recurse -Force
        if ($backupReady) {
            Restore-InstalledSettings -BackupPath $backupPath -InstallPath $resolvedInstallPath
        }
        Set-ApplicationDirectoryAccess -Path $resolvedInstallPath `
            -AdditionalReadExecuteAccount $(if ($ServiceCredential) { $serviceAccount } else { $null })

        Set-ProtectedDirectoryAccess -Path $resolvedDataPath `
            -AdditionalModifyAccount $(if ($ServiceCredential) { $serviceAccount } else { $null })

        $executable = Join-Path $resolvedInstallPath 'CrptWorkbench.Web.exe'
        Assert-ExecutableAccessible -Path $executable

        if (-not $service) {
            $newServiceParameters = @{
                Name = $ServiceName
                BinaryPathName = "`"$executable`""
                DisplayName = 'CRPT Workbench'
                Description = 'Local CRPT True API workbench'
                StartupType = 'Automatic'
            }
            if ($ServiceCredential) {
                $newServiceParameters.Credential = $ServiceCredential
            }

            New-Service @newServiceParameters | Out-Null
        }
        else {
            $configuredAccount = Get-ServiceStartName -Name $ServiceName
            $requestedAccount = $serviceAccount
            $isRequestedLocalSystem = [string]::Equals($requestedAccount, 'LocalSystem', [StringComparison]::OrdinalIgnoreCase)
            $isConfiguredLocalSystem = [string]::Equals($configuredAccount, 'LocalSystem', [StringComparison]::OrdinalIgnoreCase)
            if ($isRequestedLocalSystem -and -not $isConfiguredLocalSystem) {
                throw "Existing service runs as '$configuredAccount'. Recreate the service or pass the same service credential explicitly."
            }
            if (-not $isRequestedLocalSystem -and -not [string]::Equals($configuredAccount, $requestedAccount, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Existing service runs as '$configuredAccount'. Use the same account or recreate the service explicitly."
            }
            Set-ExistingServiceConfiguration -Name $ServiceName -Executable $executable
        }

        $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
        $existingEnvironment = Get-ItemPropertyValue -LiteralPath $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue
        $environment = @(Get-WorkbenchServiceEnvironment -Existing $existingEnvironment -Port $Port -DataPath $resolvedDataPath)
        New-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null

        & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to configure service recovery.' }

        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))

        $liveUrl = "http://127.0.0.1:$Port/health/live"
        $live = $false
        for ($attempt = 1; $attempt -le 15; $attempt++) {
            try {
                $response = Invoke-WebRequest -Uri $liveUrl -UseBasicParsing -TimeoutSec 3
                if ($response.StatusCode -eq 200) { $live = $true; break }
            }
            catch {
                Start-Sleep -Seconds 2
            }
        }
        if (-not $live) { throw "Service started but liveness check failed: $liveUrl" }
    }
    catch {
        $installationError = $_
        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
            Write-ServiceDiagnostics -Name $ServiceName
        }

        if ($serviceWasRunning) {
            try {
                Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
                (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
                if ($backupReady -and $replacementStarted) {
                    Write-Warning 'Installation failed. Restoring the previously running application binaries.'
                    Set-ProtectedDirectoryAccess -Path $backupPath
                    Assert-BackupReadable -Path $backupPath
                    if (Test-Path -LiteralPath $resolvedInstallPath) {
                        Repair-ExistingInstallationAccess -Path $resolvedInstallPath
                        $validatedInstall = Assert-SafeTarget -Path $resolvedInstallPath -AllowedRoot $env:ProgramFiles
                        Remove-Item -LiteralPath $validatedInstall -Recurse -Force
                    }
                    New-Item -ItemType Directory -Path $resolvedInstallPath -Force | Out-Null
                    Get-ChildItem -LiteralPath $backupPath -Force | Copy-Item -Destination $resolvedInstallPath -Recurse -Force
                    Set-ApplicationDirectoryAccess -Path $resolvedInstallPath `
                        -AdditionalReadExecuteAccount $(if ($ServiceCredential) { $serviceAccount } else { $null })
                    Assert-ExecutableAccessible -Path (Join-Path $resolvedInstallPath 'CrptWorkbench.Web.exe')
                }
                Start-Service -Name $ServiceName
                Write-Warning 'The previously running CRPT Workbench service was started again.'
            }
            catch {
                Write-Warning "Automatic recovery also failed: $($_.Exception.Message)"
            }
        }

        throw $installationError
    }

    Write-Host "CRPT Workbench is running at http://127.0.0.1:$Port"
    Write-Host "Service account: $serviceAccount"
    Write-Host "Runtime data: $resolvedDataPath"
    if (Test-Path -LiteralPath $backupPath) { Write-Host "Previous binaries: $backupPath" }
    Write-Host 'Open /settings, configure INN and certificate, then run Verify-Deployment.ps1 -RequireReady.'
}




