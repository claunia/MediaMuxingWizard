# Builds the Windows installer. Needs Inno Setup 6 or 7 (iscc.exe on PATH or in its default install folder).
# Usage: packaging\windows\build-installer.ps1 [-Arch x64|arm64] [-Version 0.1.0] [-Portable]
#   -Portable also writes a zip of the published files (no installer needed).
# Set MMW_BUNDLE_FFMPEG to a folder with the LGPL FFmpeg DLLs (packaging/ffmpeg/fetch-btbn.sh) to bundle them.
param([string]$Arch = "x64", [string]$Version = "0.1.0", [switch]$Portable)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$publish = "$root\artifacts\publish\win-$Arch"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
foreach ($project in "src\MMW.App\MMW.App.csproj", "src\MMW.Cli\MMW.Cli.csproj") {
    dotnet publish "$root\$project" -c Release -r "win-$Arch" -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}
if ($env:MMW_BUNDLE_FFMPEG) {
    New-Item -ItemType Directory -Force "$publish\ffmpeg" | Out-Null
    Get-ChildItem -File $env:MMW_BUNDLE_FFMPEG |
        Where-Object { $_.Name -match '^(avcodec|avformat|avutil|swresample|swscale)-\d+\.dll$|^COPYING\.LGPL|^FFMPEG-SOURCE\.txt$' } |
        Copy-Item -Destination "$publish\ffmpeg"
}

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = "${env:ProgramFiles}\Inno Setup 7\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw "Inno Setup (iscc.exe) not found" }
& $iscc "/DAppVersion=$Version" "/DArch=$Arch" "$PSScriptRoot\installer.iss"
if ($LASTEXITCODE -ne 0) { throw "iscc failed" }

if ($Portable) {
    $zip = "$root\artifacts\MediaMuxingWizard-$Version-win-$Arch.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$publish\*" -DestinationPath $zip
    Write-Host "Created $zip"
}
