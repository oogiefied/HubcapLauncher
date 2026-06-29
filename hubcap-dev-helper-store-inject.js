(() => {
  const ROOT_ID = "hubcap-dev-helper-poc";
  const STYLE_ID = "hubcap-dev-helper-poc-style";
  const HELPER_BASE = "http://127.0.0.1:18714";

  function getAppIdFromUrl() {
    return window.location.pathname.match(/\/app\/(\d+)(?:\/|$)/)?.[1] || "";
  }

  function setJsonTitle(element, value) {
    try {
      element.title = JSON.stringify(value, null, 2);
    } catch {
      element.title = String(value || "");
    }
  }

  async function helperJson(path) {
    const response = await fetch(`${HELPER_BASE}${path}`, { method: "GET" });
    const text = await response.text();
    let payload;
    try {
      payload = JSON.parse(text);
    } catch {
      throw new Error(text || `Helper returned HTTP ${response.status}.`);
    }
    if (!response.ok || payload.success === false) {
      throw new Error(payload.error || `Helper returned HTTP ${response.status}.`);
    }
    return payload;
  }

  async function resolveAppId(visibleAppId) {
    try {
      const response = await fetch(`https://store.steampowered.com/api/appdetails?appids=${visibleAppId}&filters=basic`, {
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
      console.warn("[Hubcap Dev Helper] DLC resolve failed:", error);
    }
    return { appId: visibleAppId, visibleAppId, parentName: "", isDlc: false };
  }

  function injectStyles() {
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
      #${ROOT_ID} .hp-left,
      #${ROOT_ID} .hp-right {
        align-items: center;
        display: flex;
        gap: 10px;
        min-width: 0;
      }
      #${ROOT_ID} .hp-left { flex: 1 1 auto; }
      #${ROOT_ID} .hp-right { flex: 0 0 auto; margin-left: auto; }
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
        cursor: pointer;
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
      #${ROOT_ID} .hp-usage-row { font-size: 12px; }
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
    document.head.appendChild(style);
  }

  function findHost() {
    return document.querySelector(".apphub_AppName")?.parentElement || document.querySelector("#game_highlights")?.parentElement;
  }

  function createRoot() {
    const root = document.createElement("div");
    root.id = ROOT_ID;

    const left = document.createElement("div");
    left.className = "hp-left";

    const right = document.createElement("div");
    right.className = "hp-right";

    const button = document.createElement("button");
    button.type = "button";
    button.className = "hp-main";
    button.textContent = "Checking...";
    button.dataset.state = "checking";

    const library = document.createElement("button");
    library.type = "button";
    library.className = "hp-library";
    library.textContent = "Go to Library";
    library.style.display = "none";

    const status = document.createElement("span");
    status.className = "hp-status";

    const warning = document.createElement("span");
    warning.className = "hp-warning";
    warning.textContent = "Warning: Denuvo / anti-tamper detected";

    const usage = document.createElement("div");
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
    return root;
  }

  function setStatus(root, message, tone = "idle") {
    const status = root.querySelector(".hp-status");
    status.textContent = message;
    status.dataset.tone = tone;
  }

  async function refreshUsage(root) {
    const usage = root.querySelector(".hp-usage");
    const count = root.querySelector(".hp-usage-count");
    const fill = root.querySelector(".hp-usage-fill");
    const name = root.querySelector(".hp-usage-name");
    const expiry = root.querySelector(".hp-usage-expiry");

    try {
      const payload = await helperJson("/stats");
      const used = Number(payload.dailyUsage ?? payload.apiKeyUsageCount ?? 0);
      const limit = Number(payload.dailyLimit ?? payload.roleDailyLimit ?? 0);
      count.textContent = `${used}/${limit}`;
      fill.style.width = `${limit > 0 ? Math.min(100, Math.round((used / limit) * 100)) : 0}%`;
      name.textContent = payload.username || "Hubcap";
      if (payload.apiKeyExpiresAt) {
        const days = Math.max(0, Math.ceil((new Date(payload.apiKeyExpiresAt).getTime() - Date.now()) / 86400000));
        expiry.textContent = `Expires in ${days}d`;
      }
      setJsonTitle(usage, payload);
    } catch (error) {
      count.textContent = "Limit Error";
      usage.title = error.message || String(error);
    }
  }

  async function refreshRoot() {
    const visibleAppId = getAppIdFromUrl();
    if (!visibleAppId) {
      document.getElementById(ROOT_ID)?.remove();
      document.getElementById(STYLE_ID)?.remove();
      return;
    }

    injectStyles();

    let root = document.getElementById(ROOT_ID);
    if (!root) {
      root = createRoot();
      const host = findHost();
      const highlights = document.querySelector("#game_highlights");
      if (host && highlights && highlights.parentElement === host) host.insertBefore(root, highlights);
      else if (host) host.appendChild(root);
      else document.body.prepend(root);
    }

    const button = root.querySelector(".hp-main");
    const library = root.querySelector(".hp-library");
    const warning = root.querySelector(".hp-warning");
    const denuvo = /denuvo|anti[-\s]?tamper/i.test(document.body?.innerText || "");
    warning.dataset.visible = denuvo ? "true" : "false";
    button.dataset.denuvo = denuvo ? "true" : "false";

    const resolved = await resolveAppId(visibleAppId);
    setStatus(root, resolved.isDlc ? `DLC detected: using base game ${resolved.appId}${resolved.parentName ? ` - ${resolved.parentName}` : ""}` : "");

    try {
      button.disabled = true;
      button.textContent = "Checking...";
      button.dataset.state = "checking";

      const lua = await helperJson(`/check?appid=${encodeURIComponent(resolved.appId)}`);
      if (lua.exists === true) {
        button.textContent = "Remove Lua";
        button.dataset.state = "remove";
        library.style.display = "inline-flex";
      } else {
        const availability = await helperJson(`/status?appid=${encodeURIComponent(resolved.appId)}`);
        const available = availability.available === true;
        button.textContent = available ? "Download Lua" : "Lua Unavailable";
        button.dataset.state = available ? "download" : "unavailable";
        button.disabled = !available;
        library.style.display = "none";
      }
      button.disabled = button.dataset.state === "unavailable";
    } catch (error) {
      button.textContent = "Download Lua";
      button.dataset.state = "download";
      button.disabled = false;
      setStatus(root, error.message || String(error), "error");
    }

    button.onclick = async () => {
      if (button.dataset.state === "unavailable") return;
      const action = button.dataset.state === "remove" ? "remove" : "download";
      try {
        button.disabled = true;
        button.textContent = action === "remove" ? "Removing..." : "Downloading...";
        const result = await helperJson(`/${action}?appid=${encodeURIComponent(resolved.appId)}`);
        setStatus(root, action === "remove" ? "Removed!" : "Added!", "success");
        setJsonTitle(button, result);
        await refreshUsage(root);
      } catch (error) {
        setStatus(root, error.message || String(error), "error");
      } finally {
        button.disabled = false;
        window.setTimeout(refreshRoot, 250);
      }
    };

    library.onclick = () => {
      window.location.href = `steam://nav/games/details/${resolved.appId}`;
    };

    root.querySelector(".hp-usage").onclick = () => void refreshUsage(root);
    void refreshUsage(root);
  }

  if (window.__hubcapDevHelperTimer) {
    window.clearInterval(window.__hubcapDevHelperTimer);
  }

  let lastHref = "";
  const tick = () => {
    if (window.location.href === lastHref && document.getElementById(ROOT_ID)) return;
    lastHref = window.location.href;
    void refreshRoot();
  };

  tick();
  window.__hubcapDevHelperTimer = window.setInterval(tick, 500);
  window.__hubcapDevHelperCleanup = () => {
    window.clearInterval(window.__hubcapDevHelperTimer);
    document.getElementById(ROOT_ID)?.remove();
    document.getElementById(STYLE_ID)?.remove();
  };

  console.log("[Hubcap Dev Helper] Loaded. Cleanup with window.__hubcapDevHelperCleanup?.()");
})();
