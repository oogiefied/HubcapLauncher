param(
    [int]$DevToolsPort = 8080,
    [string]$DownloadScript = (Join-Path $PSScriptRoot "download_lua.ps1")
)

$ErrorActionPreference = "Stop"

function Receive-WebSocketText {
    param([System.Net.WebSockets.ClientWebSocket]$Socket)

    $buffer = [byte[]]::new(131072)
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
    } | ConvertTo-Json -Compress -Depth 30

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

function Invoke-CdpEval {
    param(
        [System.Net.WebSockets.ClientWebSocket]$Socket,
        [ref]$NextId,
        [string]$Expression
    )

    $id = $NextId.Value
    $NextId.Value += 1
    Send-Cdp -Socket $Socket -Id $id -Method "Runtime.evaluate" -Params @{
        expression = $Expression
        awaitPromise = $true
        returnByValue = $true
    }
    return Wait-CdpResponse -Socket $Socket -Id $id
}

function Invoke-HubcapEngine {
    param([string[]]$Arguments)

    if (-not (Test-Path -LiteralPath $DownloadScript)) {
        return @{ success = $false; error = "Hubcap helper engine not found: $DownloadScript" }
    }

    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $DownloadScript @Arguments 2>&1
    $text = ($output | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return @{ success = $false; error = "Hubcap helper returned no output." }
    }

    try {
        return $text | ConvertFrom-Json
    } catch {
        return @{ success = $false; error = $text }
    }
}

function Get-StoreTarget {
    param([int]$Port)

    $targets = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/list" -Method Get -TimeoutSec 5
    return @($targets | Where-Object {
        $_.type -eq "page" -and $_.url -match "https://store\.steampowered\.com/app/\d+"
    } | Select-Object -First 1)[0]
}

function Get-AppIdFromUrl {
    param([string]$Url)
    return [regex]::Match($Url, "/app/(\d+)").Groups[1].Value
}

function Resolve-SteamAppId {
    param([string]$VisibleAppId)

    $resolved = @{
        appId = $VisibleAppId
        visibleAppId = $VisibleAppId
        parentName = ""
        isDlc = $false
    }

    try {
        $payload = Invoke-RestMethod -Uri "https://store.steampowered.com/api/appdetails?appids=$VisibleAppId&filters=basic" -Method Get -TimeoutSec 10
        $data = $payload.$VisibleAppId.data
        if ($data.type -eq "dlc" -and $data.fullgame.appid -match "^\d+$") {
            $resolved.appId = [string]$data.fullgame.appid
            $resolved.parentName = [string]$data.fullgame.name
            $resolved.isDlc = $true
        }
    } catch {
        Write-Warning "Could not resolve Steam appdetails for $VisibleAppId. Using visible app id. $($_.Exception.Message)"
    }

    return $resolved
}

function Get-HubcapState {
    param([hashtable]$Resolved)

    $AppId = [string]$Resolved.appId

    $lua = Invoke-HubcapEngine -Arguments @("-AppId", $AppId, "-CheckLua")
    $stats = Invoke-HubcapEngine -Arguments @("-UserStats")
    $status = $null

    if ($lua.success -and -not $lua.exists) {
        $status = Invoke-HubcapEngine -Arguments @("-AppId", $AppId, "-StatusOnly")
    }

    $available = $true
    if ($status -ne $null -and $status.success) {
        $available = [bool]$status.available
    }

    $statusText = ""
    if (-not $lua.success) {
        $statusText = $lua.error
    } elseif ($status -ne $null -and -not $status.success) {
        $statusText = $status.error
    } elseif ($Resolved.isDlc) {
        $statusText = "DLC detected: using base game $AppId"
        if ($Resolved.parentName) {
            $statusText += " - $($Resolved.parentName)"
        }
    }

    return @{
        appId = $AppId
        visibleAppId = [string]$Resolved.visibleAppId
        parentName = [string]$Resolved.parentName
        isDlc = [bool]$Resolved.isDlc
        exists = [bool]$lua.exists
        available = $available
        statusText = $statusText
        statusError = (-not $lua.success) -or ($status -ne $null -and -not $status.success)
        usage = @{
            username = $stats.username
            dailyUsage = $stats.dailyUsage
            dailyLimit = if ($stats.dailyLimit) { $stats.dailyLimit } else { $stats.roleDailyLimit }
            apiKeyExpiresAt = $stats.apiKeyExpiresAt
            error = if ($stats.success -eq $false) { $stats.error } else { "" }
        }
    }
}

