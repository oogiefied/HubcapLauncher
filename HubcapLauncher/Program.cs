using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

const int DevToolsPort = 8080;
ConsoleHost.AttachIfRequested(args);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

var options = LauncherOptions.Parse(args);
Logger.Configure(options);

try
{
    var needsSingleInstance = options.Mode == LauncherMode.Run;
    using var singleInstance = new Mutex(initiallyOwned: true, "Global\\HubcapLauncher", out var ownsInstance);
    if (needsSingleInstance && !ownsInstance && !options.AllowMultiple)
    {
        UserPrompts.NotifyPatchAlreadyRunning();
        Logger.Info("Steam with Hubcap patch is already running.");
        return;
    }

    Logger.Info("HubcapLauncher");

    if (!options.NoSteamLaunch)
        await SteamLauncher.EnsureDevModeSteamAsync(http, DevToolsPort, options.RestartSteam);
    await SteamLauncher.WaitForDevToolsAsync(http, DevToolsPort);

    if (options.Mode == LauncherMode.ListTargets)
    {
        var targetsJson = await http.GetStringAsync($"http://127.0.0.1:{DevToolsPort}/json/list");
        var targets = JsonSerializer.Deserialize<List<CdpTarget>>(targetsJson, JsonOptions.Default) ?? [];
        foreach (var target in targets)
            Logger.Info($"{target.Title} | {target.Url}");
        return;
    }

    if (options.Mode == LauncherMode.ProbeTargets)
    {
        var targetsJson = await http.GetStringAsync($"http://127.0.0.1:{DevToolsPort}/json/list");
        var targets = JsonSerializer.Deserialize<List<CdpTarget>>(targetsJson, JsonOptions.Default) ?? [];
        foreach (var target in targets.Where(t => t.Type == "page" && !string.IsNullOrWhiteSpace(t.WebSocketDebuggerUrl)))
        {
            try
            {
                using var cdp = new CdpSession(target.WebSocketDebuggerUrl);
                await cdp.ConnectAsync();
                await cdp.SendAsync("Runtime.enable");
                var probe = await cdp.EvaluateAsync(Scripts.TargetProbe);
                var value = probe["result"]?["result"]?["value"]?.GetValue<string>() ?? "";
                Logger.Info($"--- {target.Title}");
                Logger.Info(target.Url);
                Logger.Info(value);
            }
            catch (Exception ex)
            {
                Logger.Info($"--- {target.Title}");
                Logger.Info(target.Url);
                Logger.Info($"Probe failed: {ex.Message}");
            }
        }
        return;
    }

    if (options.Mode == LauncherMode.CheckUi)
    {
        var targetsJson = await http.GetStringAsync($"http://127.0.0.1:{DevToolsPort}/json/list");
        var targets = JsonSerializer.Deserialize<List<CdpTarget>>(targetsJson, JsonOptions.Default) ?? [];
        foreach (var target in targets.Where(t => t.Type == "page" && !string.IsNullOrWhiteSpace(t.WebSocketDebuggerUrl)))
        {
            try
            {
                using var cdp = new CdpSession(target.WebSocketDebuggerUrl);
                await cdp.ConnectAsync();
                await cdp.SendAsync("Runtime.enable");
                var probe = await cdp.EvaluateAsync("""
JSON.stringify({
  href: location.href,
  title: document.title,
  storeUi: !!document.getElementById('hubcap-cdp-ui'),
  libraryButton: !!document.getElementById('hubcap-cdp-library-remove'),
  libraryStatus: !!document.getElementById('hubcap-cdp-library-status'),
  stateSetter: typeof window.__hubcapCdpSetState === 'function',
  librarySetter: typeof window.__hubcapLibrarySetState === 'function',
  desktopLibraryButton: !!globalThis.g_PopupManager?.GetExistingPopup?.('SP Desktop_uid0')?.window?.document?.getElementById('hubcap-cdp-library-remove'),
  desktopLibraryStatus: !!globalThis.g_PopupManager?.GetExistingPopup?.('SP Desktop_uid0')?.window?.document?.getElementById('hubcap-cdp-library-status'),
  desktopLibraryButtonRect: (() => {
    const btn = globalThis.g_PopupManager?.GetExistingPopup?.('SP Desktop_uid0')?.window?.document?.getElementById('hubcap-cdp-library-remove');
    if (!btn) return null;
    const r = btn.getBoundingClientRect();
    return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) };
  })()
})
""");
                var value = probe["result"]?["result"]?["value"]?.GetValue<string>() ?? "";
                Logger.Info($"--- {target.Title}");
                Logger.Info(value);
            }
            catch (Exception ex)
            {
                Logger.Info($"--- {target.Title}: {ex.Message}");
            }
        }
        return;
    }

    var launcher = new HubcapLauncher(http, DevToolsPort);
    await launcher.RunAsync();
}
catch (Exception ex)
{
    Logger.Info($"Error: {ex.Message}");
    UserPrompts.NotifyError(ex.Message);
}

enum LauncherMode
{
    Run,
    ListTargets,
    ProbeTargets,
    CheckUi
}

sealed class LauncherOptions
{
    public bool RestartSteam { get; init; }
    public bool NoSteamLaunch { get; init; }
    public bool Quiet { get; init; }
    public bool AllowMultiple { get; init; }
    public string LogPath { get; init; } = "";
    public LauncherMode Mode { get; init; } = LauncherMode.Run;

    public static LauncherOptions Parse(string[] args)
    {
        var mode = LauncherMode.Run;
        if (args.Any(a => string.Equals(a, "--list-targets", StringComparison.OrdinalIgnoreCase))) mode = LauncherMode.ListTargets;
        if (args.Any(a => string.Equals(a, "--probe-targets", StringComparison.OrdinalIgnoreCase))) mode = LauncherMode.ProbeTargets;
        if (args.Any(a => string.Equals(a, "--check-ui", StringComparison.OrdinalIgnoreCase))) mode = LauncherMode.CheckUi;

        return new LauncherOptions
        {
            RestartSteam = Has(args, "--restart-steam"),
            NoSteamLaunch = Has(args, "--no-steam-launch"),
            Quiet = Has(args, "--quiet"),
            AllowMultiple = Has(args, "--allow-multiple"),
            LogPath = ValueAfter(args, "--log") ?? "",
            Mode = mode
        };
    }

    private static bool Has(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? ValueAfter(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}

static class Logger
{
    private static bool _quiet;
    private static string _logPath = "";
    private static bool _configured;

    public static void Configure(LauncherOptions options)
    {
        _quiet = options.Quiet;
        _logPath = options.Mode != LauncherMode.Run && string.IsNullOrWhiteSpace(options.LogPath)
            ? ""
            : string.IsNullOrWhiteSpace(options.LogPath)
            ? Path.Combine(AppContext.BaseDirectory, "HubcapLauncher.log")
            : options.LogPath;
        if (!_configured && !string.IsNullOrWhiteSpace(_logPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath) ?? AppContext.BaseDirectory);
                File.WriteAllText(_logPath, "");
            }
            catch { }
        }
        _configured = true;
    }

    public static void Info(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        if (!_quiet) Console.WriteLine(message);
        try
        {
            if (!string.IsNullOrWhiteSpace(_logPath))
                File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch { }
    }
}

static class ConsoleHost
{
    public static void AttachIfRequested(string[] args)
    {
        if (!args.Any(IsConsoleMode)) return;
        AttachConsole(ATTACH_PARENT_PROCESS);
    }

    private static bool IsConsoleMode(string arg) =>
        string.Equals(arg, "--console", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "--list-targets", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "--probe-targets", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "--check-ui", StringComparison.OrdinalIgnoreCase);

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);
}

