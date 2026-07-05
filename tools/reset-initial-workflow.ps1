<#
.SYNOPSIS
Clears SimpleLotto local state so first-install setup can be tested again.

.DESCRIPTION
Removes only SimpleLotto-owned data folders from the current Windows machine.
The installer and Program Files app binaries are left in place.

Examples:
  ./tools/reset-initial-workflow.ps1 -WhatIf
  ./tools/reset-initial-workflow.ps1 -Force
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if ($Force) {
    $ConfirmPreference = 'None'
}

function Get-KnownFolderPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$EnvironmentVariable,

        [Parameter(Mandatory = $true)]
        [string]$ChildPath
    )

    $root = [Environment]::GetEnvironmentVariable($EnvironmentVariable)
    if ([string]::IsNullOrWhiteSpace($root)) {
        return $null
    }

    Join-Path $root $ChildPath
}

$candidateFolders = @(
    (Get-KnownFolderPath -EnvironmentVariable 'LOCALAPPDATA' -ChildPath 'SimpleLotto'),
    (Get-KnownFolderPath -EnvironmentVariable 'APPDATA' -ChildPath 'SimpleLotto'),
    (Get-KnownFolderPath -EnvironmentVariable 'PROGRAMDATA' -ChildPath 'SimpleLotto'),
    (Get-KnownFolderPath -EnvironmentVariable 'TEMP' -ChildPath 'SimpleLotto'),
    (Get-KnownFolderPath -EnvironmentVariable 'LOCALAPPDATA' -ChildPath 'SimpleLotto.App'),
    (Get-KnownFolderPath -EnvironmentVariable 'APPDATA' -ChildPath 'SimpleLotto.App'),
    (Get-KnownFolderPath -EnvironmentVariable 'PROGRAMDATA' -ChildPath 'SimpleLotto.App'),
    (Get-KnownFolderPath -EnvironmentVariable 'TEMP' -ChildPath 'SimpleLotto.App')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

$existingFolders = $candidateFolders | Where-Object { Test-Path -LiteralPath $_ -PathType Container }

if ($existingFolders.Count -eq 0) {
    Write-Host 'No SimpleLotto data folders found.'
    return
}

Write-Host 'SimpleLotto folders selected for reset:'
$existingFolders | ForEach-Object { Write-Host "  $_" }

foreach ($folder in $existingFolders) {
    if ($PSCmdlet.ShouldProcess($folder, 'Remove SimpleLotto local state folder')) {
        Remove-Item -LiteralPath $folder -Recurse -Force
        Write-Host "Removed $folder"
    }
}
