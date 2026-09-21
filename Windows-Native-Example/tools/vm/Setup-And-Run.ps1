<#
.SYNOPSIS
  Inside the Windows VM: installs the .NET 8 SDK if needed, fetches the SDK + sample
  from the Mac, and runs the WPF sample.

.PARAMETER HostIp
  The Mac's IP as the VM sees it (printed by prepare-on-mac.sh).

.PARAMETER HttpPort
  Port the Mac serves the zip on. Default 8000.
#>
param(
    [Parameter(Mandatory = $true)][string]$HostIp,
    [int]$HttpPort = 8000,
    [string]$Target = "C:\amwal"
)

$ErrorActionPreference = "Stop"

function Have-Dotnet8 {
    $sdks = & dotnet --list-sdks 2>$null
    return ($LASTEXITCODE -eq 0) -and ($sdks -match '^8\.')
}

if (-not (Have-Dotnet8)) {
    Write-Host "Installing the .NET 8 SDK (includes the WPF desktop runtime)..."
    winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
    # winget updates PATH for new shells only; refresh this one.
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
                [Environment]::GetEnvironmentVariable("Path", "User")
    if (-not (Have-Dotnet8)) { throw "The .NET 8 SDK is still not on PATH - open a new PowerShell and re-run." }
}

$zip = Join-Path $env:TEMP "amwal-ecr.zip"
Write-Host "Downloading http://${HostIp}:${HttpPort}/amwal-ecr.zip ..."
Invoke-WebRequest "http://${HostIp}:${HttpPort}/amwal-ecr.zip" -OutFile $zip

if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
Expand-Archive $zip -DestinationPath $Target
Write-Host "Extracted to $Target"

Write-Host ""
Write-Host "In the app: Terminals -> add a Wi-Fi terminal at ${HostIp}:9100, then set the"
Write-Host "LAN signing key to the one prepare-on-mac.sh printed."
Write-Host ""

Set-Location (Join-Path $Target "AmwalECR-Windows-Example\Windows-Native-Example")
dotnet run --project src\AmwalECR.Example -c Release