function Get-HubcapUsageState {
    $stats = Invoke-HubcapEngine -Arguments @("-UserStats")
    return @{
        usage = @{
            username = $stats.username
            dailyUsage = $stats.dailyUsage
            dailyLimit = if ($stats.dailyLimit) { $stats.dailyLimit } else { $stats.roleDailyLimit }
            apiKeyExpiresAt = $stats.apiKeyExpiresAt
            error = if ($stats.success -eq $false) { $stats.error } else { "" }
        }
    }
}

function ConvertTo-JsLiteral {
    param([object]$Value)
    return ($Value | ConvertTo-Json -Compress -Depth 20)
}

function Get-InjectScript {
    return @'
(() => {
  const ROOT_ID = "hubcap-cdp-ui";
  const STYLE_ID = "hubcap-cdp-ui-style";

  document.getElementById(ROOT_ID)?.remove();
  document.getElementById(STYLE_ID)?.remove();

  const style = document.createElement("style");
  style.id = STYLE_ID;
  style.textContent = `
    #${ROOT_ID} {
      align-items: center;
      display: flex;
      gap: 10px;
      justify-content: space-between;
      margin: 8px 0 18px;
      min-height: 34px;
      width: 100%;
    }
    #${ROOT_ID} .hp-left, #${ROOT_ID} .hp-right {
      align-items: center;
      display: flex;
      gap: 10px;
      min-width: 0;
    }
    #${ROOT_ID} .hp-left { flex: 1 1 auto; }
    #${ROOT_ID} .hp-right { flex: 0 0 auto; margin-left: auto; }
    #${ROOT_ID} button {
      background: linear-gradient(180deg, #376f91 0%, #23465d 100%);
      border: 1px solid rgba(103, 193, 245, .28);
      border-radius: 2px;
      box-shadow: inset 0 1px 0 rgba(255, 255, 255, .08), 0 1px 2px rgba(0, 0, 0, .28);
      color: #d6f4ff;
      cursor: pointer;
      font: 14px/30px Arial, Helvetica, sans-serif;
      min-height: 32px;
      padding: 0 14px;
      white-space: nowrap;
    }
    #${ROOT_ID} button:hover {
      background: linear-gradient(180deg, #437f9f 0%, #2b5772 100%);
      border-color: rgba(103, 193, 245, .42);
      color: #fff;
    }
    #${ROOT_ID} button:disabled { cursor: default; opacity: .72; }
    #${ROOT_ID} button[data-state="checking"], #${ROOT_ID} button[data-busy="true"] {
      align-items: center;
      display: inline-flex;
      gap: 8px;
    }
    #${ROOT_ID} button[data-state="checking"]::before, #${ROOT_ID} button[data-busy="true"]::before {
      animation: hubcap-cdp-spin .8s linear infinite;
      border: 2px solid rgba(214,244,255,.35);
      border-top-color: #d6f4ff;
      border-radius: 50%;
      content: "";
      height: 12px;
      width: 12px;
    }
    #${ROOT_ID} button[data-state="download"][data-denuvo="true"] {
      background: linear-gradient(180deg, #a56325 0%, #6f3c18 100%);
      border-color: rgba(246, 162, 58, .36);
      color: #ffe7c1;
      opacity: 1;
    }
    #${ROOT_ID} button[data-state="download"][data-denuvo="true"]:hover {
      background: linear-gradient(180deg, #bd7830 0%, #81481e 100%);
      border-color: rgba(246, 162, 58, .5);
      color: #fff5e6;
    }
    #${ROOT_ID} button[data-state="remove"] {
      background: linear-gradient(180deg, #8f3a36 0%, #5f211f 100%);
      border-color: rgba(217, 75, 63, .36);
      color: #ffe0dc;
      opacity: 1;
    }
    #${ROOT_ID} button[data-state="remove"]:hover {
      background: linear-gradient(180deg, #a44741 0%, #702a27 100%);
      border-color: rgba(217, 75, 63, .5);
      color: #fff1ef;
    }
    #${ROOT_ID} .hp-status {
      color: #acdbf5;
      font: 12px Arial, Helvetica, sans-serif;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    #${ROOT_ID} .hp-status[data-tone="error"] { color: #ff9b8f; font-weight: 700; }
    #${ROOT_ID} .hp-status[data-tone="success"] { color: #a4d007; font-weight: 700; }
    #${ROOT_ID} .hp-warning {
      color: #f7c46c;
      display: none;
      font: 12px Arial, Helvetica, sans-serif;
      font-weight: 700;
      white-space: nowrap;
    }
    #${ROOT_ID} .hp-warning[data-visible="true"] { display: inline-flex; }
    #${ROOT_ID} .hp-usage {
      background: rgba(13, 27, 39, 0.48);
      border: 1px solid rgba(103, 193, 245, 0.26);
      border-radius: 3px;
      color: #d6f4ff;
      cursor: pointer;
      font-family: Arial, Helvetica, sans-serif;
      min-width: 190px;
      padding: 7px 10px 8px;
    }
    #${ROOT_ID} .hp-usage-row, #${ROOT_ID} .hp-usage-bottom {
      align-items: center;
      display: flex;
      gap: 8px;
      justify-content: space-between;
    }
    #${ROOT_ID} .hp-usage-row { font-size: 12px; }
    #${ROOT_ID} .hp-usage-bottom { font-size: 12px; justify-content: flex-end; margin-top: 5px; }
    #${ROOT_ID} .hp-usage-name { color: #fff; font-weight: 700; }
    #${ROOT_ID} .hp-usage-expiry { color: #9fc9e0; font-size: 11px; }
    #${ROOT_ID} .hp-usage-bar {
      background: rgba(0, 0, 0, 0.26);
      border-radius: 999px;
      height: 4px;
      margin-top: 6px;
      overflow: hidden;
    }
    #${ROOT_ID} .hp-usage-fill {
      background: linear-gradient(90deg, #a4d007 0%, #67c1f5 100%);
      display: block;
      height: 100%;
      width: 0%;
    }
    #${ROOT_ID} .hp-usage-spinner {
      animation: hubcap-cdp-spin .8s linear infinite;
      border: 2px solid rgba(214,244,255,.28);
      border-top-color: #d6f4ff;
      border-radius: 50%;
      display: none;
      height: 10px;
      width: 10px;
    }
    #${ROOT_ID} .hp-usage-spinner[data-visible="true"] { display: inline-flex; }
    @keyframes hubcap-cdp-spin { to { transform: rotate(360deg); } }
  `;
  document.head.appendChild(style);

  const root = document.createElement("div");
  root.id = ROOT_ID;
  root.innerHTML = `
    <div class="hp-left">
      <button class="hp-main" type="button" data-state="checking" disabled>Checking...</button>
      <button class="hp-library" type="button" style="display:none">Go to Library</button>
      <span class="hp-status"></span>
      <span class="hp-warning">Warning: Denuvo / anti-tamper detected</span>
    </div>
    <div class="hp-right">
      <div class="hp-usage">
        <div class="hp-usage-row">
          <span class="hp-usage-name">Hubcap</span>
          <span class="hp-usage-expiry">Expires --</span>
        </div>
        <div class="hp-usage-bar"><span class="hp-usage-fill"></span></div>
        <div class="hp-usage-bottom">Daily Usage: <strong class="hp-usage-count">--/--</strong><span class="hp-usage-spinner"></span></div>
      </div>
    </div>
  `;

  const host = document.querySelector(".apphub_AppName")?.parentElement || document.querySelector("#game_highlights")?.parentElement;
  const highlights = document.querySelector("#game_highlights");
  if (host && highlights && highlights.parentElement === host) host.insertBefore(root, highlights);
  else if (host) host.appendChild(root);
  else document.body.prepend(root);

  function appIdFromUrl() {
    return location.pathname.match(/\/app\/(\d+)(?:\/|$)/)?.[1] || "";
  }

  function send(action) {
    const payload = JSON.stringify({ action, appId: appIdFromUrl(), href: location.href });
    if (typeof window.hubcapNative === "function") window.hubcapNative(payload);
    else console.warn("[Hubcap CDP] native binding unavailable", payload);
  }

  window.__hubcapCdpSetState = (state) => {
    const button = root.querySelector(".hp-main");
    const library = root.querySelector(".hp-library");
    const status = root.querySelector(".hp-status");
    const warning = root.querySelector(".hp-warning");
    const usage = root.querySelector(".hp-usage");
    const name = root.querySelector(".hp-usage-name");
    const expiry = root.querySelector(".hp-usage-expiry");
    const count = root.querySelector(".hp-usage-count");
    const fill = root.querySelector(".hp-usage-fill");
    const spinner = root.querySelector(".hp-usage-spinner");
    const denuvo = /denuvo|anti[-\s]?tamper/i.test(document.body?.innerText || "");

    warning.dataset.visible = denuvo ? "true" : "false";
    button.dataset.denuvo = denuvo ? "true" : "false";

    if (state.busy) {
      button.disabled = true;
      button.dataset.busy = "true";
      button.textContent = state.busyText || "Working...";
      status.textContent = state.statusText || "";
      status.dataset.tone = "idle";
      spinner.dataset.visible = state.usageBusy ? "true" : "false";
      return;
    }

    if (state.usageOnly) {
      const dailyUsage = Number(state.usage?.dailyUsage ?? 0);
      const dailyLimit = Number(state.usage?.dailyLimit ?? 0);
      name.textContent = state.usage?.username || "Hubcap";
      count.textContent = state.usage?.error ? "Limit Error" : `${dailyUsage}/${dailyLimit}`;
      fill.style.width = `${dailyLimit > 0 ? Math.min(100, Math.round((dailyUsage / dailyLimit) * 100)) : 0}%`;
      usage.title = state.usage?.error || "";
      spinner.dataset.visible = state.usageBusy ? "true" : "false";
      if (state.usage?.apiKeyExpiresAt) {
        const days = Math.max(0, Math.ceil((new Date(state.usage.apiKeyExpiresAt).getTime() - Date.now()) / 86400000));
        expiry.textContent = `Expires in ${days}d`;
      }
      return;
    }

    button.dataset.busy = "false";
    if (state.exists) {
      button.dataset.state = "remove";
      button.textContent = "Remove Lua";
      button.disabled = false;
      library.style.display = "inline-flex";
    } else if (state.available) {
      button.dataset.state = "download";
      button.textContent = "Download Lua";
      button.disabled = false;
      library.style.display = "none";
    } else {
      button.dataset.state = "unavailable";
      button.textContent = "Lua Unavailable";
      button.disabled = true;
      library.style.display = "none";
    }

    status.textContent = state.statusText || "";
    status.dataset.tone = state.statusTone || (state.statusError ? "error" : "idle");

    const dailyUsage = Number(state.usage?.dailyUsage ?? 0);
    const dailyLimit = Number(state.usage?.dailyLimit ?? 0);
    name.textContent = state.usage?.username || "Hubcap";
    count.textContent = state.usage?.error ? "Limit Error" : `${dailyUsage}/${dailyLimit}`;
    fill.style.width = `${dailyLimit > 0 ? Math.min(100, Math.round((dailyUsage / dailyLimit) * 100)) : 0}%`;
    spinner.dataset.visible = state.usageBusy ? "true" : "false";
    usage.title = state.usage?.error || "";
    if (state.usage?.apiKeyExpiresAt) {
      const days = Math.max(0, Math.ceil((new Date(state.usage.apiKeyExpiresAt).getTime() - Date.now()) / 86400000));
      expiry.textContent = `Expires in ${days}d`;
    }
  };

  root.querySelector(".hp-main").addEventListener("click", () => {
    const state = root.querySelector(".hp-main").dataset.state;
    if (state === "download") send("download");
    if (state === "remove") send("remove");
  });
  root.querySelector(".hp-library").addEventListener("click", () => send("library"));
  root.querySelector(".hp-usage").addEventListener("click", () => send("refresh"));

  if (window.__hubcapCdpRouteTimer) clearInterval(window.__hubcapCdpRouteTimer);
  let lastHubcapAppId = appIdFromUrl();
  window.__hubcapCdpRouteTimer = setInterval(() => {
    const nextAppId = appIdFromUrl();
    if (nextAppId && nextAppId !== lastHubcapAppId) {
      lastHubcapAppId = nextAppId;
      send("route");
    }
  }, 500);

  return { ok: true, href: location.href, appId: appIdFromUrl() };
})()
'@
}