static class SteamLauncher
{
    public static async Task EnsureDevModeSteamAsync(HttpClient http, int devToolsPort, bool forceRestart)
    {
        if (await IsDevToolsReachableAsync(http, devToolsPort))
        {
            Logger.Info("Steam with Hubcap patch is already running.");
            return;
        }

        if (forceRestart)
        {
            StopSteam();
            StartSteam(devToolsPort);
            return;
        }

        if (IsSteamRunning())
        {
            var restart = UserPrompts.ConfirmRestartSteamForDevTools();
            if (!restart)
            {
                Logger.Info("Steam is currently not in dev mode. User declined switch.");
                return;
            }

            StopSteam();
        }

        StartSteam(devToolsPort);
    }

    public static void StopSteam()
    {
        foreach (var process in Process.GetProcessesByName("steam").Concat(Process.GetProcessesByName("steamwebhelper")))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        Thread.Sleep(2000);
    }

    public static void StartSteam(int devToolsPort)
    {
        var steam = GetSteamExe();
        var args = $"-dev -console -cef-enable-debugging -devtools-address 127.0.0.1 -devtools-port {devToolsPort}";
        Process.Start(new ProcessStartInfo
        {
            FileName = steam,
            Arguments = args,
            UseShellExecute = true
        });
        Logger.Info($"Steam launched with DevTools flags: {steam}");
    }

    public static async Task<bool> IsDevToolsReachableAsync(HttpClient http, int port)
    {
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/json/list");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task WaitForDevToolsAsync(HttpClient http, int port)
    {
        for (var attempt = 0; attempt < 90; attempt++)
        {
            if (await IsDevToolsReachableAsync(http, port))
            {
                Logger.Info("Steam DevTools ready.");
                return;
            }
            await Task.Delay(500);
        }
        throw new InvalidOperationException($"Steam DevTools did not become reachable at http://127.0.0.1:{port}/json/list");
    }

    public static void OpenLibraryApp(string appId)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"steam://nav/games/details/{appId}",
            UseShellExecute = true
        });
    }

    private static string GetSteamExe()
    {
        var root = HubcapService.FindSteamRoot();
        var exe = Path.Combine(root, "steam.exe");
        if (File.Exists(exe)) return exe;
        throw new InvalidOperationException(UserPrompts.SteamNotFoundMessage);
    }

    public static bool IsSteamRunning() => Process.GetProcessesByName("steam").Any();
}

static class UserPrompts
{
    public const string SteamNotFoundMessage =
        "Steam was not found.\n\nPut HubcapLauncher.exe inside your Steam folder, next to steam.exe, then run it again.";

    public static void NotifyError(string message)
    {
        const uint MB_OK = 0x00000000;
        const uint MB_ICONERROR = 0x00000010;
        const uint MB_TOPMOST = 0x00040000;

        MessageBoxW(
            IntPtr.Zero,
            message,
            "HubcapLauncher",
            MB_OK | MB_ICONERROR | MB_TOPMOST);
    }

    public static void NotifyPatchAlreadyRunning()
    {
        const uint MB_OK = 0x00000000;
        const uint MB_ICONINFORMATION = 0x00000040;
        const uint MB_TOPMOST = 0x00040000;

        MessageBoxW(
            IntPtr.Zero,
            "Steam with Hubcap patch is already running.",
            "HubcapLauncher",
            MB_OK | MB_ICONINFORMATION | MB_TOPMOST);
    }

