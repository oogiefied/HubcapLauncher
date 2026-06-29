(() => {
  const scriptPath = "file:///path/to/HubcapLauncher/hubcap-js-injection-poc.js";
  const targetWindow = globalThis.g_PopupManager?.GetExistingPopup?.("SP Desktop_uid0")?.window || window;
  const script = targetWindow.document.createElement("script");
  script.src = `${scriptPath}?t=${Date.now()}`;
  script.onload = () => console.log("[Hubcap JS POC] Loaded from local sandbox file.");
  script.onerror = (error) => console.error("[Hubcap JS POC] Failed to load local sandbox file.", error);
  targetWindow.document.documentElement.appendChild(script);
})();
