(() => {
  const ID = "oogiefied-steam-js-sandbox";

  document.getElementById(ID)?.remove();

  const panel = document.createElement("div");
  panel.id = ID;
  panel.style.position = "fixed";
  panel.style.right = "24px";
  panel.style.bottom = "72px";
  panel.style.zIndex = "999999";
  panel.style.background = "rgba(18, 28, 40, 0.92)";
  panel.style.border = "1px solid rgba(103, 193, 245, 0.35)";
  panel.style.borderRadius = "4px";
  panel.style.boxShadow = "0 4px 18px rgba(0, 0, 0, 0.35)";
  panel.style.color = "#d6f4ff";
  panel.style.fontFamily = "Arial, Helvetica, sans-serif";
  panel.style.fontSize = "13px";
  panel.style.padding = "10px";
  panel.style.minWidth = "220px";

  const title = document.createElement("div");
  title.textContent = "Steam JS Sandbox";
  title.style.fontWeight = "700";
  title.style.marginBottom = "8px";

  const route = document.createElement("pre");
  route.style.margin = "0 0 8px";
  route.style.whiteSpace = "pre-wrap";
  route.style.color = "#ffffff";

  const refresh = () => {
    const location = globalThis.MainWindowBrowserManager?.m_lastLocation;
    route.textContent = JSON.stringify(location ?? null, null, 2);
    console.log("[Steam JS Sandbox] route:", location);
  };

  const button = document.createElement("button");
  button.textContent = "Read Route";
  button.style.background = "linear-gradient(180deg, #376f91 0%, #23465d 100%)";
  button.style.border = "1px solid rgba(103, 193, 245, 0.28)";
  button.style.borderRadius = "2px";
  button.style.boxShadow = "inset 0 1px 0 rgba(255, 255, 255, 0.08), 0 1px 2px rgba(0, 0, 0, 0.28)";
  button.style.color = "#d7eef8";
  button.style.cursor = "pointer";
  button.style.padding = "6px 10px";
  button.onclick = refresh;

  panel.appendChild(title);
  panel.appendChild(route);
  panel.appendChild(button);
  document.body.appendChild(panel);

  refresh();

  globalThis.__oogiefiedSteamJsCleanup = () => {
    document.getElementById(ID)?.remove();
    delete globalThis.__oogiefiedSteamJsCleanup;
  };
})();
