$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "HubcapLauncher.csproj"
dotnet publish $project `
    -c Release `
    -r win-x86 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true

Write-Host ""
Write-Host "Published:"
Write-Host (Join-Path $PSScriptRoot "bin\Release\net9.0\win-x86\publish\HubcapLauncher.exe")
