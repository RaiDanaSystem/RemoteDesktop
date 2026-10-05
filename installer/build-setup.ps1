#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$InstallerDir = $PSScriptRoot
$PublishDir = Join-Path $InstallerDir "publish"
$PrereqDir = Join-Path $InstallerDir "prereqs"
$ArtifactsDir = Join-Path $Root "artifacts"
$Project = Join-Path $Root "src\SupportAgent\SupportAgent.csproj"

New-Item -ItemType Directory -Force -Path $PrereqDir, $ArtifactsDir | Out-Null
if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}

function Get-File([string]$Uri, [string]$OutFile, [long]$MinBytes) {
    if ((Test-Path $OutFile) -and ((Get-Item $OutFile).Length -ge $MinBytes)) {
        Write-Host "Already cached: $OutFile"
        return
    }
    Write-Host "Downloading $Uri"
    $tmp = "$OutFile.partial"
    & curl.exe -L --fail --retry 3 -o $tmp $Uri
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $Uri" }
    $len = (Get-Item $tmp).Length
    if ($len -lt $MinBytes) {
        Remove-Item $tmp -Force
        throw "Download too small ($len bytes) for $Uri"
    }
    Move-Item $tmp $OutFile -Force
}

Write-Host "=== Publishing app (win-x64) ==="
dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PlatformTarget=x64 `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "=== Downloading prerequisites ==="
Get-File "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe" (Join-Path $PrereqDir "windowsdesktop-runtime-win-x64.exe") 20MB
Get-File "https://aka.ms/vs/17/release/vc_redist.x64.exe" (Join-Path $PrereqDir "vc_redist.x64.exe") 8MB

$iscc = @(
    (Join-Path $InstallerDir "tools\innosetup\ISCC.exe"),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host "=== Installing Inno Setup (current user) ==="
    $innoSetup = Join-Path $PrereqDir "innosetup-installer.exe"
    Get-File "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe" $innoSetup 5MB
    $innoDir = Join-Path $InstallerDir "tools\innosetup"
    New-Item -ItemType Directory -Force -Path $innoDir | Out-Null
    $log = Join-Path $PrereqDir "inno-install.log"
    Start-Process -FilePath $innoSetup -ArgumentList @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/CURRENTUSER",
        "/DIR=`"$innoDir`"",
        "/LOG=`"$log`""
    ) -Wait
    $iscc = Join-Path $innoDir "ISCC.exe"
}

if (-not $iscc) {
    throw "Inno Setup compiler (ISCC.exe) was not found after install."
}

Write-Host "=== Building installer ==="
& $iscc (Join-Path $InstallerDir "RemoteSupport.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$setup = Join-Path $ArtifactsDir "RemoteSupportSetup.exe"
if (-not (Test-Path $setup)) { throw "Setup file was not created: $setup" }

Write-Host ""
Write-Host "Setup ready:"
Write-Host $setup
Write-Host "Copy this one file to the other PC and run it as Administrator."
