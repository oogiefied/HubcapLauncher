$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "HubcapLauncher.csproj"
$publishDir = Join-Path $PSScriptRoot "bin\Release\net9.0-windows10.0.17763.0\win-x64\publish"

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true

Write-Host ""
Write-Host "Published:"
Write-Host (Join-Path $publishDir "HubcapLauncher.exe")
