(() => {
  function safeRead(label, fn) {
    try {
      return fn();
    } catch (error) {
      return `[blocked: ${error?.message || error}]`;
    }
  }

  const seen = new Set();
  const rows = [];

  function visit(win, label, depth = 0) {
    if (!win || seen.has(win)) return;
    seen.add(win);

    rows.push({
      label,
      depth,
      href: safeRead("href", () => win.location.href),
      pathname: safeRead("pathname", () => win.location.pathname),
      title: safeRead("title", () => win.document?.title),
      bodyHint: safeRead("body", () => win.document?.body?.innerText?.slice(0, 120).replace(/\s+/g, " ")),
      frameCount: safeRead("frames", () => win.frames.length),
      hasAppUrl: safeRead("app", () => /\/app\/\d+(?:\/|$)/.test(win.location.pathname)),
    });

    const frameCount = safeRead("frames", () => win.frames.length);
    if (typeof frameCount === "number") {
      for (let index = 0; index < frameCount; index += 1) {
        visit(win.frames[index], `${label}.frames[${index}]`, depth + 1);
      }
    }
  }

  visit(globalThis, "current");

  const popupManager = safeRead("popupManager", () => globalThis.g_PopupManager);
  if (popupManager && typeof popupManager !== "string") {
    for (const name of ["SP Desktop_uid0", "SP Desktop_uid1", "SP Desktop_uid2", "Steam Big Picture Mode_uid0"]) {
      const popup = safeRead(name, () => popupManager.GetExistingPopup?.(name));
      if (popup && typeof popup !== "string") {
        visit(popup.window, `popup:${name}`);
      }
    }
  }

  console.table(rows);
  console.log("[Steam Target Inspector] Copy any rows where hasAppUrl=true. If none exist, this DevTools target cannot directly inject into the Store page DOM.");
})();
