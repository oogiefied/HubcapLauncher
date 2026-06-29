param(
    [int]$DevToolsPort = 8080,
    [string]$SteamExe = "",
    [switch]$RestartSteam
)

$ErrorActionPreference = "Stop"

function Get-SteamExe {
    param([string]$ExplicitPath)

    if ($ExplicitPath -and (Test-Path -LiteralPath $ExplicitPath)) {
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $registryPaths = @(
        "HKCU:\Software\Valve\Steam",
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam"
    )

    foreach ($path in $registryPaths) {
        try {
            $installPath = (Get-ItemProperty -LiteralPath $path -ErrorAction Stop).SteamPath
            $candidate = Join-Path $installPath "steam.exe"
            if (Test-Path -LiteralPath $candidate) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        } catch {}
    }

    $fallback = Join-Path ${env:ProgramFiles(x86)} "Steam\steam.exe"
    if (Test-Path -LiteralPath $fallback) {
        return (Resolve-Path -LiteralPath $fallback).Path
    }

    throw "steam.exe not found. Pass -SteamExe `"C:\Path\To\steam.exe`"."
}

function Wait-DevTools {
    param([int]$Port)

    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/json/list" -UseBasicParsing -TimeoutSec 1
            if ($response.StatusCode -eq 200) {
                return $true
            }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }

    return $false
}

$scriptRoot = Split-Path -Parent $PSCommandPath
$prototype = Join-Path $scriptRoot "Start-HubcapLauncherPrototype.ps1"
if (-not (Test-Path -LiteralPath $prototype)) {
    throw "CDP prototype not found: $prototype"
}

if ($RestartSteam) {
    Get-Process steam,steamwebhelper -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

$steam = Get-SteamExe -ExplicitPath $SteamExe
$steamArgs = @(
    "-dev",
    "-console",
    "-cef-enable-debugging",
    "-devtools-address", "127.0.0.1",
    "-devtools-port", [string]$DevToolsPort
)

Start-Process -FilePath $steam -ArgumentList $steamArgs
Write-Host "Steam launched with DevTools flags."

if (-not (Wait-DevTools -Port $DevToolsPort)) {
    throw "Steam DevTools did not become reachable at http://127.0.0.1:$DevToolsPort/json/list"
}

Write-Host "Steam DevTools is reachable at http://127.0.0.1:$DevToolsPort/json/list"
Write-Host "Starting Hubcap CDP prototype supervisor. Keep this window open."
Write-Host ""

while ($true) {
    try {
        & $prototype -DevToolsPort $DevToolsPort
    } catch {
        Write-Warning "Hubcap CDP prototype disconnected or failed: $($_.Exception.Message)"
    }

    Write-Host "Reattaching in 2 seconds..."
    Start-Sleep -Seconds 2
}
