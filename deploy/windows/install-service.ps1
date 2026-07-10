#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs an InferHub node as a Windows service (auto-start, restart-on-failure).

.DESCRIPTION
    Registers InferHub.Node.Service.exe as a Windows service, grants the service account
    write access to a writable data directory (for the node identity file), configures
    restart-on-failure recovery, optionally sets machine-scoped secrets, and starts it.

    Re-running against an existing service updates its configuration instead of throwing.

.PARAMETER ServiceName
    Windows service name (short, no spaces). Default: InferHubNode.

.PARAMETER DisplayName
    Friendly name shown in services.msc. Default: "InferHub Node".

.PARAMETER BinaryPath
    Full path to InferHub.Node.Service.exe (required).

.PARAMETER DataDirectory
    Writable state directory for the node identity file. Granted write access for the
    service account and passed to the service as Node__DataDirectory.
    Default: C:\ProgramData\InferHub\Node.

.PARAMETER Account
    Service logon account. Default: LocalSystem. Use e.g. "NT SERVICE\InferHubNode" for a
    least-privilege virtual account.

.PARAMETER EnrollmentSecret
    Optional. If supplied, set as the machine env var Coordinator__EnrollmentSecret.

.PARAMETER DelayedStart
    Configure the service for delayed auto-start (lets network / Ollama come up first).

.EXAMPLE
    ./install-service.ps1 -BinaryPath "C:\Program Files\InferHub\Node\InferHub.Node.Service.exe" -DelayedStart
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "InferHubNode",
    [string]$DisplayName = "InferHub Node",
    [Parameter(Mandatory = $true)][string]$BinaryPath,
    [string]$DataDirectory = "C:\ProgramData\InferHub\Node",
    [string]$Account = "LocalSystem",
    [string]$EnrollmentSecret,
    [switch]$DelayedStart
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $BinaryPath)) {
    throw "BinaryPath not found: $BinaryPath. Publish first (see README.md) and point -BinaryPath at InferHub.Node.Service.exe."
}
$BinaryPath = (Resolve-Path -LiteralPath $BinaryPath).Path

# 1. Writable data directory + ACL for the service account.
if (-not (Test-Path -LiteralPath $DataDirectory)) {
    New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    Write-Host "Created data directory: $DataDirectory"
}

$aclIdentity = switch ($Account) {
    "LocalSystem"    { "NT AUTHORITY\SYSTEM" }
    "LocalService"   { "NT AUTHORITY\LOCAL SERVICE" }
    "NetworkService" { "NT AUTHORITY\NETWORK SERVICE" }
    default          { $Account }
}
try {
    $acl = Get-Acl -LiteralPath $DataDirectory
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $aclIdentity, "Modify",
        "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $DataDirectory -AclObject $acl
    Write-Host "Granted '$aclIdentity' Modify on $DataDirectory"
} catch {
    Write-Warning "Could not set ACL for '$aclIdentity' on ${DataDirectory}: $($_.Exception.Message)"
}

# 2. Create or update the service.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$binWithArgs = "`"$BinaryPath`""
if ($null -eq $existing) {
    Write-Host "Creating service '$ServiceName'..."
    # sc.exe handles arbitrary accounts (incl. virtual accounts) uniformly.
    $scArgs = @(
        "create", $ServiceName,
        "binPath=", $binWithArgs,
        "DisplayName=", $DisplayName,
        "start=", "auto",
        "obj=", $Account
    )
    & sc.exe @scArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }
    & sc.exe description $ServiceName "InferHub inference node (SignalR client)." | Out-Null
} else {
    Write-Host "Service '$ServiceName' already exists; updating configuration..."
    if ($existing.Status -eq "Running") {
        Stop-Service -Name $ServiceName -Force
    }
    & sc.exe config $ServiceName binPath= $binWithArgs start= auto obj= $Account | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed with exit code $LASTEXITCODE." }
}

# 3. Delayed auto-start (optional).
if ($DelayedStart) {
    & sc.exe config $ServiceName start= delayed-auto | Out-Null
    Write-Host "Configured delayed auto-start."
}

# 4. Restart-on-failure recovery: restart after 5s, 5s, then 30s; reset the counter daily.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
Write-Host "Configured restart-on-failure recovery."

# 5. Data directory override for the node identity file.
[Environment]::SetEnvironmentVariable("Node__DataDirectory", $DataDirectory, "Machine")

# 6. Enrollment secret (optional).
if ($PSBoundParameters.ContainsKey("EnrollmentSecret") -and $EnrollmentSecret) {
    [Environment]::SetEnvironmentVariable("Coordinator__EnrollmentSecret", $EnrollmentSecret, "Machine")
    Write-Host "Set machine env var Coordinator__EnrollmentSecret."
} else {
    Write-Warning "No -EnrollmentSecret supplied. Set it before the node can enroll:"
    Write-Warning "  [Environment]::SetEnvironmentVariable('Coordinator__EnrollmentSecret','<secret>','Machine')"
    Write-Warning "It must match the coordinator's Auth:NodeEnrollmentSecret. Also confirm Coordinator:Url in appsettings.json."
}

# 7. Start and report.
Start-Service -Name $ServiceName
Start-Sleep -Seconds 1
Get-Service -Name $ServiceName | Format-Table -AutoSize
Write-Host "Installed. Check Event Viewer -> Windows Logs -> Application for '$DisplayName' startup logs."
