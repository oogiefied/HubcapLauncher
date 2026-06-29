param(
    [int]$DevToolsPort = 8080,
    [string]$TargetUrlContains = "store.steampowered.com",
    [string]$Expression = ""
)

$ErrorActionPreference = "Stop"

function Receive-WebSocketText {
    param([System.Net.WebSockets.ClientWebSocket]$Socket)

    $buffer = [byte[]]::new(65536)
    $segment = [ArraySegment[byte]]::new($buffer)
    $builder = [System.Text.StringBuilder]::new()

    do {
        $result = $Socket.ReceiveAsync($segment, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
            throw "WebSocket closed by remote endpoint."
        }
        [void]$builder.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
    } while (-not $result.EndOfMessage)

    return $builder.ToString()
}

function Send-Cdp {
    param(
        [System.Net.WebSockets.ClientWebSocket]$Socket,
        [int]$Id,
        [string]$Method,
        [hashtable]$Params = @{}
    )

    $payload = @{
        id = $Id
        method = $Method
        params = $Params
    } | ConvertTo-Json -Compress -Depth 20

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $null = $Socket.SendAsync([ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

function Wait-CdpResponse {
    param(
        [System.Net.WebSockets.ClientWebSocket]$Socket,
        [int]$Id
    )

    while ($true) {
        $message = Receive-WebSocketText -Socket $Socket
        $payload = $message | ConvertFrom-Json
        if ($payload.id -eq $Id) {
            return $payload
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Expression)) {
    $Expression = @'
(() => {
  const id = "hubcap-cdp-proof";
  document.getElementById(id)?.remove();
  const marker = document.createElement("div");
  marker.id = id;
  marker.textContent = "Hubcap CDP Injected";
  marker.style.cssText = "display:inline-flex;margin-left:12px;padding:0 10px;height:30px;align-items:center;background:linear-gradient(180deg,#376f91,#23465d);border:1px solid rgba(103,193,245,.28);color:#d7eef8;border-radius:2px;font:14px Arial;box-shadow:inset 0 1px 0 rgba(255,255,255,.08),0 1px 2px rgba(0,0,0,.28);";
  const title = document.querySelector(".apphub_AppName") || document.querySelector("h1");
  if (title?.parentElement) title.insertAdjacentElement("afterend", marker);
  else document.body.prepend(marker);
  return { ok: true, href: location.href, title: document.title };
})()
'@
}

$targets = Invoke-RestMethod -Uri "http://127.0.0.1:$DevToolsPort/json/list" -Method Get -TimeoutSec 5
$target = @($targets | Where-Object {
    $_.type -eq "page" -and (
        $_.url -like "*$TargetUrlContains*" -or
        $_.title -like "*$TargetUrlContains*"
    )
} | Select-Object -First 1)[0]

if (-not $target) {
    throw "No DevTools target found containing '$TargetUrlContains'."
}

$socket = [System.Net.WebSockets.ClientWebSocket]::new()
$null = $socket.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

try {
    Send-Cdp -Socket $socket -Id 1 -Method "Runtime.enable"
    $null = Wait-CdpResponse -Socket $socket -Id 1

    Send-Cdp -Socket $socket -Id 2 -Method "Runtime.evaluate" -Params @{
        expression = $Expression
        awaitPromise = $true
        returnByValue = $true
    }
    $result = Wait-CdpResponse -Socket $socket -Id 2

    [pscustomobject]@{
        targetTitle = $target.title
        targetUrl = $target.url
        result = $result.result.result.value
        exception = $result.result.exceptionDetails
    } | ConvertTo-Json -Depth 12
} finally {
    if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        $null = $socket.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    }
    $socket.Dispose()
}
