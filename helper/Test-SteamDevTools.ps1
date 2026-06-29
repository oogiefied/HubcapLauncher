param(
    [int[]]$Port = @(8080, 27060, 9222)
)

$ErrorActionPreference = "Stop"

function Test-Endpoint {
    param(
        [int]$CandidatePort,
        [string]$Path
    )

    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$CandidatePort$Path" -UseBasicParsing -TimeoutSec 3
        return [pscustomobject]@{
            ok = $true
            status = $response.StatusCode
            content = $response.Content
            error = $null
        }
    } catch {
        return [pscustomobject]@{
            ok = $false
            status = $null
            content = $null
            error = $_.Exception.Message
        }
    }
}

$found = $false
foreach ($candidatePort in $Port) {
    $root = Test-Endpoint -CandidatePort $candidatePort -Path "/"
    $json = Test-Endpoint -CandidatePort $candidatePort -Path "/json"
    $list = Test-Endpoint -CandidatePort $candidatePort -Path "/json/list"
    $version = Test-Endpoint -CandidatePort $candidatePort -Path "/json/version"

    [pscustomobject]@{
        port = $candidatePort
        root = $root.ok
        json = $json.ok
        list = $list.ok
        version = $version.ok
        rootError = $root.error
        jsonError = $json.error
        listError = $list.error
        versionError = $version.error
    } | Format-List

    if ($root.ok -or $json.ok -or $list.ok -or $version.ok) {
        $found = $true
        if ($list.ok) {
            Write-Host ""
            Write-Host "DevTools target list is reachable at http://127.0.0.1:$candidatePort/json/list"
            Write-Host ($list.content.Substring(0, [Math]::Min(1200, $list.content.Length)))
        } elseif ($root.ok) {
            Write-Host ""
            Write-Host "DevTools root page is reachable at http://127.0.0.1:$candidatePort/"
            Write-Host ($root.content.Substring(0, [Math]::Min(1200, $root.content.Length)))
        }
        break
    }
}

if (-not $found) {
    Write-Host ""
    Write-Host "Steam DevTools target endpoints were not reachable on the tested ports."
    Write-Host "Try launching Steam with: -dev -cef-enable-debugging -devtools-address 127.0.0.1 -devtools-port 8080"
    Write-Host ""
    Write-Host "Steam-owned listening ports:"
    $steamPids = @(Get-Process steam,steamwebhelper -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $steamPids -contains $_.OwningProcess } |
        Select-Object LocalAddress,LocalPort,State,OwningProcess |
        Sort-Object LocalPort |
        Format-Table
    exit 1
}
