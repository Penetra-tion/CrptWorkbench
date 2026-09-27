[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\windows'))
$packageRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'package'))
$applicationRoot = Join-Path $packageRoot 'app'
$zipPath = Join-Path $artifactRoot "CrptWorkbench-$Runtime.zip"

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe build path: $resolvedPath"
    }
}

Assert-ChildPath -Path $packageRoot -Parent $artifactRoot
if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $applicationRoot -Force | Out-Null

Push-Location $repositoryRoot
try {
    dotnet restore CrptWorkbench.sln -p:RestoreUseStaticGraphEvaluation=true
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    dotnet restore src/CrptWorkbench.Web/CrptWorkbench.Web.csproj -r $Runtime -p:RestoreUseStaticGraphEvaluation=true
    if ($LASTEXITCODE -ne 0) { throw 'Runtime-specific restore failed.' }

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj -c $Configuration --no-restore -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Web build failed.' }

    if (-not $SkipTests) {
        & (Join-Path $repositoryRoot 'tests\deployment\Installer.Tests.ps1')
        dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c $Configuration --no-restore -m:1 -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
        dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c $Configuration --no-build --no-ansi --no-progress --output Detailed
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }

    dotnet publish src/CrptWorkbench.Web/CrptWorkbench.Web.csproj -c $Configuration -r $Runtime --self-contained true --no-restore -m:1 -p:UseSharedCompilation=false -p:PublishSingleFile=false -o $applicationRoot
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
}
finally {
    Pop-Location
}

$forbidden = Get-ChildItem -LiteralPath $applicationRoot -Recurse -Force | Where-Object {
    $_.Name -eq 'logs' -or $_.Extension -eq '.log' -or $_.Name -eq 'App_Data' -or $_.Name -eq 'keys' -or $_.Extension -in '.db', '.pfx', '.p12', '.key'
}
if ($forbidden) {
    throw "Package contains forbidden runtime or secret files: $($forbidden.FullName -join ', ')"
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Service.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AccessControl.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Verify-Deployment.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Restore-PreviousVersion.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\deployment\windows-production.md') -Destination (Join-Path $packageRoot 'DEPLOYMENT.md')

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
Write-Host "Package: $zipPath"
Write-Host "SHA256:  $($hash.Hash)"

