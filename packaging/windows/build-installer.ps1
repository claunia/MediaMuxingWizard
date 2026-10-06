# Builds the Windows installer. Needs Inno Setup 6 (iscc.exe) on PATH.
# Usage: packaging\windows\build-installer.ps1 [-Arch x64|arm64] [-Version 0.1.0]
param([string]$Arch = "x64", [string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
foreach ($project in "src\MMW.App\MMW.App.csproj", "src\MMW.Cli\MMW.Cli.csproj") {
    dotnet publish "$root\$project" -c Release -r "win-$Arch" --self-contained true `
        -p:Version=$Version -p:PublishReadyToRun=true -p:DebugType=none -o "$root\artifacts\publish\win-$Arch"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}
iscc "/DAppVersion=$Version" "/DArch=$Arch" "$PSScriptRoot\installer.iss"
