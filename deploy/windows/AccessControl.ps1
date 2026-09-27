# Shared by installation and rollback. No native command-line ACL processing.
function Set-WorkbenchTreeAccess {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$ProtectedData,
        [string]$AdditionalAccount,
        [Security.AccessControl.FileSystemRights]$AdditionalRights = 'Modify'
    )

    $grants = @{
        'S-1-5-18' = if ($ProtectedData) { [Security.AccessControl.FileSystemRights]::FullControl } else { [Security.AccessControl.FileSystemRights]::ReadAndExecute }
        'S-1-5-32-544' = [Security.AccessControl.FileSystemRights]::FullControl
    }
    if (-not [string]::IsNullOrWhiteSpace($AdditionalAccount)) {
        $account = [Security.Principal.NTAccount]::new($AdditionalAccount)
        $accountSid = $account.Translate([Security.Principal.SecurityIdentifier]).Value
        if (-not $grants.ContainsKey($accountSid)) { $grants[$accountSid] = $AdditionalRights }
    }

    function Set-ItemAccess($Item, [bool]$IsRoot) {
        if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing ACL changes through a reparse point: $($Item.FullName)"
        }
        # Read/write the DACL only: owner, group and audit settings are untouched,
        # so persistence does not request SeSecurityPrivilege for audit changes.
        $acl = $Item.GetAccessControl([Security.AccessControl.AccessControlSections]::Access)
        $inheritance = if ($Item.PSIsContainer) {
            [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
        } else { [Security.AccessControl.InheritanceFlags]::None }
        foreach ($sid in $grants.Keys) {
            $identity = [Security.Principal.SecurityIdentifier]::new($sid)
            $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                $identity, $grants[$sid], $inheritance,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow)
            $acl.SetAccessRule($rule)
        }
        # Explicit grants are in the same descriptor BEFORE inherited entries
        # are removed. Never write an intermediate empty protected DACL.
        if ($ProtectedData -and $IsRoot) { $acl.SetAccessRuleProtection($true, $false) }
        elseif (-not $ProtectedData) { $acl.SetAccessRuleProtection($false, $false) }
        $Item.SetAccessControl($acl)

        $saved = $Item.GetAccessControl([Security.AccessControl.AccessControlSections]::Access)
        $rules = @($saved.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
        foreach ($sid in $grants.Keys) {
            $required = $grants[$sid]
            $matching = @($rules | Where-Object {
                $_.IdentityReference.Value -eq $sid -and
                $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
                ($_.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0 -and
                ($_.FileSystemRights -band $required) -eq $required
            })
            $denied = @($rules | Where-Object {
                $_.IdentityReference.Value -eq $sid -and
                $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Deny -and
                ($_.FileSystemRights -band $required) -ne 0
            })
            if ($matching.Count -eq 0 -or $denied.Count -gt 0) {
                throw "Required ACL was not established for $sid on $($Item.FullName)."
            }
            if ($Item.PSIsContainer -and -not @($matching | Where-Object {
                ($_.InheritanceFlags -band $inheritance) -eq $inheritance
            }).Count) {
                throw "Required inheritable ACL was not established for $sid on $($Item.FullName)."
            }
        }
        if ($ProtectedData -and $IsRoot -and -not $saved.AreAccessRulesProtected) {
            throw "Data root inheritance was not protected: $($Item.FullName)"
        }
        # Repair a directory before enumerating its children, including trees
        # whose older release left protected empty DACLs.
        if ($Item.PSIsContainer) {
            foreach ($child in @(Get-ChildItem -LiteralPath $Item.FullName -Force -ErrorAction Stop)) {
                Set-ItemAccess -Item $child -IsRoot $false
            }
        }
    }
    Set-ItemAccess -Item (Get-Item -LiteralPath $Path -Force -ErrorAction Stop) -IsRoot $true
}
