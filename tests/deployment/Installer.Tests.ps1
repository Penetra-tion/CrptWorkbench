$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$installer = Join-Path $repositoryRoot 'deploy\windows\Install-Service.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
# Load only pure helpers, never execute the installer or touch a Windows service.
foreach ($name in @('Set-ExistingServiceConfiguration', 'Set-ProtectedDirectoryAccess', 'Get-WorkbenchServiceEnvironment', 'ConvertFrom-WorkbenchJson', 'Restore-InstalledSettings')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (-not $definition) { throw "Missing helper: $name" }
    Invoke-Expression $definition.Extent.Text
}
function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
$freshEnvironment = @(Get-WorkbenchServiceEnvironment -Port 5080 -DataPath 'C:\ProgramData\CrptWorkbench')
Assert ($freshEnvironment.Count -eq 3) 'New installation must only set host environment, URL and data directory.'
Assert ($freshEnvironment -contains 'ASPNETCORE_ENVIRONMENT=Production') 'Host environment must remain Production.'
$updatedEnvironment = @(Get-WorkbenchServiceEnvironment -Existing @('crpt__mode=Production', 'Crpt:Mode=Mock', 'ASPNETCORE_URLS=http://old', 'CustomSetting=keep=all') -Port 5090 -DataPath 'C:\Data')
Assert (@($updatedEnvironment | Where-Object { $_ -match '^Crpt(__|:)Mode=' }).Count -eq 0) 'Legacy integration mode overrides must be removed.'
Assert ($updatedEnvironment -contains 'CustomSetting=keep=all') 'Unrelated environment settings must survive updates.'
Assert ($updatedEnvironment -contains 'ASPNETCORE_URLS=http://127.0.0.1:5090') 'Configured service port was lost.'
Assert (($updatedEnvironment -join "`n") -ceq ((@(Get-WorkbenchServiceEnvironment -Existing $updatedEnvironment -Port 5090 -DataPath 'C:\Data')) -join "`n")) 'Environment migration must be idempotent.'
$settingsText = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'src\CrptWorkbench.Web\appsettings.json'))
$settings = ConvertFrom-WorkbenchJson $settingsText
Assert ($settings.Crpt.Mode -eq 'Mock') 'Commented application config must load in Windows PowerShell.'
Assert ($settings.Crpt.TrueApiBaseUrl -eq 'https://markirovka.crpt.ru/api/v3/true-api') 'URL was damaged while stripping comments.'
$tricky = ConvertFrom-WorkbenchJson '{"url":"https://host/a/*value*/",/* block */"text":"escaped \" // text","path":"C:\\test" // line comment
}'
Assert ($tricky.text -ceq 'escaped " // text') 'Quoted comments or escaped quotes were damaged.'
Assert ($tricky.url -ceq 'https://host/a/*value*/') 'Block comment inside a string was damaged.'
Assert ($tricky.path -ceq 'C:\test') 'Backslashes were damaged.'
$script:capturedArguments = $null
$script:returnCode = 0
function Get-CimInstance { param($ClassName, $Filter) return [pscustomobject]@{ Name = 'CrptWorkbench' } }
function Invoke-CimMethod {
    param($InputObject, $MethodName, $Arguments)
    Assert ($MethodName -eq 'Change') 'Wrong Windows service method.'
    $script:capturedArguments = $Arguments
    return [pscustomobject]@{ ReturnValue = $script:returnCode }
}
$executable = 'C:\Program Files\CrptWorkbench\CrptWorkbench.Web.exe'
Set-ExistingServiceConfiguration -Name CrptWorkbench -Executable $executable
Assert ($script:capturedArguments.PathName -ceq "`"$executable`"") 'Executable path must retain quotes around spaces.'
Assert ($script:capturedArguments.StartMode -eq 'Automatic') 'Automatic startup was lost.'
Assert ($script:capturedArguments.Count -eq 2) 'Service credentials or unrelated properties must not change.'
$script:returnCode = 5
$caught = $false
try { Set-ExistingServiceConfiguration -Name CrptWorkbench -Executable $executable }
catch { $caught = $_.Exception.Message -like '*returned 5*' }
Assert $caught 'Windows service failure code was not surfaced.'
. (Join-Path $repositoryRoot 'deploy\windows\AccessControl.ps1')
# Exercise real ACL persistence in Windows PowerShell, not a mocked command line.
# Every changed file is inside this newly generated temporary directory.
$scratchParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$scratchPath = Join-Path $scratchParent ('CrptWorkbench-AclTests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratchPath -ErrorAction Stop | Out-Null
try {
    $settingsBackup = New-Item -ItemType Directory -Path (Join-Path $scratchPath 'settings-backup')
    $settingsInstall = New-Item -ItemType Directory -Path (Join-Path $scratchPath 'settings-install')
    [IO.File]::WriteAllText((Join-Path $settingsBackup.FullName 'appsettings.json'), $settingsText)
    [IO.File]::WriteAllText((Join-Path $settingsInstall.FullName 'appsettings.json'), '{}')
    Restore-InstalledSettings -BackupPath $settingsBackup.FullName -InstallPath $settingsInstall.FullName
    Assert ([IO.File]::ReadAllText((Join-Path $settingsInstall.FullName 'appsettings.json')) -ceq $settingsText) 'Installed settings and comments must survive an update byte-for-byte.'
    $nestedPath = Join-Path $scratchPath 'backups\legacy\App_Data'
    New-Item -ItemType Directory -Path $nestedPath -Force | Out-Null
    $testFile = Join-Path $nestedPath 'test.db'
    [IO.File]::WriteAllText($testFile, 'synthetic-test-data')
    $empty = Get-Acl -LiteralPath $testFile
    $empty.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($empty.Access)) { $empty.RemoveAccessRuleSpecific($rule) }
    Set-Acl -LiteralPath $testFile -AclObject $empty
    Assert (@((Get-Acl -LiteralPath $testFile).Access).Count -eq 0) 'Failed to reproduce a protected empty DACL.'
    $readDenied = $false
    try { $stream = [IO.File]::OpenRead($testFile); $stream.Dispose() }
    catch { $readDenied = $true }
    Assert $readDenied 'Empty-DACL fixture must deny reading.'
    $currentAccount = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $originalOwner = (Get-Acl -LiteralPath $testFile).Owner
    Set-ProtectedDirectoryAccess -Path $scratchPath -AdditionalModifyAccount $currentAccount
    Assert ([IO.File]::ReadAllText($testFile) -eq 'synthetic-test-data') 'Recovered database contents changed or remain unreadable.'
    Assert ((Get-Acl -LiteralPath $scratchPath).AreAccessRulesProtected) 'Data root must be protected.'
    Assert ((Get-Acl -LiteralPath $testFile).AreAccessRulesProtected) 'Legacy file protection must not be removed.'
    Assert ((Get-Acl -LiteralPath $testFile).Owner -eq $originalOwner) 'ACL repair must not change ownership.'
    Set-ProtectedDirectoryAccess -Path $scratchPath -AdditionalModifyAccount $currentAccount
    $newFile = Join-Path $nestedPath 'new.log'
    [IO.File]::WriteAllText($newFile, 'new-file')
    $newRules = @((Get-Acl -LiteralPath $newFile).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        Assert (@($newRules | Where-Object { $_.IdentityReference.Value -eq $sid -and $_.IsInherited -and
            ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq [Security.AccessControl.FileSystemRights]::FullControl }).Count -gt 0) 'New files must inherit machine/admin full access.'
    }
    Set-WorkbenchTreeAccess -Path $scratchPath -AdditionalAccount $currentAccount -AdditionalRights ReadAndExecute
    Assert (-not (Get-Acl -LiteralPath $scratchPath).AreAccessRulesProtected) 'Application directory inheritance must be enabled.'
    Assert ([IO.File]::ReadAllText($testFile) -eq 'synthetic-test-data') 'Application ACL transition lost file contents.'
    $fileItem = Get-Item -LiteralPath $testFile
    $deniedAcl = $fileItem.GetAccessControl([Security.AccessControl.AccessControlSections]::Access)
    $denyRule = [Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), 'ReadData', 'Deny')
    $deniedAcl.AddAccessRule($denyRule)
    $fileItem.SetAccessControl($deniedAcl)
    $caught = $false
    try { Set-WorkbenchTreeAccess -Path $scratchPath -ProtectedData -AdditionalAccount $currentAccount }
    catch { $caught = $_.Exception.Message -like '*Required ACL was not established*' }
    Assert $caught 'Conflicting deny must fail verification instead of reporting success.'
    $deniedAcl = $fileItem.GetAccessControl([Security.AccessControl.AccessControlSections]::Access)
    $deniedAcl.RemoveAccessRuleSpecific($denyRule)
    $fileItem.SetAccessControl($deniedAcl)
    Write-Host 'Real Windows ACL checks passed: empty-DACL recovery, nested legacy files, repeat application, new-file inheritance, preserved ownership, and conflicting-deny detection.'
}
finally {
    $validatedScratch = [IO.Path]::GetFullPath($scratchPath)
    if (-not $validatedScratch.StartsWith($scratchParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($validatedScratch) -notlike 'CrptWorkbench-AclTests-*') {
        throw 'Unsafe ACL test cleanup path.'
    }
    # Repair any fixture left inaccessible by a failed assertion before cleanup.
    Set-WorkbenchTreeAccess -Path $validatedScratch -ProtectedData -AdditionalAccount ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
    Remove-Item -LiteralPath $validatedScratch -Recurse -Force -ErrorAction Stop
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'deploy\windows') -Filter '*.ps1') {
    $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    Assert ($parseErrors.Count -eq 0) "Syntax error in $($file.Name)"
}
Write-Host 'Installer regression checks passed; all deployment scripts parse. No service was modified; ACL changes were confined to temporary test files.'
