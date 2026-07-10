#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the InferHub node Windows service.

.DESCRIPTION
    Stops the service (if running) and deletes it. The data directory (node identity file)
    is left in place so a reinstall reuses the same node id; its path is printed.

.PARAMETER ServiceName
    Windows service name. Default: InferHubNode.

.PARAMETER DataDirectory
    Data directory to report (not deleted). Default: C:\ProgramData\InferHub\Node.

.EXAMPLE
    ./uninstall-service.ps1
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "InferHubNode",
    [string]$DataDirectory = "C:\ProgramData\InferHub\Node"
)

$ErrorActionPreference = "Stop"

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $existing) {
    Write-Host "Service '$ServiceName' is not installed. Nothing to do."
    return
}

if ($existing.Status -ne "Stopped") {
    Write-Host "Stopping '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force
}

# Remove-Service exists on PS 6+; fall back to sc.exe on Windows PowerShell 5.1.
if (Get-Command Remove-Service -ErrorAction SilentlyContinue) {
    Remove-Service -Name $ServiceName
} else {
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
}

Write-Host "Removed service '$ServiceName'."
Write-Host "Data directory left in place (delete manually to discard the node id): $DataDirectory"
