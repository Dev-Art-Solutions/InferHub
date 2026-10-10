<#
.SYNOPSIS
    Builds InferHub-Node-Setup-<version>-win-x64.exe and its .sha256 (phase 101).

.DESCRIPTION
    Publishes src/InferHub.Node.WindowsService self-contained for win-x64, compiles InferHubNode.iss with
    Inno Setup 6, and writes the checksum file the node's updater requires beside the setup
    (sha256sum's "<hex>  <name>" shape). The release workflow runs exactly this.

.PARAMETER Version
    Default: <Version> from Directory.Build.props.

.PARAMETER OutputDirectory
    Default: artifacts/installer under the repository root.

.EXAMPLE
    ./deploy/windows/installer/build-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path

if (-not $Version) {
    $props = [xml](Get-Content -Raw (Join-Path $repo "Directory.Build.props"))
    $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be X.Y.Z (got '$Version')." }

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "artifacts\installer" }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

$iscc = $env:ISCC
if (-not $iscc) { $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { $iscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found. Install it (choco install innosetup) or set ISCC to ISCC.exe." }

$publish = Join-Path $repo "artifacts\publish\node-win-x64"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

Write-Host "Publishing InferHub.Node.WindowsService $Version (win-x64, self-contained)..."
& dotnet publish (Join-Path $repo "src\InferHub.Node.WindowsService") -c Release -r win-x64 -o $publish "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

Write-Host "Compiling the setup with $iscc ..."
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$OutputDirectory" (Join-Path $PSScriptRoot "InferHubNode.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

$name = "InferHub-Node-Setup-$Version-win-x64.exe"
$setup = Join-Path $OutputDirectory $name
$hash = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()
# Two spaces and the name: sha256sum -c reads it, and so does the node's updater.
[System.IO.File]::WriteAllText("$setup.sha256", "$hash  $name`n")

Write-Host "Built $setup"
Write-Host "SHA-256 $hash"
