(() => {
  function getSteamDesktopWindow() {
    try {
      return globalThis.g_PopupManager?.GetExistingPopup?.("SP Desktop_uid0")?.window || null;
    } catch (error) {
      console.warn("[Hubcap JS POC] Could not access Steam desktop popup:", error);
      return null;
    }
  }

  function runHubcapJsPoc() {
const ROOT_ID = "oogiefied-hubcap-js-poc";
  const STYLE_ID = "oogiefied-hubcap-js-poc-style";
  const API_BASE = "https://hubcapmanifest.com";
  const STORE_APP_RE = /\/app\/(\d+)(?:\/|$)/;
  const LIBRARY_APP_RE = /^\/(?:routes\/)?library\/app\/(\d+)(?:\/|$)/;

  const state = {
    apiKey: globalThis.__hubcapJsApiKey || "",
    currentAppId: "",
    currentVisibleAppId: "",
    currentPageName: "",
    parentName: "",
    isDlc: false,
    lastUrl: "",
    lastLibraryPath: "",
    busy: false,
  };

  function cleanup() {
    document.getElementById(ROOT_ID)?.remove();
    document.getElementById(STYLE_ID)?.remove();
    delete globalThis.__hubcapJsCleanup;
  }

  cleanup();

  function injectStyles() {
    const style = document.createElement("style");
    style.id = STYLE_ID;
    style.textContent = `
      #${ROOT_ID} {
        align-items: center;
        display: flex;
        gap: 10px;
        margin: 8px 0 18px;
        min-height: 34px;
        width: 100%;
      }

      #${ROOT_ID} .hp-left,
      #${ROOT_ID} .hp-right {
        align-items: center;
        display: flex;
        gap: 10px;
      }

      #${ROOT_ID} .hp-right {
        margin-left: auto;
      }

      #${ROOT_ID} button {
        background: linear-gradient(180deg, #376f91 0%, #23465d 100%);
        border: 1px solid rgba(103, 193, 245, 0.28);
        border-radius: 2px;
        box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.08), 0 1px 2px rgba(0, 0, 0, 0.28);
        color: #d7eef8;
        cursor: pointer;
        font-family: Arial, Helvetica, sans-serif;
        font-size: 14px;
        line-height: 30px;
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
        filter: grayscale(0.35);
        opacity: 0.72;
      }

      #${ROOT_ID} button[data-state="download"][data-denuvo="true"] {
        background: linear-gradient(180deg, #a56325 0%, #6f3c18 100%);
        border-color: rgba(246, 162, 58, 0.36);
        color: #ffe7c1;
        filter: none;
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
        filter: none;
        opacity: 1;
      }

      #${ROOT_ID} button[data-state="remove"]:hover {
        background: linear-gradient(180deg, #a44741 0%, #702a27 100%);
        border-color: rgba(217, 75, 63, 0.5);
        color: #fff1ef;
      }

      #${ROOT_ID} .hp-status {
        color: #9fc9e0;
        font: 12px Arial, Helvetica, sans-serif;
        min-height: 16px;
      }

      #${ROOT_ID} .hp-status[data-tone="error"] {
        color: #ff8f8f;
        font-weight: 700;
      }

      #${ROOT_ID} .hp-status[data-tone="success"] {
        color: #8bc53f;
        font-weight: 700;
      }

      #${ROOT_ID} .hp-warning {
        color: #ffc04d;
        display: none;
        font: 12px Arial, Helvetica, sans-serif;
        font-weight: 700;
      }

      #${ROOT_ID} .hp-warning[data-visible="true"] {
        display: inline-flex;
      }

      #${ROOT_ID} .hp-usage {
        background: rgba(13, 27, 39, 0.48);
        border: 1px solid rgba(103, 193, 245, 0.26);
        border-radius: 3px;
        box-shadow: 0 1px 8px rgba(0, 0, 0, 0.18);
        color: #d6f4ff;
        cursor: pointer;
        font-family: Arial, Helvetica, sans-serif;
        min-width: 190px;
        padding: 7px 10px 8px;
      }

      #${ROOT_ID} .hp-usage-row {
        align-items: center;
        display: flex;
        gap: 12px;
        justify-content: space-between;
      }

      #${ROOT_ID} .hp-usage-name {
        color: #fff;
        font-size: 12px;
        font-weight: 700;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      #${ROOT_ID} .hp-usage-expiry {
        color: #9fc9e0;
        font-size: 11px;
        white-space: nowrap;
      }

      #${ROOT_ID} .hp-usage-bar {
        background: rgba(0, 0, 0, 0.26);
        border-radius: 999px;
        height: 4px;
        margin: 6px 0;
        overflow: hidden;
      }

      #${ROOT_ID} .hp-usage-fill {
        background: linear-gradient(90deg, #a4d007 0%, #67c1f5 100%);
        display: block;
        height: 100%;
        transition: width 180ms ease;
        width: 0%;
      }

      #${ROOT_ID} .hp-usage-bottom {
        align-items: center;
        display: flex;
        font-size: 12px;
        gap: 6px;
        justify-content: flex-end;
        white-space: nowrap;
      }
    `;
    document.head.appendChild(style);
  }

  function appIdFromText(value) {
    return (
      STORE_APP_RE.exec(value || "")?.[1] ||
      /store\.steampowered\.com\/app\/(\d+)(?:\/|$)/.exec(value || "")?.[1] ||
      /\/(?:routes\/)?store\/app\/(\d+)(?:\/|$)/.exec(value || "")?.[1] ||
      ""
    );
  }

  function getSteamStoreAppId() {
    const direct =
      appIdFromText(location.href) ||
      appIdFromText(location.pathname) ||
      appIdFromText(document.URL) ||
      appIdFromText(globalThis.MainWindowBrowserManager?.m_lastLocation?.pathname || "") ||
      appIdFromText(globalThis.MainWindowBrowserManager?.m_lastLocation?.search || "");
    return direct || "";
  }

  function getSteamLibraryAppId() {
    const route = globalThis.MainWindowBrowserManager?.m_lastLocation;
    return LIBRARY_APP_RE.exec(route?.pathname || "")?.[1] || "";
  }

  function hasDenuvoWarning() {
    return /denuvo|anti[-\s]?tamper/i.test(document.body?.innerText || "");
  }

  function getDaysUntil(dateValue) {
    if (!dateValue) return null;
    const date = new Date(dateValue.endsWith("Z") ? dateValue : `${dateValue}Z`);
    const time = date.getTime();
    if (!Number.isFinite(time)) return null;
    return Math.max(0, Math.ceil((time - Date.now()) / 86400000));
  }

  function setStatus(message, tone = "idle") {
    const status = document.querySelector(`#${ROOT_ID} .hp-status`);
    if (!status) return;
    status.textContent = message;
    status.dataset.tone = tone;
  }

  function requireApiKey() {
    if (state.apiKey) return true;
    const value = prompt("Hubcap API key for this JS-injection session only:");
    if (!value) {
      setStatus("API key not set.", "error");
      return false;
    }
    state.apiKey = value.trim();
    globalThis.__hubcapJsApiKey = state.apiKey;
    return true;
  }

  async function hubcapJson(path) {
    if (!requireApiKey()) throw new Error("API key not set.");
    const response = await fetch(`${API_BASE}${path}`, {
      headers: { Authorization: `Bearer ${state.apiKey}` },
    });

    if (!response.ok) {
      if (response.status === 401) throw new Error("Invalid Hubcap API key.");
      throw new Error(`Hubcap returned HTTP ${response.status}.`);
    }

    return response.json();
  }

  async function resolveStoreAppId(visibleAppId) {
    if (!visibleAppId) return null;

    try {
      const response = await fetch(`https://store.steampowered.com/api/appdetails?appids=${visibleAppId}&filters=basic`, {
        credentials: "omit",
      });
      const payload = await response.json();
      const data = payload?.[visibleAppId]?.data;
      const fullGameAppId = data?.type === "dlc" ? data?.fullgame?.appid : null;
      const pageName = data?.name || "";

      if (fullGameAppId && /^\d+$/.test(String(fullGameAppId))) {
        return {
          appId: String(fullGameAppId),
          visibleAppId,
          pageName,
          parentName: data?.fullgame?.name || "",
          isDlc: true,
        };
      }

      return { appId: visibleAppId, visibleAppId, pageName, parentName: "", isDlc: false };
    } catch (error) {
      console.warn("[Hubcap JS POC] Failed Steam DLC resolve:", error);
    }

    return { appId: visibleAppId, visibleAppId, pageName: "", parentName: "", isDlc: false };
  }

  async function checkAvailability(appId) {
    const payload = await hubcapJson(`/api/v1/status/${encodeURIComponent(appId)}`);
    return payload?.status === "available" && payload?.manifest_file_exists === true && payload?.update_in_progress !== true;
  }

  async function refreshUsage() {
    const usage = document.querySelector(`#${ROOT_ID} .hp-usage`);
    const name = document.querySelector(`#${ROOT_ID} .hp-usage-name`);
    const expiry = document.querySelector(`#${ROOT_ID} .hp-usage-expiry`);
    const count = document.querySelector(`#${ROOT_ID} .hp-usage-count`);
    const fill = document.querySelector(`#${ROOT_ID} .hp-usage-fill`);
    if (!usage || !name || !expiry || !count || !fill) return;

    try {
      count.textContent = "--/--";
      const payload = await hubcapJson("/api/v1/user/stats");
      const dailyUsage = Number(payload.daily_usage ?? payload.api_key_usage_count ?? 0);
      const limit = Number(payload.role_daily_limit ?? payload.daily_limit ?? 0);
      const percent = limit > 0 ? Math.max(0, Math.min(100, Math.round((dailyUsage / limit) * 100))) : 0;
      const days = getDaysUntil(payload.api_key_expires_at);

      name.textContent = payload.username || "Hubcap";
      expiry.textContent = days !== null ? `Expires in ${days}d` : "Expires --";
      count.textContent = `${dailyUsage}/${limit || "--"}`;
      fill.style.width = `${percent}%`;
      usage.title = [
        payload.username ? `Hubcap user: ${payload.username}` : "",
        `Daily usage: ${dailyUsage}/${limit || "--"}`,
        days !== null ? `API key expires in ${days} day${days === 1 ? "" : "s"}` : "",
      ].filter(Boolean).join("\n");
    } catch (error) {
      count.textContent = "Limit Error";
      setStatus(error.message || String(error), "error");
    }
  }

  async function downloadManifestZip(appId) {
    if (!requireApiKey()) return;

    setStatus("Downloading zip...", "idle");
    const response = await fetch(`${API_BASE}/api/v1/manifest/${encodeURIComponent(appId)}`, {
      headers: { Authorization: `Bearer ${state.apiKey}` },
    });

    if (!response.ok) {
      if (response.status === 401) throw new Error("Invalid Hubcap API key.");
      throw new Error(`Download failed: HTTP ${response.status}.`);
    }

    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `hubcap-${appId}.zip`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
    setStatus("Downloaded zip. Pure JS cannot install files automatically.", "success");
    await refreshUsage();
  }

  function goToLibrary(appId) {
    const nav = globalThis.Navigation;
    if (nav?.Navigate) nav.Navigate(`/library/app/${appId}`);
    else location.href = `steam://nav/games/details/${appId}`;
  }

  function isVisibleElement(element) {
    if (!element) return false;
    const rect = element.getBoundingClientRect();
    const styles = getComputedStyle(element);
    return rect.width > 0 && rect.height > 0 && styles.display !== "none" && styles.visibility !== "hidden";
  }

  function normalizeTitle(value) {
    return String(value || "").replace(/\s+/g, " ").trim().toLowerCase();
  }

  function findStoreAnchor(pageName) {
    const knownAnchor =
      document.querySelector(".apphub_AppName") ||
      document.querySelector("#appHubAppName") ||
      document.querySelector("[data-featuretarget='app-title']");
    if (isVisibleElement(knownAnchor)) return knownAnchor;

    const wanted = normalizeTitle(pageName);
    if (wanted) {
      const textAnchor = Array.from(document.querySelectorAll("h1, h2, .page_title_area div, .apphub_AppName"))
        .find((element) => isVisibleElement(element) && normalizeTitle(element.textContent) === wanted);
      if (textAnchor) return textAnchor;
    }

    return null;
  }

  function createFloatingFallback(reason) {
    let root = document.getElementById(ROOT_ID);
    if (!root) {
      root = createRoot();
      root.style.position = "fixed";
      root.style.right = "24px";
      root.style.bottom = "72px";
      root.style.zIndex = "999999";
      root.style.background = "rgba(18, 28, 40, 0.94)";
      root.style.border = "1px solid rgba(103, 193, 245, 0.35)";
      root.style.borderRadius = "4px";
      root.style.boxShadow = "0 4px 18px rgba(0, 0, 0, 0.35)";
      root.style.margin = "0";
      root.style.padding = "10px";
      root.style.width = "min(520px, calc(100vw - 48px))";
      document.body.appendChild(root);
    }

    const main = root.querySelector(".hp-main");
    const library = root.querySelector(".hp-library");
    main.disabled = true;
    main.textContent = "JS POC Loaded";
    main.dataset.state = "checking";
    library.style.display = "none";
    setStatus(reason, "error");
    console.log("[Hubcap JS POC] Diagnostic", {
      reason,
      href: location.href,
      pathname: location.pathname,
      documentUrl: document.URL,
      title: document.title,
      steamRoute: globalThis.MainWindowBrowserManager?.m_lastLocation,
      canonical: document.querySelector('link[rel="canonical"]')?.href || null,
      storeLinks: Array.from(document.querySelectorAll("a[href]")).map((link) => link.href).filter((href) => /store\.steampowered\.com\/app\//.test(href)).slice(0, 10),
    });
    return root;
  }

  function createRoot() {
    const root = document.createElement("div");
    root.id = ROOT_ID;

    const left = document.createElement("div");
    left.className = "hp-left";

    const right = document.createElement("div");
    right.className = "hp-right";

    const main = document.createElement("button");
    main.className = "hp-main";
    main.type = "button";
    main.textContent = "Checking...";
    main.dataset.state = "checking";

    const library = document.createElement("button");
    library.className = "hp-library";
    library.type = "button";
    library.textContent = "Go to Library";
    library.style.display = "none";

    const status = document.createElement("span");
    status.className = "hp-status";

    const warning = document.createElement("span");
    warning.className = "hp-warning";
    warning.textContent = "Warning: Denuvo / anti-tamper detected";

    const usage = document.createElement("div");
    usage.className = "hp-usage";
    usage.title = "Hubcap usage details";
    usage.innerHTML = `
      <div class="hp-usage-row">
        <span class="hp-usage-name">Hubcap</span>
        <span class="hp-usage-expiry">Expires --</span>
      </div>
      <div class="hp-usage-bar"><span class="hp-usage-fill"></span></div>
      <div class="hp-usage-bottom">Daily Usage: <strong class="hp-usage-count">--/--</strong></div>
    `;
    usage.addEventListener("click", () => void refreshUsage());

    left.appendChild(main);
    left.appendChild(library);
    left.appendChild(status);
    left.appendChild(warning);
    right.appendChild(usage);
    root.appendChild(left);
    root.appendChild(right);

    main.addEventListener("click", async () => {
      if (!state.currentAppId || main.dataset.state === "unavailable") return;
      try {
        main.disabled = true;
        main.textContent = "Downloading...";
        await downloadManifestZip(state.currentAppId);
        main.dataset.state = "remove";
        main.textContent = "Remove Lua";
        library.style.display = "inline-flex";
      } catch (error) {
        setStatus(error.message || String(error), "error");
      } finally {
        main.disabled = false;
      }
    });

    library.addEventListener("click", () => {
      if (state.currentAppId) goToLibrary(state.currentAppId);
    });

    return root;
  }

  function attachRoot(root, anchor) {
    if (!root || !anchor) return;
    root.style.position = "";
    root.style.right = "";
    root.style.bottom = "";
    root.style.zIndex = "";
    root.style.background = "";
    root.style.border = "";
    root.style.borderRadius = "";
    root.style.boxShadow = "";
    root.style.margin = "";
    root.style.padding = "";
    root.style.width = "";

    if (root.previousElementSibling !== anchor) {
      anchor.insertAdjacentElement("afterend", root);
    }
  }

  async function syncStoreUi() {
    const visibleAppId = getSteamStoreAppId();
    if (!visibleAppId) {
      if (getSteamLibraryAppId()) {
        document.getElementById(ROOT_ID)?.remove();
        return;
      }
      document.getElementById(ROOT_ID)?.remove();
      console.warn("[Hubcap JS POC] Loaded, but no Store app id found in this DevTools context.", {
        href: location.href,
        pathname: location.pathname,
        documentUrl: document.URL,
        title: document.title,
        steamRoute: globalThis.MainWindowBrowserManager?.m_lastLocation,
      });
      return;
    }

    if (state.lastUrl === location.href && state.currentAppId && document.getElementById(ROOT_ID)) return;
    state.lastUrl = location.href;

    const resolved = await resolveStoreAppId(visibleAppId);
    if (!resolved) return;

    const anchor = findStoreAnchor(resolved.pageName);
    if (!anchor) {
      document.getElementById(ROOT_ID)?.remove();
      console.warn("[Hubcap JS POC] Store app id found, but no safe store-page title anchor found.", {
        visibleAppId,
        pageName: resolved.pageName,
        href: location.href,
        title: document.title,
      });
      return;
    }

    let root = document.getElementById(ROOT_ID);
    if (!root) root = createRoot();
    attachRoot(root, anchor);

    const main = root.querySelector(".hp-main");
    const library = root.querySelector(".hp-library");
    const warning = root.querySelector(".hp-warning");

    const denuvo = hasDenuvoWarning();
    warning.dataset.visible = denuvo ? "true" : "false";
    main.dataset.denuvo = denuvo ? "true" : "false";

    state.currentAppId = resolved.appId;
    state.currentVisibleAppId = resolved.visibleAppId;
    state.currentPageName = resolved.pageName || "";
    state.parentName = resolved.parentName;
    state.isDlc = resolved.isDlc;

    try {
      main.disabled = true;
      main.textContent = "Checking...";
      main.dataset.state = "checking";
      library.style.display = "none";
      setStatus(resolved.isDlc ? `DLC detected: using base game ${resolved.appId}${resolved.parentName ? ` - ${resolved.parentName}` : ""}` : "");

      const available = await checkAvailability(resolved.appId);
      if (!available) {
        main.textContent = "Lua Unavailable";
        main.dataset.state = "unavailable";
        setStatus("Lua unavailable.", "error");
        return;
      }

      main.textContent = "Download Lua";
      main.dataset.state = "download";
    } catch (error) {
      main.textContent = "Download Lua";
      main.dataset.state = "download";
      setStatus(error.message || String(error), "error");
    } finally {
      main.disabled = false;
      void refreshUsage();
    }
  }

  injectStyles();
  void syncStoreUi();

  const interval = setInterval(() => {
    void syncStoreUi();
  }, 1000);

  globalThis.__hubcapJsCleanup = () => {
    clearInterval(interval);
    cleanup();
  };

  console.log("[Hubcap JS POC] Loaded. Cleanup with globalThis.__hubcapJsCleanup?.()");
  }

  const targetWindow = getSteamDesktopWindow();
  const isSharedContext = document.title === "SharedJSContext" || location.hostname === "steamloopback.host";

  if (isSharedContext && targetWindow && targetWindow !== window) {
    targetWindow.eval("(" + runHubcapJsPoc.toString() + ")();");
    console.log("[Hubcap JS POC] Loaded into Steam desktop popup window.");
    globalThis.__hubcapJsCleanup = () => targetWindow.__hubcapJsCleanup?.();
    return;
  }

  runHubcapJsPoc();
})();
