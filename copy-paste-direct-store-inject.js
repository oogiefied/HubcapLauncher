(() => {
  const ROOT_ID = "hubcap-direct-store-poc";
  const STYLE_ID = "hubcap-direct-store-poc-style";
  const API_BASE = "https://hubcapmanifest.com";

  function getAppIdFromUrl(win) {
    const match = win.location.pathname.match(/\/app\/(\d+)(?:\/|$)/);
    return match?.[1] || "";
  }

  function collectReachableWindows(rootWindow) {
    const windows = [];
    const seen = new Set();

    function visit(win) {
      if (!win || seen.has(win)) return;
      seen.add(win);
      windows.push(win);

      try {
        for (let index = 0; index < win.frames.length; index += 1) {
          visit(win.frames[index]);
        }
      } catch {
        // Cross-origin frames are expected in Steam.
      }
    }

    visit(rootWindow);

    try {
      const desktop = rootWindow.g_PopupManager?.GetExistingPopup?.("SP Desktop_uid0")?.window;
      visit(desktop);
    } catch {
      // Steam globals are not always available in every DevTools target.
    }

    return windows;
  }

  function findStoreWindow() {
    return collectReachableWindows(globalThis).find((win) => {
      try {
        return Boolean(getAppIdFromUrl(win) && win.document?.body);
      } catch {
        return false;
      }
    });
  }

  function runInStoreWindow(win) {
    const doc = win.document;
    const appId = getAppIdFromUrl(win);
    if (!appId) {
      console.warn("[Hubcap Direct POC] No /app/<appid> URL in this DevTools context.");
      return;
    }

    if (win.__hubcapDirectPocLocationTimer) {
      win.clearInterval(win.__hubcapDirectPocLocationTimer);
      delete win.__hubcapDirectPocLocationTimer;
    }

    doc.getElementById(ROOT_ID)?.remove();
    doc.getElementById(STYLE_ID)?.remove();

    const style = doc.createElement("style");
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

      #${ROOT_ID} .hp-left,
      #${ROOT_ID} .hp-right {
        align-items: center;
        display: flex;
        gap: 10px;
        min-width: 0;
      }

      #${ROOT_ID} .hp-left {
        flex: 1 1 auto;
      }

      #${ROOT_ID} .hp-right {
        flex: 0 0 auto;
        margin-left: auto;
      }

      #${ROOT_ID} button {
        background: linear-gradient(180deg, #376f91 0%, #23465d 100%);
        border: 1px solid rgba(103, 193, 245, 0.28);
        border-radius: 2px;
        box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.08), 0 1px 2px rgba(0, 0, 0, 0.28);
        color: #d7eef8;
        cursor: pointer;
        font: 14px/30px Arial, Helvetica, sans-serif;
        min-height: 32px;
        padding: 0 14px;
        white-space: nowrap;
      }

      #${ROOT_ID} button:hover {
        background: linear-gradient(180deg, #437f9f 0%, #2b5772 100%);
        border-color: rgba(103, 193, 245, 0.42);
        color: #fff;
      }

      #${ROOT_ID} button:disabled {
        cursor: default;
        opacity: 0.72;
      }

      #${ROOT_ID} button[data-state="download"][data-denuvo="true"] {
        background: linear-gradient(180deg, #a56325 0%, #6f3c18 100%);
        border-color: rgba(246, 162, 58, 0.36);
        color: #ffe7c1;
        opacity: 1;
      }

      #${ROOT_ID} button[data-state="download"][data-denuvo="true"]:hover {
        background: linear-gradient(180deg, #bd7830 0%, #81481e 100%);
        border-color: rgba(246, 162, 58, 0.5);
        color: #fff5e6;
      }

      #${ROOT_ID} button[data-state="remove"] {
        background: linear-gradient(180deg, #8f3a36 0%, #5f211f 100%);
        border-color: rgba(217, 75, 63, 0.36);
        color: #ffe0dc;
        opacity: 1;
      }

      #${ROOT_ID} button[data-state="remove"]:hover {
        background: linear-gradient(180deg, #a44741 0%, #702a27 100%);
        border-color: rgba(217, 75, 63, 0.5);
        color: #fff1ef;
      }

      #${ROOT_ID} .hp-status {
        color: #acdbf5;
        font: 12px Arial, Helvetica, sans-serif;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      #${ROOT_ID} .hp-status[data-tone="error"] {
        color: #ff9b8f;
        font-weight: 700;
      }

      #${ROOT_ID} .hp-status[data-tone="success"] {
        color: #a4d007;
        font-weight: 700;
      }

      #${ROOT_ID} .hp-warning {
        color: #f7c46c;
        display: none;
        font: 12px Arial, Helvetica, sans-serif;
        font-weight: 700;
        white-space: nowrap;
      }

      #${ROOT_ID} .hp-warning[data-visible="true"] {
        display: inline-flex;
      }

      #${ROOT_ID} .hp-usage {
        background: rgba(13, 27, 39, 0.48);
        border: 1px solid rgba(103, 193, 245, 0.26);
        border-radius: 3px;
        color: #d6f4ff;
        font-family: Arial, Helvetica, sans-serif;
        min-width: 190px;
        padding: 7px 10px 8px;
      }

      #${ROOT_ID} .hp-usage-row,
      #${ROOT_ID} .hp-usage-bottom {
        align-items: center;
        display: flex;
        gap: 8px;
        justify-content: space-between;
      }

      #${ROOT_ID} .hp-usage-row {
        font-size: 12px;
      }

      #${ROOT_ID} .hp-usage-bottom {
        font-size: 12px;
        justify-content: flex-end;
        margin-top: 5px;
      }

      #${ROOT_ID} .hp-usage-name {
        color: #fff;
        font-weight: 700;
      }

      #${ROOT_ID} .hp-usage-expiry {
        color: #9fc9e0;
        font-size: 11px;
      }

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
    `;
    doc.head.appendChild(style);

    const root = doc.createElement("div");
    root.id = ROOT_ID;

    const left = doc.createElement("div");
    left.className = "hp-left";

    const right = doc.createElement("div");
    right.className = "hp-right";

    const button = doc.createElement("button");
    button.type = "button";
    button.textContent = "Checking...";
    button.dataset.state = "checking";

    const library = doc.createElement("button");
    library.type = "button";
    library.textContent = "Go to Library";
    library.style.display = "none";

    const status = doc.createElement("span");
    status.className = "hp-status";

    const warning = doc.createElement("span");
    warning.className = "hp-warning";
    warning.textContent = "Warning: Denuvo / anti-tamper detected";

    const usage = doc.createElement("div");
    usage.className = "hp-usage";
    usage.innerHTML = `
      <div class="hp-usage-row">
        <span class="hp-usage-name">Hubcap</span>
        <span class="hp-usage-expiry">Expires --</span>
      </div>
      <div class="hp-usage-bar"><span class="hp-usage-fill"></span></div>
      <div class="hp-usage-bottom">Daily Usage: <strong class="hp-usage-count">--/--</strong></div>
    `;

    left.append(button, library, status, warning);
    right.append(usage);
    root.append(left, right);

    const host = doc.querySelector(".apphub_AppName")?.parentElement || doc.querySelector("#game_highlights")?.parentElement;
    const highlights = doc.querySelector("#game_highlights");
    if (host && highlights && highlights.parentElement === host) host.insertBefore(root, highlights);
    else if (host) host.appendChild(root);
    else doc.body.prepend(root);

    function setStatus(message, tone = "idle") {
      status.textContent = message;
      status.dataset.tone = tone;
    }

    function requireApiKey() {
      if (!win.__hubcapDirectPocApiKey) {
        const key = win.prompt("Hubcap API key for this JS-only test:");
        if (key) win.__hubcapDirectPocApiKey = key.trim();
      }
      return win.__hubcapDirectPocApiKey || "";
    }

    win.hubcapSetApiKey = (key) => {
      win.__hubcapDirectPocApiKey = String(key || "").trim();
      setStatus(win.__hubcapDirectPocApiKey ? "API key set for this JS session." : "API key cleared.", win.__hubcapDirectPocApiKey ? "success" : "error");
      void refreshButton();
      void refreshUsage();
    };

    async function hubcapJson(path) {
      const apiKey = requireApiKey();
      if (!apiKey) throw new Error("API key not set.");
      const response = await win.fetch(`${API_BASE}${path}`, {
        headers: { Authorization: `Bearer ${apiKey}` },
      });
      if (!response.ok) {
        if (response.status === 401) throw new Error("Invalid Hubcap API key.");
        throw new Error(`Hubcap returned HTTP ${response.status}.`);
      }
      return response.json();
    }

    async function resolveAppId(visibleAppId) {
      try {
        const response = await win.fetch(`https://store.steampowered.com/api/appdetails?appids=${visibleAppId}&filters=basic`, {
          credentials: "omit",
        });
        const payload = await response.json();
        const data = payload?.[visibleAppId]?.data;
        const fullGameAppId = data?.type === "dlc" ? data?.fullgame?.appid : null;
        if (fullGameAppId && /^\d+$/.test(String(fullGameAppId))) {
          return {
            appId: String(fullGameAppId),
            visibleAppId,
            parentName: data?.fullgame?.name || "",
            isDlc: true,
          };
        }
      } catch (error) {
        console.warn("[Hubcap Direct POC] DLC resolve failed:", error);
      }
      return { appId: visibleAppId, visibleAppId, parentName: "", isDlc: false };
    }

    async function refreshUsage() {
      const count = root.querySelector(".hp-usage-count");
      const fill = root.querySelector(".hp-usage-fill");
      const name = root.querySelector(".hp-usage-name");
      const expiry = root.querySelector(".hp-usage-expiry");
      if (!win.__hubcapDirectPocApiKey) {
        count.textContent = "--/--";
        usage.title = "Click Download Lua or run hubcapSetApiKey(\"YOUR_KEY\") to set an API key for this JS session.";
        return;
      }

      try {
        const payload = await hubcapJson("/api/v1/user/stats");
        const used = Number(payload.daily_usage ?? payload.api_key_usage_count ?? 0);
        const limit = Number(payload.daily_limit ?? payload.role_daily_limit ?? 0);
        count.textContent = `${used}/${limit}`;
        fill.style.width = `${limit > 0 ? Math.min(100, Math.round((used / limit) * 100)) : 0}%`;
        name.textContent = payload.username || "Hubcap";
        if (payload.api_key_expires_at) {
          const days = Math.max(0, Math.ceil((new Date(payload.api_key_expires_at).getTime() - Date.now()) / 86400000));
          expiry.textContent = `Expires in ${days}d`;
        }
      } catch (error) {
        count.textContent = "Limit Error";
        usage.title = error.message || String(error);
      }
    }

    async function refreshButton() {
      const resolved = await resolveAppId(appId);
      const denuvo = /denuvo|anti[-\s]?tamper/i.test(doc.body?.innerText || "");
      button.dataset.denuvo = denuvo ? "true" : "false";
      warning.dataset.visible = denuvo ? "true" : "false";

      setStatus(resolved.isDlc ? `DLC detected: using base game ${resolved.appId}${resolved.parentName ? ` - ${resolved.parentName}` : ""}` : "");
      button.disabled = true;
      button.textContent = "Checking...";
      button.dataset.state = "checking";

      try {
        if (!win.__hubcapDirectPocApiKey) {
          button.disabled = false;
          button.textContent = "Download Lua";
          button.dataset.state = "download";
          setStatus("API key not set. Click Download Lua or run hubcapSetApiKey(\"YOUR_KEY\").", "error");
          return;
        }

        const availability = await hubcapJson(`/api/v1/status/${encodeURIComponent(resolved.appId)}`);
        const available = availability?.status === "available" && availability?.manifest_file_exists === true && availability?.update_in_progress !== true;
        button.disabled = !available;
        button.textContent = available ? "Download Lua" : "Lua Unavailable";
        button.dataset.state = available ? "download" : "unavailable";
      } catch (error) {
        button.disabled = false;
        button.textContent = "Download Lua";
        button.dataset.state = "download";
        setStatus(error.message || String(error), "error");
      }

      button.onclick = async () => {
        if (button.dataset.state !== "download") return;
        try {
          button.disabled = true;
          button.textContent = "Downloading...";
          const response = await win.fetch(`${API_BASE}/api/v1/manifest/${encodeURIComponent(resolved.appId)}`, {
            headers: { Authorization: `Bearer ${requireApiKey()}` },
          });
          if (!response.ok) throw new Error(response.status === 401 ? "Invalid Hubcap API key." : `Hubcap returned HTTP ${response.status}.`);
          const blob = await response.blob();
          const url = win.URL.createObjectURL(blob);
          const link = doc.createElement("a");
          link.href = url;
          link.download = `hubcap-${resolved.appId}.zip`;
          doc.body.appendChild(link);
          link.click();
          link.remove();
          win.setTimeout(() => win.URL.revokeObjectURL(url), 2000);
          setStatus("Downloaded zip. JS-only test cannot install files automatically.", "success");
          library.style.display = "inline-flex";
          await refreshUsage();
        } catch (error) {
          setStatus(error.message || String(error), "error");
        } finally {
          button.disabled = false;
          button.textContent = "Download Lua";
        }
      };

      library.onclick = () => {
        win.location.href = `steam://nav/games/details/${resolved.appId}`;
      };
    }

    usage.onclick = () => void refreshUsage();
    void refreshButton();
    void refreshUsage();
    let lastHref = win.location.href;
    win.__hubcapDirectPocLocationTimer = win.setInterval(() => {
      if (win.location.href === lastHref) return;
      lastHref = win.location.href;
      if (getAppIdFromUrl(win)) {
        runInStoreWindow(win);
      } else {
        doc.getElementById(ROOT_ID)?.remove();
        doc.getElementById(STYLE_ID)?.remove();
      }
    }, 500);
    console.log(`[Hubcap Direct POC] Attached to Store app ${appId}.`);
  }

  const storeWindow = findStoreWindow();
  if (!storeWindow) {
    console.warn("[Hubcap Direct POC] Could not find a reachable Store /app/<appid> window. In DevTools, switch the console target to the Store page/webview if Steam exposes it.");
    return;
  }

  runInStoreWindow(storeWindow);
})();