function Set-PageState {
    param(
        [System.Net.WebSockets.ClientWebSocket]$Socket,
        [ref]$NextId,
        [hashtable]$State
    )

    $json = ConvertTo-JsLiteral $State
    $expression = "window.__hubcapCdpSetState && window.__hubcapCdpSetState($json)"
    $null = Invoke-CdpEval -Socket $Socket -NextId $NextId -Expression $expression
}

$target = $null
while (-not $target) {
    $target = Get-StoreTarget -Port $DevToolsPort
    if (-not $target) {
        Write-Host "Waiting for a Steam Store app page at http://127.0.0.1:$DevToolsPort/json/list..."
        Start-Sleep -Seconds 1
    }
}

$visibleAppId = Get-AppIdFromUrl -Url $target.url
if (-not $visibleAppId) {
    throw "Could not parse app id from target URL: $($target.url)"
}
$resolvedApp = Resolve-SteamAppId -VisibleAppId $visibleAppId
$appId = [string]$resolvedApp.appId

$socket = [System.Net.WebSockets.ClientWebSocket]::new()
$socket.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null

$nextId = 1

try {
    Send-Cdp -Socket $socket -Id $nextId -Method "Runtime.enable"
    $null = Wait-CdpResponse -Socket $socket -Id $nextId
    $nextId += 1

    Send-Cdp -Socket $socket -Id $nextId -Method "Runtime.addBinding" -Params @{ name = "hubcapNative" }
    $null = Wait-CdpResponse -Socket $socket -Id $nextId
    $nextId += 1

    $injectResult = Invoke-CdpEval -Socket $socket -NextId ([ref]$nextId) -Expression (Get-InjectScript)
    if ($injectResult.result.exceptionDetails) {
        throw ($injectResult.result.exceptionDetails | ConvertTo-Json -Compress -Depth 10)
    }

    $state = Get-HubcapState -Resolved $resolvedApp
    Set-PageState -Socket $socket -NextId ([ref]$nextId) -State $state

    Write-Host "Hubcap CDP prototype attached to app $appId."
    Write-Host "Keep this PowerShell window open. Press Ctrl+C to stop."

    while ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        $message = Receive-WebSocketText -Socket $socket
        $payload = $message | ConvertFrom-Json
        if ($payload.method -ne "Runtime.bindingCalled" -or $payload.params.name -ne "hubcapNative") {
            continue
        }

        $event = $payload.params.payload | ConvertFrom-Json
        $eventVisibleAppId = if ($event.appId -match "^\d+$") { [string]$event.appId } else { $visibleAppId }
        $eventResolvedApp = Resolve-SteamAppId -VisibleAppId $eventVisibleAppId
        $eventAppId = [string]$eventResolvedApp.appId
        $actionLabel = if ($eventResolvedApp.isDlc) { "parent game $eventAppId" } else { "app $eventAppId" }

        if ($event.action -eq "library") {
            $expression = "location.href = 'steam://nav/games/details/$eventAppId'"
            $null = Invoke-CdpEval -Socket $socket -NextId ([ref]$nextId) -Expression $expression
            continue
        }

        if ($event.action -eq "route") {
            Set-PageState -Socket $socket -NextId ([ref]$nextId) -State @{
                busy = $true
                busyText = "Checking..."
                statusText = "Checking Lua status..."
                usage = @{}
            }
            $state = Get-HubcapState -Resolved $eventResolvedApp
            Set-PageState -Socket $socket -NextId ([ref]$nextId) -State $state
            continue
        }

        if ($event.action -eq "download") {
            Set-PageState -Socket $socket -NextId ([ref]$nextId) -State @{
                busy = $true
                busyText = "Downloading..."
                statusText = "Downloading Lua for $actionLabel..."
                usage = @{}
            }
            $result = Invoke-HubcapEngine -Arguments @("-AppId", $eventAppId)
            $state = Get-HubcapState -Resolved $eventResolvedApp
            $state.statusText = if ($result.success) { "Added!" } else { $result.error }
            $state.statusTone = if ($result.success) { "success" } else { "error" }
            $state.statusError = -not [bool]$result.success
            Set-PageState -Socket $socket -NextId ([ref]$nextId) -State $state
            continue
        }

        if ($event.action -eq "remove") {
            Set-PageState -Socket $socket -NextId ([ref]$nextId) -State @{
                busy = $true
                busyText = "Removing..."
                statusText = "Removing Lua for $actionLabel..."
                usage = @{}
            }
            $result = Invoke-HubcapEngine -Arguments @("-AppId", $eventAppId, "-DeleteLua")
            $state = Get-HubcapState -Resolved $eventResolvedApp
            $state.statusText = if ($result.success) { "Removed!" } else { $result.error }
            $state.statusTone = if ($result.success) { "success" } else { "error" }
            $state.statusError = -not [bool]$result.success
            Set-PageState -Socket $socket -NextId ([ref]$nextId) -State $state
            continue
        }

        if ($event.action -eq "refresh") {
            Set-PageState -Socket $socket -NextId ([ref]$nextId) -State @{
                usageOnly = $true
                usageBusy = $true
                usage = @{}
            }
            $state = Get-HubcapUsageState
            $state.usageOnly = $true
            $state.usageBusy = $false
            Set-PageState -Socket $socket -NextId ([ref]$nextId) -State $state
        }
    }
} finally {
    if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        $socket.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    }
    $socket.Dispose()
}
