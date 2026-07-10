#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Updates an installed InferHub node service in place with fresh publish output.

.DESCRIPTION
    Stops the service, copies new publish output over the install directory, then starts
    it again. Simple in-place update (no versioned side-by-side). appsettings.json in the
    install directory is preserved by default so operator edits survive an update.

.PARAMETER ServiceName
    Windows service name. Default: InferHubNode.

.PARAMETER PublishDirectory
    Directory containing fresh publish output (InferHub.Node.Service.exe + friends).

.PARAMETER InstallDirectory
    Existing install directory to overwrite. Default: C:\Program Files\InferHub\Node.

.PARAMETER OverwriteConfig
    Also overwrite appsettings.json (by default the installed config is kept).

.EXAMPLE
    ./update-service.ps1 -PublishDirectory ".\publish"
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "InferHubNode",
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [string]$InstallDirectory = "C:\Program Files\InferHub\Node",
    [switch]$OverwriteConfig
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PublishDirectory)) {
    throw "PublishDirectory not found: $PublishDirectory"
}
if (-not (Test-Path -LiteralPath $InstallDirectory)) {
    throw "InstallDirectory not found: $InstallDirectory. Run install-service.ps1 first."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$wasRunning = $existing -and $existing.Status -eq "Running"
if ($wasRunning) {
    Write-Host "Stopping '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force
}

$excludes = @()
if (-not $OverwriteConfig) { $excludes += "appsettings.json" }

Write-Host "Copying publish output to $InstallDirectory..."
Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring((Resolve-Path -LiteralPath $PublishDirectory).Path.Length).TrimStart('\')
    if ($excludes -contains $relative) {
        Write-Host "  keeping installed $relative"
        return
    }
    $target = Join-Path $InstallDirectory $relative
    $targetDir = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
    Copy-Item -LiteralPath $_.FullName -Destination $target -Force
}

if ($wasRunning) {
    Start-Service -Name $ServiceName
    Write-Host "Started '$ServiceName'."
}
Get-Service -Name $ServiceName | Format-Table -AutoSize