    public static bool ConfirmRestartSteamForDevTools()
    {
        const uint MB_YESNO = 0x00000004;
        const uint MB_ICONWARNING = 0x00000030;
        const uint MB_TOPMOST = 0x00040000;
        const int IDYES = 6;

        var result = MessageBoxW(
            IntPtr.Zero,
            "Steam is currently not in dev mode.\n\nSwitch Steam to dev mode so HubcapLauncher can add the buttons?\n\nSteam will restart.",
            "HubcapLauncher",
            MB_YESNO | MB_ICONWARNING | MB_TOPMOST);

        return result == IDYES;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

sealed class HubcapLauncher
{
    private readonly HttpClient _http;
    private readonly int _port;
    private readonly HubcapService _hubcap;
    private readonly ConcurrentDictionary<string, Task<ResolvedApp>> _resolvedApps = new();

    public HubcapLauncher(HttpClient http, int port)
    {
        _http = http;
        _port = port;
        _hubcap = new HubcapService(http);
    }

    public async Task RunAsync()
    {
        var running = new Dictionary<string, Task>();
        var waitingLogged = false;
        while (true)
        {
            if (!SteamLauncher.IsSteamRunning())
            {
                Logger.Info("Steam closed. Exiting HubcapLauncher.");
                return;
            }

            try
            {
                var targets = await GetTargetsAsync();
                var attachable = targets.Where(IsAttachableTarget).ToList();
                if (attachable.Count > 0)
                {
                    waitingLogged = false;
                }
                foreach (var target in attachable)
                {
                    if (running.ContainsKey(target.Id)) continue;
                    running[target.Id] = Task.Run(async () =>
                    {
                        try
                        {
                            await RunTargetAsync(target);
                        }
                        catch (Exception ex)
                        {
                            Logger.Info($"Target {target.Id} ended: {ex.Message}");
                        }
                    });
                }

                foreach (var done in running.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToList())
                    running.Remove(done);

                if (running.Count == 0 && !waitingLogged)
                {
                    Logger.Info("Waiting for Steam Store or Library app targets...");
                    waitingLogged = true;
                }

                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Logger.Info($"CDP loop ended: {ex.Message}");
                await Task.Delay(1500);
            }
        }
    }

    private static bool IsAttachableTarget(CdpTarget target)
    {
        if (target.Type != "page" || string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl)) return false;
        return IsStoreTarget(target) || IsSharedContextTarget(target);
    }

    private static bool IsStoreTarget(CdpTarget target) =>
        target.Url.StartsWith("https://store.steampowered.com/", StringComparison.OrdinalIgnoreCase);

    private static bool IsLibraryTarget(CdpTarget target) =>
        target.Url.Contains("/library/app/", StringComparison.OrdinalIgnoreCase) ||
        target.Url.Contains("tracking:", StringComparison.OrdinalIgnoreCase) ||
        target.Title.Contains("/library/app/", StringComparison.OrdinalIgnoreCase) ||
        target.Title.Contains("tracking:", StringComparison.OrdinalIgnoreCase);

    private static bool IsSharedContextTarget(CdpTarget target) =>
        target.Title.Equals("SharedJSContext", StringComparison.OrdinalIgnoreCase);

    private async Task<List<CdpTarget>> GetTargetsAsync()
    {
        var json = await _http.GetStringAsync($"http://127.0.0.1:{_port}/json/list");
        return JsonSerializer.Deserialize<List<CdpTarget>>(json, JsonOptions.Default) ?? [];
    }

    private async Task RunTargetAsync(CdpTarget target)
    {
        using var cdp = new CdpSession(target.WebSocketDebuggerUrl);
        cdp.BindingCalled += (_, payload) => _ = Task.Run(() => HandleEventAsync(cdp, payload));
        await cdp.ConnectAsync();
        await cdp.SendAsync("Runtime.enable");
        await cdp.SendAsync("Runtime.addBinding", new { name = "hubcapNative" });

        var inject = await cdp.EvaluateAsync(IsStoreTarget(target) ? Scripts.StoreUi : Scripts.SharedLibraryUi);
        if (inject["exceptionDetails"] is not null)
            throw new InvalidOperationException(inject["exceptionDetails"]!.ToJsonString());

        var visibleAppId = IsStoreTarget(target) ? AppIdFromUrl(target.Url) : "";
        if (!string.IsNullOrWhiteSpace(visibleAppId))
        {
            await RefreshStateAsync(cdp, visibleAppId);
        }

        Logger.Info(IsStoreTarget(target) ? "Attached to Steam Store." : "Attached to Steam Library context.");
        if (IsStoreTarget(target))
            _ = Task.Run(() => StoreWatchdogAsync(cdp));
        else if (IsSharedContextTarget(target))
            _ = Task.Run(() => SharedLibraryWatchdogAsync(cdp));
        await cdp.ReceiveLoopTask;
    }

    private async Task StoreWatchdogAsync(CdpSession cdp)
    {
        var lastAppId = "";
        var lastLuaVersion = _hubcap.LuaVersion;
        while (!cdp.IsClosed)
        {
            try
            {
                var probe = await cdp.EvaluateAsync("""
(() => JSON.stringify({
  appId: location.pathname.match(/\/app\/(\d+)(?:\/|$)/)?.[1] || "",
  hasUi: !!document.getElementById("hubcap-cdp-ui"),
  hasSetter: typeof window.__hubcapCdpSetState === "function"
}))()
""");
                var value = probe["result"]?["result"]?["value"]?.GetValue<string>() ?? "";
                var state = JsonSerializer.Deserialize<StoreWatchdogState>(value, JsonOptions.Default);
                if (!string.IsNullOrWhiteSpace(state?.AppId) && (!state.HasUi || !state.HasSetter))
                {
                    var inject = await cdp.EvaluateAsync(Scripts.StoreUi);
                    if (inject["exceptionDetails"] is not null) continue;
                }

                var luaVersion = _hubcap.LuaVersion;
                if (!string.IsNullOrWhiteSpace(state?.AppId) && (state.AppId != lastAppId || luaVersion != lastLuaVersion))
                {
                    lastAppId = state.AppId;
                    lastLuaVersion = luaVersion;
                    await RefreshStateAsync(cdp, state.AppId);
                }
            }
            catch (Exception ex)
            {
                if (cdp.IsClosed) return;
                Logger.Info($"Store watchdog skipped refresh: {ex.Message}");
            }

            await Task.Delay(350);
        }
    }

    private async Task SharedLibraryWatchdogAsync(CdpSession cdp)
    {
        var lastAppId = "";
        var lastLuaVersion = _hubcap.LuaVersion;
        while (!cdp.IsClosed)
        {
            try
            {
                var probe = await cdp.EvaluateAsync("""
(() => globalThis.MainWindowBrowserManager?.m_lastLocation?.pathname?.match(/^\/(?:routes\/)?library\/app\/(\d+)(?:\/|$)/)?.[1] || "")()
""");
                var appId = probe["result"]?["result"]?["value"]?.GetValue<string>() ?? "";
                var luaVersion = _hubcap.LuaVersion;
                if (!string.IsNullOrWhiteSpace(appId) && (appId != lastAppId || luaVersion != lastLuaVersion))
                {
                    lastAppId = appId;
                    lastLuaVersion = luaVersion;
                    await RefreshLibraryStateAsync(cdp, appId);
                }
            }
            catch (Exception ex)
            {
                if (cdp.IsClosed) return;
                Logger.Info($"Library watchdog skipped refresh: {ex.Message}");
            }

            await Task.Delay(350);
        }
    }

    private async Task HandleEventAsync(CdpSession cdp, string raw)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<HubcapEvent>(raw, JsonOptions.Default);
            if (evt is null) return;

            var visibleAppId = !string.IsNullOrWhiteSpace(evt.AppId) ? evt.AppId : AppIdFromUrl(evt.Href);
            if (string.IsNullOrWhiteSpace(visibleAppId)) return;

            var resolved = await ResolveSteamAppAsync(visibleAppId);
            var appId = resolved.AppId;
            switch (evt.Action)
            {
                case "libraryRoute":
                    await RefreshLibraryStateAsync(cdp, visibleAppId);
                    return;

                case "libraryRemove":
                    var libraryRemove = await _hubcap.RemoveLuaAsync(appId);
                    await SetLibraryStateAsync(cdp, new LibraryUiState
                    {
                        AppId = appId,
                        Exists = false,
                        Removed = libraryRemove.Success,
                        StatusText = libraryRemove.Success ? "" : libraryRemove.Error,
                        StatusTone = libraryRemove.Success ? "success" : "error"
                    });
                    return;

                case "library":
                    SteamLauncher.OpenLibraryApp(appId);
                    return;

                case "route":
                    await RefreshStateAsync(cdp, visibleAppId);
                    return;

                case "refresh":
                    await SetStateAsync(cdp, new UiState { UsageOnly = true, UsageBusy = true });
                    var usage = await _hubcap.GetUsageAsync(forceRefresh: true);
                    await SetStateAsync(cdp, new UiState { UsageOnly = true, Usage = usage });
                    return;

                case "download":
                    await SetStateAsync(cdp, new UiState
                    {
                        Busy = true,
                        BusyText = "Downloading...",
                        StatusText = ""
                    });
                    var add = await _hubcap.DownloadAsync(appId);
                    await RefreshStateAsync(cdp, visibleAppId, add.Success ? "Added!" : add.Error, add.Success ? "success" : "error", forceUsageRefresh: add.Success);
                    return;

                case "remove":
                    await SetStateAsync(cdp, new UiState
                    {
                        Busy = true,
                        BusyText = "Removing...",
                        StatusText = ""
                    });
                    var remove = await _hubcap.RemoveLuaAsync(appId);
                    await RefreshStateAsync(cdp, visibleAppId, remove.Success ? "" : remove.Error, remove.Success ? "success" : "error");
                    return;
            }
        }
        catch (Exception ex)
        {
            await SetStateAsync(cdp, new UiState { StatusText = ex.Message, StatusTone = "error", StatusError = true });
        }
    }

    private async Task RefreshStateAsync(CdpSession cdp, string visibleAppId, string? temporaryStatus = null, string? tone = null, bool forceUsageRefresh = false)
    {
        var resolved = await ResolveSteamAppAsync(visibleAppId);
        var state = await _hubcap.GetStateAsync(resolved);
        if (!string.IsNullOrWhiteSpace(temporaryStatus))
        {
            state.StatusText = temporaryStatus;
            state.StatusTone = tone ?? "idle";
            state.StatusError = tone == "error";
        }
        await SetStateAsync(cdp, state);

        try
        {
            await SetStateAsync(cdp, new UiState { UsageOnly = true, UsageBusy = true });
            var usage = await _hubcap.GetUsageAsync(forceRefresh: forceUsageRefresh);
            await SetStateAsync(cdp, new UiState { UsageOnly = true, Usage = usage });
        }
        catch (Exception ex)
        {
            await SetStateAsync(cdp, new UiState { UsageOnly = true, Usage = new UsageState { Error = ex.Message } });
        }
    }

    private async Task RefreshLibraryStateAsync(CdpSession cdp, string visibleAppId)
    {
        var resolved = await ResolveSteamAppAsync(visibleAppId);
        var exists = _hubcap.HasLua(resolved.AppId, out var error);
        await SetLibraryStateAsync(cdp, new LibraryUiState
        {
            AppId = resolved.AppId,
            Exists = exists,
            StatusText = error ?? ""
        });
    }

    private static async Task SetStateAsync(CdpSession cdp, UiState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions.Default);
        await cdp.EvaluateAsync($"window.__hubcapCdpSetState && window.__hubcapCdpSetState({json})");
    }

    private static async Task SetLibraryStateAsync(CdpSession cdp, LibraryUiState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions.Default);
        await cdp.EvaluateAsync($"window.__hubcapLibrarySetState && window.__hubcapLibrarySetState({json})");
    }

    private async Task<ResolvedApp> ResolveSteamAppAsync(string visibleAppId)
    {
        if (string.IsNullOrWhiteSpace(visibleAppId)) return new ResolvedApp("", "", "", false);
        return await _resolvedApps.GetOrAdd(visibleAppId, ResolveSteamAppUncachedAsync);
    }

    private async Task<ResolvedApp> ResolveSteamAppUncachedAsync(string visibleAppId)
    {
        try
        {
            var json = await _http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(visibleAppId)}&filters=basic");
            var root = JsonNode.Parse(json);
            var data = root?[visibleAppId]?["data"];
            var isDlc = string.Equals(data?["type"]?.GetValue<string>(), "dlc", StringComparison.OrdinalIgnoreCase);
            var fullGame = data?["fullgame"];
            var parentId = AppIdNodeToString(fullGame?["appid"]);
            if (isDlc && !string.IsNullOrWhiteSpace(parentId))
            {
                return new ResolvedApp(parentId, visibleAppId, fullGame?["name"]?.GetValue<string>() ?? "", true);
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"Could not resolve appdetails for {visibleAppId}: {ex.Message}");
        }

        return new ResolvedApp(visibleAppId, visibleAppId, "", false);
    }

    private static string AppIdNodeToString(JsonNode? node)
    {
        if (node is null) return "";
        try
        {
            if (node.GetValue<int?>() is int number) return number.ToString();
        }
        catch { }
        try
        {
            return node.GetValue<string?>() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string AppIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var match = System.Text.RegularExpressions.Regex.Match(url, @"/app/(\d+)");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string LibraryAppIdFromTarget(CdpTarget target)
    {
        var fromUrl = AppIdFromUrl(target.Url);
        if (!string.IsNullOrWhiteSpace(fromUrl)) return fromUrl;
        var haystack = $"{Uri.UnescapeDataString(target.Url ?? "")}\n{Uri.UnescapeDataString(target.Title ?? "")}";
        var match = System.Text.RegularExpressions.Regex.Match(haystack, @"/library/app/(\d+)");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string EscapeJs(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}

sealed class HubcapService
{
    private readonly HttpClient _http;
    private readonly object _cacheLock = new();
    private readonly ConcurrentDictionary<string, CachedStatusResult> _statusCache = new();
    private FileSystemWatcher? _luaWatcher;
    private HubcapConfig? _cachedConfig;
    private string _cachedConfigKey = "";
    private HashSet<string> _luaAppIds = new(StringComparer.OrdinalIgnoreCase);
    private int _luaVersion;
    private UsageState? _usageCache;
    private DateTimeOffset _usageCacheExpiresAt;

    public HubcapService(HttpClient http) => _http = http;

    public int LuaVersion
    {
        get
        {
            try { EnsureLuaCache(); } catch { }
            return Volatile.Read(ref _luaVersion);
        }
    }

    public async Task<UiState> GetStateAsync(ResolvedApp app)
    {
        try
        {
            var config = EnsureLuaCache();
            var exists = HasLuaCached(app.AppId);
            var available = true;
            string statusText = "";
            bool statusError = false;

            if (!exists)
            {
                var status = await GetStatusAsync(config, app.AppId);
                available = status.Available;
                if (!status.Success)
                {
                    statusText = status.Error;
                    statusError = true;
                }
                else if (!available)
                {
                    statusText = "Lua unavailable.";
                    statusError = true;
                }
            }

            if (app.IsDlc && (string.IsNullOrWhiteSpace(statusText) || string.Equals(statusText, "Lua unavailable.", StringComparison.OrdinalIgnoreCase)))
            {
                statusText = $"DLC detected: using base game {app.AppId}" +
                    (!string.IsNullOrWhiteSpace(app.ParentName) ? $" - {app.ParentName}" : "");
                statusError = false;
            }

            return new UiState
            {
                AppId = app.AppId,
                VisibleAppId = app.VisibleAppId,
                ParentName = app.ParentName,
                IsDlc = app.IsDlc,
                Exists = exists,
                Available = available,
                StatusText = statusText,
                StatusError = statusError,
                Usage = null
            };
        }
        catch (Exception ex)
        {
            return new UiState
            {
                AppId = app.AppId,
                VisibleAppId = app.VisibleAppId,
                ParentName = app.ParentName,
                IsDlc = app.IsDlc,
                Exists = false,
                Available = true,
                StatusText = ex.Message,
                StatusTone = "error",
                StatusError = true,
                Usage = null
            };
        }
    }

    public bool HasLua(string appId, out string? error)
    {
        try
        {
            EnsureLuaCache();
            error = null;
            return HasLuaCached(appId);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public async Task<UsageState> GetUsageAsync(bool forceRefresh = false) => await GetUsageAsync(EnsureLuaCache(), forceRefresh);

    private async Task<UsageState> GetUsageAsync(HubcapConfig config, bool forceRefresh = false)
    {
        if (!forceRefresh && _usageCache is not null && DateTimeOffset.UtcNow < _usageCacheExpiresAt)
            return _usageCache;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://hubcapmanifest.com/api/v1/user/stats");
            request.Headers.Authorization = new("Bearer", config.ApiKey);
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new UsageState { Error = ErrorForStatus(response.StatusCode) };

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var usage = new UsageState
            {
                Username = json?["username"]?.GetValue<string>() ?? "",
                DailyUsage = json?["daily_usage"]?.GetValue<int?>() ?? json?["api_key_usage_count"]?.GetValue<int?>() ?? 0,
                DailyLimit = json?["daily_limit"]?.GetValue<int?>() ?? json?["role_daily_limit"]?.GetValue<int?>() ?? 0,
                ApiKeyExpiresAt = json?["api_key_expires_at"]?.GetValue<string>() ?? ""
            };
            _usageCache = usage;
            _usageCacheExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60);
            return usage;
        }
        catch (Exception ex)
        {
            return new UsageState { Error = ex.Message };
        }
    }

    private async Task<StatusResult> GetStatusAsync(HubcapConfig config, string appId)
    {
        if (_statusCache.TryGetValue(appId, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresAt)
            return cached.Result;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://hubcapmanifest.com/api/v1/status/{Uri.EscapeDataString(appId)}");
        request.Headers.Authorization = new("Bearer", config.ApiKey);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return new StatusResult(false, false, ErrorForStatus(response.StatusCode));

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var available =
            string.Equals(json?["status"]?.GetValue<string>(), "available", StringComparison.OrdinalIgnoreCase) &&
            json?["manifest_file_exists"]?.GetValue<bool?>() == true &&
            json?["update_in_progress"]?.GetValue<bool?>() != true;
        var result = new StatusResult(true, available, "");
        _statusCache[appId] = new CachedStatusResult(result, DateTimeOffset.UtcNow.AddMinutes(2));
        return result;
    }

    public async Task<ActionResult> DownloadAsync(string appId)
    {
        try
        {
            var config = EnsureLuaCache();
            Directory.CreateDirectory(config.LuaDir);
            var steamRoot = FindSteamRoot();
            var manifestDir = Path.Combine(steamRoot, "depotcache");
            Directory.CreateDirectory(manifestDir);

            var tempRoot = Path.Combine(Path.GetTempPath(), "HubcapLauncher");
            var downloadDir = Path.Combine(tempRoot, $"{appId}-bundle-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(downloadDir);
            var zipPath = Path.Combine(downloadDir, $"{appId}-bundle.zip");
            var extractDir = Path.Combine(downloadDir, "extracted");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://hubcapmanifest.com/api/v1/manifest/{Uri.EscapeDataString(appId)}");
                request.Headers.Authorization = new("Bearer", config.ApiKey);
                using var response = await _http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    return new ActionResult(false, ErrorForStatus(response.StatusCode));

                await using (var file = File.Create(zipPath))
                    await response.Content.CopyToAsync(file);

                ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

                var luaFiles = Directory.GetFiles(extractDir, "*.lua", SearchOption.AllDirectories);
                var manifestFiles = Directory.GetFiles(extractDir, "*.manifest", SearchOption.AllDirectories);
                if (luaFiles.Length == 0) return new ActionResult(false, "Downloaded ZIP did not contain a .lua file.");
                if (manifestFiles.Length == 0) return new ActionResult(false, "Downloaded ZIP did not contain a .manifest file.");

                foreach (var lua in luaFiles)
                    File.Copy(lua, Path.Combine(config.LuaDir, Path.GetFileName(lua)), overwrite: true);
                foreach (var manifest in manifestFiles)
                    File.Copy(manifest, Path.Combine(manifestDir, Path.GetFileName(manifest)), overwrite: true);

                DeleteMarkerFiles(steamRoot, manifestDir, appId);
                _usageCache = null;
                RescanLuaFiles(config.LuaDir);
                return new ActionResult(true, "");
            }
            finally
            {
                TryDeleteDirectory(downloadDir);
                if (Directory.Exists(tempRoot) && !Directory.EnumerateFileSystemEntries(tempRoot).Any())
                    TryDeleteDirectory(tempRoot);
            }
        }
        catch (Exception ex)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    public Task<ActionResult> RemoveLuaAsync(string appId)
    {
        try
        {
            var config = EnsureLuaCache();
            var steamRoot = FindSteamRoot();
            var manifestDir = Path.Combine(steamRoot, "depotcache");
            var luaPath = Path.Combine(config.LuaDir, $"{appId}.lua");
            if (File.Exists(luaPath)) File.Delete(luaPath);
            DeleteMarkerFiles(steamRoot, manifestDir, appId);
            MarkLua(appId, exists: false);
            return Task.FromResult(new ActionResult(true, ""));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ActionResult(false, ex.Message));
        }
    }

    private static void DeleteMarkerFiles(string steamRoot, string manifestDir, string appId)
    {
        foreach (var marker in new[]
        {
            Path.Combine(steamRoot, "config", $"hubcapplugin-manifest-{appId}.txt"),
            Path.Combine(manifestDir, $".hubcapmanifest-{appId}")
        })
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    private HubcapConfig EnsureLuaCache()
    {
        lock (_cacheLock)
        {
            if (_cachedConfig is not null)
                return _cachedConfig;
        }

        var config = ReadConfig();
        var configKey = $"{config.ApiKey}\n{config.LuaDir}";

        lock (_cacheLock)
        {
            if (_cachedConfig is not null && string.Equals(_cachedConfigKey, configKey, StringComparison.Ordinal))
                return _cachedConfig;

            _cachedConfig = config;
            _cachedConfigKey = configKey;
            _usageCache = null;
            _statusCache.Clear();
            ResetLuaWatcher(config.LuaDir);
            RescanLuaFilesLocked(config.LuaDir);
            return config;
        }
    }

    private bool HasLuaCached(string appId)
    {
        EnsureLuaCache();
        lock (_cacheLock)
            return _luaAppIds.Contains(appId);
    }

    private void ResetLuaWatcher(string luaDir)
    {
        try
        {
            Directory.CreateDirectory(luaDir);
            _luaWatcher?.Dispose();
            _luaWatcher = new FileSystemWatcher(luaDir, "*.lua")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };
            _luaWatcher.Created += (_, _) => RescanLuaFiles(luaDir);
            _luaWatcher.Deleted += (_, _) => RescanLuaFiles(luaDir);
            _luaWatcher.Renamed += (_, _) => RescanLuaFiles(luaDir);
            _luaWatcher.Changed += (_, _) => RescanLuaFiles(luaDir);
            _luaWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Logger.Info($"Lua watcher unavailable: {ex.Message}");
        }
    }

    private void RescanLuaFiles(string luaDir)
    {
        lock (_cacheLock)
            RescanLuaFilesLocked(luaDir);
    }

    private void RescanLuaFilesLocked(string luaDir)
    {
        _luaAppIds = Directory.Exists(luaDir)
            ? Directory.GetFiles(luaDir, "*.lua", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Interlocked.Increment(ref _luaVersion);
    }

    private void MarkLua(string appId, bool exists)
    {
        lock (_cacheLock)
        {
            if (exists) _luaAppIds.Add(appId);
            else _luaAppIds.Remove(appId);
            Interlocked.Increment(ref _luaVersion);
        }
    }

    private static HubcapConfig ReadConfig()
    {
        var steamRoot = FindSteamRoot();
        var configPath = Path.Combine(steamRoot, "config", "hubcaptools", "config.yaml");
        if (!File.Exists(configPath))
            throw new InvalidOperationException($"HubcapTool config.yaml not found at {configPath}");

        string apiKey = "";
        string luaDir = "";
        foreach (var line in File.ReadLines(configPath))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2) continue;
            var key = parts[0].Trim();
            var value = Unquote(parts[1].Trim());
            if (key == "HubcapApiKey") apiKey = value;
            if (key == "HubcapLuaDir") luaDir = Environment.ExpandEnvironmentVariables(value);
        }

        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException($"HubcapApiKey is missing in {configPath}");
        if (string.IsNullOrWhiteSpace(luaDir)) throw new InvalidOperationException($"HubcapLuaDir is missing in {configPath}");
        return new HubcapConfig(apiKey, luaDir);
    }

    public static string FindSteamRoot()
    {
        foreach (var keyName in new[]
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"
        })
        {
            var value = Registry.GetValue(keyName, "SteamPath", null)?.ToString();
            if (IsSteamRoot(value))
                return value!;
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (IsSteamRoot(fallback)) return fallback;

        var launcherFolder = AppContext.BaseDirectory;
        if (IsSteamRoot(launcherFolder)) return launcherFolder;

        throw new InvalidOperationException(UserPrompts.SteamNotFoundMessage);
    }

    private static bool IsSteamRoot(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            Directory.Exists(path) &&
            File.Exists(Path.Combine(path, "steam.exe"));
    }

    private static string Unquote(string value)
    {
        if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
            return value[1..^1];
        return value;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private static string ErrorForStatus(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized => "Invalid Hubcap API key or unauthorized access.",
        System.Net.HttpStatusCode.Forbidden => "Hubcap rejected this request. Check that your API key has access.",
        System.Net.HttpStatusCode.NotFound => "Hubcap does not have this file for this app yet.",
        (System.Net.HttpStatusCode)429 => "Hubcap rate limit or daily limit reached. Try again later.",
        System.Net.HttpStatusCode.InternalServerError => "Hubcap server error. Try again later.",
        System.Net.HttpStatusCode.BadGateway => "Hubcap gateway error. Try again later.",
        System.Net.HttpStatusCode.ServiceUnavailable => "Hubcap service unavailable. Try again later.",
        System.Net.HttpStatusCode.GatewayTimeout => "Hubcap request timed out. Try again later.",
        _ => $"Hubcap returned HTTP {(int)status}."
    };
}

sealed class CdpSession : IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private int _nextId = 1;

    public CdpSession(string websocketUrl) => WebsocketUrl = websocketUrl;

    public string WebsocketUrl { get; }
    public Task ReceiveLoopTask { get; private set; } = Task.CompletedTask;
    public bool IsClosed => _socket.State is WebSocketState.Aborted or WebSocketState.Closed or WebSocketState.CloseReceived or WebSocketState.CloseSent;
    public event EventHandler<string>? BindingCalled;

    public async Task ConnectAsync()
    {
        await _socket.ConnectAsync(new Uri(WebsocketUrl), CancellationToken.None);
        ReceiveLoopTask = Task.Run(ReceiveLoopAsync);
    }

    public async Task<JsonObject> SendAsync(string method, object? parameters = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var payload = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters is null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters, JsonOptions.Default)
        };
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(JsonOptions.Default));
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        return await tcs.Task;
    }

    public async Task<JsonObject> EvaluateAsync(string expression)
    {
        return await SendAsync("Runtime.evaluate", new
        {
            expression,
            awaitPromise = true,
            returnByValue = true
        });
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[256 * 1024];
        while (_socket.State == WebSocketState.Open)
        {
            var builder = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            var message = JsonNode.Parse(builder.ToString())?.AsObject();
            if (message is null) continue;

            if (message["id"]?.GetValue<int?>() is int id && _pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetResult(message);
                continue;
            }

            if (message["method"]?.GetValue<string>() == "Runtime.bindingCalled" &&
                message["params"]?["name"]?.GetValue<string>() == "hubcapNative")
            {
                BindingCalled?.Invoke(this, message["params"]?["payload"]?.GetValue<string>() ?? "");
            }
        }
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}

sealed record CdpTarget(string Id, string Title, string Type, string Url, string WebSocketDebuggerUrl);
sealed record ResolvedApp(string AppId, string VisibleAppId, string ParentName, bool IsDlc);
sealed record HubcapConfig(string ApiKey, string LuaDir);
sealed record StatusResult(bool Success, bool Available, string Error);
sealed record CachedStatusResult(StatusResult Result, DateTimeOffset ExpiresAt);
sealed record ActionResult(bool Success, string Error);
sealed record HubcapEvent(string Action, string AppId, string Href);
sealed record StoreWatchdogState(string AppId, bool HasUi, bool HasSetter);

sealed class UiState
{
    public string AppId { get; set; } = "";
    public string VisibleAppId { get; set; } = "";
    public string ParentName { get; set; } = "";
    public bool IsDlc { get; set; }
    public bool Exists { get; set; }
    public bool Available { get; set; } = true;
    public string StatusText { get; set; } = "";
    public string StatusTone { get; set; } = "idle";
    public bool StatusError { get; set; }
    public bool Busy { get; set; }
    public string BusyText { get; set; } = "";
    public bool UsageOnly { get; set; }
    public bool UsageBusy { get; set; }
    public UsageState? Usage { get; set; }
}

sealed class LibraryUiState
{
    public string AppId { get; set; } = "";
    public bool Exists { get; set; }
    public bool Removed { get; set; }
    public string StatusText { get; set; } = "";
    public string StatusTone { get; set; } = "idle";
}

sealed class UsageState
{
    public string Username { get; set; } = "";
    public int DailyUsage { get; set; }
    public int DailyLimit { get; set; }
    public string ApiKeyExpiresAt { get; set; } = "";
    public string Error { get; set; } = "";
}

static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

static class Scripts
{
    public const string TargetProbe = """
(() => {
  const text = [location.href, document.title, document.body?.innerText?.slice(0, 500) || ""].join("\n");
  const appId =
    text.match(/\/app\/(\d+)/)?.[1] ||
    text.match(/\/library\/app\/(\d+)/)?.[1] ||
    text.match(/tracking:[^:]*:\/library\/app\/(\d+)/)?.[1] ||
    "";
  return JSON.stringify({
    href: location.href,
    title: document.title,
    appId,
    hasStoreTitle: !!document.querySelector(".apphub_AppName"),
    hasGameHighlights: !!document.querySelector("#game_highlights"),
    hasPlayButtonText: /(^|\s)PLAY(\s|$)/i.test(document.body?.innerText || ""),
    hasLibraryRouteText: /\/library\/app\/\d+/.test(text),
    shared: (() => {
      try {
        const win = globalThis.g_PopupManager?.GetExistingPopup?.("SP Desktop_uid0")?.window || null;
        const doc = win?.document || null;
        const route = globalThis.MainWindowBrowserManager?.m_lastLocation?.pathname || "";
        const button = doc?.getElementById("hubcap-cdp-library-remove") || null;
        const rect = button?.getBoundingClientRect();
        return {
          route,
          hasDesktop: !!doc,
          desktopTitle: doc?.title || "",
          libraryButton: !!button,
          libraryButtonRect: rect ? { x: Math.round(rect.x), y: Math.round(rect.y), w: Math.round(rect.width), h: Math.round(rect.height) } : null
        };
      } catch (e) {
        return { error: String(e && e.message || e) };
      }
    })()
  });
})()
""";

    public const string StoreUi = """
(() => {
  const ROOT_ID = "hubcap-cdp-ui";
  const STYLE_ID = "hubcap-cdp-ui-style";
  const USAGE_CACHE_KEY = "hubcap-cdp-usage-cache";
  document.getElementById(STYLE_ID)?.remove();
  const style = document.createElement("style");
  style.id = STYLE_ID;
  style.textContent = `
    #${ROOT_ID}{align-items:center;display:flex;gap:10px;justify-content:space-between;margin:8px 0 18px;min-height:34px;width:100%}
    #${ROOT_ID} .hp-left,#${ROOT_ID} .hp-right{align-items:center;display:flex;gap:10px;min-width:0}
    #${ROOT_ID} .hp-left{flex:1 1 auto;min-height:34px}
    #${ROOT_ID} .hp-right{flex:0 0 auto;margin-left:auto}
    #${ROOT_ID} button{align-items:center;background:rgba(13,27,39,.48);border:1px solid rgba(103,193,245,.26);border-radius:3px;box-shadow:inset 0 1px 0 rgba(255,255,255,.06),0 1px 2px rgba(0,0,0,.24);color:#d6f4ff;cursor:pointer;display:inline-flex;font:14px Arial,Helvetica,sans-serif;height:32px;justify-content:center;line-height:1;min-height:32px;min-width:124px;padding:0 14px;text-align:center;white-space:nowrap}
    #${ROOT_ID} button:hover{background:rgba(26,52,70,.62);border-color:rgba(103,193,245,.42);color:#fff}
    #${ROOT_ID} button:disabled{cursor:default;opacity:.72}
    #${ROOT_ID} button[data-state="checking"],#${ROOT_ID} button[data-busy="true"]{align-items:center;display:inline-flex;gap:8px}
    #${ROOT_ID} button[data-state="checking"]::before,#${ROOT_ID} button[data-busy="true"]::before{animation:hubcap-cdp-spin .8s linear infinite;border:2px solid rgba(214,244,255,.35);border-top-color:#d6f4ff;border-radius:50%;content:"";height:12px;width:12px}
    #${ROOT_ID} button[data-state="download"][data-denuvo="true"]{background:rgba(111,60,24,.58);border-color:rgba(246,162,58,.36);color:#ffe7c1;opacity:1}
    #${ROOT_ID} button[data-state="download"][data-denuvo="true"]:hover{background:rgba(129,72,30,.72);border-color:rgba(246,162,58,.5);color:#fff5e6}
    #${ROOT_ID} button[data-state="remove"]{background:rgba(95,33,31,.58);border-color:rgba(217,75,63,.36);color:#ffe0dc;opacity:1}
    #${ROOT_ID} button[data-state="remove"]:hover{background:rgba(112,42,39,.72);border-color:rgba(217,75,63,.5);color:#fff1ef}
    #${ROOT_ID} .hp-status{color:#acdbf5;font:12px Arial,Helvetica,sans-serif;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
    #${ROOT_ID} .hp-status[data-tone="error"]{color:#ff9b8f;font-weight:700}
    #${ROOT_ID} .hp-status[data-tone="success"]{color:#a4d007;font-weight:700}
    #${ROOT_ID} .hp-warning{color:#f7c46c;display:none;font:12px Arial,Helvetica,sans-serif;font-weight:700;white-space:nowrap}
    #${ROOT_ID} .hp-warning[data-visible="true"]{display:inline-flex}
    #${ROOT_ID} .hp-usage{background:rgba(13,27,39,.48);border:1px solid rgba(103,193,245,.26);border-radius:3px;color:#d6f4ff;cursor:pointer;font-family:Arial,Helvetica,sans-serif;min-width:190px;padding:7px 10px 8px}
    #${ROOT_ID} .hp-usage-row,#${ROOT_ID} .hp-usage-bottom{align-items:center;display:flex;gap:8px;justify-content:space-between}
    #${ROOT_ID} .hp-usage-row{font-size:12px}
    #${ROOT_ID} .hp-usage-bottom{font-size:12px;justify-content:flex-end;margin-top:5px}
    #${ROOT_ID} .hp-usage-name{color:#fff;font-weight:700}
    #${ROOT_ID} .hp-usage-expiry{color:#9fc9e0;font-size:11px}
    #${ROOT_ID} .hp-usage-bar{background:rgba(0,0,0,.26);border-radius:999px;height:4px;margin-top:6px;overflow:hidden}
    #${ROOT_ID} .hp-usage-fill{background:linear-gradient(90deg,#a4d007 0%,#67c1f5 100%);display:block;height:100%;width:0%}
    #${ROOT_ID} .hp-usage-spinner{animation:hubcap-cdp-spin .8s linear infinite;border:2px solid rgba(214,244,255,.28);border-top-color:#d6f4ff;border-radius:50%;display:none;height:10px;width:10px}
    #${ROOT_ID} .hp-usage-spinner[data-visible="true"]{display:inline-flex}
    @keyframes hubcap-cdp-spin{to{transform:rotate(360deg)}}
  `;
  document.head.appendChild(style);
  const existingRoot = document.getElementById(ROOT_ID);
  const root = existingRoot || document.createElement("div");
  root.id = ROOT_ID;
  if (!existingRoot) root.innerHTML = `
    <div class="hp-left"><button class="hp-main" type="button" data-state="checking" disabled>Checking...</button><button class="hp-library" type="button" style="display:none">Go to Library</button><span class="hp-status"></span><span class="hp-warning"></span></div>
    <div class="hp-right"><div class="hp-usage"><div class="hp-usage-row"><span class="hp-usage-name">Hubcap</span><span class="hp-usage-expiry">Expires --</span></div><div class="hp-usage-bar"><span class="hp-usage-fill"></span></div><div class="hp-usage-bottom">Daily Usage: <strong class="hp-usage-count">--/--</strong><span class="hp-usage-spinner"></span></div></div></div>`;
  const appIdFromText = value => (String(value || "").match(/\/app\/(\d+)(?:\/|$)/)?.[1] || String(value || "").match(/store\.steampowered\.com\/app\/(\d+)(?:\/|$)/)?.[1] || "");
  const appIdFromUrl = () => appIdFromText(location.href) || appIdFromText(location.pathname) || appIdFromText(document.URL) || appIdFromText(document.querySelector('link[rel="canonical"]')?.href) || appIdFromText(document.querySelector('meta[property="og:url"]')?.content) || appIdFromText(globalThis.MainWindowBrowserManager?.m_lastLocation?.pathname || "") || appIdFromText(globalThis.MainWindowBrowserManager?.m_lastLocation?.href || "");
  function headerHost(){return document.querySelector(".apphub_HeaderStandardTop") || document.querySelector(".apphub_AppName")?.parentElement || null;}
  function earlyHost(){return headerHost() || document.querySelector("#game_highlights")?.parentElement || document.querySelector(".game_background_glow") || document.querySelector(".game_page_background") || null;}
  function looksLikeAppPage(){return !!(appIdFromUrl()||headerHost()||document.querySelector("#game_highlights")||document.querySelector(".game_page_background"));}
  function placeRoot() {
    const host = earlyHost();
    if (!looksLikeAppPage() || !host) {
      root.style.display = "none";
      if (!root.parentElement) document.body.prepend(root);
      return false;
    }
    root.style.display = "flex";
    const title = document.querySelector(".apphub_AppName");
    const highlights = document.querySelector("#game_highlights");
    if (title?.parentElement === host && title.nextElementSibling !== root) title.insertAdjacentElement("afterend", root);
    else if (highlights?.parentElement === host && root.parentElement !== host) host.insertBefore(root, highlights);
    else if (root.parentElement !== host) host.appendChild(root);
    return true;
  }
  placeRoot();
  const send = action => {
    const payload = JSON.stringify({ action, appId: appIdFromUrl(), href: location.href });
    if (typeof window.hubcapNative === "function") window.hubcapNative(payload);
    else console.warn("[Hubcap CDP] native binding unavailable", payload);
  };
  window.__hubcapCdpSetState = state => {
    placeRoot();
    const button=root.querySelector(".hp-main"), library=root.querySelector(".hp-library"), status=root.querySelector(".hp-status"), warning=root.querySelector(".hp-warning"), usage=root.querySelector(".hp-usage"), name=root.querySelector(".hp-usage-name"), expiry=root.querySelector(".hp-usage-expiry"), count=root.querySelector(".hp-usage-count"), fill=root.querySelector(".hp-usage-fill"), spinner=root.querySelector(".hp-usage-spinner");
    const denuvo=/denuvo|anti[-\s]?tamper/i.test(document.body?.innerText||"");
    warning.textContent="Warning: Denuvo / 3rd-party anti-tamper detected";
    warning.dataset.visible=denuvo?"true":"false"; button.dataset.denuvo=denuvo?"true":"false";
    if(state.busy){button.disabled=true;button.dataset.state="checking";button.dataset.busy="true";button.textContent=state.busyText||"Working...";status.textContent=state.statusText||"";status.dataset.tone="idle";spinner.dataset.visible=state.usageBusy?"true":"false";return;}
    if(state.usageOnly){updateUsage(state);return;}
    button.dataset.busy="false";
    if(state.exists){button.dataset.state="remove";button.textContent="Remove Lua";button.disabled=false;library.style.display="inline-flex";}
    else if(state.available){button.dataset.state="download";button.textContent="Download Lua";button.disabled=false;library.style.display="none";}
    else{button.dataset.state="unavailable";button.textContent="Unavailable";button.disabled=true;library.style.display="none";}
    status.textContent=state.isDlc&&state.statusText?state.statusText:(state.statusError&&state.statusText&&!/lua unavailable/i.test(state.statusText)?state.statusText:"");status.dataset.tone=state.statusTone||(state.statusError?"error":"idle");if(state.usage)updateUsage(state);else spinner.dataset.visible=state.usageBusy?"true":"false";
    function updateUsage(s){const u=s.usage||{},du=Number(u.dailyUsage||0),dl=Number(u.dailyLimit||0);name.textContent=u.username||"Hubcap";count.textContent=u.error?"Limit Error":`${du}/${dl}`;fill.style.width=`${dl>0?Math.min(100,Math.round((du/dl)*100)):0}%`;usage.title=u.error||"";spinner.dataset.visible=s.usageBusy?"true":"false";if(u.apiKeyExpiresAt){const days=Math.max(0,Math.ceil((new Date(u.apiKeyExpiresAt).getTime()-Date.now())/86400000));expiry.textContent=`Expires in ${days}d`;}else if(u.error){expiry.textContent="Expires --";}if(s.usage&&!u.error)try{sessionStorage.setItem(USAGE_CACHE_KEY,JSON.stringify(u));}catch{}}
  };
  function hydrateCachedUsage(){
    try{
      const cached=JSON.parse(sessionStorage.getItem(USAGE_CACHE_KEY)||"null");
      if(cached) window.__hubcapCdpSetState({usageOnly:true,usage:cached});
    }catch{}
  }
  if (!root.dataset.bound) {
    root.querySelector(".hp-main").addEventListener("click",()=>{const state=root.querySelector(".hp-main").dataset.state;if(state==="download")send("download");if(state==="remove")send("remove");});
    root.querySelector(".hp-library").addEventListener("click",()=>send("library"));
    root.querySelector(".hp-usage").addEventListener("click",()=>send("refresh"));
    root.dataset.bound = "true";
  }
  if(window.__hubcapCdpRouteTimer)clearInterval(window.__hubcapCdpRouteTimer);
  let lastHubcapAppId=appIdFromUrl();
  if(!lastHubcapAppId)window.__hubcapCdpSetState({busy:true,busyText:"Checking...",statusText:""});
  hydrateCachedUsage();
  if(lastHubcapAppId)setTimeout(()=>send("route"),0);
  window.__hubcapCdpRouteTimer=setInterval(()=>{const nextAppId=appIdFromUrl();placeRoot();if(!nextAppId&&lastHubcapAppId){lastHubcapAppId="";window.__hubcapCdpSetState({busy:true,busyText:"Checking...",statusText:""});return;}if(nextAppId&&nextAppId!==lastHubcapAppId){lastHubcapAppId=nextAppId;send("route");}},150);
  return {ok:true,href:location.href,appId:appIdFromUrl()};
})()
""";

    public const string SharedLibraryUi = """
(() => {
  const BUTTON_ID = "hubcap-cdp-library-remove";
  const STATUS_ID = "hubcap-cdp-library-status";
  const STYLE_ID = "hubcap-cdp-library-style";

  function desktopWindow() {
    try {
      return globalThis.g_PopupManager?.GetExistingPopup?.("SP Desktop_uid0")?.window || null;
    } catch {
      return null;
    }
  }

  function routeAppId() {
    const pathname = globalThis.MainWindowBrowserManager?.m_lastLocation?.pathname || "";
    return pathname.match(/^\/(?:routes\/)?library\/app\/(\d+)(?:\/|$)/)?.[1] || "";
  }

  function send(action, appId) {
    const payload = JSON.stringify({ action, appId: appId || routeAppId(), href: globalThis.MainWindowBrowserManager?.m_lastLocation?.pathname || location.href });
    if (typeof globalThis.hubcapNative === "function") globalThis.hubcapNative(payload);
    else console.warn("[Hubcap CDP Shared Library] native binding unavailable", payload);
  }

  function ensureStyle(doc) {
    if (doc.getElementById(STYLE_ID)) return;
    const style = doc.createElement("style");
    style.id = STYLE_ID;
    style.textContent = `
      #${BUTTON_ID}{align-items:center;align-self:center;background:rgba(95,33,31,.58);border:1px solid rgba(217,75,63,.36);border-radius:3px;box-shadow:inset 0 1px 0 rgba(255,255,255,.06),0 1px 2px rgba(0,0,0,.24);color:#ffe0dc;cursor:pointer;display:inline-flex;font:14px Arial,Helvetica,sans-serif;height:32px;justify-content:center;line-height:1;margin-right:8px;min-width:104px;padding:0 14px;text-align:center;transform:translateY(3px);white-space:nowrap}
      #${BUTTON_ID}:hover{background:rgba(112,42,39,.72);border-color:rgba(217,75,63,.5);color:#fff1ef}
      #${BUTTON_ID}:disabled{cursor:default;opacity:.72}
      #${BUTTON_ID}[data-removed="true"]{animation:hubcap-library-fade-out 1.8s ease forwards;background:rgba(38,72,42,.58);border-color:rgba(139,197,63,.36);color:#dff5cf}
      #${STATUS_ID}{align-items:center;align-self:center;color:#8bc53f;display:inline-flex;font:700 14px/32px Arial,Helvetica,sans-serif;height:32px;justify-content:center;margin-right:8px;min-width:104px;padding:0 14px;text-shadow:0 1px 2px rgba(0,0,0,.55);transform:translateY(3px);white-space:nowrap}
      #${STATUS_ID}[data-tone="error"]{color:#ff9b8f}
      @keyframes hubcap-library-fade-out{0%,45%{opacity:1}100%{opacity:0}}
    `;
    doc.head.appendChild(style);
  }

  function removeElements(doc) {
    if (!doc) return;
    doc.querySelectorAll(`#${BUTTON_ID},#${STATUS_ID}`).forEach(el => el.remove());
  }

  function actionAnchorElement(doc) {
    const viewportWidth = Math.max(doc.documentElement.clientWidth || 0, desktopWindow()?.innerWidth || 0);
    const viewportHeight = Math.max(doc.documentElement.clientHeight || 0, desktopWindow()?.innerHeight || 0);
    return Array.from(doc.querySelectorAll("button, div, a"))
      .map(el => ({ el, r: el.getBoundingClientRect() }))
      .filter(x => x.r.width >= 24 && x.r.width <= 64 && x.r.height >= 24 && x.r.height <= 64)
      .filter(x => x.r.x > viewportWidth * .72 && x.r.y > 260 && x.r.y < Math.min(viewportHeight - 90, 560))
      .sort((a, b) => a.r.y - b.r.y || a.r.x - b.r.x)[0]?.el || null;
  }

  function actionRowForAnchor(anchor) {
    let node = anchor?.parentElement || null;
    while (node && node !== anchor.ownerDocument.body) {
      const row = node.getBoundingClientRect();
      const compactChildren = Array.from(node.children)
        .map(child => child.getBoundingClientRect())
        .filter(r => r.width >= 24 && r.width <= 80 && r.height >= 24 && r.height <= 80 && Math.abs(r.y - anchor.getBoundingClientRect().y) < 12);
      if (compactChildren.length >= 2 && row.width >= 90 && row.height <= 80) return node;
      node = node.parentElement;
    }
    return anchor?.parentElement || null;
  }

  function placeInActionBar(doc, element) {
    const anchor = actionAnchorElement(doc);
    const row = actionRowForAnchor(anchor);
    if (!anchor || !row) return false;
    element.style.position = "";
    element.style.left = "";
    element.style.top = "";
    element.style.zIndex = "";
    element.style.marginRight = "8px";
    const anchorX = anchor.getBoundingClientRect().x;
    const before = Array.from(row.children)
      .filter(child => child.id !== BUTTON_ID && child.id !== STATUS_ID)
      .filter(child => Math.abs(child.getBoundingClientRect().y - anchor.getBoundingClientRect().y) < 16)
      .filter(child => child.getBoundingClientRect().x >= anchorX - 8)
      .sort((a, b) => a.getBoundingClientRect().x - b.getBoundingClientRect().x)[0] || anchor;
    row.insertBefore(element, before);
    return true;
  }

  globalThis.__hubcapLibrarySetState = state => {
    removeElements(globalThis.document);
    const win = desktopWindow();
    const doc = win?.document;
    if (!doc) return;

    ensureStyle(doc);
    removeElements(doc);

    if (!state.exists && !state.removed && !state.statusText) return;
    globalThis.__hubcapLastLibraryState = state.removed ? null : state;

    if (state.exists) {
      const button = doc.createElement("button");
      button.id = BUTTON_ID;
      button.type = "button";
      button.dataset.appId = state.appId || routeAppId();
      button.textContent = "Remove Lua";
      button.title = `Remove Lua for app ${button.dataset.appId}`;
      button.addEventListener("click", event => {
        event.preventDefault();
        event.stopPropagation();
        button.disabled = true;
        button.textContent = "Removing...";
        send("libraryRemove", button.dataset.appId);
      });
      placeInActionBar(doc, button);
      return;
    }

    if (state.removed) {
      const button = doc.createElement("button");
      button.id = BUTTON_ID;
      button.type = "button";
      button.disabled = true;
      button.dataset.removed = "true";
      button.dataset.appId = state.appId || routeAppId();
      button.textContent = "Removed";
      placeInActionBar(doc, button);
      setTimeout(() => {
        if (button.isConnected) button.remove();
        if (globalThis.__hubcapLastLibraryState?.removed) globalThis.__hubcapLastLibraryState = null;
      }, 1900);
      return;
    }

    if (state.statusText) {
      const status = doc.createElement("span");
      status.id = STATUS_ID;
      status.dataset.tone = state.statusTone || "error";
      status.dataset.appId = state.appId || routeAppId();
      status.textContent = state.statusText;
      placeInActionBar(doc, status);
    }
  };

  if (globalThis.__hubcapLibraryRouteTimer) clearInterval(globalThis.__hubcapLibraryRouteTimer);
  let lastAppId = "";
  function cleanupWhenNotLibraryApp() {
    removeElements(globalThis.document);
    const appId = routeAppId();
    if (!appId) {
      const win = desktopWindow();
      if (win?.document) removeElements(win.document);
      globalThis.__hubcapLastLibraryState = null;
      lastAppId = "";
    }
    return appId;
  }
  globalThis.__hubcapLibraryRouteTimer = setInterval(() => {
    const appId = cleanupWhenNotLibraryApp();
    if (!appId) return;
    const win = desktopWindow();
    if (win?.document) {
      const current = win.document.getElementById(BUTTON_ID) || win.document.getElementById(STATUS_ID);
      if (current && current.dataset.appId !== appId) removeElements(win.document);
      if (!current && globalThis.__hubcapLastLibraryState?.appId === appId && !globalThis.__hubcapLastLibraryState?.removed) {
        globalThis.__hubcapLibrarySetState(globalThis.__hubcapLastLibraryState);
      }
    }
    if (appId !== lastAppId) {
      lastAppId = appId;
      send("libraryRoute", appId);
    }
  }, 100);

  const initialAppId = cleanupWhenNotLibraryApp();
  if (initialAppId) send("libraryRoute", initialAppId);
  return { ok: true, appId: initialAppId, href: location.href, title: document.title };
})()
""";
}
