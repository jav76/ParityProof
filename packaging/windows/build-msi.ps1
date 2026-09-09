param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Arch,
    [Parameter(Mandatory=$true)][string]$OutputDir
)

$ErrorActionPreference = "Stop"

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$PublishDir = (Resolve-Path $PublishDir).Path

# MSI requires numerical version format: Major.Minor.Build[.Revision]
$cleanSemVer = $Version.Split('-')[0]
$parts = $cleanSemVer.Split('.')
while ($parts.Count -lt 3) {
    $parts += "0"
}
$msiVersion = "$($parts[0]).$($parts[1]).$($parts[2])"

# Map architecture to WiX architecture
$wixArch = switch ($Arch) {
    "x64" { "x64" }
    "arm64" { "arm64" }
    "x86" { "x86" }
    default { "x64" }
}

$msiName = "ParityProof-v$Version-win-$Arch.msi"
$outputMsiPath = Join-Path $OutputDir $msiName

Write-Host "Building Windows MSI Installer: $outputMsiPath (Version: $msiVersion, Arch: $wixArch)"

# Ensure WiX tool is available
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "Installing WiX .NET global tool..."
    dotnet tool install --global wix
}

$dotnetTools = Join-Path $env:USERPROFILE ".dotnet\tools"
if ($env:PATH -notlike "*$dotnetTools*") {
    $env:PATH = "$dotnetTools;$env:PATH"
}

$wixFilePath = Join-Path $PSScriptRoot "ParityProof.wxs"
wix build "$wixFilePath" `
    -d "PublishDir=$PublishDir" `
    -d "Version=$msiVersion" `
    -arch $wixArch `
    -o "$outputMsiPath"

Write-Host "Successfully generated $outputMsiPath"
