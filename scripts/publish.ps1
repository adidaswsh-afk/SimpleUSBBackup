$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet run --project tests/SimpleUSBBackup.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Backup integration tests failed.' }
    dotnet publish SimpleUSBBackup.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false -o artifacts/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Write-Host 'Ready: artifacts/win-x64/SimpleUSBBackup.exe'
} finally {
    Pop-Location
}
