using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

const int DevToolsPort = 8080;
ConsoleHost.AttachIfRequested(args);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

var options = LauncherOptions.Parse(args);
Logger.Configure(options);

try
{
    if (options.LoadingTest)
    {
        LoadingScreen.RunPreview();
        return;
    }

    if (options.UpdatePromptPreview)
    {
        LauncherAutoUpdater.RunPromptPreview();
        return;
    }

    var needsSingleInstance = options.Mode == LauncherMode.Run;
    using var singleInstance = new Mutex(initiallyOwned: true, "Global\\HubcapLauncher", out var ownsInstance);
    if (needsSingleInstance && !ownsInstance && !options.AllowMultiple)
    {
        if (await SteamLauncher.IsDevToolsReachableAsync(http, DevToolsPort))
        {
            Logger.Info("Steam DevTools already available. Restarting HubcapLauncher.");
            LauncherProcess.StopOtherLauncherInstances();
            try
            {
                ownsInstance = singleInstance.WaitOne(TimeSpan.FromSeconds(6));
            }
            catch (AbandonedMutexException)
            {
                ownsInstance = true;
            }
        }

        if (!ownsInstance)
        {
            UserPrompts.NotifyPatchAlreadyRunning();
            Logger.Info("Steam with Hubcap patch is already running.");
            return;
        }
    }

    using var loading = LoadingScreen.StartFor(options);
    Logger.Info("HubcapLauncher");

    if (options.Mode == LauncherMode.Run &&
        (!options.SkipUpdate || options.UpdateFailed || !string.IsNullOrWhiteSpace(options.UpdatedFromVersion)))
    {
        var update = await LauncherAutoUpdater.CheckAndApplyAsync(http, loading, options);
        if (update.ShouldExit)
            return;
    }

    if (options.Mode == LauncherMode.Run && options.ConfirmRestart)
    {
        if (!LauncherAutoUpdater.ConfirmRestart(options.UpdatedFromVersion))
            return;
    }

    var devToolsAlreadyOpen = await SteamLauncher.IsDevToolsReachableAsync(http, DevToolsPort);
    loading?.SetStatus(options.NoSteamLaunch
        ? "Connecting to Steam DevTools..."
        : devToolsAlreadyOpen
            ? "Connecting to Steam DevTools..."
            : "Starting Steam in dev mode...");
    if (!options.NoSteamLaunch)
        await SteamLauncher.EnsureDevModeSteamAsync(http, DevToolsPort, options.RestartSteam);
    loading?.SetStatus("Waiting for Steam web UI...");
    await SteamLauncher.WaitForDevToolsAsync(http, DevToolsPort);

    if (options.Mode == LauncherMode.ListTargets)
    {
        loading?.Close();
        var targetsJson = await http.GetStringAsync($"http://127.0.0.1:{DevToolsPort}/json/list");
        var targets = JsonSerializer.Deserialize<List<CdpTarget>>(targetsJson, JsonOptions.Default) ?? [];
        foreach (var target in targets)
            Logger.Info($"{target.Title} | {target.Url}");
        return;
    }

    if (options.Mode == LauncherMode.ProbeTargets)
    {
        loading?.Close();
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
        loading?.Close();
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
    loading?.SetStatus("Finding Store and Library targets...");
    var launcherTask = launcher.RunAsync();
    if (loading is not null)
    {
        var readyTimeout = Task.Delay(TimeSpan.FromSeconds(45));
        var readyTask = await Task.WhenAny(launcher.Ready, launcherTask, readyTimeout);
        if (readyTask == launcher.Ready)
        {
            loading.SetStatus("Hubcap controls ready...");
            await Task.Delay(450);
        }
        else if (readyTask == readyTimeout)
        {
            Logger.Info("Loading screen timed out waiting for Steam UI attach; keeping launcher running.");
        }
        loading.Close();
    }
    await launcherTask;
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
    public bool LoadingTest { get; init; }
    public bool UpdatePromptPreview { get; init; }
    public bool SkipUpdate { get; init; }
    public string UpdatedFromVersion { get; init; } = "";
    public bool UpdateFailed { get; init; }
    public bool ConfirmRestart { get; init; }
    public string UpdateFeedUrl { get; init; } = "";
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
            LoadingTest = Has(args, "--loading-test"),
            UpdatePromptPreview = Has(args, "--update-prompt-preview"),
            SkipUpdate = Has(args, "--skip-update") || Has(args, "--updated-from") || Has(args, "--update-failed"),
            UpdatedFromVersion = ValueAfter(args, "--updated-from") ?? "",
            UpdateFailed = Has(args, "--update-failed"),
            ConfirmRestart = Has(args, "--confirm-restart"),
            UpdateFeedUrl = ValueAfter(args, "--update-feed-url") ??
                Environment.GetEnvironmentVariable("HUBCAP_LAUNCHER_UPDATE_FEED_URL") ??
                "",
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

static class LauncherProcess
{
    public static void StopOtherLauncherInstances()
    {
        var currentId = Environment.ProcessId;
        var currentPath = Environment.ProcessPath;
        foreach (var process in Process.GetProcessesByName("HubcapLauncher"))
        {
            if (process.Id == currentId) continue;

            try
            {
                var processPath = GetProcessPath(process);
                if (!string.IsNullOrWhiteSpace(currentPath) &&
                    (string.IsNullOrWhiteSpace(processPath) ||
                     !string.Equals(processPath, currentPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Logger.Info($"Stopping existing HubcapLauncher instance: {process.Id}");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                Logger.Info($"Could not stop HubcapLauncher instance {process.Id}: {ex.Message}");
            }
        }
    }

    private static string GetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }
}

sealed class LoadingScreen : IDisposable
{
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private System.Windows.Forms.Timer? _dotsTimer;
    private LoadingForm? _form;
    private bool _closed;

    private LoadingScreen(string initialStatus)
    {
        _thread = new Thread(() =>
        {
            try
            {
                LoadingScreen.ConfigureHighDpi();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(true);

                var form = new LoadingForm(initialStatus);
                _form = form;
                _dotsTimer = new System.Windows.Forms.Timer { Interval = 450 };
                _dotsTimer.Tick += (_, _) => form.AdvanceStatusDots();
                form.FormClosed += (_, _) =>
                {
                    _dotsTimer?.Dispose();
                    _dotsTimer = null;
                };
                _dotsTimer.Start();
                _ready.Set();
                Application.Run(form);
            }
            catch (Exception ex)
            {
                Logger.Info($"Loading screen unavailable: {ex.Message}");
                _ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "HubcapLauncherLoadingScreen"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public static LoadingScreen? StartFor(LauncherOptions options)
    {
        if (options.Mode != LauncherMode.Run) return null;
        return new LoadingScreen("Preparing HubcapLauncher...");
    }

    public static void RunPreview()
    {
        ConfigureHighDpi();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(true);
        var form = new LoadingForm("Starting Steam in dev mode...", allowClose: true);
        var messages = new[]
        {
            "Starting Steam in dev mode...",
            "Enabling Chromium DevTools...",
            "Waiting for Steam web UI...",
            "Checking Library context...",
            "Convincing Steam to behave...",
            "Asking Gabe nicely...",
            "Finding Store and Library targets...",
            "Injecting Hubcap controls...",
            "Polishing the Lua button...",
            "Steam is ready. Adding Hubcap controls..."
        };
        var index = 0;
        var messageTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        messageTimer.Tick += (_, _) =>
        {
            index = (index + 1) % messages.Length;
            form.SetStatus(messages[index]);
        };
        var dotsTimer = new System.Windows.Forms.Timer { Interval = 450 };
        dotsTimer.Tick += (_, _) => form.AdvanceStatusDots();
        form.FormClosed += (_, _) =>
        {
            messageTimer.Dispose();
            dotsTimer.Dispose();
        };
        messageTimer.Start();
        dotsTimer.Start();
        Application.Run(form);
    }

    public static void ConfigureHighDpi()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch { }
    }

    public void SetStatus(string status)
    {
        if (_closed || !_ready.Wait(TimeSpan.FromSeconds(3))) return;

        var form = _form;
        if (form is null || form.IsDisposed) return;

        try
        {
            form.BeginInvoke(new Action(() => form.SetStatus(status)));
        }
        catch { }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;

        if (!_ready.Wait(TimeSpan.FromSeconds(3))) return;

        var form = _form;
        if (form is null || form.IsDisposed) return;

        try
        {
            form.BeginInvoke(new Action(form.Close));
        }
        catch { }
    }

    public void Dispose()
    {
        Close();
        _ready.Dispose();
    }
}

static class LoaderFonts
{
    private static readonly List<IntPtr> EmbeddedFontMemory = [];
    private static readonly PrivateFontCollection? EmbeddedFonts = LoadEmbeddedFonts();
    private static readonly FontFamily? EmbeddedRegularFamily = FindEmbeddedFamily("IBM Plex Sans");
    private static readonly string FallbackFamilyName = ResolveFallbackFamilyName();

    public static Font Regular(float size) => Create(EmbeddedRegularFamily, size, FontStyle.Regular);

    public static Font Bold(float size) => Create(EmbeddedRegularFamily, size, FontStyle.Bold);

    private static Font Create(FontFamily? embeddedFamily, float size, FontStyle style)
    {
        if (embeddedFamily is not null)
        {
            try
            {
                return new Font(embeddedFamily, size, style, GraphicsUnit.Point);
            }
            catch
            {
                return new Font(embeddedFamily, size, FontStyle.Regular, GraphicsUnit.Point);
            }
        }

        return new Font(FallbackFamilyName, size, style, GraphicsUnit.Point);
    }

    private static FontFamily? FindEmbeddedFamily(string familyName) =>
        EmbeddedFonts?.Families.FirstOrDefault(family => string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase));

    private static PrivateFontCollection? LoadEmbeddedFonts()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly
                .GetManifestResourceNames()
                .Where(name => name.EndsWith(".IBMPlexSans-Regular-loader.ttf", StringComparison.OrdinalIgnoreCase) ||
                               name.EndsWith(".IBMPlexSans-Bold-loader.ttf", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (resourceNames.Count == 0) return null;

            var fonts = new PrivateFontCollection();
            foreach (var resourceName in resourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                var pointer = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                EmbeddedFontMemory.Add(pointer);
                fonts.AddMemoryFont(pointer, bytes.Length);
            }

            return fonts;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveFallbackFamilyName()
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            var names = fonts.Families.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (names.Contains("IBM Plex Sans")) return "IBM Plex Sans";
            if (names.Contains("Inter")) return "Inter";
            if (names.Contains("Motiva Sans")) return "Motiva Sans";
            if (names.Contains("Motiva Sans Regular")) return "Motiva Sans Regular";
            if (names.Contains("Arial")) return "Arial";
        }
        catch { }

        return "Segoe UI";
    }
}

static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString()
            : informational;

        if (string.IsNullOrWhiteSpace(version))
            return "1.0.0";

        var metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex > 0 ? version[..metadataIndex] : version;
    }
}

sealed record AutoUpdateResult(bool ShouldExit);

static class UpdatePrompt
{
    public static bool ShowUpdateAvailable(string currentVersion, string latestVersion, string changelog) =>
        Show(
            title: "Update Available",
            heading: $"HubcapLauncher v{latestVersion} is available",
            message: $"You are running v{currentVersion}. Install the update now?",
            changelog: changelog,
            acceptText: "Update Now",
            cancelText: "Later");

    public static bool ShowRestart(string currentVersion, string previousVersion) =>
        Show(
            title: "Update Installed",
            heading: $"HubcapLauncher v{currentVersion} is ready",
            message: $"Updated from {previousVersion}. Start the new launcher now?",
            changelog: "",
            acceptText: "Restart Now",
            cancelText: "Later");

    private static bool Show(string title, string heading, string message, string changelog, string acceptText, string cancelText)
    {
        var result = false;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                LoadingScreen.ConfigureHighDpi();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(true);
                using var form = new UpdatePromptForm(title, heading, message, changelog, acceptText, cancelText);
                result = form.ShowDialog() == DialogResult.OK;
            }
            catch (Exception ex)
            {
                Logger.Info($"Update prompt unavailable: {ex.Message}");
                result = false;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "HubcapLauncherUpdatePrompt"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        done.Wait();
        done.Dispose();
        return result;
    }
}

static class LauncherAutoUpdater
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/oogiefied/HubcapLauncher/releases/latest";
    private const string ExecutableName = "HubcapLauncher.exe";

    public static async Task<AutoUpdateResult> CheckAndApplyAsync(HttpClient http, LoadingScreen? loading, LauncherOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.UpdatedFromVersion))
        {
            loading?.SetStatus($"Updated to v{AppVersion.Current}...");
            await Task.Delay(700);
            return new AutoUpdateResult(false);
        }

        if (options.UpdateFailed)
        {
            loading?.SetStatus("Update failed. Starting current version...");
            await Task.Delay(900);
            return new AutoUpdateResult(false);
        }

        try
        {
            loading?.SetStatus("Checking for updates...");
            using var checkCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var latest = await GetLatestReleaseAsync(http, options.UpdateFeedUrl, checkCts.Token);
            if (latest is null || latest.Version <= ParseVersion(AppVersion.Current))
            {
                if (latest is not null)
                    Logger.Info($"No launcher update available. Current={AppVersion.Current}, Latest={latest.TagName}.");
                loading?.SetStatus("HubcapLauncher is up to date...");
                await Task.Delay(350, checkCts.Token);
                return new AutoUpdateResult(false);
            }

            Logger.Info($"Launcher update available. Current={AppVersion.Current}, Latest={latest.TagName}.");
            if (!UpdatePrompt.ShowUpdateAvailable(AppVersion.Current, latest.TagName, latest.Changelog))
            {
                Logger.Info($"Launcher update skipped by user. Current={AppVersion.Current}, Latest={latest.TagName}.");
                loading?.SetStatus("Update skipped. Starting current version...");
                await Task.Delay(450);
                return new AutoUpdateResult(false);
            }

            loading?.SetStatus($"Downloading v{latest.TagName}...");
            using var downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var updateExe = await DownloadUpdateAsync(latest, downloadCts.Token);
            var currentExe = ResolveCurrentExecutablePath();
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
                return new AutoUpdateResult(false);

            loading?.SetStatus("Installing update...");
            var launched = LaunchUpdater(updateExe, currentExe, latest.UpdateRoot, AppVersion.Current);
            if (!launched)
            {
                loading?.SetStatus("Update could not start. Continuing...");
                await Task.Delay(900);
                return new AutoUpdateResult(false);
            }

            loading?.SetStatus("Restarting updated launcher...");
            await Task.Delay(500);
            loading?.Close();
            return new AutoUpdateResult(true);
        }
        catch (OperationCanceledException)
        {
            loading?.SetStatus("Update check timed out. Continuing...");
            await Task.Delay(700);
            return new AutoUpdateResult(false);
        }
        catch (Exception ex)
        {
            Logger.Info($"Update check failed: {ex.Message}");
            loading?.SetStatus("Update check skipped. Continuing...");
            await Task.Delay(700);
            return new AutoUpdateResult(false);
        }
    }

    public static bool ConfirmRestart(string previousVersion)
    {
        var from = string.IsNullOrWhiteSpace(previousVersion) ? "the previous version" : $"v{previousVersion}";
        return UpdatePrompt.ShowRestart(AppVersion.Current, from);
    }

    public static void RunPromptPreview()
    {
        var acceptedUpdate = UpdatePrompt.ShowUpdateAvailable(
            AppVersion.Current,
            "1.0.5",
            """
            - Added Group Lua persistence outside config.yaml
            - Improved Library button placement
            - Added update prompts and changelog display
            """);
        if (acceptedUpdate)
            UpdatePrompt.ShowRestart("1.0.5", $"v{AppVersion.Current}");
    }

    private static async Task<LatestRelease?> GetLatestReleaseAsync(HttpClient http, string updateFeedUrl, CancellationToken cancellationToken)
    {
        var latestReleaseUrl = string.IsNullOrWhiteSpace(updateFeedUrl) ? LatestReleaseUrl : updateFeedUrl.Trim();
        if (TryResolveLocalPath(latestReleaseUrl, out var localFeedPath))
        {
            await using var localStream = File.OpenRead(localFeedPath);
            return await ParseLatestReleaseAsync(localStream, cancellationToken);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, latestReleaseUrl);
        request.Headers.UserAgent.ParseAdd($"HubcapLauncher/{AppVersion.Current}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Logger.Info($"Update check returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ParseLatestReleaseAsync(stream, cancellationToken);
    }

    private static async Task<LatestRelease?> ParseLatestReleaseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? "" : "";
        var body = root.TryGetProperty("body", out var bodyNode) && bodyNode.ValueKind == JsonValueKind.String
            ? bodyNode.GetString() ?? ""
            : "";
        var version = ParseVersion(tag);
        if (version <= new Version(0, 0))
            return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlNode) ? urlNode.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(downloadUrl)) continue;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains("HubcapLauncher", StringComparison.OrdinalIgnoreCase)) continue;

            return new LatestRelease(tag.TrimStart('v', 'V'), version, downloadUrl, body);
        }

        return null;
    }

    private static async Task<string> DownloadUpdateAsync(LatestRelease latest, CancellationToken cancellationToken)
    {
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HubcapLauncher",
            "updates");
        TryDeleteDirectory(updateRoot);
        Directory.CreateDirectory(updateRoot);

        var zipPath = Path.Combine(updateRoot, $"HubcapLauncher-{latest.TagName}.zip");
        if (TryResolveLocalPath(latest.DownloadUrl, out var localZipPath))
        {
            File.Copy(localZipPath, zipPath, overwrite: true);
        }
        else
        {
            using var downloadHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var request = new HttpRequestMessage(HttpMethod.Get, latest.DownloadUrl);
            {
                request.Headers.UserAgent.ParseAdd($"HubcapLauncher/{AppVersion.Current}");
                using var response = await downloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(zipPath);
                await input.CopyToAsync(output, cancellationToken);
            }
        }

        var extractDir = Path.Combine(updateRoot, "extract");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        var exe = Directory
            .EnumerateFiles(extractDir, ExecutableName, SearchOption.AllDirectories)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(exe))
            throw new InvalidOperationException("Downloaded update did not contain HubcapLauncher.exe.");

        latest.UpdateRoot = updateRoot;
        return exe;
    }

    private static bool LaunchUpdater(string updateExe, string currentExe, string updateRoot, string previousVersion)
    {
        try
        {
            var scriptPath = Path.Combine(updateRoot, "apply-update.cmd");
            var currentPid = Environment.ProcessId;
            var escapedUpdateRoot = EscapeCmdValue(updateRoot);
            File.WriteAllText(scriptPath, $"""
@echo off
setlocal
set "PID={currentPid}"
set "SRC={EscapeCmdValue(updateExe)}"
set "DST={EscapeCmdValue(currentExe)}"
set "ROOT={escapedUpdateRoot}"
set "PREV={EscapeCmdValue(previousVersion)}"

:wait
tasklist /FI "PID eq %PID%" 2>NUL | find "%PID%" >NUL
if not errorlevel 1 (
  timeout /t 1 /nobreak >NUL
  goto wait
)

copy /Y "%SRC%" "%DST%" >NUL
if errorlevel 1 (
  start "" "%DST%" --update-failed
  exit /b 1
)

start "" "%DST%" --confirm-restart --updated-from "%PREV%"
cd /d "%TEMP%"
rmdir /s /q "%ROOT%" >NUL 2>NUL
del "%~f0" >NUL 2>NUL
""", Encoding.ASCII);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            return process is not null;
        }
        catch (Exception ex)
        {
            Logger.Info($"Could not launch updater: {ex.Message}");
            return false;
        }
    }

    private static bool TryResolveLocalPath(string value, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            path = uri.LocalPath;
            return File.Exists(path);
        }

        if (File.Exists(value))
        {
            path = value;
            return true;
        }

        return false;
    }

    private static string ResolveCurrentExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            return Environment.ProcessPath;

        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static Version ParseVersion(string version)
    {
        var clean = version.Trim().TrimStart('v', 'V');
        var suffixIndex = clean.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
            clean = clean[..suffixIndex];

        return Version.TryParse(clean, out var parsed) ? parsed : new Version(0, 0);
    }

    private static string EscapeCmdValue(string value) =>
        value.Replace("^", "^^", StringComparison.Ordinal)
            .Replace("&", "^&", StringComparison.Ordinal)
            .Replace("|", "^|", StringComparison.Ordinal)
            .Replace("<", "^<", StringComparison.Ordinal)
            .Replace(">", "^>", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A stale update folder should not block launching.
        }
    }

    private sealed record LatestRelease(string TagName, Version Version, string DownloadUrl, string Changelog)
    {
        public string UpdateRoot { get; set; } = "";
    }
}

sealed class LoadingForm : Form
{
    private readonly Icon? _windowIcon;
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private readonly Label _statusLabel;
    private readonly Label _versionLabel;
    private readonly LogoSpinnerControl _spinner;
    private readonly Panel _topBorder;
    private readonly Panel _bottomBorder;
    private readonly Panel _leftBorder;
    private readonly Panel _rightBorder;
    private readonly bool _allowClose;
    private bool _closeHovered;
    private bool _closePressed;
    private Rectangle _closeRect;
    private string _statusBaseText = "";
    private int _statusDotCount = 1;

    public LoadingForm(string initialStatus, bool allowClose = false)
    {
        _allowClose = allowClose;
        Text = "HubcapLauncher";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        Opacity = 1.0;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = LoaderFallbackBackground;
        ForeColor = Color.White;
        Font = LoaderFonts.Regular(9F);
        Padding = Padding.Empty;
        _windowIcon = LoaderImages.LoadIcon();
        if (_windowIcon is not null)
            Icon = _windowIcon;

        _spinner = new LogoSpinnerControl
        {
            Anchor = AnchorStyles.Top,
            Size = new Size(86, 86)
        };

        _titleLabel = new Label
        {
            AutoSize = false,
            Text = "HubcapLauncher",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = LoaderFonts.Bold(18F),
            BackColor = Color.Transparent,
            ForeColor = Color.White,
            UseCompatibleTextRendering = false
        };

        _subtitleLabel = new Label
        {
            AutoSize = false,
            Text = "Getting Steam ready for Hubcap controls.",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = LoaderFonts.Regular(10F),
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(184, 198, 209),
            UseCompatibleTextRendering = false
        };

        var initial = SplitStatusDots(initialStatus);
        _statusBaseText = initial.BaseText;
        _statusDotCount = initial.DotCount;

        _statusLabel = new Label
        {
            AutoSize = false,
            Text = FormatStatusText(),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = LoaderFonts.Regular(9.5F),
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(103, 193, 245),
            UseCompatibleTextRendering = false
        };

        _versionLabel = new Label
        {
            AutoSize = false,
            Text = $"V{AppVersion.Current}",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = LoaderFonts.Regular(8F),
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(142, 158, 170),
            UseCompatibleTextRendering = false
        };

        _topBorder = CreateBorderStrip();
        _bottomBorder = CreateBorderStrip();
        _leftBorder = CreateBorderStrip();
        _rightBorder = CreateBorderStrip();

        Controls.Add(_spinner);
        Controls.Add(_titleLabel);
        Controls.Add(_subtitleLabel);
        Controls.Add(_statusLabel);
        Controls.Add(_versionLabel);
        Controls.Add(_topBorder);
        Controls.Add(_bottomBorder);
        Controls.Add(_leftBorder);
        Controls.Add(_rightBorder);
        EnableDragMove(_spinner);
        EnableDragMove(_titleLabel);
        EnableDragMove(_subtitleLabel);
        EnableDragMove(_statusLabel);
        EnableDragMove(_versionLabel);
        EnableDragMove(_topBorder);
        EnableDragMove(_bottomBorder);
        EnableDragMove(_leftBorder);
        EnableDragMove(_rightBorder);

        ApplyDpiLayout();
    }

    public void SetStatus(string status)
    {
        var parsed = SplitStatusDots(status);
        _statusBaseText = parsed.BaseText;
        _statusDotCount = parsed.DotCount;
        _statusLabel.Text = FormatStatusText();
    }

    public void AdvanceStatusDots()
    {
        _statusDotCount = (_statusDotCount % 3) + 1;
        _statusLabel.Text = FormatStatusText();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (_allowClose && _closeRect.Contains(e.Location))
        {
            _closePressed = true;
            Invalidate(_closeRect);
            return;
        }

        DragMove();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hovered = _allowClose && _closeRect.Contains(e.Location);
        if (hovered == _closeHovered) return;

        _closeHovered = hovered;
        Cursor = hovered ? Cursors.Hand : Cursors.Default;
        Invalidate(_closeRect);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_closeHovered && !_closePressed) return;

        _closeHovered = false;
        _closePressed = false;
        Cursor = Cursors.Default;
        Invalidate(_closeRect);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_closePressed) return;

        var shouldClose = _allowClose && _closeRect.Contains(e.Location);
        _closePressed = false;
        Invalidate(_closeRect);
        if (shouldClose)
            Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_allowClose)
            DrawCloseIcon(e.Graphics);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _spinner.Dispose();
            _windowIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeWindowChrome.TryApplyRoundedCorners(Handle);
        BackColor = LoaderFallbackBackground;
        TransparencyKey = Color.Empty;
        ApplyDpiLayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyDpiLayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutBorderStrips();
        Invalidate();
    }

    private void ApplyDpiLayout()
    {
        var width = ScaleForDpi(336);
        var height = ScaleForDpi(250);
        if (ClientSize.Width != width || ClientSize.Height != height)
            ClientSize = new Size(width, height);

        var spinnerSize = ScaleForDpi(86);
        _spinner.Size = new Size(spinnerSize, spinnerSize);
        _spinner.Location = new Point((ClientSize.Width - _spinner.Width) / 2, ScaleForDpi(38));

        _titleLabel.SetBounds(0, ScaleForDpi(126), ClientSize.Width, ScaleForDpi(34));
        _subtitleLabel.SetBounds(ScaleForDpi(24), ScaleForDpi(161), ClientSize.Width - ScaleForDpi(48), ScaleForDpi(28));
        _statusLabel.SetBounds(ScaleForDpi(24), ScaleForDpi(195), ClientSize.Width - ScaleForDpi(48), ScaleForDpi(28));
        _versionLabel.SetBounds(
            (ClientSize.Width - ScaleForDpi(80)) / 2,
            ClientSize.Height - ScaleForDpi(25),
            ScaleForDpi(80),
            ScaleForDpi(18));

        var closeSize = ScaleForDpi(28);
        _closeRect = new Rectangle(ClientSize.Width - ScaleForDpi(36), ScaleForDpi(8), closeSize, closeSize);

        LayoutBorderStrips();
        Invalidate();
    }

    private int ScaleForDpi(int value) => (int)Math.Round(value * (DeviceDpi / 96F));

    private float ScaleForDpi(float value) => value * (DeviceDpi / 96F);

    private string FormatStatusText() => $"{_statusBaseText}{new string('.', _statusDotCount)}";

    private static (string BaseText, int DotCount) SplitStatusDots(string status)
    {
        var trimmed = status.TrimEnd();
        var dotCount = 0;
        while (trimmed.EndsWith(".", StringComparison.Ordinal) && dotCount < 3)
        {
            dotCount++;
            trimmed = trimmed[..^1];
        }

        return (trimmed, Math.Clamp(dotCount, 1, 3));
    }

    private void DrawCloseIcon(Graphics graphics)
    {
        if (_closeHovered || _closePressed)
        {
            using var background = new SolidBrush(_closePressed
                ? Color.FromArgb(205, 38, 38)
                : Color.FromArgb(232, 53, 53));
            graphics.FillRectangle(background, _closeRect);
        }

        var color = _closeHovered || _closePressed
            ? Color.White
            : Color.FromArgb(184, 198, 209);

        using var pen = new Pen(color, ScaleForDpi(2F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        var scaleX = _closeRect.Width / 24F;
        var scaleY = _closeRect.Height / 24F;
        var offsetX = _closeRect.Left;
        var offsetY = _closeRect.Top;
        graphics.DrawLine(pen, offsetX + (18 * scaleX), offsetY + (6 * scaleY), offsetX + (6 * scaleX), offsetY + (18 * scaleY));
        graphics.DrawLine(pen, offsetX + (6 * scaleX), offsetY + (6 * scaleY), offsetX + (18 * scaleX), offsetY + (18 * scaleY));
    }

    private Panel CreateBorderStrip()
    {
        var panel = new Panel
        {
            BackColor = LoaderBorderColor,
            TabStop = false
        };
        return panel;
    }

    private static readonly Color LoaderFallbackBackground = Color.FromArgb(27, 40, 56);
    private static readonly Color LoaderBorderColor = Color.FromArgb(58, 78, 94);
    private void LayoutBorderStrips()
    {
        if (_topBorder is null || _bottomBorder is null || _leftBorder is null || _rightBorder is null)
            return;

        var width = ClientSize.Width;
        var height = ClientSize.Height;
        if (width <= 0 || height <= 0) return;

        var thickness = Math.Max(1, ScaleForDpi(1));
        _topBorder.SetBounds(0, 0, width, thickness);
        _bottomBorder.SetBounds(0, height - thickness, width, thickness);
        _leftBorder.SetBounds(0, 0, thickness, height);
        _rightBorder.SetBounds(width - thickness, 0, thickness, height);
        _topBorder.BringToFront();
        _bottomBorder.BringToFront();
        _leftBorder.BringToFront();
        _rightBorder.BringToFront();
    }

    private void DragMove()
    {
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    private void EnableDragMove(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                DragMove();
        };
    }

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}

sealed class UpdatePromptForm : Form
{
    private static readonly string UiFamilyName = ResolveUiFamilyName();
    private static readonly Color Background = Color.FromArgb(27, 40, 56);
    private static readonly Color PanelBackground = Color.FromArgb(13, 27, 39);
    private static readonly Color BorderColor = Color.FromArgb(58, 78, 94);
    private static readonly Color AccentColor = Color.FromArgb(103, 193, 245);
    private static readonly Color SuccessColor = Color.FromArgb(164, 208, 7);
    private readonly Label _headingLabel;
    private readonly Label _messageLabel;
    private readonly TextBox _changelogBox;
    private readonly Button _acceptButton;
    private readonly Button _cancelButton;
    private readonly Panel _topBorder;
    private readonly Panel _bottomBorder;
    private readonly Panel _leftBorder;
    private readonly Panel _rightBorder;
    private readonly bool _hasChangelog;
    private readonly Icon? _windowIcon;

    public UpdatePromptForm(string title, string heading, string message, string changelog, string acceptText, string cancelText)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Background;
        ForeColor = Color.White;
        Font = UiFont(9F);
        ClientSize = new Size(500, string.IsNullOrWhiteSpace(changelog) ? 184 : 386);
        MinimumSize = new Size(440, 210);
        _windowIcon = LoaderImages.LoadIcon();
        if (_windowIcon is not null)
            Icon = _windowIcon;

        _hasChangelog = !string.IsNullOrWhiteSpace(changelog);
        _topBorder = CreateBorderStrip();
        _bottomBorder = CreateBorderStrip();
        _leftBorder = CreateBorderStrip();
        _rightBorder = CreateBorderStrip();

        _headingLabel = new Label
        {
            AutoSize = false,
            Text = heading,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = UiFont(13F, FontStyle.Bold),
            BackColor = Color.Transparent,
            ForeColor = Color.White,
            UseCompatibleTextRendering = false
        };

        _messageLabel = new Label
        {
            AutoSize = false,
            Text = message,
            TextAlign = ContentAlignment.TopLeft,
            Font = UiFont(9.5F),
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(184, 198, 209),
            UseCompatibleTextRendering = false
        };

        _changelogBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(10, 24, 36),
            ForeColor = Color.FromArgb(214, 244, 255),
            Font = UiFont(9F),
            HideSelection = true,
            TabStop = false,
            Text = FormatChangelog(changelog),
            Visible = _hasChangelog
        };

        _acceptButton = CreateButton(acceptText, primary: true);
        _acceptButton.DialogResult = DialogResult.OK;
        _cancelButton = CreateButton(cancelText, primary: false);
        _cancelButton.DialogResult = DialogResult.Cancel;
        AcceptButton = _acceptButton;
        CancelButton = _cancelButton;

        Controls.Add(_headingLabel);
        Controls.Add(_messageLabel);
        Controls.Add(_changelogBox);
        Controls.Add(_acceptButton);
        Controls.Add(_cancelButton);
        Controls.Add(_topBorder);
        Controls.Add(_bottomBorder);
        Controls.Add(_leftBorder);
        Controls.Add(_rightBorder);
        ApplyPromptLayout();
        Shown += (_, _) =>
        {
            _changelogBox.SelectionStart = 0;
            _changelogBox.SelectionLength = 0;
            _acceptButton.Select();
        };
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyPromptLayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new LinearGradientBrush(ClientRectangle, Color.FromArgb(18, 38, 53), Color.FromArgb(10, 21, 32), 135F);
        e.Graphics.FillRectangle(background, ClientRectangle);
        using var glow = new SolidBrush(Color.FromArgb(32, AccentColor));
        e.Graphics.FillRectangle(glow, new Rectangle(0, 0, ClientSize.Width, ScaleForDpi(48)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _windowIcon?.Dispose();
        base.Dispose(disposing);
    }

    private void ApplyPromptLayout()
    {
        if (_topBorder is null || _bottomBorder is null || _leftBorder is null || _rightBorder is null ||
            _headingLabel is null || _messageLabel is null || _changelogBox is null ||
            _acceptButton is null || _cancelButton is null)
        {
            return;
        }

        var pad = ScaleForDpi(16);
        var buttonWidth = ScaleForDpi(104);
        var buttonHeight = ScaleForDpi(30);
        var buttonTop = ClientSize.Height - pad - buttonHeight;
        var border = Math.Max(1, ScaleForDpi(1));

        _topBorder.SetBounds(0, 0, ClientSize.Width, border);
        _bottomBorder.SetBounds(0, ClientSize.Height - border, ClientSize.Width, border);
        _leftBorder.SetBounds(0, 0, border, ClientSize.Height);
        _rightBorder.SetBounds(ClientSize.Width - border, 0, border, ClientSize.Height);

        _headingLabel.SetBounds(pad, ScaleForDpi(16), ClientSize.Width - (pad * 2), ScaleForDpi(28));
        _messageLabel.SetBounds(pad, ScaleForDpi(50), ClientSize.Width - (pad * 2), ScaleForDpi(42));
        if (_hasChangelog)
            _changelogBox.SetBounds(pad, ScaleForDpi(104), ClientSize.Width - (pad * 2), Math.Max(ScaleForDpi(104), buttonTop - ScaleForDpi(116)));

        _cancelButton.SetBounds(ClientSize.Width - pad - buttonWidth, buttonTop, buttonWidth, buttonHeight);
        _acceptButton.SetBounds(_cancelButton.Left - ScaleForDpi(10) - buttonWidth, buttonTop, buttonWidth, buttonHeight);
        _topBorder.BringToFront();
        _bottomBorder.BringToFront();
        _leftBorder.BringToFront();
        _rightBorder.BringToFront();
    }

    private Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            Font = UiFont(9F, FontStyle.Bold),
            ForeColor = primary ? Color.White : Color.FromArgb(214, 244, 255),
            BackColor = primary ? Color.FromArgb(82, 124, 20) : PanelBackground,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = primary ? SuccessColor : AccentColor;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(102, 151, 27) : Color.FromArgb(26, 52, 70);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(67, 101, 16) : Color.FromArgb(18, 38, 53);
        return button;
    }

    private static Panel CreateBorderStrip() => new()
    {
        BackColor = BorderColor,
        TabStop = false
    };

    private static string FormatChangelog(string changelog)
    {
        changelog = (changelog ?? "").Trim();
        return string.IsNullOrWhiteSpace(changelog)
            ? "No changelog provided."
            : changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    private static Font UiFont(float size, FontStyle style = FontStyle.Regular) => new(UiFamilyName, size, style, GraphicsUnit.Point);

    private static string ResolveUiFamilyName()
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            var names = fonts.Families.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Contains("Motiva Sans")) return "Motiva Sans";
            if (names.Contains("Motiva Sans Regular")) return "Motiva Sans Regular";
            if (names.Contains("Arial")) return "Arial";
        }
        catch { }

        return "Segoe UI";
    }

    private int ScaleForDpi(int value) => (int)Math.Round(value * (DeviceDpi / 96F));
}

static class NativeWindowChrome
{
    public static void TryApplyRoundedCorners(IntPtr hwnd)
    {
        try
        {
            var preference = DwmWindowCornerPreference.Round;
            DwmSetWindowAttribute(hwnd, DwmWindowAttribute.WindowCornerPreference, ref preference, Marshal.SizeOf<int>());
        }
        catch
        {
            // Older Windows builds keep square corners.
        }
    }

    private enum DwmWindowAttribute
    {
        WindowCornerPreference = 33
    }

    private enum DwmWindowCornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        DwmWindowAttribute dwAttribute,
        ref DwmWindowCornerPreference pvAttribute,
        int cbAttribute);
}

static class AcrylicBackdrop
{
    public static void TryApply(IntPtr hwnd, Color tint, byte opacity)
    {
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentState.AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                GradientColor = ToAbgr(tint, opacity)
            };

            var size = Marshal.SizeOf<AccentPolicy>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.AccentPolicy,
                    Data = pointer,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch
        {
            // Windows versions without acrylic support keep the normal solid loader background.
        }
    }

    private static int ToAbgr(Color color, byte alpha) =>
        (alpha << 24) | (color.B << 16) | (color.G << 8) | color.R;

    private enum AccentState
    {
        AccentDisabled = 0,
        AccentEnableGradient = 1,
        AccentEnableTransparentGradient = 2,
        AccentEnableBlurBehind = 3,
        AccentEnableAcrylicBlurBehind = 4
    }

    private enum WindowCompositionAttribute
    {
        AccentPolicy = 19
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}

static class LoaderImages
{
    public static Image? LoadLogo()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(".hubcaplogo.png", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(resourceName)) return null;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return null;

            using var image = Image.FromStream(stream);
            using var bitmap = new Bitmap(image);
            return CreatePaddedLogo(bitmap);
        }
        catch
        {
            return null;
        }
    }

    public static Icon? LoadIcon()
    {
        using var logo = LoadLogo();
        if (logo is null) return null;

        using var bitmap = new Bitmap(logo, new Size(64, 64));
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Bitmap CreatePaddedLogo(Bitmap source)
    {
        var padding = Math.Max(8, source.Width / 8);
        var padded = new Bitmap(source.Width + (padding * 2), source.Height + (padding * 2), PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(padded);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, padding, padding, source.Width, source.Height);
        TrimLogoRim(padded, new Rectangle(padding, padding, source.Width, source.Height));
        return padded;
    }

    private static void TrimLogoRim(Bitmap bitmap, Rectangle contentBounds)
    {
        var centerX = contentBounds.Left + ((contentBounds.Width - 1) / 2F);
        var centerY = contentBounds.Top + ((contentBounds.Height - 1) / 2F);
        var hardRadius = (Math.Min(contentBounds.Width, contentBounds.Height) / 2F) - 3.2F;
        var softRadius = hardRadius + 2.2F;

        for (var y = contentBounds.Top; y < contentBounds.Bottom; y++)
        {
            for (var x = contentBounds.Left; x < contentBounds.Right; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                var distance = MathF.Sqrt((dx * dx) + (dy * dy));
                if (distance <= hardRadius)
                    continue;

                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A == 0)
                    continue;

                if (distance >= softRadius)
                {
                    bitmap.SetPixel(x, y, Color.FromArgb(0, pixel));
                    continue;
                }

                var fade = 1F - ((distance - hardRadius) / (softRadius - hardRadius));
                fade = fade * fade * (3F - (2F * fade));
                bitmap.SetPixel(x, y, Color.FromArgb((int)Math.Round(pixel.A * fade), pixel));
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

sealed class LogoSpinnerControl : Control
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Image? _logo;
    private readonly Stopwatch _animationClock = Stopwatch.StartNew();

    public LogoSpinnerControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);

        BackColor = Color.Transparent;
        _logo = LoaderImages.LoadLogo();
        _timer = new System.Windows.Forms.Timer { Interval = 15 };
        _timer.Tick += (_, _) => Invalidate();
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var size = Math.Min(ClientSize.Width, ClientSize.Height);
        var ringInset = ScaleForDpi(8F);
        var ringRect = new RectangleF(ringInset, ringInset, size - (ringInset * 2), size - (ringInset * 2));
        using var track = new Pen(Color.FromArgb(42, 103, 193, 245), ScaleForDpi(2.75F));
        using var arc = new Pen(Color.FromArgb(255, 45, 184, 255), ScaleForDpi(2.75F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        e.Graphics.DrawEllipse(track, ringRect);
        var angle = (float)((_animationClock.Elapsed.TotalSeconds * 220) % 360);
        e.Graphics.DrawArc(arc, ringRect, angle, 278);

        var logoSize = size - ScaleForDpi(14F);
        var logoRect = new RectangleF(
            (ClientSize.Width - logoSize) / 2F,
            (ClientSize.Height - logoSize) / 2F,
            logoSize,
            logoSize);

        if (_logo is not null)
        {
            e.Graphics.DrawImage(_logo, logoRect);
        }
        else
        {
            using var fallback = new SolidBrush(Color.FromArgb(255, 19, 135, 184));
            e.Graphics.FillEllipse(fallback, logoRect);
        }
    }

    private float ScaleForDpi(float value) => value * (DeviceDpi / 96F);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _logo?.Dispose();
        }
        base.Dispose(disposing);
    }

}

static class SteamLauncher
{
    public static async Task EnsureDevModeSteamAsync(HttpClient http, int devToolsPort, bool forceRestart)
    {
        if (forceRestart)
        {
            StopSteam();
            StartSteam(devToolsPort);
            return;
        }

        if (await IsDevToolsReachableAsync(http, devToolsPort))
        {
            Logger.Info("Steam with Hubcap patch is already running.");
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
        const uint MB_SETFOREGROUND = 0x00010000;

        MessageBoxW(
            WindowOwner.GetBestOwner(),
            message,
            "HubcapLauncher",
            MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND);
    }

    public static void NotifyPatchAlreadyRunning()
    {
        const uint MB_OK = 0x00000000;
        const uint MB_ICONINFORMATION = 0x00000040;
        const uint MB_TOPMOST = 0x00040000;
        const uint MB_SETFOREGROUND = 0x00010000;

        MessageBoxW(
            WindowOwner.GetBestOwner(),
            "Steam with Hubcap patch is already running.",
            "HubcapLauncher",
            MB_OK | MB_ICONINFORMATION | MB_TOPMOST | MB_SETFOREGROUND);
    }

    public static bool ConfirmRestartSteamForDevTools()
    {
        const uint MB_YESNO = 0x00000004;
        const uint MB_ICONWARNING = 0x00000030;
        const uint MB_TOPMOST = 0x00040000;
        const uint MB_SETFOREGROUND = 0x00010000;
        const int IDYES = 6;

        var result = MessageBoxW(
            WindowOwner.GetBestOwner(),
            "Steam is currently not in dev mode.\n\nSwitch Steam to dev mode so HubcapLauncher can add the buttons?\n\nSteam will restart.",
            "HubcapLauncher",
            MB_YESNO | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND);

        return result == IDYES;
    }

    public static bool ConfirmRestartLauncherForLuaFolder()
    {
        const uint MB_YESNO = 0x00000004;
        const uint MB_ICONWARNING = 0x00000030;
        const uint MB_TOPMOST = 0x00040000;
        const uint MB_SETFOREGROUND = 0x00010000;
        const int IDYES = 6;

        MessageBeep(MB_ICONWARNING);
        var result = MessageBoxW(
            WindowOwner.GetBestOwner(),
            "Lua folder changed.\n\nRestart HubcapLauncher and Steam now so the new folder is used?\n\nSteam will reopen in dev mode.",
            "HubcapLauncher",
            MB_YESNO | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND);

        return result == IDYES;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MessageBeep(uint uType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

static class LauncherRestarter
{
    public static void RestartFullLauncherAndExit()
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new InvalidOperationException("Could not find HubcapLauncher.exe to restart.");

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--allow-multiple --quiet --restart-steam",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        Environment.Exit(0);
    }
}

static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static void SetText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("Could not open Windows clipboard.");

        var handle = IntPtr.Zero;
        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("Could not allocate clipboard memory.");

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException("Could not lock clipboard memory.");

            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
                throw new InvalidOperationException("Could not set clipboard data.");

            handle = IntPtr.Zero;
        }
        finally
        {
            CloseClipboard();
            if (handle != IntPtr.Zero) GlobalFree(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}

static class FolderPicker
{
    public static string? Pick(string initialFolder)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return PickOnSta(initialFolder);

        string? selected = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                selected = PickOnSta(initialFolder);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null) throw error;
        return selected;
    }

    private static string? PickOnSta(string initialFolder)
    {
        var dialogType = Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"));
        if (dialogType is null) throw new InvalidOperationException("Windows folder picker is unavailable.");

        var dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;
        try
        {
            const uint FOS_PICKFOLDERS = 0x00000020;
            const uint FOS_FORCEFILESYSTEM = 0x00000040;
            const uint FOS_PATHMUSTEXIST = 0x00000800;
            const uint FOS_NOCHANGEDIR = 0x00000008;

            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
            dialog.SetTitle("Select Lua Folder");
            dialog.SetOkButtonLabel("Select Folder");

            if (Directory.Exists(initialFolder) &&
                SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, typeof(IShellItem).GUID, out var initialItem) == 0)
            {
                dialog.SetFolder(initialItem);
            }

            var owner = WindowOwner.GetBestOwner();
            var hr = dialog.Show(owner);
            if (hr == unchecked((int)0x800704C7)) return null;
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);

            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out var pathPtr);
            try
            {
                var selectedPath = Marshal.PtrToStringUni(pathPtr);
                if (string.IsNullOrWhiteSpace(selectedPath)) return null;
                return selectedPath;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        private readonly string _name;

        [MarshalAs(UnmanagedType.LPWStr)]
        private readonly string _spec;

        public COMDLG_FILTERSPEC(string name, string spec)
        {
            _name = name;
            _spec = spec;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] COMDLG_FILTERSPEC[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid([MarshalAs(UnmanagedType.LPStruct)] Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }
}

static class SteamDbVersionPicker
{
    public static List<LuaManifestOptionState> Pick(string appId, string depotId, string currentManifestId, bool visible = false)
    {
        var result = PickInternal(appId, depotId, currentManifestId, visible);
        if (!visible && result.NeedsVisibleFallback)
            result = PickInternal(appId, depotId, currentManifestId, visible: true);
        return result.Options;
    }

    private static SteamDbPickResult PickInternal(string appId, string depotId, string currentManifestId, bool visible)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return PickOnSta(appId, depotId, currentManifestId, visible);

        SteamDbPickResult? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = PickOnSta(appId, depotId, currentManifestId, visible);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null) throw error;
        return result ?? new SteamDbPickResult([], false);
    }

    private static SteamDbPickResult PickOnSta(string appId, string depotId, string currentManifestId, bool visible)
    {
        var result = new List<LuaManifestOptionState>();
        var needsVisibleFallback = false;
        var userDataFolder = "";
        using var form = new System.Windows.Forms.Form
        {
            Text = $"SteamDB Versions - App {appId}",
            Width = 980,
            Height = 700,
            StartPosition = visible ? System.Windows.Forms.FormStartPosition.CenterScreen : System.Windows.Forms.FormStartPosition.Manual,
            MinimizeBox = false,
            MaximizeBox = true,
            ShowInTaskbar = visible,
            TopMost = visible,
            Opacity = visible ? 1 : 0,
            FormBorderStyle = visible ? System.Windows.Forms.FormBorderStyle.Sizable : System.Windows.Forms.FormBorderStyle.FixedToolWindow
        };
        if (!visible)
            form.Location = new System.Drawing.Point(-32000, -32000);

        var webView = new WebView2 { Dock = System.Windows.Forms.DockStyle.Fill };
        var bottom = new System.Windows.Forms.Panel
        {
            Dock = System.Windows.Forms.DockStyle.Bottom,
            Height = 44,
            Padding = new System.Windows.Forms.Padding(8)
        };
        var status = new System.Windows.Forms.Label
        {
            AutoSize = false,
            Dock = System.Windows.Forms.DockStyle.Fill,
            Text = "Loading SteamDB manifests...",
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        };
        var useButton = new System.Windows.Forms.Button
        {
            Text = "Use loaded rows",
            Dock = System.Windows.Forms.DockStyle.Right,
            Width = 132,
            Enabled = false
        };
        var cancelButton = new System.Windows.Forms.Button
        {
            Text = "Cancel",
            Dock = System.Windows.Forms.DockStyle.Right,
            Width = 82
        };
        bottom.Controls.Add(status);
        bottom.Controls.Add(cancelButton);
        bottom.Controls.Add(useButton);
        form.Controls.Add(webView);
        form.Controls.Add(bottom);

        var manifestRows = new List<SteamDbManifestRow>();
        var buildRows = new List<SteamDbBuildRow>();
        var latest = new List<LuaManifestOptionState>();
        var loadingBuilds = false;
        var buildLookupComplete = false;
        var buildPolls = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        var timeout = new System.Windows.Forms.Timer { Interval = 120000 };

        void RefreshLatest()
        {
            latest = RowsToOptions(manifestRows, buildRows, currentManifestId);
            useButton.Enabled = latest.Count > 0 && (!loadingBuilds || buildLookupComplete);
        }

        async Task ExtractManifestsAsync()
        {
            if (webView.CoreWebView2 is null) return;
            var json = await webView.CoreWebView2.ExecuteScriptAsync("""
(() => {
  const text = document.body?.innerText || "";
  const rows = [];
  const seen = new Set();
  const datePattern = "\\d{1,2}\\s+[A-Za-z]+\\s+\\d{4}\\s+[\\u2013\\u2014-]\\s+\\d{2}:\\d{2}:\\d{2}\\s+UTC";
  const dateRe = new RegExp(datePattern, "i");
  const manifestRe = /\b\d{12,20}\b/g;
  const dateFrom = value => (String(value || "").match(dateRe) || [""])[0];
  const add = (manifestId, date) => {
    manifestId = String(manifestId || "").trim();
    date = String(date || "").trim();
    if (!/^\d{12,20}$/.test(manifestId) || seen.has(manifestId)) return;
    seen.add(manifestId);
    rows.push({ manifestId, date });
  };

  const display = text.match(new RegExp("Displaying manifest\\s+(\\d{12,20})\\s+dated\\s+(" + datePattern + ")", "i"));
  if (display) add(display[1], display[2]);

  for (const link of document.querySelectorAll("a[href], a")) {
    const combined = `${link.textContent || ""} ${link.getAttribute("href") || ""}`;
    const manifestIds = combined.match(manifestRe) || [];
    if (!manifestIds.length) continue;
    const row = link.closest("tr") || link.closest("li") || link.parentElement;
    let date = dateFrom(row?.innerText || "");
    if (!date && row?.previousElementSibling) date = dateFrom(row.previousElementSibling.innerText || "");
    for (const manifestId of manifestIds) add(manifestId, date);
  }

  const start = text.search(/Previously seen manifests/i);
  const section = start >= 0 ? text.slice(start) : text;
  const lines = section.split(/\n+/).map(line => line.trim()).filter(Boolean);
  let lastDate = "";
  for (const line of lines) {
    const date = dateFrom(line);
    if (date) lastDate = date;
    const manifestIds = line.match(manifestRe) || [];
    for (const manifestId of manifestIds) add(manifestId, lastDate);
  }
  return rows;
})()
""");
            manifestRows = JsonSerializer.Deserialize<List<SteamDbManifestRow>>(json, JsonOptions.Default) ?? [];
            RefreshLatest();
        }

        async Task<bool> IsChallengePageAsync()
        {
            if (webView.CoreWebView2 is null) return false;
            var json = await webView.CoreWebView2.ExecuteScriptAsync("""
(() => {
  const text = `${document.title || ""}\n${document.body?.innerText || ""}`.toLowerCase();
  return /captcha|cloudflare|verify you are human|checking your browser|attention required|cf-challenge|challenge-platform|ray id/.test(text);
})()
""");
            return bool.TryParse(json, out var blocked) && blocked;
        }

        async Task ExtractBuildsAsync()
        {
            if (webView.CoreWebView2 is null) return;
            var json = await webView.CoreWebView2.ExecuteScriptAsync("""
(() => {
  const text = document.body?.innerText || "";
  const rows = [];
  const seen = new Set();
  const datePattern = "\\d{1,2}\\s+[A-Za-z]+\\s+\\d{4}(?:\\s+[\\u2013\\u2014-]\\s+\\d{2}:\\d{2}(?::\\d{2})?\\s+UTC|,\\s+[A-Za-z]{3},\\s+\\d{2}:\\d{2}|\\s+[A-Za-z]{3}\\s+\\d{2}:\\d{2})";
  const dateRe = new RegExp(datePattern, "i");
  const dateOnlyRe = /^\d{1,2}\s+[A-Za-z]+\s+\d{4}$/i;
  const dayRe = /^(Mon|Tue|Wed|Thu|Fri|Sat|Sun)$/i;
  const timeRe = /^\d{1,2}:\d{2}$/;
  const buildRe = /\b\d{5,12}\b/g;
  const dateFrom = value => (String(value || "").match(dateRe) || [""])[0];
  const add = (buildId, date) => {
    buildId = String(buildId || "").trim();
    date = String(date || "").trim();
    if (!/^\d{5,12}$/.test(buildId)) return;
    if (seen.has(buildId)) {
      const existing = rows.find(row => row.buildId === buildId);
      if (existing && !existing.date && date) existing.date = date;
      return;
    }
    seen.add(buildId);
    rows.push({ buildId, date });
  };

  const buildTable = Array.from(document.querySelectorAll("table")).find(table => {
    const tableText = table.innerText || "";
    return /Patch\s+Title/i.test(tableText) && /\bBuildID\b/i.test(tableText) && /\bDate\b/i.test(tableText);
  });

  if (buildTable) {
    for (const tr of buildTable.querySelectorAll("tbody tr")) {
      const cells = Array.from(tr.querySelectorAll("td")).map(td => (td.innerText || "").trim()).filter(Boolean);
      if (cells.length < 4) continue;
      const buildIds = cells[cells.length - 1].match(buildRe) || [];
      const buildId = buildIds.length ? buildIds[buildIds.length - 1] : "";
      if (!buildId) continue;
      const date = `${cells[0] || ""} ${cells[1] || ""} ${cells[2] || ""}`.trim();
      add(buildId, date);
    }
  }

  let buildStart = text.search(/Date\s+Day\s+Time\s+Patch\s+Title\s+BuildID/i);
  if (buildStart < 0) {
    const buildsMatch = text.match(/\nBuilds\s*(?:\nRSS)?\n/i);
    buildStart = buildsMatch ? buildsMatch.index : -1;
  }
  const buildSection = buildStart >= 0
    ? text.slice(buildStart).split(/\nSteamDB|\n\s*ABOUT|\n\s*DISCOVERY|\n\s*TOOLS|\n\s*DATABASE|\nImprove our data coverage/i)[0]
    : "";

  for (const link of (buildTable || document.createElement("div")).querySelectorAll("a[href]")) {
    const href = link.getAttribute("href") || "";
    const match = href.match(/\/patchnotes\/(\d{5,12})\/?/i);
    if (!match) continue;
    const row = link.closest("tr") || link.closest("li") || link.parentElement;
    let date = dateFrom(row?.innerText || "");
    if (!date && row?.previousElementSibling) date = dateFrom(row.previousElementSibling.innerText || "");
    add(match[1], date);
  }

  const lines = buildSection.split(/\n+/).map(line => line.trim()).filter(Boolean);
  let lastDate = "";
  for (const line of lines) {
    const date = dateFrom(line);
    if (date) lastDate = date;
    const buildMatch = line.match(/\bBuild\s+(\d{5,12})\b/i) || line.match(/^(\d{5,12})$/);
    if (buildMatch) add(buildMatch[1], lastDate);
    if (date) {
      const buildIds = line.match(buildRe) || [];
      for (const buildId of buildIds) add(buildId, date);
    }
  }

  for (let i = 0; i < lines.length; i++) {
    if (!dateOnlyRe.test(lines[i])) continue;
    const day = lines.slice(i + 1, i + 5).find(line => dayRe.test(line)) || "";
    const time = lines.slice(i + 1, i + 6).find(line => timeRe.test(line)) || "";
    if (!day || !time) continue;
    const buildId = lines.slice(i + 1, i + 10).find(line => /^\d{5,12}$/.test(line)) || "";
    if (buildId) add(buildId, `${lines[i]} ${day} ${time}`);
  }
  return rows;
})()
""");
            buildRows = JsonSerializer.Deserialize<List<SteamDbBuildRow>>(json, JsonOptions.Default) ?? [];
            buildPolls++;
            if (buildRows.Count > 0 || buildPolls >= 10)
                buildLookupComplete = true;
            RefreshLatest();
        }

        async Task ExtractAsync(bool closeOnSuccess)
        {
            if (webView.CoreWebView2 is null) return;
            try
            {
                var source = webView.CoreWebView2.Source ?? "";
                if (!visible && await IsChallengePageAsync())
                {
                    needsVisibleFallback = true;
                    timer.Stop();
                    form.Close();
                    return;
                }

                if (source.Contains("/patchnotes", StringComparison.OrdinalIgnoreCase))
                {
                    await ExtractBuildsAsync();
                    if (buildRows.Count > 0)
                    {
                        status.Text = $"Loaded {manifestRows.Count} ManifestIDs and {buildRows.Count} BuildIDs.";
                    }
                    else if (!buildLookupComplete)
                    {
                        status.Text = $"Loaded {manifestRows.Count} ManifestIDs. Still waiting for BuildIDs...";
                    }
                    else
                    {
                        status.Text = $"Loaded {manifestRows.Count} ManifestIDs. BuildIDs unavailable on this SteamDB page.";
                    }
                    if (!visible && buildLookupComplete && manifestRows.Count > 0 && !HasMatchedBuildIds(latest))
                    {
                        needsVisibleFallback = true;
                        result = latest;
                        timer.Stop();
                        form.Close();
                        return;
                    }
                    if (closeOnSuccess && manifestRows.Count > 0 && (HasMatchedBuildIds(latest) || (!visible && buildLookupComplete)))
                    {
                        result = latest;
                        timer.Stop();
                        form.Close();
                    }
                    return;
                }

                await ExtractManifestsAsync();
                if (manifestRows.Count == 0)
                {
                    status.Text = "No ManifestIDs found yet. Finish any browser check, then use loaded rows.";
                    return;
                }

                status.Text = $"Found {manifestRows.Count} ManifestIDs. Loading BuildIDs...";
                if (!loadingBuilds)
                {
                    loadingBuilds = true;
                    buildLookupComplete = false;
                    buildPolls = 0;
                    useButton.Enabled = false;
                    webView.CoreWebView2.Navigate($"https://steamdb.info/app/{Uri.EscapeDataString(appId)}/patchnotes/");
                }
            }
            catch (Exception ex)
            {
        status.Text = $"Could not read page yet: {ex.Message}";
            }
        }

        useButton.Click += async (_, _) =>
        {
            await ExtractAsync(closeOnSuccess: false);
            result = latest;
            form.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            result = [];
            form.Close();
        };
        timer.Tick += async (_, _) => await ExtractAsync(closeOnSuccess: true);
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            RefreshLatest();
            if (!visible && (latest.Count == 0 || !HasMatchedBuildIds(latest)))
                needsVisibleFallback = true;
            result = latest;
            form.Close();
        };
        form.FormClosed += (_, _) =>
        {
            timer.Stop();
            timeout.Stop();
        };

        form.Shown += async (_, _) =>
        {
            try
            {
                form.WindowState = System.Windows.Forms.FormWindowState.Normal;
                if (visible)
                {
                    form.Show();
                    form.BringToFront();
                    form.Activate();
                    _ = Task.Delay(2500).ContinueWith(_ =>
                    {
                        if (!form.IsDisposed)
                        {
                            try { form.BeginInvoke(() => form.TopMost = false); } catch { }
                        }
                    });
                }
                timeout.Start();
                userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HubcapLauncher",
                    "SteamDbWebView2");
                Directory.CreateDirectory(userDataFolder);
                TryDeleteOldSteamDbTempProfiles();
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.NavigationCompleted += async (_, _) =>
                {
                    await ExtractAsync(closeOnSuccess: true);
                    timer.Start();
                };
                webView.CoreWebView2.Navigate($"https://steamdb.info/depot/{Uri.EscapeDataString(depotId)}/manifests/");
            }
            catch (Exception ex)
            {
                status.Text = $"WebView2 failed: {ex.Message}";
            }
        };

        System.Windows.Forms.Application.Run(form);
        form.Dispose();
        CleanSteamDbWebViewCache(userDataFolder);
        return new SteamDbPickResult(result, needsVisibleFallback);
    }

    private static void TryDeleteOldSteamDbTempProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "HubcapLauncher");
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root, "SteamDbWebView2-*"))
            TryDeleteDirectory(dir);
    }

    private static void CleanSteamDbWebViewCache(string userDataFolder)
    {
        if (string.IsNullOrWhiteSpace(userDataFolder) || !Directory.Exists(userDataFolder)) return;
        var cacheNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cache",
            "Code Cache",
            "GPUCache",
            "DawnGraphiteCache",
            "DawnWebGPUCache",
            "GrShaderCache",
            "ShaderCache",
            "GraphiteDawnCache",
            "BrowserMetrics",
            "CrashpadMetrics-active.pma",
            "CrashpadMetrics.pma"
        };

        var cleanupTargets = Directory.EnumerateFileSystemEntries(userDataFolder, "*", SearchOption.AllDirectories)
            .Where(path => cacheNames.Contains(Path.GetFileName(path)))
            .OrderByDescending(path => path.Length)
            .ToList();

        foreach (var path in cleanupTargets)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Cache cleanup is best-effort; keep cookies/session data intact.
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(250);
            }
        }
    }

    private static List<LuaManifestOptionState> RowsToOptions(
        List<SteamDbManifestRow> manifestRows,
        List<SteamDbBuildRow> buildRows,
        string currentManifestId)
    {
        var buildByDate = buildRows
            .Select(row => new { Key = SteamDbTimestampKey(row.Date), row.BuildId })
            .Where(row => !string.IsNullOrWhiteSpace(row.Key) && !string.IsNullOrWhiteSpace(row.BuildId))
            .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().BuildId.Trim(), StringComparer.OrdinalIgnoreCase);
        var buildRowsInDisplayOrder = buildRows
            .Where(row => Regex.IsMatch(row.BuildId.Trim(), @"^\d{5,12}$"))
            .ToList();
        var useDisplayOrderFallback = manifestRows.Count > 0 &&
            buildRowsInDisplayOrder.Count >= manifestRows.Count &&
            !manifestRows.Any(row => buildByDate.ContainsKey(SteamDbTimestampKey(row.Date)));
        var options = new Dictionary<string, LuaManifestOptionState>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < manifestRows.Count; index++)
        {
            var row = manifestRows[index];
            var manifestId = row.ManifestId.Trim();
            if (!Regex.IsMatch(manifestId, @"^\d{12,20}$")) continue;
            var isCurrent = string.Equals(manifestId, currentManifestId, StringComparison.OrdinalIgnoreCase);
            var date = row.Date.Trim();
            buildByDate.TryGetValue(SteamDbTimestampKey(date), out var buildId);
            if (string.IsNullOrWhiteSpace(buildId) &&
                useDisplayOrderFallback &&
                index < buildRowsInDisplayOrder.Count)
            {
                buildId = buildRowsInDisplayOrder[index].BuildId.Trim();
            }
            var label = !string.IsNullOrWhiteSpace(buildId)
                ? $"{buildId} - {manifestId}"
                : manifestId;
            var dateOnly = SteamDbDateOnlyLabel(date);
            if (!string.IsNullOrWhiteSpace(dateOnly)) label += $" - {dateOnly}";
            if (isCurrent) label += " (current)";
            options[manifestId] = new LuaManifestOptionState
            {
                ManifestId = manifestId,
                BuildId = buildId ?? "",
                Date = date,
                Label = label,
                IsCurrent = isCurrent
            };
        }

        if (!string.IsNullOrWhiteSpace(currentManifestId) && !options.ContainsKey(currentManifestId))
        {
            options[currentManifestId] = new LuaManifestOptionState
            {
                ManifestId = currentManifestId,
                BuildId = "",
                Label = $"{currentManifestId} (current)",
                IsCurrent = true
            };
        }

        return options.Values
            .OrderByDescending(option => ParseSteamDbDateForPicker(option.Date))
            .ThenByDescending(option => option.IsCurrent)
            .ToList();
    }

    private static bool HasMatchedBuildIds(List<LuaManifestOptionState> options) =>
        options.Any(option => Regex.IsMatch(option.Label, @"^\d{5,12}\s+-\s+\d{12,20}\b"));

    private static string SteamDbTimestampKey(string value)
    {
        return TryParseSteamDbTimestamp(value, out var parsed)
            ? parsed.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "";
    }

    private static DateTimeOffset ParseSteamDbDateForPicker(string value)
    {
        return TryParseSteamDbTimestamp(value, out var parsed) ? parsed : DateTimeOffset.MinValue;
    }

    private static string SteamDbDateOnlyLabel(string value)
    {
        return TryParseSteamDbTimestamp(value, out var parsed)
            ? parsed.ToUniversalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
            : "";
    }

    private static bool TryParseSteamDbTimestamp(string value, out DateTimeOffset parsed)
    {
        parsed = DateTimeOffset.MinValue;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = Regex.Replace(value, @"\s+", " ")
            .Replace("\u2013", "-", StringComparison.Ordinal)
            .Replace("\u2014", "-", StringComparison.Ordinal)
            .Trim();
        var match = Regex.Match(
            normalized,
            @"(?<day>\d{1,2})\s+(?<month>[A-Za-z]+)\s+(?<year>\d{4})(?:\s*-\s*|,\s*(?:[A-Za-z]{3},\s*)?|\s+[A-Za-z]{3}\s+)(?<hour>\d{1,2}):(?<minute>\d{2})(?::(?<second>\d{2}))?",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        var second = match.Groups["second"].Success ? match.Groups["second"].Value : "00";
        var stamp = $"{match.Groups["day"].Value} {match.Groups["month"].Value} {match.Groups["year"].Value} {match.Groups["hour"].Value}:{match.Groups["minute"].Value}:{second} +00:00";
        return DateTimeOffset.TryParseExact(
            stamp,
            "d MMMM yyyy H:mm:ss zzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
    }

    private sealed class SteamDbManifestRow
    {
        public string ManifestId { get; set; } = "";
        public string Date { get; set; } = "";
    }

    private sealed class SteamDbBuildRow
    {
        public string BuildId { get; set; } = "";
        public string Date { get; set; } = "";
    }

    private sealed record SteamDbPickResult(List<LuaManifestOptionState> Options, bool NeedsVisibleFallback);
}

static class WindowOwner
{
    public static IntPtr GetBestOwner()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero) return foreground;

        foreach (var name in new[] { "steamwebhelper", "steam" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                    return process.MainWindowHandle;
            }
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

sealed class HubcapLauncher
{
    private readonly HttpClient _http;
    private readonly int _port;
    private readonly HubcapService _hubcap;
    private readonly ConcurrentDictionary<string, Task<ResolvedApp>> _resolvedApps = new();
    private readonly ConcurrentDictionary<CdpSession, byte> _storeUiSessions = new();
    private readonly ConcurrentDictionary<CdpSession, byte> _storePageSessions = new();
    private readonly ConcurrentDictionary<CdpSession, string> _webPageSurfaces = new();
    private readonly ConcurrentDictionary<CdpSession, byte> _sharedContextSessions = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _collectionSyncLock = new(1, 1);
    private readonly SemaphoreSlim _settingsOpenLock = new(1, 1);
    private int _lastCollectionSyncVersion = -1;
    private DateTimeOffset _nextCollectionSyncRetryAt = DateTimeOffset.MinValue;

    public Task Ready => _ready.Task;

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
        return IsStoreUiTarget(target) || IsSharedContextTarget(target);
    }

    private static bool IsStoreTarget(CdpTarget target) =>
        target.Url.StartsWith("https://store.steampowered.com/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSteamShellTarget(CdpTarget target) =>
        target.Title.Equals("Steam", StringComparison.OrdinalIgnoreCase) &&
        target.Url.Contains("browserType=4", StringComparison.OrdinalIgnoreCase);

    private static bool IsSteamWebTarget(CdpTarget target) =>
        target.Url.StartsWith("https://store.steampowered.com/", StringComparison.OrdinalIgnoreCase) ||
        target.Url.StartsWith("https://steamcommunity.com/", StringComparison.OrdinalIgnoreCase);

    private static bool IsStoreUiTarget(CdpTarget target) =>
        IsSteamWebTarget(target) || IsSteamShellTarget(target);

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
        var isStoreUiTarget = IsStoreUiTarget(target);
        var isStorePageTarget = IsSteamWebTarget(target);
        var isSteamShellTarget = IsSteamShellTarget(target);
        if (isStoreUiTarget)
            _storeUiSessions[cdp] = 0;
        if (isStorePageTarget)
            _storePageSessions[cdp] = 0;
        if (IsSharedContextTarget(target))
            _sharedContextSessions[cdp] = 0;
        cdp.BindingCalled += (_, payload) => _ = Task.Run(() => HandleEventAsync(cdp, payload));
        try
        {
            await cdp.ConnectAsync();
            await cdp.SendAsync("Runtime.enable");
            await cdp.SendAsync("Runtime.addBinding", new { name = "hubcapNative" });

            var inject = await cdp.EvaluateAsync(isStoreUiTarget ? Scripts.StoreUi : Scripts.SharedLibraryUi);
            if (inject["exceptionDetails"] is not null)
                throw new InvalidOperationException(inject["exceptionDetails"]!.ToJsonString());

            var visibleAppId = IsStoreTarget(target) ? AppIdFromUrl(target.Url) : "";
            if (!string.IsNullOrWhiteSpace(visibleAppId))
            {
                await RefreshStateAsync(cdp, visibleAppId);
            }

            Logger.Info(isStoreUiTarget ? "Attached to Steam Store." : "Attached to Steam Library context.");
            _ready.TrySetResult();
            if (isSteamShellTarget)
                _ = Task.Run(() => RefreshUsageOnlyAsync(cdp, forceRefresh: false));
            if (isStoreUiTarget)
                _ = Task.Run(() => StoreWatchdogAsync(cdp, isSteamShellTarget));
            else if (IsSharedContextTarget(target))
            {
                await SyncLuaCollectionAsync(cdp, force: true);
                _ = Task.Run(() => SharedLibraryWatchdogAsync(cdp));
            }
            await cdp.ReceiveLoopTask;
        }
        finally
        {
            if (isStoreUiTarget)
                _storeUiSessions.TryRemove(cdp, out _);
            if (isStorePageTarget)
                _storePageSessions.TryRemove(cdp, out _);
            else if (isStoreUiTarget)
                _storePageSessions.TryRemove(cdp, out _);
            _webPageSurfaces.TryRemove(cdp, out _);
            if (IsSharedContextTarget(target))
                _sharedContextSessions.TryRemove(cdp, out _);
        }
    }

    private async Task StoreWatchdogAsync(CdpSession cdp, bool isSteamShellTarget)
    {
        var lastAppId = "";
        var lastLuaVersion = _hubcap.LuaVersion;
        while (!cdp.IsClosed)
        {
            try
            {
                if (isSteamShellTarget)
                {
                    var enableShellFallback = !_storePageSessions.Keys.Any(session => !session.IsClosed);
                    await cdp.EvaluateAsync($"window.__hubcapCdpSetShellFallbackEnabled && window.__hubcapCdpSetShellFallbackEnabled({JsonSerializer.Serialize(enableShellFallback, JsonOptions.Default)})");
                }

                var probe = await cdp.EvaluateAsync("""
(() => JSON.stringify({
  appId: typeof window.__hubcapCdpGetAppId === "function" ? window.__hubcapCdpGetAppId() : (location.pathname.match(/\/app\/(\d+)(?:\/|$)/)?.[1] || ""),
  activeSurface: typeof window.__hubcapCdpGetActiveSurface === "function" ? window.__hubcapCdpGetActiveSurface() : "",
  isStoreDocument: location.hostname === "store.steampowered.com" || location.hostname === "steamcommunity.com",
  hasUi: !!document.getElementById("hubcap-cdp-ui"),
  hasSetter: typeof window.__hubcapCdpSetState === "function"
}))()
""");
                var value = probe["result"]?["result"]?["value"]?.GetValue<string>() ?? "";
                var state = JsonSerializer.Deserialize<StoreWatchdogState>(value, JsonOptions.Default);
                if (!isSteamShellTarget)
                {
                    if (state is { IsStoreDocument: true, HasUi: true, HasSetter: true })
                    {
                        _storePageSessions[cdp] = 0;
                        _webPageSurfaces[cdp] = string.IsNullOrWhiteSpace(state.ActiveSurface) ? "WEB" : state.ActiveSurface;
                    }
                    else
                    {
                        _storePageSessions.TryRemove(cdp, out _);
                        _webPageSurfaces.TryRemove(cdp, out _);
                    }
                }
                if (state is not null && (!state.HasUi || !state.HasSetter))
                {
                    var inject = await cdp.EvaluateAsync(Scripts.StoreUi);
                    if (inject["exceptionDetails"] is not null) continue;
                }

                var luaVersion = _hubcap.LuaVersion;
                var luaChanged = luaVersion != lastLuaVersion;
                if (luaChanged)
                    lastLuaVersion = luaVersion;
                await SyncLuaCollectionIfNeededAsync(cdp, luaChanged);
                if (!string.IsNullOrWhiteSpace(state?.AppId) && (state.AppId != lastAppId || luaChanged))
                {
                    lastAppId = state.AppId;
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
                var luaChanged = luaVersion != lastLuaVersion;
                if (luaChanged)
                    lastLuaVersion = luaVersion;
                await SyncLuaCollectionIfNeededAsync(cdp, luaChanged);
                if (!string.IsNullOrWhiteSpace(appId) && (appId != lastAppId || luaChanged))
                {
                    lastAppId = appId;
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

    private async Task SyncLuaCollectionIfNeededAsync(CdpSession cdp, bool luaChanged)
    {
        var luaVersion = _hubcap.LuaVersion;
        if (!luaChanged && luaVersion == Volatile.Read(ref _lastCollectionSyncVersion))
            return;
        if (!luaChanged && DateTimeOffset.UtcNow < _nextCollectionSyncRetryAt)
            return;

        var sync = await SyncLuaCollectionAsync(cdp);
        _nextCollectionSyncRetryAt = sync.Success ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddSeconds(3);
    }

    private async Task WaitForLuaAppReadyForCollectionAsync(CdpSession cdp, string appId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        var syncSession = CollectionSessionFor(cdp);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var hasLocalLua = _hubcap.HasLua(appId, out _);
            var visibleToSteam = await IsAppVisibleToSteamLibraryAsync(syncSession, appId);
            if (hasLocalLua && visibleToSteam)
            {
                await Task.Delay(350);
                return;
            }

            await Task.Delay(250);
        }

        Logger.Info($"Lua collection sync proceeding after wait: app {appId} may still be settling in Steam.");
    }

    private static async Task<bool> IsAppVisibleToSteamLibraryAsync(CdpSession cdp, string appId)
    {
        var appIdJson = JsonSerializer.Serialize(appId);
        var result = await cdp.EvaluateAsync($$"""
(() => {
  const appId = Number.parseInt({{appIdJson}}, 10);
  if (!Number.isInteger(appId) || appId <= 0) return false;
  try {
    return !!globalThis.appStore?.GetAppOverviewByAppID?.(appId);
  } catch {
    return false;
  }
})()
""");
        return result["result"]?["result"]?["value"]?.GetValue<bool?>() == true;
    }

    private async Task<ActionResult> SyncLuaCollectionAsync(CdpSession cdp, bool force = false)
    {
        if (!_hubcap.GetCollectionSyncEnabled())
            return new ActionResult(true, "");

        var luaVersion = _hubcap.LuaVersion;
        if (!force && luaVersion == Volatile.Read(ref _lastCollectionSyncVersion))
            return new ActionResult(true, "");

        if (!await _collectionSyncLock.WaitAsync(TimeSpan.FromSeconds(1)))
            return new ActionResult(false, "Collection sync is already running.");

        try
        {
            luaVersion = _hubcap.LuaVersion;
            if (!force && luaVersion == _lastCollectionSyncVersion)
                return new ActionResult(true, "");

            var appIds = _hubcap.GetLuaAppIds();
            var collectionName = _hubcap.GetCollectionName();
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                Logger.Info("Lua collection sync skipped: no collection selected.");
                return new ActionResult(false, "Choose a collection for Group Lua.");
            }
            if (appIds.Count == 0)
            {
                _lastCollectionSyncVersion = luaVersion;
                Logger.Info($"Lua collection sync skipped: no Lua files found.");
                return new ActionResult(true, "No Lua files found.");
            }

            var syncSession = CollectionSessionFor(cdp);
            var sync = await syncSession.EvaluateAsync(Scripts.SyncLuaCollection(collectionName, appIds));
            if (sync["exceptionDetails"] is not null)
            {
                var error = sync["exceptionDetails"]!.ToJsonString();
                Logger.Info($"Lua collection sync failed: {error}");
                return new ActionResult(false, error);
            }

            var resultJson = sync["result"]?["result"]?["value"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(resultJson))
            {
                try
                {
                    using var result = JsonDocument.Parse(resultJson);
                    var root = result.RootElement;
                    var ok = root.TryGetProperty("ok", out var okNode) && okNode.ValueKind == JsonValueKind.True;
                    var name = root.TryGetProperty("collectionName", out var nameNode) ? nameNode.GetString() ?? collectionName : collectionName;
                    var added = root.TryGetProperty("added", out var addedNode) && addedNode.TryGetInt32(out var addedValue) ? addedValue : 0;
                    var total = root.TryGetProperty("totalLuaApps", out var totalNode) && totalNode.TryGetInt32(out var totalValue) ? totalValue : appIds.Count;
                    var addable = root.TryGetProperty("addableLuaApps", out var addableNode) && addableNode.TryGetInt32(out var addableValue) ? addableValue : total;
                    var removed = root.TryGetProperty("removed", out var removedNode) && removedNode.TryGetInt32(out var removedValue) ? removedValue : 0;
                    if (ok)
                    {
                        if (addable < total)
                        {
                            var pending = total - addable;
                            Logger.Info($"Lua collection sync pending: {pending} Lua game(s) are not visible to Steam yet.");
                            return new ActionResult(false, $"{pending} Lua game(s) are not visible to Steam yet. Try again in a few seconds.");
                        }
                        if (added < addable)
                        {
                            var pending = addable - added;
                            Logger.Info($"Lua collection sync pending: {pending} Lua game(s) were not added by Steam yet.");
                            return new ActionResult(false, $"{pending} Lua game(s) were not added by Steam yet. Try again in a few seconds.");
                        }
                        _lastCollectionSyncVersion = luaVersion;
                        Logger.Info($"Lua collection sync: {added}/{total} Lua games are in \"{name}\". Removed {removed} extras.");
                        return new ActionResult(true, $"Grouped {added}/{total} Lua games in \"{name}\".");
                    }
                    else
                    {
                        var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() ?? "unknown error" : "unknown error";
                        Logger.Info($"Lua collection sync failed: {error}");
                        return new ActionResult(false, error);
                    }
                }
                catch
                {
                    Logger.Info($"Lua collection sync returned: {resultJson}");
                    return new ActionResult(false, resultJson);
                }
            }

            return new ActionResult(false, "Steam collection sync returned no result.");
        }
        catch (Exception ex)
        {
            Logger.Info($"Lua collection sync failed: {ex.Message}");
            return new ActionResult(false, ex.Message);
        }
        finally
        {
            _collectionSyncLock.Release();
        }
    }

    private CdpSession CollectionSessionFor(CdpSession fallback)
    {
        foreach (var session in _sharedContextSessions.Keys.ToList())
        {
            if (!session.IsClosed)
                return session;
        }

        return fallback;
    }

    private async Task<ActionResult> RemoveLuaCollectionAsync(CdpSession cdp, string collectionName)
    {
        collectionName = (collectionName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(collectionName))
            return new ActionResult(false, "Collection name is empty.");

        try
        {
            var removeSession = CollectionSessionFor(cdp);
            var remove = await removeSession.EvaluateAsync(Scripts.RemoveLuaCollection(collectionName));
            if (remove["exceptionDetails"] is not null)
            {
                var exceptionJson = remove["exceptionDetails"]!.ToJsonString();
                Logger.Info($"Lua collection remove failed: {exceptionJson}");
                return new ActionResult(false, exceptionJson);
            }

            var resultJson = remove["result"]?["result"]?["value"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(resultJson))
                return new ActionResult(false, "Steam collection remove returned no result.");

            using var result = JsonDocument.Parse(resultJson);
            var root = result.RootElement;
            var ok = root.TryGetProperty("ok", out var okNode) && okNode.ValueKind == JsonValueKind.True;
            var name = root.TryGetProperty("collectionName", out var nameNode) ? nameNode.GetString() ?? collectionName : collectionName;
            if (ok)
            {
                Logger.Info($"Lua collection removed: {name}");
                return new ActionResult(true, "");
            }

            var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() ?? "unknown error" : "unknown error";
            Logger.Info($"Lua collection remove failed: {error}");
            return new ActionResult(false, error);
        }
        catch (Exception ex)
        {
            Logger.Info($"Lua collection remove failed: {ex.Message}");
            return new ActionResult(false, ex.Message);
        }
    }

    private async Task HandleEventAsync(CdpSession cdp, string raw)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<HubcapEvent>(raw, JsonOptions.Default);
            if (evt is null) return;

            switch (evt.Action)
            {
                case "toggleSettings":
                    await ToggleSettingsAsync(cdp, evt.SettingsAnchorScreenLeft, evt.SettingsAnchorScreenTop, evt.SettingsAnchorClientLeft, evt.SettingsAnchorClientTop, evt.SettingsPreferSource, evt.SettingsSurface);
                    return;

                case "settings":
                    await OpenSettingsAsync(cdp, evt.SettingsAnchorScreenLeft, evt.SettingsAnchorScreenTop, evt.SettingsAnchorClientLeft, evt.SettingsAnchorClientTop, evt.SettingsPreferSource, evt.SettingsSurface);
                    return;

                case "closeSettings":
                    await CloseSettingsPanelsAsync();
                    return;

                case "usage":
                    await RefreshUsageOnlyAsync(cdp, forceRefresh: false);
                    return;

                case "openLuaFolder":
                    var currentSettings = _hubcap.GetSettings();
                    var chosenFolder = _hubcap.ChooseLuaFolder();
                    var folderChanged = string.IsNullOrWhiteSpace(chosenFolder.Error) && chosenFolder.LuaDir != currentSettings.LuaDir;
                    var folderState = new UiState
                    {
                        SettingsOnly = true,
                        Settings = chosenFolder,
                        SettingsDraft = folderChanged,
                        StatusText = folderChanged ? "Folder selected. Save to apply." : chosenFolder.Error,
                        StatusTone = string.IsNullOrWhiteSpace(chosenFolder.Error) ? "idle" : "error",
                        StatusError = !string.IsNullOrWhiteSpace(chosenFolder.Error)
                    };
                    await SetStateAsync(cdp, folderState);
                    await BroadcastSettingsAsync(folderState, except: cdp);
                    return;

                case "saveSettings":
                    var settingsBeforeSave = _hubcap.GetSettings();
                    var groupLuaChanged =
                        settingsBeforeSave.CollectionSyncEnabled != evt.CollectionSyncEnabled ||
                        !string.Equals(settingsBeforeSave.CollectionName, evt.CollectionName, StringComparison.OrdinalIgnoreCase);
                    var savedSettings = _hubcap.SaveSettings(evt.LuaDir, evt.ApiKey, evt.CollectionSyncEnabled, evt.CollectionName);
                    ActionResult? syncResult = null;
                    if (string.IsNullOrWhiteSpace(savedSettings.Error) && savedSettings.CollectionSyncEnabled && groupLuaChanged)
                        syncResult = await SyncLuaCollectionAsync(cdp, force: true);
                    savedSettings.CollectionNames = await LoadCollectionNamesAsync(cdp, savedSettings.CollectionName, includeSelectedFallback: savedSettings.CollectionSyncEnabled && !string.IsNullOrWhiteSpace(savedSettings.CollectionName));
                    var saveError = !string.IsNullOrWhiteSpace(savedSettings.Error);
                    var syncError = syncResult is { Success: false };
                    var savedState = new UiState
                    {
                        SettingsOnly = true,
                        Settings = savedSettings,
                        SettingsDraft = saveError || syncError,
                        StatusText = saveError ? savedSettings.Error : syncError ? $"Saved, but Group Lua failed: {syncResult!.Error}" : syncResult is not null ? (syncResult.Error is { Length: > 0 } ? $"Saved. {syncResult.Error}" : "Saved. Group Lua synced.") : "Saved.",
                        StatusTone = saveError || syncError ? "error" : "success",
                        StatusError = saveError || syncError
                    };
                    await SetStateAsync(cdp, savedState);
                    await BroadcastSettingsAsync(savedState, except: cdp);
                    if (string.IsNullOrWhiteSpace(savedSettings.Error) &&
                        savedSettings.LuaDirChanged &&
                        UserPrompts.ConfirmRestartLauncherForLuaFolder())
                    {
                        LauncherRestarter.RestartFullLauncherAndExit();
                    }
                    return;

                case "removeCollection":
                    var removeCollection = await RemoveLuaCollectionAsync(cdp, evt.CollectionName);
                    var currentAfterRemove = _hubcap.GetSettings();
                    if (removeCollection.Success && string.Equals(currentAfterRemove.CollectionName, evt.CollectionName, StringComparison.OrdinalIgnoreCase))
                    {
                        currentAfterRemove = _hubcap.SaveSettings(currentAfterRemove.LuaDir, currentAfterRemove.ApiKey, false, "");
                    }
                    currentAfterRemove.CollectionNames = await LoadCollectionNamesAsync(cdp, removeCollection.Success ? "" : evt.CollectionName, includeSelectedFallback: !removeCollection.Success);
                    if (removeCollection.Success && currentAfterRemove.CollectionNames.Count == 0)
                        currentAfterRemove.CollectionName = "";
                    var removeState = new UiState
                    {
                        SettingsOnly = true,
                        Settings = currentAfterRemove,
                        SettingsDraft = !removeCollection.Success,
                        StatusText = removeCollection.Success ? $"Removed collection \"{evt.CollectionName}\"." : $"Remove failed: {removeCollection.Error}",
                        StatusTone = removeCollection.Success ? "success" : "error",
                        StatusError = !removeCollection.Success
                    };
                    await SetStateAsync(cdp, removeState);
                    await BroadcastSettingsAsync(removeState, except: cdp);
                    return;

                case "copyApiKey":
                    var copyKey = _hubcap.CopyApiKeyToClipboard();
                    if (!copyKey.Success)
                    {
                        var copyState = new UiState { SettingsOnly = true, Settings = _hubcap.GetSettings(), StatusText = copyKey.Error, StatusTone = "error", StatusError = true };
                        await SetStateAsync(cdp, copyState);
                        await BroadcastSettingsAsync(copyState, except: cdp);
                    }
                    return;

                case "refresh":
                    await RefreshUsageOnlyAsync(cdp, forceRefresh: true);
                    return;
            }

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
                        GameName = resolved.GameName,
                        Exists = false,
                        Removed = libraryRemove.Success,
                        StatusText = libraryRemove.Success ? "" : libraryRemove.Error,
                        StatusTone = libraryRemove.Success ? "success" : "error"
                    });
                    return;

                case "libraryCheck":
                    var libraryCheck = _hubcap.OpenLua(appId);
                    if (libraryCheck.Success)
                    {
                        await RefreshLibraryStateAsync(cdp, visibleAppId);
                    }
                    else
                    {
                        await SetLibraryStateAsync(cdp, new LibraryUiState
                        {
                            AppId = appId,
                            GameName = resolved.GameName,
                            Exists = _hubcap.HasLua(appId, out _),
                            StatusText = libraryCheck.Error,
                            StatusTone = "error"
                        });
                    }
                    return;

                case "libraryLuaVersion":
                    await SetLibraryStateAsync(cdp, new LibraryUiState
                    {
                        AppId = appId,
                        GameName = resolved.GameName,
                        Exists = _hubcap.HasLua(appId, out _),
                        LuaVersion = LuaVersionState.CreateLoading(appId, resolved.GameName)
                    });
                    var luaVersion = await _hubcap.GetLuaVersionWithBrowserAsync(appId, resolved.GameName);
                    luaVersion.GameName = resolved.GameName;
                    await SetLibraryStateAsync(cdp, new LibraryUiState
                    {
                        AppId = appId,
                        GameName = resolved.GameName,
                        Exists = _hubcap.HasLua(appId, out _),
                        LuaVersion = luaVersion
                    });
                    return;

                case "libraryApplyLuaVersion":
                    await SetLibraryStateAsync(cdp, new LibraryUiState
                    {
                        AppId = appId,
                        GameName = resolved.GameName,
                        Exists = _hubcap.HasLua(appId, out _),
                        LuaVersion = LuaVersionState.CreateApplying(appId, resolved.GameName)
                    });
                    var applyLuaVersion = await _hubcap.ApplyLuaVersionAsync(appId, evt.ManifestId);
                    var appliedLuaVersion = _hubcap.GetLuaVersionCurrentOnly(appId, resolved.GameName);
                    var existingOptions = evt.Options?.Where(option => !string.IsNullOrWhiteSpace(option.ManifestId)).ToList() ?? [];
                    if (existingOptions.Count == 0 && !string.IsNullOrWhiteSpace(evt.ManifestId))
                    {
                        existingOptions.Add(new LuaManifestOptionState
                        {
                            ManifestId = evt.ManifestId,
                            BuildId = evt.BuildId,
                            Date = evt.Date,
                            Label = string.IsNullOrWhiteSpace(evt.Label) ? evt.ManifestId : evt.Label
                        });
                    }
                    foreach (var option in existingOptions)
                    {
                        option.IsCurrent = string.Equals(option.ManifestId, appliedLuaVersion.CurrentManifestId, StringComparison.OrdinalIgnoreCase);
                        option.Label = LuaManifestOptionLabel(option);
                    }
                    var currentOption = existingOptions.FirstOrDefault(option => option.IsCurrent);
                    appliedLuaVersion.Options = existingOptions;
                    appliedLuaVersion.SelectedManifestId = appliedLuaVersion.CurrentManifestId;
                    appliedLuaVersion.CurrentBuildId = currentOption?.BuildId ?? appliedLuaVersion.CurrentBuildId;
                    appliedLuaVersion.CurrentDate = currentOption?.Date ?? appliedLuaVersion.CurrentDate;
                    appliedLuaVersion.StatusText = applyLuaVersion.Success ? "Lua manifest updated." : applyLuaVersion.Error;
                    appliedLuaVersion.StatusTone = applyLuaVersion.Success ? "success" : "error";
                    await SetLibraryStateAsync(cdp, new LibraryUiState
                    {
                        AppId = appId,
                        GameName = resolved.GameName,
                        Exists = _hubcap.HasLua(appId, out _),
                        LuaVersion = appliedLuaVersion
                    });
                    return;

                case "libraryLoadLuaVersions":
                    await SetLibraryStateAsync(cdp, new LibraryUiState
                    {
                        AppId = appId,
                        GameName = resolved.GameName,
                        Exists = _hubcap.HasLua(appId, out _),
                        LuaVersion = LuaVersionState.CreateBrowserLoading(appId, resolved.GameName)
                    });
                    var browserLuaVersion = await _hubcap.GetLuaVersionWithBrowserAsync(appId, resolved.GameName);
                    await SetLibraryStateAsync(cdp, new LibraryUiState
                    {
                        AppId = appId,
                        GameName = resolved.GameName,
                        Exists = _hubcap.HasLua(appId, out _),
                        LuaVersion = browserLuaVersion
                    });
                    return;

                case "library":
                    SteamLauncher.OpenLibraryApp(appId);
                    return;

                case "route":
                    await RefreshStateAsync(cdp, visibleAppId);
                    return;

                case "download":
                    await SetStateAsync(cdp, new UiState
                    {
                        Busy = true,
                        BusyText = "Downloading...",
                        StatusText = ""
                    });
                    var add = await _hubcap.DownloadAsync(appId);
                    ActionResult? downloadSync = null;
                    if (add.Success && _hubcap.GetCollectionSyncEnabled())
                    {
                        await WaitForLuaAppReadyForCollectionAsync(cdp, appId);
                        downloadSync = await SyncLuaCollectionAsync(cdp, force: true);
                    }
                    var downloadStatus = add.Success
                        ? downloadSync is { Success: false }
                            ? $"Added, but Group Lua failed: {downloadSync.Error}"
                            : "Added!"
                        : add.Error;
                    await RefreshStateAsync(cdp, visibleAppId, downloadStatus, add.Success && downloadSync is not { Success: false } ? "success" : "error", forceUsageRefresh: add.Success);
                    return;

                case "remove":
                    await SetStateAsync(cdp, new UiState
                    {
                        Busy = true,
                        BusyText = "Removing...",
                        StatusText = ""
                    });
                    var remove = await _hubcap.RemoveLuaAsync(appId);
                    if (remove.Success && _hubcap.GetCollectionSyncEnabled())
                        _ = await SyncLuaCollectionAsync(cdp, force: true);
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
            await BroadcastUsageAsync(usage, except: cdp);
        }
        catch (Exception ex)
        {
            await SetStateAsync(cdp, new UiState { UsageOnly = true, Usage = new UsageState { Error = ex.Message, ErrorLabel = "Stats Error" } });
        }
    }

    private async Task BroadcastUsageAsync(UsageState usage, CdpSession? except = null)
    {
        foreach (var session in _storeUiSessions.Keys.ToList())
        {
            if (ReferenceEquals(session, except) || session.IsClosed) continue;
            try
            {
                await SetStateAsync(session, new UiState { UsageOnly = true, Usage = usage });
            }
            catch
            {
                // A closing Steam target should not block the active action.
            }
        }
    }

    private async Task RefreshUsageOnlyAsync(CdpSession cdp, bool forceRefresh)
    {
        if (cdp.IsClosed) return;
        try
        {
            await SetStateAsync(cdp, new UiState { UsageOnly = true, UsageBusy = true });
            var usage = await _hubcap.GetUsageAsync(forceRefresh: forceRefresh);
            await SetStateAsync(cdp, new UiState { UsageOnly = true, Usage = usage });
            await BroadcastUsageAsync(usage, except: cdp);
        }
        catch (Exception ex)
        {
            if (!cdp.IsClosed)
                await SetStateAsync(cdp, new UiState { UsageOnly = true, Usage = new UsageState { Error = ex.Message, ErrorLabel = "Stats Error" } });
        }
    }

    private async Task BroadcastSettingsAsync(UiState state, CdpSession? except = null)
    {
        foreach (var session in _storeUiSessions.Keys.ToList())
        {
            if (ReferenceEquals(session, except) || session.IsClosed) continue;
            try
            {
                if (!await IsSettingsPanelVisibleAsync(session)) continue;
                await SetStateAsync(session, state);
            }
            catch
            {
                // A closing Steam target should not block the active settings popup.
            }
        }
    }

    private static async Task<bool> IsSettingsPanelVisibleAsync(CdpSession session)
    {
        var probe = await session.EvaluateAsync("""
(() => Array.from(document.querySelectorAll(".hp-settings")).some(panel => panel.dataset.visible === "true"))()
""");
        return probe["result"]?["result"]?["value"]?.GetValue<bool?>() == true;
    }

    private async Task<bool> AnySettingsPanelOpenAsync()
    {
        foreach (var session in _storeUiSessions.Keys.ToList())
        {
            if (session.IsClosed) continue;
            try
            {
                if (await IsSettingsPanelVisibleAsync(session))
                    return true;
            }
            catch
            {
                // A target can close while toggling settings.
            }
        }

        return false;
    }

    private List<CdpSession> LiveStorePageSessions() =>
        _storePageSessions.Keys.Where(session => !session.IsClosed).ToList();

    private CdpSession SettingsTargetFor(CdpSession source, bool preferSource = false, string desiredSurface = "")
    {
        if (preferSource)
            return source;

        if (_storePageSessions.ContainsKey(source) && !source.IsClosed)
            return source;

        if (!string.IsNullOrWhiteSpace(desiredSurface))
        {
            var matchingWebPage = _storePageSessions.Keys
                .Where(session => !session.IsClosed)
                .FirstOrDefault(session => _webPageSurfaces.TryGetValue(session, out var surface) && string.Equals(surface, desiredSurface, StringComparison.OrdinalIgnoreCase));
            if (matchingWebPage is not null)
                return matchingWebPage;
        }

        var liveStorePage = LiveStorePageSessions().FirstOrDefault();
        return liveStorePage ?? source;
    }

    private async Task CloseSettingsPanelsAsync()
    {
        foreach (var session in _storeUiSessions.Keys.ToList())
        {
            if (session.IsClosed) continue;
            try
            {
                await session.EvaluateAsync("""
(() => {
  window.__hubcapSettingsGloballyVisible = "false";
  window.__hubcapSettingsAnchor = null;
  document.querySelectorAll(".hp-settings").forEach(panel => {
    panel.dataset.visible = "false";
    panel.style.removeProperty("display");
  });
})()
""");
            }
            catch
            {
                // A target can close while toggling settings.
            }
        }
    }

    private async Task ToggleSettingsAsync(CdpSession source, double? anchorLeft = null, double? anchorTop = null, double? anchorClientLeft = null, double? anchorClientTop = null, bool preferSource = false, string desiredSurface = "")
    {
        if (await AnySettingsPanelOpenAsync())
        {
            await CloseSettingsPanelsAsync();
            return;
        }

        await OpenSettingsAsync(source, anchorLeft, anchorTop, anchorClientLeft, anchorClientTop, preferSource, desiredSurface);
    }

    private async Task OpenSettingsAsync(CdpSession source, double? anchorLeft = null, double? anchorTop = null, double? anchorClientLeft = null, double? anchorClientTop = null, bool preferSource = false, string desiredSurface = "")
    {
        if (!await _settingsOpenLock.WaitAsync(TimeSpan.FromSeconds(1)))
            return;

        try
        {
            var target = SettingsTargetFor(source, preferSource, desiredSurface);
            var useSourceAnchor = ReferenceEquals(target, source);
            if (!useSourceAnchor)
                await CloseSettingsPanelsAsync();

            var settings = _hubcap.GetSettings();
            settings.CollectionNames = string.IsNullOrWhiteSpace(settings.CollectionName) ? [] : [settings.CollectionName];
            var initialState = new UiState
            {
                SettingsOnly = true,
                Settings = settings,
                SettingsAnchorScreenLeft = anchorLeft,
                SettingsAnchorScreenTop = anchorTop,
                SettingsAnchorClientLeft = useSourceAnchor ? anchorClientLeft : null,
                SettingsAnchorClientTop = useSourceAnchor ? anchorClientTop : null
            };
            await SetStateAsync(target, initialState);
            await BroadcastSettingsAsync(initialState, except: target);
            try
            {
                settings = _hubcap.GetSettings();
                settings.CollectionNames = await LoadCollectionNamesAsync(target, settings.CollectionName, includeSelectedFallback: !string.IsNullOrWhiteSpace(settings.CollectionName));
                var state = new UiState
                {
                    SettingsOnly = true,
                    Settings = settings,
                    SettingsAnchorScreenLeft = anchorLeft,
                    SettingsAnchorScreenTop = anchorTop,
                    SettingsAnchorClientLeft = useSourceAnchor ? anchorClientLeft : null,
                    SettingsAnchorClientTop = useSourceAnchor ? anchorClientTop : null
                };
                await SetStateAsync(target, state);
                await BroadcastSettingsAsync(state, except: target);
            }
            catch (Exception ex)
            {
                Logger.Info($"Settings collection list skipped: {ex.Message}");
            }
        }
        finally
        {
            _settingsOpenLock.Release();
        }
    }

    private bool HasLiveStorePageSession() =>
        _storePageSessions.Keys.Any(session => !session.IsClosed);

    private async Task<List<string>> LoadCollectionNamesAsync(CdpSession source, string selectedName)
    {
        return await LoadCollectionNamesAsync(source, selectedName, includeSelectedFallback: true);
    }

    private async Task<List<string>> LoadCollectionNamesAsync(CdpSession source, string selectedName, bool includeSelectedFallback)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (includeSelectedFallback && !string.IsNullOrWhiteSpace(selectedName))
            names.Add(selectedName);

        foreach (var session in new[] { source }.Concat(_sharedContextSessions.Keys.ToList()))
        {
            if (session.IsClosed) continue;
            try
            {
                var probe = await session.EvaluateAsync("""
(() => {
  const store = globalThis.collectionStore;
  if (!store) return "";
  const idOf = item => String(item?.m_strId || item?.id || "");
  const nameOf = item => item?.displayName || item?.m_strName || item?.name || "";
  const values = Array.isArray(store.userCollections) ? store.userCollections : [];
  const names = values
    .filter(item => idOf(item).startsWith("uc-"))
    .map(nameOf)
    .filter(Boolean);
  return JSON.stringify(Array.from(new Set(names)).sort((a,b)=>a.localeCompare(b)));
})()
""");
                var value = probe["result"]?["result"]?["value"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(value)) continue;
                foreach (var name in JsonSerializer.Deserialize<List<string>>(value, JsonOptions.Default) ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name.Trim());
                }
                if (names.Count > 1)
                    break;
            }
            catch
            {
                // Steam's collection store is only available after Library initializes.
            }
        }

        return names.ToList();
    }

    private async Task RefreshLibraryStateAsync(CdpSession cdp, string visibleAppId)
    {
        var resolved = await ResolveSteamAppAsync(visibleAppId);
        var exists = _hubcap.HasLua(resolved.AppId, out var error);
        await SetLibraryStateAsync(cdp, new LibraryUiState
        {
            AppId = resolved.AppId,
            GameName = resolved.GameName,
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

    private static string LuaManifestOptionLabel(LuaManifestOptionState option)
    {
        var label = Regex.Replace(option.Label ?? "", @"\s+\(current\)$", "", RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            label = !string.IsNullOrWhiteSpace(option.BuildId)
                ? $"{option.BuildId} - {option.ManifestId}"
                : option.ManifestId;
        }
        return option.IsCurrent ? $"{label} (current)" : label;
    }

    private async Task<ResolvedApp> ResolveSteamAppAsync(string visibleAppId)
    {
        if (string.IsNullOrWhiteSpace(visibleAppId)) return new ResolvedApp("", "", "", "", false);
        return await _resolvedApps.GetOrAdd(visibleAppId, ResolveSteamAppUncachedAsync);
    }

    private async Task<ResolvedApp> ResolveSteamAppUncachedAsync(string visibleAppId)
    {
        try
        {
            var json = await _http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(visibleAppId)}&filters=basic");
            var root = JsonNode.Parse(json);
            var data = root?[visibleAppId]?["data"];
            var appName = data?["name"]?.GetValue<string>() ?? "";
            var isDlc = string.Equals(data?["type"]?.GetValue<string>(), "dlc", StringComparison.OrdinalIgnoreCase);
            var fullGame = data?["fullgame"];
            var parentId = AppIdNodeToString(fullGame?["appid"]);
            if (isDlc && !string.IsNullOrWhiteSpace(parentId))
            {
                var parentName = fullGame?["name"]?.GetValue<string>() ?? "";
                return new ResolvedApp(parentId, visibleAppId, string.IsNullOrWhiteSpace(parentName) ? appName : parentName, parentName, true);
            }

            return new ResolvedApp(visibleAppId, visibleAppId, appName, "", false);
        }
        catch (Exception ex)
        {
            Logger.Info($"Could not resolve appdetails for {visibleAppId}: {ex.Message}");
        }

        return new ResolvedApp(visibleAppId, visibleAppId, "", "", false);
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
    private const string ConfigMissingMessage = "Config file not found. Make sure HubcapTools is installed.";
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

    public IReadOnlyList<string> GetLuaAppIds()
    {
        EnsureLuaCache();
        lock (_cacheLock)
            return _luaAppIds
                .Where(appId => Regex.IsMatch(appId, @"^\d+$"))
                .OrderBy(appId => long.TryParse(appId, out var numeric) ? numeric : long.MaxValue)
                .ToList();
    }

    public string GetCollectionName()
    {
        var config = EnsureLuaCache();
        return config.CollectionName.Trim();
    }

    public bool GetCollectionSyncEnabled()
    {
        var config = EnsureLuaCache();
        return config.CollectionSyncEnabled;
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
                ControlsDisabled = true,
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
                return new UsageState
                {
                    Error = await ErrorForResponseAsync(response),
                    ErrorLabel = UsageErrorLabel(response.StatusCode)
                };

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
            return new UsageState { Error = ex.Message, ErrorLabel = "Stats Error" };
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

    public ActionResult OpenLua(string appId)
    {
        try
        {
            var config = EnsureLuaCache();
            var luaPath = Path.Combine(config.LuaDir, $"{appId}.lua");
            if (!File.Exists(luaPath))
            {
                MarkLua(appId, exists: false);
                return new ActionResult(false, "Lua file no longer exists.");
            }

            Process.Start(new ProcessStartInfo(luaPath) { UseShellExecute = true });
            return new ActionResult(true, "");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    public LuaVersionState GetLuaVersionCurrentOnly(string appId, string gameName = "")
    {
        try
        {
            var config = EnsureLuaCache();
            var luaPath = Path.Combine(config.LuaDir, $"{appId}.lua");
            if (!File.Exists(luaPath))
                return LuaVersionState.Error(appId, "", "", "Lua file no longer exists.", gameName);

            var entries = ReadLuaManifestEntries(luaPath);
            var depotId = MainDepotId(appId);
            var current = SelectLuaVersionEntry(entries, appId);
            if (current is null)
                return LuaVersionState.Error(appId, depotId, "", $"No active manifest found for depot {depotId} in this Lua.", gameName);

            return LuaVersionState.ForCurrent(appId, current.DepotId, current.ManifestId, gameName);
        }
        catch (Exception ex)
        {
            return LuaVersionState.Error(appId, MainDepotId(appId), "", ex.Message, gameName);
        }
    }

    public Task<LuaVersionState> GetLuaVersionWithBrowserAsync(string appId, string gameName = "")
    {
        var state = GetLuaVersionCurrentOnly(appId, gameName);
        if (string.IsNullOrWhiteSpace(state.DepotId) || string.IsNullOrWhiteSpace(state.CurrentManifestId))
        {
            state.StatusText = string.IsNullOrWhiteSpace(state.StatusText) ? "No current Lua manifest to match against." : state.StatusText;
            state.StatusTone = "error";
            return Task.FromResult(state);
        }

        try
        {
            var options = SteamDbVersionPicker.Pick(state.AppId, state.DepotId, state.CurrentManifestId);
            if (options.Count == 0)
            {
                state.StatusText = "No manifest rows were readable from the SteamDB window.";
                state.StatusTone = "error";
                return Task.FromResult(state);
            }

            state.Options = options;
            state.SelectedManifestId = state.CurrentManifestId;
            var current = options.FirstOrDefault(option => string.Equals(option.ManifestId, state.CurrentManifestId, StringComparison.OrdinalIgnoreCase));
            state.CurrentDate = current?.Date ?? state.CurrentDate;
            state.CurrentBuildId = current?.BuildId ?? state.CurrentBuildId;
            var hasBuildIds = options.Any(option => Regex.IsMatch(option.Label, @"^\d{5,12}\s+-\s+\d{12,20}\b"));
            state.StatusText = hasBuildIds
                ? $"Loaded {options.Count} versions from SteamDB."
                : $"Loaded {options.Count} ManifestIDs. BuildIDs were not available on SteamDB for this app.";
            state.StatusTone = "success";
            return Task.FromResult(state);
        }
        catch (Exception ex)
        {
            state.StatusText = $"SteamDB browser picker failed: {ex.Message}";
            state.StatusTone = "error";
            return Task.FromResult(state);
        }
    }

    public async Task<ActionResult> ApplyLuaVersionAsync(string appId, string manifestId)
    {
        try
        {
            manifestId = (manifestId ?? "").Trim();
            if (!Regex.IsMatch(manifestId, @"^\d{12,20}$"))
                return new ActionResult(false, "Select a valid ManifestID first.");

            var config = EnsureLuaCache();
            var luaPath = Path.Combine(config.LuaDir, $"{appId}.lua");
            if (!File.Exists(luaPath))
                return new ActionResult(false, "Lua file no longer exists.");

            var entries = ReadLuaManifestEntries(luaPath);
            var target = SelectLuaVersionEntry(entries, appId);
            if (target is null)
                return new ActionResult(false, $"No active manifest found for depot {MainDepotId(appId)} in this Lua.");
            if (string.Equals(target.ManifestId, manifestId, StringComparison.OrdinalIgnoreCase))
                return new ActionResult(false, "That ManifestID is already selected.");

            var text = await File.ReadAllTextAsync(luaPath);
            var depotPattern = Regex.Escape(target.DepotId);
            var pattern = $"""(?m)^(\s*setManifestid\s*\(\s*{depotPattern}\s*,\s*["'])(\d+)(["'])""";
            var replaced = Regex.Replace(
                text,
                pattern,
                match => $"{match.Groups[1].Value}{manifestId}{match.Groups[3].Value}",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
            if (string.Equals(text, replaced, StringComparison.Ordinal))
                return new ActionResult(false, $"Could not update depot {target.DepotId} in the Lua.");

            await File.WriteAllTextAsync(luaPath, replaced);
            MarkLua(appId, exists: true);
            return new ActionResult(true, "");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    public SettingsState GetSettings()
    {
        try
        {
            var source = ResolveConfigSource();
            if (!File.Exists(source.Path))
                return MissingConfigSettings(source.Path);

            return ReadSettings(source);
        }
        catch (Exception ex)
        {
            return new SettingsState { Error = ex.Message };
        }
    }

    public SettingsState ChooseLuaFolder()
    {
        try
        {
            var settings = GetSettings();
            if (settings.ConfigMissing) return settings;

            var currentLuaDir = NormalizeBackslashes(settings.LuaDir);
            var initialFolder = Directory.Exists(currentLuaDir)
                ? currentLuaDir
                : Path.Combine(FindSteamRoot(), "config");
            var selected = FolderPicker.Pick(initialFolder);
            if (string.IsNullOrWhiteSpace(selected)) return GetSettings();

            return new SettingsState
            {
                LuaDir = ToConfigPath(NormalizeBackslashes(selected)),
                ApiKey = settings.ApiKey,
                CollectionName = settings.CollectionName,
                CollectionSyncEnabled = settings.CollectionSyncEnabled
            };
        }
        catch (Exception ex)
        {
            var settings = GetSettings();
            settings.Error = ex.Message;
            return settings;
        }
    }

    public SettingsState SaveSettings(string luaDir, string apiKey, bool collectionSyncEnabled, string collectionName)
    {
        var luaDirChanged = false;
        try
        {
            var source = ResolveConfigSource();
            if (!File.Exists(source.Path))
                throw new InvalidOperationException(ConfigMissingMessage);

            var previousSettings = ReadSettings(source);
            var previousLuaDir = NormalizeBackslashes(previousSettings.LuaDir);

            luaDir = NormalizeBackslashes(Environment.ExpandEnvironmentVariables((luaDir ?? "").Trim()));
            apiKey = (apiKey ?? "").Trim();
            collectionName = (collectionName ?? "").Trim();
            var luaDirWasBlank = string.IsNullOrWhiteSpace(luaDir);
            if (luaDirWasBlank)
                luaDir = DefaultLuaDir();
            if (!collectionSyncEnabled)
                collectionName = "";
            if (collectionSyncEnabled && string.IsNullOrWhiteSpace(collectionName))
                throw new InvalidOperationException("Choose a collection for Group Lua.");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("API key is required.");
            if (!luaDirWasBlank && !Directory.Exists(luaDir))
                throw new InvalidOperationException("Lua folder does not exist.");
            luaDirChanged = !string.Equals(previousLuaDir, luaDir, StringComparison.OrdinalIgnoreCase);

            var apiKeyChanged = !string.Equals(previousSettings.ApiKey, apiKey, StringComparison.Ordinal);
            if (luaDirChanged || apiKeyChanged)
                WriteSettings(source, luaDir, apiKey);
            WriteLauncherSettings(collectionSyncEnabled, collectionName);

            lock (_cacheLock)
            {
                _cachedConfig = null;
                _cachedConfigKey = "";
                _luaWatcher?.Dispose();
                _luaWatcher = null;
            }

            var settings = GetSettings();
            settings.LuaDirChanged = luaDirChanged;
            return settings;
        }
        catch (Exception ex)
        {
            var settings = GetSettings();
            settings.LuaDir = ToConfigPath(NormalizeBackslashes(luaDir ?? settings.LuaDir));
            settings.ApiKey = apiKey ?? settings.ApiKey;
            settings.CollectionSyncEnabled = collectionSyncEnabled;
            settings.CollectionName = collectionSyncEnabled ? collectionName : "";
            settings.Error = ex.Message;
            settings.LuaDirChanged = false;
            return settings;
        }
    }

    public ActionResult CopyApiKeyToClipboard()
    {
        try
        {
            var config = EnsureLuaCache();
            ClipboardHelper.SetText(config.ApiKey);
            return new ActionResult(true, "");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, ex.Message);
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

    private static List<LuaManifestEntry> ReadLuaManifestEntries(string luaPath)
    {
        var entries = new List<LuaManifestEntry>();

        foreach (var line in File.ReadLines(luaPath))
        {
            var manifestMatch = Regex.Match(line, @"^\s*setManifestid\s*\(\s*(?<depot>\d+)\s*,\s*[""'](?<manifest>\d+)[""']\s*(?:,\s*(?<size>\d+))?\s*\)", RegexOptions.IgnoreCase);
            if (!manifestMatch.Success) continue;

            entries.Add(new LuaManifestEntry
            {
                DepotId = manifestMatch.Groups["depot"].Value,
                ManifestId = manifestMatch.Groups["manifest"].Value,
                Size = manifestMatch.Groups["size"].Value
            });
        }

        return entries;
    }

    private static LuaManifestEntry? SelectLuaVersionEntry(List<LuaManifestEntry> entries, string appId)
    {
        var mainDepotId = MainDepotId(appId);
        return entries.FirstOrDefault(entry => string.Equals(entry.DepotId, mainDepotId, StringComparison.OrdinalIgnoreCase));
    }

    private static string MainDepotId(string appId) =>
        long.TryParse(appId, out var numeric) ? (numeric + 1).ToString() : "";

    private HubcapConfig EnsureLuaCache()
    {
        lock (_cacheLock)
        {
            if (_cachedConfig is not null)
                return _cachedConfig;
        }

        var config = ReadConfig();
        var configKey = $"{config.ApiKey}\n{config.LuaDir}\n{config.CollectionSyncEnabled}\n{config.CollectionName}";

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
            if (!Directory.Exists(luaDir))
            {
                Logger.Info($"Lua watcher unavailable: folder not found: {luaDir}");
                return;
            }

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
        var source = ResolveConfigSource();
        if (!File.Exists(source.Path))
            throw new InvalidOperationException(ConfigMissingMessage);

        var config = source.Kind == ConfigSourceKind.ManifestJson
            ? ReadJsonConfig(source.Path)
            : ReadYamlConfig(source.Path);
        var launcherSettings = ReadLauncherSettings();
        return config with
        {
            CollectionName = launcherSettings.CollectionSyncEnabled ? launcherSettings.CollectionName : "",
            CollectionSyncEnabled = launcherSettings.CollectionSyncEnabled
        };
    }

    private static SettingsState ReadSettings(ConfigSource source)
    {
        var config = source.Kind == ConfigSourceKind.ManifestJson
            ? ReadJsonConfig(source.Path)
            : ReadYamlConfig(source.Path);
        var launcherSettings = ReadLauncherSettings();
        config = config with
        {
            CollectionName = launcherSettings.CollectionSyncEnabled ? launcherSettings.CollectionName : "",
            CollectionSyncEnabled = launcherSettings.CollectionSyncEnabled
        };

        return new SettingsState
        {
            LuaDir = ToConfigPath(config.LuaDir),
            ApiKey = config.ApiKey,
            CollectionName = config.CollectionSyncEnabled ? config.CollectionName : "",
            CollectionSyncEnabled = config.CollectionSyncEnabled,
            ConfigPath = DisplayPath(source.Path),
            Error = string.IsNullOrWhiteSpace(config.ApiKey) ? "API Key missing." : ""
        };
    }

    private static HubcapConfig ReadYamlConfig(string configPath)
    {
        string apiKey = "";
        string luaDir = "";
        foreach (var line in File.ReadLines(configPath))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2) continue;
            var key = parts[0].Trim();
            var value = Unquote(parts[1].Trim());
            if (key == "HubcapApiKey") apiKey = value;
            if (key == "HubcapLuaDir") luaDir = NormalizeBackslashes(Environment.ExpandEnvironmentVariables(value));
        }

        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("API Key missing.");
        if (string.IsNullOrWhiteSpace(luaDir)) luaDir = DefaultLuaDir();
        return new HubcapConfig(apiKey, luaDir, "", false);
    }

    private static HubcapConfig ReadJsonConfig(string configPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = doc.RootElement;
        var apiKey = JsonString(root, "ApiKey");
        var luaDir = NormalizeBackslashes(Environment.ExpandEnvironmentVariables(
            JsonString(root, "HubcapToolsLuaPath") ??
            JsonString(root, "SteamToolsLuaPath") ??
            ""));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("API Key missing.");
        if (string.IsNullOrWhiteSpace(luaDir)) luaDir = DefaultLuaDir();
        return new HubcapConfig(apiKey, luaDir, "", false);
    }

    private static LauncherSettings ReadLauncherSettings()
    {
        try
        {
            var path = GetLauncherSettingsPath();
            if (!File.Exists(path))
                return new LauncherSettings();

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var collectionSyncEnabled = JsonBool(root, "CollectionSyncEnabled");
            var collectionName = JsonString(root, "CollectionName")?.Trim() ?? "";
            if (!collectionSyncEnabled)
                collectionName = "";
            return new LauncherSettings(collectionSyncEnabled, collectionName);
        }
        catch (Exception ex)
        {
            Logger.Info($"Launcher settings unavailable: {ex.Message}");
            return new LauncherSettings();
        }
    }

    private static void WriteLauncherSettings(bool collectionSyncEnabled, string collectionName)
    {
        collectionName = collectionSyncEnabled ? (collectionName ?? "").Trim() : "";
        var path = GetLauncherSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var node = new JsonObject
        {
            ["CollectionSyncEnabled"] = collectionSyncEnabled,
            ["CollectionName"] = collectionName
        };
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSettings(ConfigSource source, string luaDir, string apiKey)
    {
        if (source.Kind == ConfigSourceKind.ManifestJson)
        {
            var node = JsonNode.Parse(File.ReadAllText(source.Path))?.AsObject() ?? [];
            node["ApiKey"] = apiKey;
            node["HubcapToolsLuaPath"] = luaDir;
            File.WriteAllText(source.Path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        var lines = File.Exists(source.Path) ? File.ReadAllLines(source.Path).ToList() : [];
        var updatedLuaDir = false;
        var updatedApiKey = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var parts = lines[i].Split(':', 2);
            if (parts.Length != 2) continue;
            var key = parts[0].Trim();
            if (key == "HubcapLuaDir")
            {
                lines[i] = $"HubcapLuaDir: {QuoteConfigValue(ToConfigPath(luaDir))}";
                updatedLuaDir = true;
            }
            else if (key == "HubcapApiKey")
            {
                lines[i] = $"HubcapApiKey: {QuoteConfigValue(apiKey)}";
                updatedApiKey = true;
            }
        }

        if (!updatedApiKey) lines.Add($"HubcapApiKey: {QuoteConfigValue(apiKey)}");
        if (!updatedLuaDir) lines.Add($"HubcapLuaDir: {QuoteConfigValue(ToConfigPath(luaDir))}");
        Directory.CreateDirectory(Path.GetDirectoryName(source.Path)!);
        File.WriteAllLines(source.Path, lines);
    }

    private static bool ParseConfigBool(string value) =>
        value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("1", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);

    private static string GetConfigPath()
    {
        var steamRoot = FindSteamRoot();
        return Path.Combine(steamRoot, "config", "hubcaptools", "config.yaml");
    }

    private static string GetLauncherSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HubcapLauncher",
            "settings.json");

    private static ConfigSource ResolveConfigSource()
    {
        var yamlPath = GetConfigPath();
        return new ConfigSource(yamlPath, ConfigSourceKind.Yaml);
    }

    private static string GetManifestSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HubcapManifestApp", "settings.json");

    private static string DefaultLuaDir() => Path.Combine(FindSteamRoot(), "config", "hubcap-lua");

    private static SettingsState MissingConfigSettings(string configPath) => new()
    {
        ConfigMissing = true,
        ConfigPath = DisplayPath(configPath),
        Error = ConfigMissingMessage
    };

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

    private static string DisplayPath(string path) => ToConfigPath(path);

    private static string JsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static string JsonString(JsonObject node, string propertyName) =>
        node.TryGetPropertyValue(propertyName, out var value) && value is not null
            ? value.GetValue<string?>() ?? ""
            : "";

    private static bool JsonBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => ParseConfigBool(property.GetString() ?? ""),
            JsonValueKind.Number => property.TryGetInt32(out var value) && value != 0,
            _ => false
        };

    private static string NormalizeBackslashes(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        value = value.Trim();
        var isUnc = value.StartsWith(@"\\");
        value = value.Replace('/', '\\');
        while (value.Contains(@"\\"))
            value = value.Replace(@"\\", @"\");
        return isUnc && !value.StartsWith(@"\\") ? @"\" + value : value;
    }

    private static string Unquote(string value)
    {
        if (value.StartsWith('"') && value.EndsWith('"'))
            return UnescapeConfigValue(value[1..^1]);
        if (value.StartsWith('\'') && value.EndsWith('\''))
            return value[1..^1];
        return value;
    }

    private static string QuoteConfigValue(string value) =>
        $"\"{value.Replace("\"", "\\\"")}\"";

    private static string ToConfigPath(string value) => value.Replace('\\', '/');

    private static string UnescapeConfigValue(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                builder.Append(next switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => $"\\{next}"
                });
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
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

    private static async Task<string> ErrorForResponseAsync(HttpResponseMessage response)
    {
        var fallback = ErrorForStatus(response.StatusCode);
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return fallback;
            var json = JsonNode.Parse(body);
            return json?["detail"]?.GetValue<string>() ??
                json?["message"]?.GetValue<string>() ??
                json?["error"]?.GetValue<string>() ??
                fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string UsageErrorLabel(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized => "API Key Error",
        System.Net.HttpStatusCode.Forbidden => "Access Error",
        (System.Net.HttpStatusCode)429 => "Limit Reached",
        System.Net.HttpStatusCode.InternalServerError or
            System.Net.HttpStatusCode.BadGateway or
            System.Net.HttpStatusCode.ServiceUnavailable or
            System.Net.HttpStatusCode.GatewayTimeout => "Server Error",
        _ => "Stats Error"
    };
}

sealed class CdpSession : IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
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
        await _sendLock.WaitAsync();
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
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
sealed record ResolvedApp(string AppId, string VisibleAppId, string GameName, string ParentName, bool IsDlc);
sealed record HubcapConfig(string ApiKey, string LuaDir, string CollectionName, bool CollectionSyncEnabled);
sealed record LauncherSettings(bool CollectionSyncEnabled = false, string CollectionName = "");
sealed record StatusResult(bool Success, bool Available, string Error);
sealed record CachedStatusResult(StatusResult Result, DateTimeOffset ExpiresAt);
sealed record ActionResult(bool Success, string Error);
sealed record HubcapEvent(
    string Action,
    string AppId,
    string Href,
    string LuaDir = "",
    string ApiKey = "",
    bool CollectionSyncEnabled = false,
    string CollectionName = "",
    string ManifestId = "",
    string BuildId = "",
    string Date = "",
    string Label = "",
    bool SettingsPreferSource = false,
    string SettingsSurface = "",
    double? SettingsAnchorScreenLeft = null,
    double? SettingsAnchorScreenTop = null,
    double? SettingsAnchorClientLeft = null,
    double? SettingsAnchorClientTop = null,
    List<LuaManifestOptionState>? Options = null);
sealed record StoreWatchdogState(string AppId, string ActiveSurface, bool IsStoreDocument, bool HasUi, bool HasSetter);

sealed record ConfigSource(string Path, ConfigSourceKind Kind);

enum ConfigSourceKind
{
    Yaml,
    ManifestJson
}

sealed class LuaManifestEntry
{
    public string DepotId { get; init; } = "";
    public string ManifestId { get; init; } = "";
    public string Size { get; init; } = "";
}

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
    public bool ControlsDisabled { get; set; }
    public bool SettingsOnly { get; set; }
    public SettingsState? Settings { get; set; }
    public bool SettingsDraft { get; set; }
    public double? SettingsAnchorScreenLeft { get; set; }
    public double? SettingsAnchorScreenTop { get; set; }
    public double? SettingsAnchorClientLeft { get; set; }
    public double? SettingsAnchorClientTop { get; set; }
}

sealed class SettingsState
{
    public string LuaDir { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public List<string> CollectionNames { get; set; } = [];
    public bool CollectionSyncEnabled { get; set; }
    public string Error { get; set; } = "";
    public bool ConfigMissing { get; set; }
    public string ConfigPath { get; set; } = "";
    public bool LuaDirChanged { get; set; }
}

sealed class LibraryUiState
{
    public string AppId { get; set; } = "";
    public string GameName { get; set; } = "";
    public bool Exists { get; set; }
    public bool Removed { get; set; }
    public string StatusText { get; set; } = "";
    public string StatusTone { get; set; } = "idle";
    public LuaVersionState? LuaVersion { get; set; }
}

sealed class LuaVersionState
{
    public bool Visible { get; set; }
    public bool Loading { get; set; }
    public bool Applying { get; set; }
    public string AppId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string DepotId { get; set; } = "";
    public string CurrentManifestId { get; set; } = "";
    public string CurrentBuildId { get; set; } = "";
    public string CurrentDate { get; set; } = "";
    public string SelectedManifestId { get; set; } = "";
    public string StatusText { get; set; } = "";
    public string StatusTone { get; set; } = "idle";
    public List<LuaManifestOptionState> Options { get; set; } = [];

    public static LuaVersionState CreateLoading(string appId, string gameName = "") => new()
    {
        Visible = true,
        Loading = true,
        AppId = appId,
        GameName = gameName
    };

    public static LuaVersionState CreateBrowserLoading(string appId, string gameName = "") => new()
    {
        Visible = true,
        Loading = true,
        AppId = appId,
        GameName = gameName,
        StatusText = "Loading versions from SteamDB...",
        StatusTone = "idle"
    };

    public static LuaVersionState CreateApplying(string appId, string gameName = "") => new()
    {
        Visible = true,
        Applying = true,
        AppId = appId,
        GameName = gameName,
        StatusText = "Applying...",
        StatusTone = "idle"
    };

    public static LuaVersionState ForCurrent(string appId, string depotId, string manifestId, string gameName = "") => new()
    {
        Visible = true,
        AppId = appId,
        GameName = gameName,
        DepotId = depotId,
        CurrentManifestId = manifestId,
        SelectedManifestId = manifestId
    };

    public static LuaVersionState Error(string appId, string depotId, string manifestId, string error, string gameName = "") => new()
    {
        Visible = true,
        AppId = appId,
        GameName = gameName,
        DepotId = depotId,
        CurrentManifestId = manifestId,
        SelectedManifestId = manifestId,
        StatusText = error,
        StatusTone = "error"
    };
}

sealed class LuaManifestOptionState
{
    public string ManifestId { get; set; } = "";
    public string BuildId { get; set; } = "";
    public string Date { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsCurrent { get; set; }
}

sealed class UsageState
{
    public string Username { get; set; } = "";
    public int DailyUsage { get; set; }
    public int DailyLimit { get; set; }
    public string ApiKeyExpiresAt { get; set; } = "";
    public string Error { get; set; } = "";
    public string ErrorLabel { get; set; } = "";
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
    public static string SyncLuaCollection(string collectionName, IReadOnlyList<string> appIds)
    {
        var collectionJson = JsonSerializer.Serialize(collectionName);
        var appIdsJson = JsonSerializer.Serialize(appIds);
        return $$"""
(() => (async () => {
  const collectionName = {{collectionJson}};
  const luaAppIds = {{appIdsJson}}
    .map(value => Number.parseInt(String(value), 10))
    .filter(value => Number.isInteger(value) && value > 0);
  const store = globalThis.collectionStore;
  if (!store) return JSON.stringify({ ok: false, error: "Steam collectionStore unavailable.", collectionName, totalLuaApps: luaAppIds.length, added: 0 });
  if (!collectionName || !collectionName.trim()) return JSON.stringify({ ok: false, error: "Collection name is empty.", collectionName, totalLuaApps: luaAppIds.length, added: 0 });
  if (!luaAppIds.length) return JSON.stringify({ ok: true, collectionName, collectionId: "", created: false, totalLuaApps: 0, added: 0 });

  const nameOf = collection => collection?.displayName || collection?.m_strName || collection?.name || "";
  const idOf = collection => collection?.id || collection?.m_strId || "";
  const sameName = value => String(value || "").toLocaleLowerCase() === collectionName.toLocaleLowerCase();
  let collection = (store.GetUserCollectionsByName?.(collectionName) || []).find(item => sameName(nameOf(item)));
  let created = false;

  if (!collection) {
    if (typeof store.NewUnsavedCollection !== "function" || typeof store.SaveCollection !== "function") {
      return JSON.stringify({ ok: false, error: "Steam collection creation API unavailable.", collectionName, totalLuaApps: luaAppIds.length, added: 0 });
    }

    collection = store.NewUnsavedCollection(collectionName, undefined, []);
    await Promise.resolve(store.SaveCollection(collection));
    created = true;
  }

  const collectionId = idOf(collection);
  if (!collectionId) return JSON.stringify({ ok: false, error: "Could not resolve Steam collection id.", collectionName, totalLuaApps: luaAppIds.length, added: 0 });

  const wasInCollection = appId => {
    try {
      return (store.GetCollectionListForAppID?.(appId) || []).some(item => idOf(item) === collectionId);
    } catch {
      return false;
    }
  };
  const collectionAppIds = () => {
    try {
      const current = store.GetCollection?.(collectionId) || collection;
      const apps = current?.apps || current?.allApps || current?.visibleApps || current?.m_rgApps || [];
      return Array.from(apps)
        .map(app => Number.parseInt(String(typeof app === "number" ? app : (app?.appid || app?.m_unAppID || app?.id || 0)), 10))
        .filter(value => Number.isInteger(value) && value > 0);
    } catch {
      return [];
    }
  };
  const manualValues = () => {
    try {
      const current = store.GetCollection?.(collectionId) || collection;
      const source = current?.m_setAddedManually || current?.m_setApps || [];
      return Array.from(source);
    } catch {
      return [];
    }
  };
  const pruneManualCollection = async () => {
    const current = store.GetCollection?.(collectionId) || collection;
    let removed = 0;
    for (const value of manualValues()) {
      const appId = Number.parseInt(String(value), 10);
      if (Number.isInteger(appId) && appId > 0 && desired.has(appId)) continue;
      if (typeof current?.m_setAddedManually?.delete === "function" && current.m_setAddedManually.delete(value)) removed++;
      if (typeof current?.m_setApps?.delete === "function") current.m_setApps.delete(value);
    }

    if (removed > 0) {
      if (typeof store.SaveCollection === "function") await Promise.resolve(store.SaveCollection(current));
      else if (typeof current?.Save === "function") await Promise.resolve(current.Save());
    }

    return removed;
  };
  const canAddApp = appId => {
    try {
      return !!globalThis.appStore?.GetAppOverviewByAppID?.(appId);
    } catch {
      return false;
    }
  };
  const addableLuaAppIds = luaAppIds.filter(canAddApp);
  const before = new Set(addableLuaAppIds.filter(wasInCollection));
  const desired = new Set(luaAppIds);
  const beforeCollection = collectionAppIds();
  const toRemove = beforeCollection.filter(appId => !desired.has(appId));

  if (typeof store.AddOrRemoveApp !== "function") {
    return JSON.stringify({ ok: false, error: "Steam AddOrRemoveApp API unavailable.", collectionName, collectionId, created, totalLuaApps: luaAppIds.length, added: before.size });
  }

  if (addableLuaAppIds.length) store.AddOrRemoveApp(addableLuaAppIds, true, collectionId);
  const removedManually = await pruneManualCollection();
  await new Promise(resolve => setTimeout(resolve, 250));

  const after = new Set(addableLuaAppIds.filter(wasInCollection));
  const afterCollection = collectionAppIds();
  return JSON.stringify({
    ok: true,
    collectionName: nameOf(collection) || collectionName,
    collectionId,
    created,
    totalLuaApps: luaAppIds.length,
    addableLuaApps: addableLuaAppIds.length,
    added: after.size,
    collectionSize: afterCollection.length,
    newlyAdded: Array.from(after).filter(appId => !before.has(appId)).length,
    removed: removedManually,
    staleBefore: toRemove.length
  });
})().catch(error => JSON.stringify({
  ok: false,
  error: String(error?.message || error),
  collectionName: {{collectionJson}},
  totalLuaApps: ({{appIdsJson}} || []).length,
  added: 0
})))()
""";
    }

    public static string RemoveLuaCollection(string collectionName)
    {
        var collectionJson = JsonSerializer.Serialize(collectionName);
        return $$"""
(() => (async () => {
  const collectionName = {{collectionJson}};
  const store = globalThis.collectionStore;
  if (!store) return JSON.stringify({ ok: false, error: "Steam collectionStore unavailable.", collectionName });
  if (!collectionName || !collectionName.trim()) return JSON.stringify({ ok: false, error: "Collection name is empty.", collectionName });

  const idOf = collection => String(collection?.m_strId || collection?.id || "");
  const nameOf = collection => collection?.displayName || collection?.m_strName || collection?.name || "";
  const sameName = value => String(value || "").toLocaleLowerCase() === collectionName.toLocaleLowerCase();
  const collection = (store.userCollections || []).find(item => idOf(item).startsWith("uc-") && sameName(nameOf(item)));
  if (!collection) return JSON.stringify({ ok: false, error: "Collection not found.", collectionName });
  const collectionId = idOf(collection);
  if (!collectionId) return JSON.stringify({ ok: false, error: "Could not resolve Steam collection id.", collectionName });
  if (typeof store.DeleteCollection !== "function") return JSON.stringify({ ok: false, error: "Steam collection delete API unavailable.", collectionName, collectionId });

  await Promise.resolve(store.DeleteCollection(collectionId));
  if (typeof store.WriteLocalStorage === "function") {
    try { await Promise.resolve(store.WriteLocalStorage()); } catch {}
  }
  return JSON.stringify({ ok: true, collectionName: nameOf(collection) || collectionName, collectionId });
})().catch(error => JSON.stringify({
  ok: false,
  error: String(error?.message || error),
  collectionName: {{collectionJson}}
})))()
""";
    }

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
  const TOP_STATS_ID = "hubcap-cdp-top-stats";
  const CONFIRM_ID = "hubcap-cdp-remove-confirm";
  const STYLE_ID = "hubcap-cdp-ui-style";
  const USAGE_CACHE_KEY = "hubcap-cdp-usage-cache";
  const UI_VERSION = "2026-07-09-trash-remove-collection";
  const STATS_Z_INDEX = 2147483647;
  delete window.__hubcapCdpTopStatsPointer;
  document.getElementById(STYLE_ID)?.remove();
  if (window.__hubcapCdpController) {
    try { window.__hubcapCdpController.abort(); } catch {}
  }
  document.querySelectorAll("[data-hubcap-launcher-settings-nav='true']").forEach(nav => { delete nav.dataset.hubcapLauncherSettingsBoundVersion; });
  const controller = new AbortController();
  window.__hubcapCdpController = controller;
  const listen = (target, type, handler, options = {}) => target?.addEventListener?.(type, handler, { ...options, signal: controller.signal });
  const style = document.createElement("style");
  style.id = STYLE_ID;
  style.textContent = `
    #${ROOT_ID}{align-items:center;display:flex;gap:10px;justify-content:space-between;margin:8px 0 18px;min-height:34px;width:100%}
    #${ROOT_ID} .hp-left,#${ROOT_ID} .hp-right,#${TOP_STATS_ID}{align-items:center;display:flex;gap:10px;min-width:0}
    #${ROOT_ID} .hp-left{flex:1 1 auto;min-height:34px}
    #${ROOT_ID} .hp-right{flex:0 0 auto;margin-left:auto;position:relative}
    #${TOP_STATS_ID}{background:transparent;border:0;box-sizing:border-box;color:inherit;height:auto;margin:0;overflow:visible;padding:0;position:relative;width:max-content}
    #${TOP_STATS_ID}[data-hidden="true"]{opacity:0!important;pointer-events:none!important;visibility:hidden!important}
    #${TOP_STATS_ID}[data-shell-global="true"]{display:flex!important;gap:8px;pointer-events:none;position:fixed!important;z-index:${STATS_Z_INDEX}!important}
    #${TOP_STATS_ID}[data-shell-global="true"] .hp-settings-button{display:none!important}
    #${TOP_STATS_ID}[data-web-hidden="true"]{display:none!important}
    #${ROOT_ID}[data-web-document="true"] .hp-settings-button{display:none!important}
    #${ROOT_ID} .hp-main,#${ROOT_ID} .hp-library{align-items:center;background:rgba(13,27,39,.48);border:1px solid rgba(103,193,245,.26);border-radius:3px;box-shadow:inset 0 1px 0 rgba(255,255,255,.06),0 1px 2px rgba(0,0,0,.24);color:#d6f4ff;cursor:pointer;display:inline-flex;font:14px Arial,Helvetica,sans-serif;height:32px;justify-content:center;line-height:1;min-height:32px;min-width:124px;padding:0 14px;text-align:center;white-space:nowrap}
    #${ROOT_ID} .hp-main:hover,#${ROOT_ID} .hp-library:hover{background:rgba(26,52,70,.62);border-color:rgba(103,193,245,.42);color:#fff}
    #${ROOT_ID} .hp-main:disabled,#${ROOT_ID} .hp-library:disabled{cursor:default;opacity:.72}
    #${ROOT_ID} .hp-main[data-state="checking"],#${ROOT_ID} .hp-main[data-busy="true"]{align-items:center;display:inline-flex;gap:8px}
    #${ROOT_ID} .hp-main[data-state="checking"]::before,#${ROOT_ID} .hp-main[data-busy="true"]::before{animation:hubcap-cdp-spin .8s linear infinite;border:2px solid rgba(214,244,255,.35);border-top-color:#d6f4ff;border-radius:50%;content:"";height:12px;width:12px}
    #${ROOT_ID} .hp-main[data-state="download"][data-denuvo="true"]{background:rgba(111,60,24,.58);border-color:rgba(246,162,58,.36);color:#ffe7c1;opacity:1}
    #${ROOT_ID} .hp-main[data-state="download"][data-denuvo="true"]:hover{background:rgba(129,72,30,.72);border-color:rgba(246,162,58,.5);color:#fff5e6}
    #${ROOT_ID} .hp-main[data-state="remove"]{background:rgba(95,33,31,.58);border-color:rgba(217,75,63,.36);color:#ffe0dc;opacity:1}
    #${ROOT_ID} .hp-main[data-state="remove"]:hover{background:rgba(112,42,39,.72);border-color:rgba(217,75,63,.5);color:#fff1ef}
    #${ROOT_ID} .hp-main[data-state="disabled"]{background:rgba(13,27,39,.32);border-color:rgba(180,198,210,.18);color:rgba(214,244,255,.56);opacity:1}
    #${ROOT_ID} .hp-main[data-state="disabled"]:hover{background:rgba(13,27,39,.32);border-color:rgba(180,198,210,.18);color:rgba(214,244,255,.56)}
    #${ROOT_ID} .hp-status{color:#acdbf5;font:12px Arial,Helvetica,sans-serif;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
    #${ROOT_ID} .hp-status[data-tone="error"]{color:#ff9b8f;font-weight:700}
    #${ROOT_ID} .hp-status[data-tone="success"]{color:#a4d007;font-weight:700}
    #${ROOT_ID} .hp-warning{color:#f7c46c;display:none;font:12px Arial,Helvetica,sans-serif;font-weight:700;white-space:nowrap}
    #${ROOT_ID} .hp-warning[data-visible="true"]{display:inline-flex}
    .hubcap-cdp-stats .hp-usage{align-self:center;background:rgba(13,27,39,.78);border:1px solid rgba(103,193,245,.32);border-radius:3px;box-shadow:0 8px 22px rgba(0,0,0,.34);box-sizing:border-box;color:#d6f4ff;cursor:pointer;font-family:Arial,Helvetica,sans-serif;min-width:190px;padding:7px 10px 8px;position:relative;transition:background .12s ease,border-color .12s ease,box-shadow .12s ease,color .12s ease,filter .12s ease}
    .hubcap-cdp-stats .hp-usage:hover,.hubcap-cdp-stats .hp-usage[data-hover="true"]{background:rgba(26,52,70,.88);border-color:rgba(103,193,245,.62);box-shadow:0 0 0 1px rgba(103,193,245,.18),0 8px 24px rgba(0,0,0,.42);color:#fff;filter:brightness(1.08)}
    .hubcap-cdp-stats .hp-usage:active,.hubcap-cdp-stats .hp-usage[data-pressed="true"]{background:rgba(18,38,53,.94);border-color:rgba(103,193,245,.78);box-shadow:0 0 0 1px rgba(103,193,245,.26),inset 0 1px 4px rgba(0,0,0,.38)}
    #${TOP_STATS_ID} .hp-usage{height:auto;min-width:210px;overflow:visible;padding:7px 10px 8px}
    #${TOP_STATS_ID}[data-compact="true"] .hp-usage{align-items:center;display:flex;height:24px;min-width:0;width:max-content;padding:0 9px;gap:8px}
    #${TOP_STATS_ID}[data-compact="true"] .hp-usage-row,#${TOP_STATS_ID}[data-compact="true"] .hp-usage-bottom{align-items:center;display:flex;gap:8px;line-height:24px;margin:0;white-space:nowrap}
    #${TOP_STATS_ID}[data-compact="true"] .hp-usage-name{max-width:82px;overflow:hidden;text-overflow:ellipsis}
    #${TOP_STATS_ID}[data-compact="true"] .hp-usage-expiry{font-size:11px;white-space:nowrap}
    #${TOP_STATS_ID}[data-compact="true"] .hp-usage-count-wrap{line-height:24px}
    #${TOP_STATS_ID}[data-compact="true"] .hp-usage-bar{display:none}
    .hubcap-cdp-stats .hp-usage-row,.hubcap-cdp-stats .hp-usage-bottom{align-items:center;display:flex;gap:8px;justify-content:space-between}
    .hubcap-cdp-stats .hp-usage-row{font-size:12px}
    .hubcap-cdp-stats .hp-usage-bottom{font-size:12px;justify-content:space-between;margin-top:5px}
    #${TOP_STATS_ID} .hp-usage-row{font-size:12px;line-height:15px}
    #${TOP_STATS_ID} .hp-usage-bottom{font-size:12px;line-height:15px;margin-top:5px}
    #${TOP_STATS_ID} .hp-usage-bar{display:block}
    .hubcap-cdp-stats .hp-usage-count-wrap{align-items:center;display:inline-flex;gap:6px;margin-left:0;white-space:nowrap}
    .hubcap-cdp-stats .hp-usage-name{color:#fff;font-weight:700}
    .hubcap-cdp-stats .hp-usage-expiry{color:#9fc9e0;font-size:11px}
    .hubcap-cdp-stats .hp-usage-bar{background:rgba(0,0,0,.26);border-radius:999px;height:4px;margin-top:6px;overflow:hidden}
    .hubcap-cdp-stats .hp-usage-fill{background:linear-gradient(90deg,#a4d007 0%,#67c1f5 100%);display:block;height:100%;width:0%}
    .hubcap-cdp-stats .hp-usage-spinner{animation:hubcap-cdp-spin .8s linear infinite;border:2px solid rgba(214,244,255,.28);border-top-color:#d6f4ff;border-radius:50%;box-sizing:border-box;display:inline-flex;flex:0 0 10px;height:10px;opacity:0;visibility:hidden;width:10px}
    .hubcap-cdp-stats .hp-usage-spinner[data-visible="true"]{opacity:1;visibility:visible}
    .hp-settings-button{align-items:center;background:rgba(13,27,39,.78);border:1px solid rgba(103,193,245,.32);border-radius:3px;box-shadow:0 8px 22px rgba(0,0,0,.34);box-sizing:border-box;color:#9fc9e0;cursor:pointer;display:inline-flex;font:13px Arial,Helvetica,sans-serif;height:24px;justify-content:center;min-height:24px;min-width:28px;padding:0;transition:background .12s ease,border-color .12s ease,box-shadow .12s ease,color .12s ease;width:28px}
    .hp-settings-button:hover,.hp-settings-button[data-hover="true"]{background:rgba(26,52,70,.82);border-color:rgba(103,193,245,.58);color:#fff;box-shadow:0 0 8px rgba(103,193,245,.18)}
    .hp-settings-button:active,.hp-settings-button[data-pressed="true"]{background:rgba(18,38,53,.94);border-color:rgba(103,193,245,.76);box-shadow:0 0 0 1px rgba(103,193,245,.24),inset 0 1px 4px rgba(0,0,0,.34);color:#fff}
    .hp-settings{background:rgba(13,27,39,.98);border:1px solid rgba(103,193,245,.34);border-radius:4px;box-shadow:0 18px 52px rgba(0,0,0,.58);box-sizing:border-box;color:#d6f4ff;cursor:default;display:none;font-family:Arial,Helvetica,sans-serif;max-height:calc(100vh - 48px);max-width:calc(100vw - 16px);min-width:380px;overflow:auto;padding:12px;position:absolute;right:0;top:calc(100% + 8px);z-index:${STATS_Z_INDEX}}
    .hp-settings[data-floating="true"]{position:fixed;right:auto;top:auto}
    .hp-settings[data-visible="true"]{display:block}
    .hp-settings-head{align-items:center;cursor:default;display:flex;font-size:12px;font-weight:700;justify-content:space-between;margin-bottom:9px;user-select:none}
    .hp-settings-close{align-items:center;background:transparent;border:0;box-shadow:none;color:#9fc9e0;cursor:pointer;display:inline-flex;font:16px Arial,Helvetica,sans-serif;height:20px;justify-content:center;min-height:20px;min-width:20px;padding:0;width:20px}
    .hp-settings-close:hover{background:rgba(255,255,255,.08);color:#fff}
    .hp-field{display:grid;gap:4px;margin-top:8px}
    .hp-field label{color:#9fc9e0;font-size:11px;font-weight:700}
    .hp-field-row{align-items:center;display:flex;gap:6px}
    .hp-field input,.hp-field select{background:rgba(0,0,0,.24);border:1px solid rgba(103,193,245,.2);border-radius:2px;box-sizing:border-box;color:#d6f4ff;font:12px Consolas,monospace;height:28px;min-width:0;padding:0 8px;width:100%}
    .hp-settings input,.hp-settings textarea{cursor:text}
    .hp-settings button,.hp-settings select{cursor:pointer}
    .hp-field select{-webkit-appearance:none;appearance:none;background-color:rgba(14,29,42,.96);background-image:linear-gradient(45deg,transparent 50%,#9fc9e0 50%),linear-gradient(135deg,#9fc9e0 50%,transparent 50%),linear-gradient(180deg,rgba(25,49,66,.96),rgba(14,29,42,.96));background-position:calc(100% - 16px) 52%,calc(100% - 11px) 52%,0 0;background-repeat:no-repeat;background-size:5px 5px,5px 5px,100% 100%;border:1px solid rgba(103,193,245,.34);border-radius:3px;box-shadow:inset 0 1px 0 rgba(255,255,255,.05),0 1px 2px rgba(0,0,0,.22);color:#d6f4ff;cursor:pointer;font:12px Arial,Helvetica,sans-serif;height:30px;line-height:30px;outline:0;padding:0 34px 0 10px}
    .hp-field select:hover{background-image:linear-gradient(45deg,transparent 50%,#fff 50%),linear-gradient(135deg,#fff 50%,transparent 50%),linear-gradient(180deg,rgba(31,59,78,.98),rgba(17,35,50,.98));border-color:rgba(103,193,245,.52);color:#fff}
    .hp-field select:focus{border-color:rgba(103,193,245,.72);box-shadow:0 0 0 1px rgba(103,193,245,.2),inset 0 1px 0 rgba(255,255,255,.05)}
    .hp-field select option{background:#102334;color:#d6f4ff}
    .hp-toggle-row{align-items:center;display:flex;justify-content:space-between;margin-top:10px}
    .hp-toggle-label{align-items:center;color:#9fc9e0;display:inline-flex;font-size:11px;font-weight:700;gap:7px;min-width:0}
    .hp-current-collection{color:#d6f4ff;font-size:11px;font-weight:400;max-width:210px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
    .hp-group-lua-toggle{align-items:center;background:rgba(0,0,0,.26);border:1px solid rgba(103,193,245,.28);border-radius:999px;box-sizing:border-box;cursor:pointer;display:inline-flex;height:22px;min-width:42px;padding:2px;transition:background .12s ease,border-color .12s ease;width:42px}
    .hp-group-lua-toggle::before{background:#8f98a0;border-radius:50%;box-shadow:0 1px 3px rgba(0,0,0,.34);content:"";display:block;height:16px;transition:transform .12s ease,background .12s ease;width:16px}
    .hp-group-lua-toggle[data-on="true"]{background:rgba(103,193,245,.28);border-color:rgba(103,193,245,.58)}
    .hp-group-lua-toggle[data-on="true"]::before{background:#67c1f5;transform:translateX(18px)}
    .hp-group-lua-options{display:none}
    .hp-group-lua-options[data-visible="true"]{display:grid}
    .hp-new-collection-row{display:none}
    .hp-new-collection-row[data-visible="true"]{display:flex}
    .hp-remove-collection[data-visible="false"]{display:none}
    .hp-icon-button{align-items:center;background:rgba(13,27,39,.48);border:1px solid rgba(103,193,245,.26);border-radius:3px;color:#d6f4ff;cursor:pointer;display:inline-flex;font:13px Arial,Helvetica,sans-serif;height:28px;justify-content:center;min-height:28px;min-width:30px;padding:0;width:30px}
    .hp-icon-button:hover{background:rgba(26,52,70,.62);border-color:rgba(103,193,245,.42);color:#fff}
    .hp-icon-button.hp-remove-collection{background:rgba(95,33,31,.82);border-color:rgba(217,75,63,.62);color:#ffe0dc;font-size:15px}
    .hp-icon-button.hp-remove-collection:hover{background:rgba(128,39,34,.94);border-color:rgba(255,118,105,.82);color:#fff}
    .hp-config-missing{display:none;gap:10px;margin-top:8px}
    .hp-config-missing[data-visible="false"]{display:none!important}
    .hp-config-missing[data-visible="true"]{display:grid}
    .hp-config-missing-text{color:#ff9b8f;font-size:12px;font-weight:700;line-height:1.35}
    .hp-settings-note{color:#9fc9e0;font-size:11px;min-height:14px;margin-top:8px}
    .hp-settings-note[data-tone="error"]{color:#ff9b8f;font-weight:700}
    .hp-settings-note[data-tone="success"]{color:#a4d007;font-weight:700}
    .hp-settings-actions{align-items:center;display:flex;justify-content:flex-end;margin-top:8px}
    .hp-save-settings{align-items:center;background:rgba(13,27,39,.48);border:1px solid rgba(103,193,245,.26);border-radius:3px;color:#d6f4ff;cursor:pointer;display:none;font:12px Arial,Helvetica,sans-serif;height:28px;justify-content:center;min-height:28px;min-width:64px;padding:0 12px}
    .hp-save-settings[data-visible="true"]{display:inline-flex}
    .hp-save-settings:hover{background:rgba(26,52,70,.62);border-color:rgba(103,193,245,.42);color:#fff}
    #${CONFIRM_ID}{align-items:center;background:rgba(0,0,0,.58);bottom:0;box-sizing:border-box;display:flex;font-family:Arial,Helvetica,sans-serif;justify-content:center;left:0;padding:18px;position:fixed;right:0;top:0;z-index:9999999}
    #${CONFIRM_ID} .hp-confirm-box{background:linear-gradient(135deg,rgba(22,40,55,.98),rgba(16,31,43,.98));border:1px solid rgba(103,193,245,.3);border-radius:4px;box-shadow:0 18px 48px rgba(0,0,0,.55);box-sizing:border-box;color:#dfe3e6;max-width:360px;padding:16px;width:min(360px,calc(100vw - 36px))}
    #${CONFIRM_ID} .hp-confirm-title{color:#fff;font-size:16px;font-weight:700;line-height:20px;margin-bottom:8px}
    #${CONFIRM_ID} .hp-confirm-message{color:#b8c6d1;font-size:13px;line-height:18px;margin-bottom:14px;overflow-wrap:anywhere}
    #${CONFIRM_ID} .hp-confirm-actions{align-items:center;display:flex;gap:10px;justify-content:flex-end}
    #${CONFIRM_ID} button{align-items:center;border-radius:3px;box-sizing:border-box;cursor:pointer;display:inline-flex;font:700 13px/1 Arial,Helvetica,sans-serif;height:32px;justify-content:center;min-width:72px;padding:0 14px;text-align:center}
    #${CONFIRM_ID} .hp-confirm-no{background:rgba(13,27,39,.68);border:1px solid rgba(103,193,245,.32);color:#d6f4ff}
    #${CONFIRM_ID} .hp-confirm-no:hover{background:rgba(26,52,70,.78);color:#fff}
    #${CONFIRM_ID} .hp-confirm-yes{background:rgba(95,33,31,.78);border:1px solid rgba(217,75,63,.48);color:#ffe0dc}
    #${CONFIRM_ID} .hp-confirm-yes:hover{background:rgba(112,42,39,.88);color:#fff1ef}
    @keyframes hubcap-cdp-spin{to{transform:rotate(360deg)}}
  `;
  document.head.appendChild(style);
  const existingRoot = document.getElementById(ROOT_ID);
  const root = existingRoot || document.createElement("div");
  root.id = ROOT_ID;
  const shouldRebuild = !existingRoot || root.dataset.uiVersion !== UI_VERSION || !root.querySelector(`#${TOP_STATS_ID}`) || !root.querySelector(".hp-settings-button") || !root.querySelector(".hp-config-missing") || !root.querySelector(".hp-group-lua-toggle") || root.querySelector(".hp-open-config-file");
  if (shouldRebuild) {
    root.dataset.bound = "";
    root.dataset.uiVersion = UI_VERSION;
    document.querySelectorAll(`#${TOP_STATS_ID}`).forEach(panel => panel.remove());
    document.querySelectorAll(".hp-settings").forEach(panel => { if (!root.contains(panel)) panel.remove(); });
    root.innerHTML = `
    <div class="hp-left"><button class="hp-main" type="button" data-state="checking" disabled>Checking...</button><button class="hp-library" type="button" style="display:none">Go to Library</button><span class="hp-status"></span><span class="hp-warning"></span></div>
    <div class="hp-right hubcap-cdp-stats" id="${TOP_STATS_ID}"><button class="hp-settings-button" type="button" title="Hubcap settings">&#9881;</button><div class="hp-usage" title="Refresh usage"><div class="hp-usage-row"><span class="hp-usage-name">Hubcap</span><span class="hp-usage-expiry">Expires --</span></div><div class="hp-usage-bar"><span class="hp-usage-fill"></span></div><div class="hp-usage-bottom"><span class="hp-usage-count-wrap">Daily Usage: <strong class="hp-usage-count">--/--</strong><span class="hp-usage-spinner"></span></span></div></div><div class="hp-settings"><div class="hp-settings-head"><span>Hubcap Settings</span><button class="hp-settings-close" type="button" title="Close">&times;</button></div><div class="hp-config-missing"><div class="hp-config-missing-text">Config file not found. Make sure HubcapTools is installed.</div></div><div class="hp-field"><label>Lua Folder</label><div class="hp-field-row"><input class="hp-lua-dir" type="text"><button class="hp-icon-button hp-open-lua-folder" type="button" title="Select Lua folder">&#128193;</button></div></div><div class="hp-field"><label>API Key</label><div class="hp-field-row"><input class="hp-api-key" type="password"><button class="hp-icon-button hp-toggle-api-key" type="button" title="Show API key">&#128065;</button><button class="hp-icon-button hp-copy-api-key" type="button" title="Copy API key">&#128203;</button></div></div><div class="hp-toggle-row hp-group-lua-row"><span class="hp-toggle-label">Group Lua <span class="hp-current-collection"></span></span><button class="hp-group-lua-toggle" type="button" data-on="false" title="Group Lua"></button></div><div class="hp-field hp-group-lua-options"><label>Collection</label><div class="hp-field-row"><select class="hp-collection-name"></select><button class="hp-icon-button hp-remove-collection" type="button" title="Remove selected collection" aria-label="Remove selected collection">&#128465;</button></div><div class="hp-field-row hp-new-collection-row"><input class="hp-new-collection-name" type="text" placeholder="New collection name"></div></div><div class="hp-settings-note"></div><div class="hp-settings-actions"><button class="hp-save-settings" type="button">Save</button></div></div></div>`;
  }
  const appIdFromText = value => (String(value || "").match(/\/app\/(\d+)(?:\/|$)/)?.[1] || String(value || "").match(/store\.steampowered\.com\/app\/(\d+)(?:\/|$)/)?.[1] || "");
  const appIdFromUrl = () => appIdFromText(location.href) || appIdFromText(location.pathname) || appIdFromText(document.URL) || appIdFromText(document.querySelector('link[rel="canonical"]')?.href) || appIdFromText(document.querySelector('meta[property="og:url"]')?.content) || appIdFromText(globalThis.MainWindowBrowserManager?.m_lastLocation?.pathname || "") || appIdFromText(globalThis.MainWindowBrowserManager?.m_lastLocation?.href || "") || appIdFromText(document.body?.innerText || "");
  window.__hubcapCdpGetAppId = appIdFromUrl;
  window.__hubcapCdpShellFallbackEnabled = window.__hubcapCdpShellFallbackEnabled === true;
  window.__hubcapCdpSetShellFallbackEnabled = enabled => {
    window.__hubcapCdpShellFallbackEnabled = !!enabled;
    placeRoot();
  };
  const statsRoot = () => document.getElementById(TOP_STATS_ID) || root.querySelector(`#${TOP_STATS_ID}`) || root;
  const settingsPanel = () => document.querySelector('.hp-settings[data-hubcap-settings="true"]') || statsRoot()?.querySelector?.(".hp-settings") || root.querySelector(".hp-settings");
  function headerHost(){return document.querySelector(".apphub_HeaderStandardTop") || document.querySelector(".apphub_AppName")?.parentElement || null;}
  function earlyHost(){return headerHost() || document.querySelector("#game_highlights")?.parentElement || document.querySelector(".game_page_background") || document.querySelector(".game_background_glow") || null;}
  function looksLikeAppPage(){return !!(appIdFromUrl()||headerHost()||document.querySelector("#game_highlights")||document.querySelector(".game_page_background"));}
  function isStoreDocument(){return location.hostname === "store.steampowered.com";}
  function isSteamCommunityDocument(){return location.hostname === "steamcommunity.com";}
  function isSteamWebDocument(){return isStoreDocument() || isSteamCommunityDocument();}
  function isSteamShellDocument(){return document.title === "Steam" && /STORE\s*LIBRARY\s*COMMUNITY/i.test(document.body?.innerText || "");}
  function shellContentHost(){
    if (!isSteamShellDocument()) return null;
    const hosts = Array.from(document.querySelectorAll("main,section,div"))
      .filter(element => !element.closest?.(`#${ROOT_ID}`) && element !== document.body)
      .map(element => ({ element, rect: element.getBoundingClientRect(), text: element.innerText || element.textContent || "" }))
      .filter(item => item.rect.width >= 640 && item.rect.height >= 220 && item.rect.top >= 60 && item.rect.top <= 160 && /store\.steampowered\.com/i.test(item.text))
      .sort((a,b) => (a.rect.width * a.rect.height) - (b.rect.width * b.rect.height));
    return hosts[0]?.element || null;
  }
  function closeSettings(event) {
    event?.preventDefault?.();
    event?.stopPropagation?.();
    const panel = settingsPanel();
    window.__hubcapSettingsGloballyVisible = "false";
    window.__hubcapSettingsAnchor = null;
    if (panel) {
      panel.dataset.visible = "false";
      panel.style.removeProperty("display");
    }
    if (event) send("closeSettings");
  }
  function clampSettingsPanel(panel, left, top) {
    const rect = panel.getBoundingClientRect();
    const width = Math.max(380, rect.width || 380);
    const height = Math.max(80, rect.height || 80);
    const nextLeft = Math.max(8, Math.min(window.innerWidth - width - 8, Math.round(left)));
    const nextTop = Math.max(8, Math.min(window.innerHeight - height - 8, Math.round(top)));
    panel.style.left = `${nextLeft}px`;
    panel.style.top = `${nextTop}px`;
  }
  function canDragSettingsFrom(target) {
    return !target?.closest?.("input,textarea,select,button,a");
  }
  function wireSettingsDrag(panel) {
    if (!panel || panel.dataset.dragBound === "true") return;
    panel.dataset.dragBound = "true";
    panel.addEventListener("pointerdown", event => {
      if (event.button !== 0 || !canDragSettingsFrom(event.target)) return;
      const rect = panel.getBoundingClientRect();
      const startX = event.clientX;
      const startY = event.clientY;
      const startLeft = rect.left;
      const startTop = rect.top;
      panel.dataset.dragged = "true";
      panel.setPointerCapture?.(event.pointerId);
      event.preventDefault();
      event.stopPropagation();
      const move = moveEvent => {
        clampSettingsPanel(panel, startLeft + moveEvent.clientX - startX, startTop + moveEvent.clientY - startY);
      };
      const done = upEvent => {
        panel.releasePointerCapture?.(event.pointerId);
        document.removeEventListener("pointermove", move, true);
        document.removeEventListener("pointerup", done, true);
        document.removeEventListener("pointercancel", done, true);
        upEvent?.stopPropagation?.();
      };
      document.addEventListener("pointermove", move, true);
      document.addEventListener("pointerup", done, true);
      document.addEventListener("pointercancel", done, true);
    }, true);
  }
  function ensureSettingsPanelLayer(panel = settingsPanel()) {
    if (!panel) return null;
    panel.dataset.hubcapSettings = "true";
    const floating = isSteamWebDocument() || isSteamShellDocument();
    panel.dataset.floating = floating ? "true" : "false";
    if (floating && panel.parentElement !== document.body) document.body.appendChild(panel);
    wireSettingsDrag(panel);
    return panel;
  }
  function wireSettingsPopup(panel) {
    panel = ensureSettingsPanelLayer(panel);
    const close = panel?.querySelector?.(".hp-settings-close");
    if (close) close.onclick = closeSettings;
    const luaDir = panel?.querySelector?.(".hp-lua-dir");
    const apiKey = panel?.querySelector?.(".hp-api-key");
    const groupLuaToggle = panel?.querySelector?.(".hp-group-lua-toggle");
    const collectionName = panel?.querySelector?.(".hp-collection-name");
    const newCollectionName = panel?.querySelector?.(".hp-new-collection-name");
    const removeCollection = panel?.querySelector?.(".hp-remove-collection");
    const folder = panel?.querySelector?.(".hp-open-lua-folder");
    const save = panel?.querySelector?.(".hp-save-settings");
    const toggle = panel?.querySelector?.(".hp-toggle-api-key");
    const copy = panel?.querySelector?.(".hp-copy-api-key");
    if (luaDir) luaDir.oninput = updateSaveState;
    if (apiKey) apiKey.oninput = updateSaveState;
    if (groupLuaToggle) groupLuaToggle.onclick = event => { event.preventDefault(); event.stopPropagation(); event.stopImmediatePropagation?.(); setGroupLuaEnabled(groupLuaToggle.dataset.on !== "true"); updateSaveState(); };
    if (collectionName) collectionName.onchange = () => { updateGroupLuaNewRow(); updateSaveState(); };
    if (newCollectionName) newCollectionName.oninput = updateSaveState;
    if (removeCollection) removeCollection.onclick = event => { event.preventDefault(); event.stopPropagation(); event.stopImmediatePropagation?.(); removeSelectedCollection(); };
    if (folder) folder.onclick = event => { event.preventDefault(); event.stopPropagation(); setSettingsNote("Selecting Lua folder..."); send("openLuaFolder"); };
    if (save) save.onclick = event => { event.preventDefault(); event.stopPropagation(); saveSettings(); };
    if (toggle) toggle.onclick = event => { event.preventDefault(); event.stopPropagation(); const input = settingsPanel()?.querySelector(".hp-api-key"); if (input) input.type = input.type === "password" ? "text" : "password"; };
    if (copy) copy.onclick = async event => { event.preventDefault(); event.stopPropagation(); const value = settingsPanel()?.querySelector(".hp-api-key").value || ""; try { await navigator.clipboard.writeText(value); setSettingsNote("API key copied."); } catch { send("copyApiKey"); setSettingsNote("API key copied."); } };
  }
  function revealSettingsPopup(anchor = null) {
    const panel = ensureSettingsPanelLayer(settingsPanel());
    if (!panel) return null;
    wireSettingsPopup(panel);
    panel.dataset.visible = "true";
    positionSettingsPanel(panel, anchor || currentSettingsAnchor());
    return panel;
  }
  function repositionVisibleSettingsPanel() {
    const panel = settingsPanel();
    if (panel?.dataset.visible === "true") positionSettingsPanel(panel, window.__hubcapSettingsAnchor || currentSettingsAnchor());
  }
  function currentSettingsAnchor() {
    const stats = statsRoot();
    const button = stats?.querySelector?.(".hp-settings-button");
    const target = button || stats;
    const statsRect = stats?.getBoundingClientRect?.();
    const targetRect = target?.getBoundingClientRect?.();
    if (!targetRect || !statsRect || targetRect.width <= 0 || statsRect.height <= 0) return null;
    return {
      settingsAnchorScreenLeft: window.screenX + targetRect.left,
      settingsAnchorScreenTop: window.screenY + statsRect.bottom + 8,
      settingsAnchorClientLeft: targetRect.left,
      settingsAnchorClientTop: statsRect.bottom + 8
    };
  }
  function settingsAnchorForElement(element) {
    const rect = element?.getBoundingClientRect?.();
    if (!rect || rect.width <= 0 || rect.height <= 0) return null;
    return {
      settingsAnchorScreenLeft: window.screenX + rect.left,
      settingsAnchorScreenTop: window.screenY + rect.bottom + 8,
      settingsAnchorClientLeft: rect.left,
      settingsAnchorClientTop: rect.bottom + 8
    };
  }
  function normalizeSettingsAnchor(anchor) {
    const screenLeft = Number(anchor?.settingsAnchorScreenLeft ?? anchor?.screenLeft ?? NaN);
    const screenTop = Number(anchor?.settingsAnchorScreenTop ?? anchor?.screenTop ?? NaN);
    const clientLeft = Number(anchor?.settingsAnchorClientLeft ?? anchor?.clientLeft ?? NaN);
    const clientTop = Number(anchor?.settingsAnchorClientTop ?? anchor?.clientTop ?? NaN);
    if ((!Number.isFinite(screenLeft) || !Number.isFinite(screenTop)) && (!Number.isFinite(clientLeft) || !Number.isFinite(clientTop))) return null;
    return { settingsAnchorScreenLeft: screenLeft, settingsAnchorScreenTop: screenTop, settingsAnchorClientLeft: clientLeft, settingsAnchorClientTop: clientTop };
  }
  function positionSettingsPanel(panel, anchor = null) {
    panel = ensureSettingsPanelLayer(panel);
    const stats = statsRoot();
    if (!panel || !stats) {
      panel?.style.removeProperty("left");
      panel?.style.removeProperty("top");
      return;
    }
    if (panel.dataset.dragged === "true" && panel.style.left && panel.style.top) {
      const rect = panel.getBoundingClientRect();
      clampSettingsPanel(panel, rect.left, rect.top);
      return;
    }
    const normalizedAnchor = normalizeSettingsAnchor(anchor) || normalizeSettingsAnchor(window.__hubcapSettingsAnchor);
    if (normalizedAnchor) {
      const left = Number.isFinite(normalizedAnchor.settingsAnchorClientLeft) ? normalizedAnchor.settingsAnchorClientLeft : normalizedAnchor.settingsAnchorScreenLeft - window.screenX;
      const top = Number.isFinite(normalizedAnchor.settingsAnchorClientTop) ? normalizedAnchor.settingsAnchorClientTop : normalizedAnchor.settingsAnchorScreenTop - window.screenY;
      clampSettingsPanel(panel, left, top);
      return;
    }
    const rect = stats.getBoundingClientRect();
    const width = Math.max(380, panel.getBoundingClientRect().width || 380);
    const hasAnchor = rect.width > 0 && rect.height > 0;
    const left = hasAnchor ? rect.left : window.innerWidth - width - 22;
    const top = hasAnchor ? rect.bottom + 8 : isStoreDocument() ? 40 : 100;
    clampSettingsPanel(panel, left, top);
  }
  function openSettings(event, extra = {}) {
    event?.preventDefault?.();
    event?.stopPropagation?.();
    if (event?.__hubcapSettingsHandled) return "";
    if (event) event.__hubcapSettingsHandled = true;
    window.__hubcapSettingsOpenedAt=Date.now();
    const panel = settingsPanel();
    if (panel?.dataset.visible !== "true" && window.__hubcapSettingsGloballyVisible === "true") {
      window.__hubcapSettingsGloballyVisible = "false";
    }
    if (panel?.dataset.visible === "true" || window.__hubcapSettingsGloballyVisible === "true") {
      closeSettings(event);
      if (!event) send("closeSettings");
      return "closeSettings";
    }
    if (panel) panel.dataset.dragged = "false";
    window.__hubcapSettingsGloballyVisible = "true";
    const anchor = currentSettingsAnchor();
    if (anchor) window.__hubcapSettingsAnchor = anchor;
    revealSettingsPopup();
    setSettingsNote("Loading...");
    send("settings", { ...(anchor || {}), ...extra });
    return "settings";
  }
  function setPressableState(element, hover = null, pressed = null) {
    if (!element) return;
    if (hover !== null) element.dataset.hover = hover ? "true" : "false";
    if (pressed !== null) element.dataset.pressed = pressed ? "true" : "false";
  }
  function wirePressable(element, onActivate) {
    if (!element) return;
    if (element.dataset.pressableBound !== "true") {
      element.dataset.pressableBound = "true";
      element.addEventListener("pointerenter", () => setPressableState(element, true, null));
      element.addEventListener("pointerleave", () => setPressableState(element, false, false));
      element.addEventListener("mouseover", () => setPressableState(element, true, null));
      element.addEventListener("mouseout", event => { if (!element.contains(event.relatedTarget)) setPressableState(element, false, false); });
      element.addEventListener("pointerdown", event => {
        setPressableState(element, true, true);
      }, { capture: true });
      element.addEventListener("mousedown", event => {
        setPressableState(element, true, true);
      }, { capture: true });
      element.addEventListener("pointerup", () => setPressableState(element, true, false));
      element.addEventListener("mouseup", () => setPressableState(element, true, false));
      element.addEventListener("click", event => onActivate?.(event), { capture: true });
    }
  }
  function wireStatsControls() {
    const stats = statsRoot();
    if (!stats || stats === root) return;
    wirePressable(stats.querySelector(".hp-settings-button"), event => {
      openSettings(event);
      event?.stopImmediatePropagation?.();
    });
    wirePressable(stats.querySelector(".hp-usage"), null);
  }
  function findConsoleNavElement() {
    if (!isSteamShellDocument()) return null;
    const exact = Array.from(document.querySelectorAll("div,span,a,button"))
      .map(element => ({ element, text: (element.innerText || element.textContent || "").trim(), rect: element.getBoundingClientRect(), cursor: getComputedStyle(element).cursor }))
      .filter(item => item.text === "CONSOLE" || item.text === "LAUNCHER SETTINGS")
      .filter(item => item.rect.top >= 28 && item.rect.top <= 72 && item.rect.width > 0 && item.rect.height > 0)
      .sort((a, b) => (a.rect.width * a.rect.height) - (b.rect.width * b.rect.height));
    const label = exact[0]?.element || null;
    if (!label) return null;
    let nav = label;
    for (let node = label.parentElement; node && node !== document.body; node = node.parentElement) {
      const rect = node.getBoundingClientRect();
      const text = (node.innerText || node.textContent || "").trim();
      if (!["CONSOLE", "LAUNCHER SETTINGS"].includes(text)) break;
      if (rect.width >= nav.getBoundingClientRect().width && rect.height >= nav.getBoundingClientRect().height) nav = node;
    }
    return nav;
  }
  function activeSteamShellTab() {
    if (!isSteamShellDocument()) return "";
    const looksBlue = color => {
      const match = String(color || "").match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/i);
      if (!match) return false;
      const r = Number(match[1]), g = Number(match[2]), b = Number(match[3]);
      return b >= 180 && g >= 100 && r <= 80;
    };
    const labels = Array.from(document.querySelectorAll("div,span,a,button"))
      .map(element => {
        const style = getComputedStyle(element);
        return { text: (element.innerText || element.textContent || "").trim(), rect: element.getBoundingClientRect(), color: style.color, borderBottomColor: style.borderBottomColor };
      })
      .filter(item => item.text && item.text !== "LAUNCHER SETTINGS" && item.rect.top >= 28 && item.rect.top <= 72);
    const highlighted = labels.find(item => looksBlue(item.color) || looksBlue(item.borderBottomColor))?.text;
    if (highlighted === "STORE" || highlighted === "LIBRARY") return highlighted;
    if (highlighted) return "WEB";
    const topText = Array.from(document.querySelectorAll("div,span,input"))
      .map(element => ({ text: (element.value || element.innerText || element.textContent || "").trim(), rect: element.getBoundingClientRect() }))
      .filter(item => item.rect.top >= 50 && item.rect.top <= 150 && item.rect.width > 80)
      .map(item => item.text)
      .join("\n");
    if (/https?:\/\/store\.steampowered\.com|store\.steampowered\.com/i.test(topText)) return "STORE";
    if (/https?:\/\/steamcommunity\.com|steamcommunity\.com/i.test(topText)) return "WEB";
    if (/\/library(?:\/home|\/app\/|$)|tracking:.*\/library/i.test(topText)) return "LIBRARY";
    return "";
  }
  window.__hubcapCdpGetActiveSurface = () => isStoreDocument() ? "STORE" : isSteamCommunityDocument() ? "WEB" : isSteamShellDocument() ? activeSteamShellTab() : "";
  function wireLauncherSettingsNav() {
    const nav = findConsoleNavElement();
    if (!nav) return;
    const label = Array.from(nav.querySelectorAll("div,span,a,button"))
      .find(element => ["CONSOLE", "LAUNCHER SETTINGS"].includes((element.innerText || element.textContent || "").trim())) || nav;
    if ((label.innerText || label.textContent || "").trim() !== "LAUNCHER SETTINGS") {
      label.textContent = "LAUNCHER SETTINGS";
    }
    nav.title = "Hubcap launcher settings";
    nav.dataset.hubcapLauncherSettingsNav = "true";
    if (nav.dataset.hubcapLauncherSettingsBoundVersion !== UI_VERSION) {
      nav.dataset.hubcapLauncherSettingsBoundVersion = UI_VERSION;
      ["pointerdown", "mousedown", "pointerup", "mouseup"].forEach(type => listen(nav, type, event => {
        event.preventDefault?.();
        event.stopPropagation?.();
        event.stopImmediatePropagation?.();
      }, { capture: true }));
      listen(nav, "click", event => openLauncherSettingsFromNav(event, nav), { capture: true });
    }
  }
  function openLauncherSettingsFromNav(event, nav) {
    event?.preventDefault?.();
    event?.stopPropagation?.();
    event?.stopImmediatePropagation?.();
    const panel = settingsPanel();
    if (panel?.dataset.visible !== "true") window.__hubcapSettingsGloballyVisible = "false";
    const anchor = settingsAnchorForElement(nav);
    const activeSurface = window.__hubcapCdpGetActiveSurface();
    window.__hubcapSettingsAnchor = anchor;
    if (activeSurface && activeSurface !== "LIBRARY") {
      if (panel) {
        panel.dataset.visible = "false";
        panel.style.removeProperty("display");
      }
      window.__hubcapSettingsGloballyVisible = "false";
      window.__hubcapSettingsOpenedAt = Date.now();
      send("toggleSettings", { ...(anchor || {}), settingsPreferSource: false, settingsSurface: activeSurface });
      return;
    }
    if (panel?.dataset.visible === "true" || window.__hubcapSettingsGloballyVisible === "true") {
      closeSettings(event);
      return;
    }
    if (panel) panel.dataset.dragged = "false";
    window.__hubcapSettingsGloballyVisible = "true";
    window.__hubcapSettingsOpenedAt = Date.now();
    revealSettingsPopup(anchor);
    setSettingsNote("Loading...");
    send("settings", { ...(anchor || {}), settingsPreferSource: true });
  }
  function startLauncherSettingsNavObserver() {
    if (!isSteamShellDocument() || window.__hubcapLauncherSettingsNavObserver) return;
    window.__hubcapLauncherSettingsNavObserver = new MutationObserver(() => wireLauncherSettingsNav());
    window.__hubcapLauncherSettingsNavObserver.observe(document.body, { childList: true, subtree: true, characterData: true });
    wireLauncherSettingsNav();
  }
  function positionShellStats(stats) {
    if (!stats) return;
    const topControls = Array.from(document.querySelectorAll("div,button,a"))
      .filter(element => !element.closest?.(`#${TOP_STATS_ID},#${ROOT_ID},.hp-settings`))
      .map(element => ({ element, rect: element.getBoundingClientRect() }))
      .filter(item =>
        item.rect.width >= 24 &&
        item.rect.width <= 140 &&
        item.rect.height >= 18 &&
        item.rect.height <= 34 &&
        item.rect.top >= 2 &&
        item.rect.top <= 16 &&
        item.rect.left > window.innerWidth - 520 &&
        item.rect.left < window.innerWidth - 80
      )
      .sort((a, b) => a.rect.left - b.rect.left);
    const firstControl = topControls[0]?.rect || null;
    const width = Math.max(120, stats.getBoundingClientRect().width || 210);
    const left = firstControl ? firstControl.left - width - 10 : window.innerWidth - width - 360;
    const top = firstControl ? firstControl.top : 7;
    stats.style.left = `${Math.max(8, Math.round(left))}px`;
    stats.style.top = `${Math.max(4, Math.round(top))}px`;
    stats.style.removeProperty("right");
  }
  function placeStats() {
    const stats = statsRoot();
    if (!stats || stats === root) return;
    if (isSteamShellDocument()) {
      if (stats.parentElement !== document.body) document.body.appendChild(stats);
      stats.dataset.compact = "true";
      stats.dataset.hidden = "false";
      stats.dataset.shell = "true";
      stats.dataset.store = "false";
      stats.dataset.shellGlobal = "true";
      stats.dataset.webHidden = "false";
      stats.style.removeProperty("display");
      positionShellStats(stats);
      return;
    }
    stats.dataset.shellGlobal = "false";
    if (isSteamWebDocument()) {
      stats.dataset.webHidden = "true";
      return;
    }
    stats.dataset.webHidden = "false";
    if (stats.parentElement !== root) root.appendChild(stats);
    stats.dataset.compact = "false";
    stats.dataset.shell = "false";
    stats.dataset.store = "false";
    stats.dataset.hidden = "false";
    stats.style.removeProperty("left");
    stats.style.removeProperty("top");
    stats.style.removeProperty("right");
  }
  function resetRootPlacement() {
    root.style.removeProperty("left");
    root.style.removeProperty("position");
    root.style.removeProperty("right");
    root.style.removeProperty("top");
    root.style.removeProperty("width");
    root.style.removeProperty("z-index");
  }
  function placeRoot() {
    const activeSurface = window.__hubcapCdpGetActiveSurface();
    root.dataset.webDocument = isSteamWebDocument() ? "true" : "false";
    if (isSteamShellDocument() && activeSurface && activeSurface !== "LIBRARY") {
      const panel = settingsPanel();
      if (panel) {
        panel.dataset.visible = "false";
        panel.style.removeProperty("display");
      }
      window.__hubcapSettingsGloballyVisible = "false";
    }
    const host = earlyHost();
    if (!looksLikeAppPage() || !host) {
      const shellHost = isSteamShellDocument() && appIdFromUrl() && window.__hubcapCdpShellFallbackEnabled && activeSurface === "LIBRARY" ? shellContentHost() : null;
      if (shellHost) {
        root.style.display = "flex";
        resetRootPlacement();
        root.dataset.shellFallback = "true";
        if (root.parentElement !== shellHost) shellHost.prepend(root);
        placeStats();
        return true;
      }
      root.style.display = "none";
      root.dataset.shellFallback = "false";
      resetRootPlacement();
      if (!root.parentElement) document.body.prepend(root);
      placeStats();
      return isStoreDocument();
    }
    root.style.display = "flex";
    root.dataset.shellFallback = "false";
    resetRootPlacement();
    const title = document.querySelector(".apphub_AppName");
    const highlights = document.querySelector("#game_highlights");
    if (title?.parentElement === host && title.nextElementSibling !== root) title.insertAdjacentElement("afterend", root);
    else if (highlights?.parentElement === host && root.parentElement !== host) host.insertBefore(root, highlights);
    else if (host.classList?.contains("game_page_background") && root.parentElement !== host) host.prepend(root);
    else if (root.parentElement !== host) host.appendChild(root);
    placeStats();
    return true;
  }
  placeRoot();
  startLauncherSettingsNavObserver();
  const send = (action, extra = {}) => {
    const payload = JSON.stringify({ action, appId: appIdFromUrl(), href: location.href, ...extra });
    if (typeof window.hubcapNative === "function") window.hubcapNative(payload);
    else console.warn("[Hubcap CDP] native binding unavailable", payload);
  };
  function showConfirm(title, message, onConfirm) {
    document.getElementById(CONFIRM_ID)?.remove();
    const modal = document.createElement("div");
    modal.id = CONFIRM_ID;
    modal.innerHTML = `<div class="hp-confirm-box"><div class="hp-confirm-title"></div><div class="hp-confirm-message"></div><div class="hp-confirm-actions"><button class="hp-confirm-yes" type="button">Yes</button><button class="hp-confirm-no" type="button">No</button></div></div>`;
    modal.querySelector(".hp-confirm-title").textContent = title;
    modal.querySelector(".hp-confirm-message").textContent = message;
    const close = () => modal.remove();
    modal.querySelector(".hp-confirm-no").addEventListener("click", event => { event.preventDefault(); event.stopPropagation(); close(); });
    modal.querySelector(".hp-confirm-yes").addEventListener("click", event => { event.preventDefault(); event.stopPropagation(); close(); onConfirm(); });
    modal.addEventListener("click", event => { if (event.target === modal) close(); });
    document.body.appendChild(modal);
    modal.querySelector(".hp-confirm-no").focus();
  }
  function showRemoveConfirm(onConfirm) {
    const gameName = document.querySelector(".apphub_AppName")?.textContent?.trim() || `app ${appIdFromUrl()}`;
    showConfirm("Remove Lua?", `Remove Lua for ${gameName}? This will delete the local Lua file.`, onConfirm);
  }
  function setSettingsNote(message, tone = "idle") {
    const note = settingsPanel()?.querySelector(".hp-settings-note");
    if (!note) return;
    note.textContent = message || "";
    note.dataset.tone = tone;
  }
  function updateGroupLuaNewRow() {
    const panel = settingsPanel();
    const select = panel?.querySelector(".hp-collection-name");
    const row = panel?.querySelector(".hp-new-collection-row");
    const remove = panel?.querySelector(".hp-remove-collection");
    if (row) row.dataset.visible = select?.value === "__new__" ? "true" : "false";
    if (remove) remove.dataset.visible = select?.value && select.value !== "__new__" ? "true" : "false";
  }
  function setGroupLuaEnabled(enabled) {
    const panel = settingsPanel();
    const toggle = panel?.querySelector(".hp-group-lua-toggle");
    const options = panel?.querySelector(".hp-group-lua-options");
    const currentCollection = panel?.querySelector(".hp-current-collection");
    if (toggle) toggle.dataset.on = enabled ? "true" : "false";
    if (options) options.dataset.visible = enabled ? "true" : "false";
    if (currentCollection) currentCollection.textContent = enabled ? `Current: ${root.dataset.collectionName || "None"}` : "";
    updateGroupLuaNewRow();
  }
  function selectedCollectionName() {
    const panel = settingsPanel();
    const select = panel?.querySelector(".hp-collection-name");
    const input = panel?.querySelector(".hp-new-collection-name");
    const selected = select?.value || "";
    return (selected === "__new__" ? input?.value : selected).trim();
  }
  function populateCollectionNames(names, selectedName) {
    const panel = settingsPanel();
    const select = panel?.querySelector(".hp-collection-name");
    if (!select) return;
    const selected = selectedName || "";
    const unique = Array.from(new Set((Array.isArray(names) ? names : []).filter(Boolean)));
    select.innerHTML = "";
    for (const name of unique) {
      const option = document.createElement("option");
      option.value = name;
      option.textContent = name;
      select.appendChild(option);
    }
    const add = document.createElement("option");
    add.value = "__new__";
    add.textContent = "Add collection...";
    select.appendChild(add);
    select.value = selected && unique.includes(selected) ? selected : "__new__";
    const input = panel?.querySelector(".hp-new-collection-name");
    if (input && select.value === "__new__" && selected) input.value = selected;
    updateGroupLuaNewRow();
  }
  function saveSettings() {
    const panel = settingsPanel();
    const enabled = panel?.querySelector(".hp-group-lua-toggle")?.dataset.on === "true";
    setSettingsNote("Saving...");
    send("saveSettings",{
      luaDir: panel?.querySelector(".hp-lua-dir").value || "",
      apiKey: panel?.querySelector(".hp-api-key").value || "",
      collectionSyncEnabled: enabled,
      collectionName: selectedCollectionName()
    });
  }
  function removeSelectedCollection() {
    const name = selectedCollectionName();
    if (!name) return;
    showConfirm("Remove Collection?", `Remove Steam collection "${name}"? Lua files will not be deleted.`, () => {
      setSettingsNote("Removing collection...");
      send("removeCollection", { collectionName: name });
    });
  }
  function updateSaveState() {
    const panel = settingsPanel();
    const luaDir = panel?.querySelector(".hp-lua-dir");
    const apiKey = panel?.querySelector(".hp-api-key");
    const groupLuaToggle = panel?.querySelector(".hp-group-lua-toggle");
    const save = panel?.querySelector(".hp-save-settings");
    if (!luaDir || !apiKey || !groupLuaToggle || !save) return;
    save.dataset.visible = hasSettingsChanges() ? "true" : "false";
  }
  function hasSettingsChanges() {
    const panel = settingsPanel();
    const luaDir = panel?.querySelector(".hp-lua-dir");
    const apiKey = panel?.querySelector(".hp-api-key");
    const groupLuaToggle = panel?.querySelector(".hp-group-lua-toggle");
    const enabled = groupLuaToggle?.dataset.on === "true";
    return !!luaDir && !!apiKey && !!groupLuaToggle && (luaDir.value !== (root.dataset.luaDir || "") || apiKey.value !== (root.dataset.apiKey || "") || String(enabled) !== (root.dataset.collectionSyncEnabled || "false") || selectedCollectionName() !== (root.dataset.collectionName || ""));
  }
  function showSettings(settings, draft = false, reveal = true, anchor = null) {
    const panel = ensureSettingsPanelLayer(settingsPanel());
    wireSettingsPopup(panel);
    const luaDir = panel?.querySelector(".hp-lua-dir");
    const apiKey = panel?.querySelector(".hp-api-key");
    const groupLuaToggle = panel?.querySelector(".hp-group-lua-toggle");
    if (!panel || !luaDir || !apiKey || !groupLuaToggle) return;
    panel.dataset.visible = reveal ? "true" : "false";
    const normalizedAnchor = normalizeSettingsAnchor(anchor);
    if (normalizedAnchor) window.__hubcapSettingsAnchor = normalizedAnchor;
    if (reveal) window.__hubcapSettingsGloballyVisible = "true";
    if (reveal) positionSettingsPanel(panel, normalizedAnchor);
    if (!reveal) {
      panel.style.removeProperty("display");
    }
    const missing = !!settings?.configMissing;
    panel.querySelector(".hp-config-missing").dataset.visible = missing ? "true" : "false";
    panel.querySelectorAll(".hp-field,.hp-toggle-row").forEach(field => field.style.display = missing ? "none" : "");
    panel.querySelector(".hp-settings-actions").style.display = missing ? "none" : "flex";
    if (missing) {
      luaDir.value = "";
      apiKey.value = "";
      root.dataset.luaDir = "";
      root.dataset.apiKey = "";
      setGroupLuaEnabled(false);
      const currentCollection = panel?.querySelector(".hp-current-collection");
      if (currentCollection) currentCollection.textContent = "";
      populateCollectionNames([], "");
      root.dataset.collectionSyncEnabled = "false";
      root.dataset.collectionName = "";
      setSettingsNote("");
      updateSaveState();
      return;
    }
    luaDir.value = settings?.luaDir || "";
    apiKey.value = settings?.apiKey || "";
    const collectionName = settings?.collectionName || "";
    const currentCollection = panel?.querySelector(".hp-current-collection");
    const availableCollections = settings?.collectionNames || [];
    populateCollectionNames(availableCollections, collectionName);
    setGroupLuaEnabled(!!settings?.collectionSyncEnabled);
    if (currentCollection) currentCollection.textContent = settings?.collectionSyncEnabled ? `Current: ${collectionName || "None"}` : "";
    if (!draft) {
      root.dataset.luaDir = luaDir.value;
      root.dataset.apiKey = apiKey.value;
      root.dataset.collectionSyncEnabled = String(!!settings?.collectionSyncEnabled);
      root.dataset.collectionName = collectionName;
    }
    apiKey.type = "password";
    setSettingsNote(settings?.error || "", settings?.error ? "error" : "idle");
    updateSaveState();
  }
  window.__hubcapCdpSetState = state => {
    placeRoot();
    wireLauncherSettingsNav();
    wireStatsControls();
    const stats = statsRoot();
    const button=root.querySelector(".hp-main"), library=root.querySelector(".hp-library"), status=root.querySelector(".hp-status"), warning=root.querySelector(".hp-warning"), usage=stats.querySelector(".hp-usage"), name=stats.querySelector(".hp-usage-name"), expiry=stats.querySelector(".hp-usage-expiry"), count=stats.querySelector(".hp-usage-count"), fill=stats.querySelector(".hp-usage-fill"), spinner=stats.querySelector(".hp-usage-spinner");
    if(state.settingsOnly){showSettings(state.settings||{},!!state.settingsDraft,true,state);if(state.statusText)setSettingsNote(state.statusText,state.statusTone||"idle");return;}
    const denuvo=/denuvo|anti[-\s]?tamper/i.test(document.body?.innerText||"");
    warning.textContent="Warning: Denuvo / 3rd-party anti-tamper detected";
    warning.dataset.visible=denuvo?"true":"false"; button.dataset.denuvo=denuvo?"true":"false";
    if(state.busy){button.disabled=true;button.dataset.state="checking";button.dataset.busy="true";button.textContent=state.busyText||"Working...";status.textContent=state.statusText||"";status.dataset.tone="idle";spinner.dataset.visible=state.usageBusy?"true":"false";return;}
    if(state.usageOnly&&!state.usage){spinner.dataset.visible=state.usageBusy?"true":"false";return;}
    if(state.usageOnly){updateUsage(state);return;}
    button.dataset.busy="false";
    if(state.exists){button.dataset.state="remove";button.textContent="Remove Lua";button.disabled=false;library.style.display="inline-flex";}
    else if(state.available){button.dataset.state="download";button.textContent="Download Lua";button.disabled=false;library.style.display="none";}
    else{button.dataset.state="unavailable";button.textContent="Unavailable";button.disabled=true;library.style.display="none";}
    if(state.controlsDisabled){button.dataset.state="disabled";button.disabled=true;library.disabled=true;}
    else{library.disabled=false;}
    status.textContent=state.isDlc&&state.statusText?state.statusText:(state.statusError&&state.statusText&&!/lua unavailable/i.test(state.statusText)?state.statusText:"");status.dataset.tone=state.statusTone||(state.statusError?"error":"idle");if(state.usage)updateUsage(state);else spinner.dataset.visible=state.usageBusy?"true":"false";
    function updateUsage(s){const u=s.usage||{},du=Number(u.dailyUsage||0),dl=Number(u.dailyLimit||0);name.textContent=u.username||"Hubcap";count.textContent=u.error?(u.errorLabel||"Stats Error"):`${du}/${dl}`;fill.style.width=`${dl>0?Math.min(100,Math.round((du/dl)*100)):0}%`;usage.title=u.error||"";spinner.dataset.visible=s.usageBusy?"true":"false";if(u.apiKeyExpiresAt){const days=Math.max(0,Math.ceil((new Date(u.apiKeyExpiresAt).getTime()-Date.now())/86400000));expiry.textContent=`Expires in ${days}d`;}else if(u.error){expiry.textContent="Expires --";}if(s.usage&&!u.error)try{sessionStorage.setItem(USAGE_CACHE_KEY,JSON.stringify(u));}catch{}}
  };
  function hydrateCachedUsage(){
    try{
      const cached=JSON.parse(sessionStorage.getItem(USAGE_CACHE_KEY)||"null");
      if(cached) window.__hubcapCdpSetState({usageOnly:true,usage:cached});
    }catch{}
  }
  wireStatsControls();
  startLauncherSettingsNavObserver();
  if (!root.dataset.bound) {
    listen(root.querySelector(".hp-main"), "click",()=>{const state=root.querySelector(".hp-main").dataset.state;if(state==="download")send("download");if(state==="remove")showRemoveConfirm(()=>send("remove"));});
    listen(root.querySelector(".hp-library"), "click",()=>send("library"));
    const stats = statsRoot();
    listen(document, "click", event=>{
      const navSettings = event.target.closest?.("[data-hubcap-launcher-settings-nav='true']");
      if(navSettings){openLauncherSettingsFromNav(event, navSettings);return;}
      if(event.target.closest?.(".hp-settings-button")){openSettings(event);event.stopImmediatePropagation?.();return;}
      if(event.target.closest?.(".hp-settings-close")){closeSettings(event);event.stopImmediatePropagation?.();return;}
      if(event.target.closest?.(`#${TOP_STATS_ID} .hp-usage`) && !event.target.closest?.(".hp-settings")){event.preventDefault();event.stopPropagation();event.stopImmediatePropagation?.();send("refresh");return;}
    }, { capture: true });
    listen(document, "click", async event=>{
      const navSettings = event.target.closest?.("[data-hubcap-launcher-settings-nav='true']");
      if(navSettings){openLauncherSettingsFromNav(event, navSettings);return;}
      const currentStats=statsRoot();
      const settingsButton=event.target.closest?.(".hp-settings-button");
      const settingsPanelHit=event.target.closest?.(".hp-settings");
      if(settingsButton){openSettings(event);return;}
      if(event.target.closest?.(".hp-settings-close")){closeSettings(event);return;}
      if(event.target.closest?.(".hp-open-lua-folder")){event.preventDefault();event.stopPropagation();setSettingsNote("Selecting Lua folder...");send("openLuaFolder");return;}
      if(event.target.closest?.(".hp-save-settings")){event.preventDefault();event.stopPropagation();saveSettings();return;}
      if(event.target.closest?.(".hp-remove-collection")){event.preventDefault();event.stopPropagation();removeSelectedCollection();return;}
      if(event.target.closest?.(".hp-toggle-api-key")){event.preventDefault();event.stopPropagation();const input=settingsPanel()?.querySelector(".hp-api-key");if(input)input.type=input.type==="password"?"text":"password";return;}
      if(event.target.closest?.(".hp-copy-api-key")){event.preventDefault();event.stopPropagation();const value=settingsPanel()?.querySelector(".hp-api-key").value||"";try{await navigator.clipboard.writeText(value);setSettingsNote("API key copied.");}catch{send("copyApiKey");setSettingsNote("API key copied.");}return;}
      if(settingsPanelHit){event.stopPropagation();return;}
      const usageHit=event.target.closest?.(".hp-usage");
      if(usageHit && !event.target.closest?.(".hp-settings-button,.hp-settings,.hp-icon-button,.hp-save-settings")){event.preventDefault();event.stopPropagation();send("refresh");}
    });
    listen(stats.querySelector(".hp-settings-button"), "click", openSettings);
    listen(stats.querySelector(".hp-lua-dir"), "input", updateSaveState);
    listen(stats.querySelector(".hp-api-key"), "input", updateSaveState);
    listen(stats.querySelector(".hp-collection-name"), "change", ()=>{updateGroupLuaNewRow();updateSaveState();});
    listen(stats.querySelector(".hp-new-collection-name"), "input", updateSaveState);
    listen(stats.querySelector(".hp-settings-close"), "click", closeSettings);
    listen(document, "click", event=>{const currentStats=statsRoot(),panel=settingsPanel(),button=currentStats.querySelector(".hp-settings-button"),usage=currentStats.querySelector(".hp-usage"),openedAt=Number(window.__hubcapSettingsOpenedAt||0);if(openedAt&&Date.now()-openedAt<750)return;if(panel?.dataset.visible==="true"&&!panel.contains(event.target)&&!button?.contains(event.target)&&!usage?.contains(event.target)&&!hasSettingsChanges())panel.dataset.visible="false";}, { capture: true });
    listen(window, "resize", repositionVisibleSettingsPanel);
    listen(window.visualViewport, "resize", repositionVisibleSettingsPanel);
    root.dataset.bound = "true";
  }
  if(window.__hubcapCdpRouteTimer)clearInterval(window.__hubcapCdpRouteTimer);
  let lastHubcapAppId=appIdFromUrl();
  hydrateCachedUsage();
  if(lastHubcapAppId){window.__hubcapCdpSetState({busy:true,busyText:"Checking...",statusText:"",usageBusy:true});setTimeout(()=>send("route"),0);}
  window.__hubcapCdpRouteTimer=setInterval(()=>{const nextAppId=appIdFromUrl();placeRoot();wireLauncherSettingsNav();if(!nextAppId&&lastHubcapAppId){lastHubcapAppId="";window.__hubcapCdpSetState({usageOnly:true,usageBusy:false});return;}if(nextAppId&&nextAppId!==lastHubcapAppId){lastHubcapAppId=nextAppId;window.__hubcapCdpSetState({busy:true,busyText:"Checking...",statusText:"",usageBusy:true});send("route");}},150);
  return {ok:true,href:location.href,appId:appIdFromUrl()};
})()
""";

    public const string SharedLibraryUi = """
(() => {
  const BUTTON_ID = "hubcap-cdp-library-remove";
  const CHECK_BUTTON_ID = "hubcap-cdp-library-check";
  const VERSION_BUTTON_ID = "hubcap-cdp-library-version";
  const VERSION_PANEL_ID = "hubcap-cdp-library-version-panel";
  const CONFIRM_ID = "hubcap-cdp-library-remove-confirm";
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

  function send(action, appId, extra = {}) {
    const payload = JSON.stringify({ action, appId: appId || routeAppId(), href: globalThis.MainWindowBrowserManager?.m_lastLocation?.pathname || location.href, ...extra });
    if (typeof globalThis.hubcapNative === "function") globalThis.hubcapNative(payload);
    else console.warn("[Hubcap CDP Shared Library] native binding unavailable", payload);
  }

  function ensureStyle(doc) {
    const style = doc.getElementById(STYLE_ID) || doc.createElement("style");
    style.id = STYLE_ID;
    style.textContent = `
      #${CHECK_BUTTON_ID}{align-items:center;align-self:center;appearance:none;background:rgba(13,27,39,.48);border:1px solid rgba(103,193,245,.26);border-radius:3px;box-shadow:inset 0 1px 0 rgba(255,255,255,.06),0 1px 2px rgba(0,0,0,.24);box-sizing:border-box;color:#d6f4ff;cursor:pointer;display:inline-flex;font:14px Arial,Helvetica,sans-serif;height:32px;justify-content:center;line-height:1;margin:0 8px 0 0;min-height:32px;min-width:104px;padding:0 14px;text-align:center;transform:translateY(3px);vertical-align:middle;white-space:nowrap}
      #${CHECK_BUTTON_ID}:hover{background:rgba(26,52,70,.62);border-color:rgba(103,193,245,.42);color:#fff}
      #${CHECK_BUTTON_ID}:disabled{cursor:default;opacity:.72}
      #${VERSION_BUTTON_ID}{align-items:center;align-self:center;appearance:none;background:rgba(13,27,39,.48);border:1px solid rgba(103,193,245,.26);border-radius:3px;box-shadow:inset 0 1px 0 rgba(255,255,255,.06),0 1px 2px rgba(0,0,0,.24);box-sizing:border-box;color:#d6f4ff;cursor:pointer;display:inline-flex;font:14px Arial,Helvetica,sans-serif;height:32px;justify-content:center;line-height:1;margin:0 8px 0 0;min-height:32px;min-width:104px;padding:0 14px;text-align:center;transform:translateY(3px);vertical-align:middle;white-space:nowrap}
      #${VERSION_BUTTON_ID}:hover{background:rgba(26,52,70,.62);border-color:rgba(103,193,245,.42);color:#fff}
      #${VERSION_BUTTON_ID}:disabled{cursor:default;opacity:.72}
      #${BUTTON_ID}{align-items:center;align-self:center;appearance:none;background:rgba(95,33,31,.58);border:1px solid rgba(217,75,63,.36);border-radius:3px;box-shadow:inset 0 1px 0 rgba(255,255,255,.06),0 1px 2px rgba(0,0,0,.24);box-sizing:border-box;color:#ffe0dc;cursor:pointer;display:inline-flex;font:14px Arial,Helvetica,sans-serif;height:32px;justify-content:center;line-height:1;margin:0 8px 0 0;min-height:32px;min-width:104px;padding:0 14px;text-align:center;transform:translateY(3px);vertical-align:middle;white-space:nowrap}
      #${BUTTON_ID}:hover{background:rgba(112,42,39,.72);border-color:rgba(217,75,63,.5);color:#fff1ef}
      #${BUTTON_ID}:disabled{cursor:default;opacity:.72}
      #${BUTTON_ID}[data-removed="true"]{animation:hubcap-library-fade-out 1.8s ease forwards;background:rgba(38,72,42,.58);border-color:rgba(139,197,63,.36);color:#dff5cf}
      #${STATUS_ID}{align-items:center;align-self:center;color:#ff9b8f;display:inline-flex;font:700 14px/32px Arial,Helvetica,sans-serif;height:32px;justify-content:center;margin-right:8px;min-width:104px;padding:0 14px;text-shadow:0 1px 2px rgba(0,0,0,.55);transform:translateY(3px);white-space:nowrap}
      #${STATUS_ID}[data-tone="success"]{color:#8bc53f}
      #${STATUS_ID}[data-tone="error"]{color:#ff9b8f}
      #${VERSION_PANEL_ID}{background:linear-gradient(135deg,rgba(22,40,55,.98),rgba(16,31,43,.98));border:1px solid rgba(103,193,245,.22);border-radius:4px;box-shadow:0 16px 42px rgba(0,0,0,.45);box-sizing:border-box;color:#dfe3e6;font:13px Arial,Helvetica,sans-serif;left:50%;max-width:460px;padding:14px;position:fixed;top:96px;transform:translateX(-50%);width:min(460px,calc(100vw - 32px));z-index:999999}
      #${VERSION_PANEL_ID} .hpv-head{align-items:center;display:flex;gap:12px;justify-content:space-between;margin-bottom:12px}
      #${VERSION_PANEL_ID} .hpv-title{color:#fff;font-size:16px;font-weight:700;line-height:20px}
      #${VERSION_PANEL_ID} .hpv-close{appearance:none;background:transparent;border:0;color:#b8c6d1;cursor:pointer;font-size:20px;height:24px;line-height:20px;padding:0;width:24px}
      #${VERSION_PANEL_ID} .hpv-close:hover{color:#fff}
      #${VERSION_PANEL_ID} .hpv-note{color:#b8c6d1;font-size:12px;line-height:17px;margin:-4px 0 10px}
      #${VERSION_PANEL_ID} .hpv-label{color:#8fb8cd;font-size:11px;font-weight:700;letter-spacing:.04em;margin:10px 0 4px;text-transform:uppercase}
      #${VERSION_PANEL_ID} .hpv-value{color:#fff;font-size:13px;font-weight:700;line-height:18px;overflow-wrap:anywhere}
      #${VERSION_PANEL_ID} select{appearance:auto;background:#182b3a;border:1px solid rgba(103,193,245,.34);border-radius:3px;box-sizing:border-box;color:#fff;font:13px Arial,Helvetica,sans-serif;min-height:32px;padding:6px;width:100%}
      #${VERSION_PANEL_ID} select:disabled{opacity:.68}
      #${VERSION_PANEL_ID} .hpv-actions{align-items:center;display:flex;gap:10px;justify-content:flex-end;margin-top:12px}
      #${VERSION_PANEL_ID} .hpv-apply{align-items:center;appearance:none;background:linear-gradient(90deg,#75a313,#8abf19);border:0;border-radius:3px;color:#fff;cursor:pointer;display:inline-flex;font:700 13px/1 Arial,Helvetica,sans-serif;height:32px;justify-content:center;min-width:70px;padding:0 16px;text-align:center}
      #${VERSION_PANEL_ID} .hpv-apply:hover{filter:brightness(1.08)}
      #${VERSION_PANEL_ID} .hpv-apply:disabled{cursor:default;filter:none;opacity:.62}
      #${VERSION_PANEL_ID} .hpv-status{color:#b8c6d1;font-size:12px;line-height:17px;margin-top:10px;overflow-wrap:anywhere}
      #${VERSION_PANEL_ID} .hpv-status[data-tone="success"]{color:#8bc53f}
      #${VERSION_PANEL_ID} .hpv-status[data-tone="error"]{color:#ff9b8f}
      #${CONFIRM_ID}{align-items:center;background:rgba(0,0,0,.58);bottom:0;box-sizing:border-box;display:flex;font-family:Arial,Helvetica,sans-serif;justify-content:center;left:0;padding:18px;position:fixed;right:0;top:0;z-index:1000000}
      #${CONFIRM_ID} .hpc-box{background:linear-gradient(135deg,rgba(22,40,55,.98),rgba(16,31,43,.98));border:1px solid rgba(103,193,245,.3);border-radius:4px;box-shadow:0 18px 48px rgba(0,0,0,.55);box-sizing:border-box;color:#dfe3e6;max-width:360px;padding:16px;width:min(360px,calc(100vw - 36px))}
      #${CONFIRM_ID} .hpc-title{color:#fff;font-size:16px;font-weight:700;line-height:20px;margin-bottom:8px}
      #${CONFIRM_ID} .hpc-message{color:#b8c6d1;font-size:13px;line-height:18px;margin-bottom:14px;overflow-wrap:anywhere}
      #${CONFIRM_ID} .hpc-actions{align-items:center;display:flex;gap:10px;justify-content:flex-end}
      #${CONFIRM_ID} button{align-items:center;border-radius:3px;box-sizing:border-box;cursor:pointer;display:inline-flex;font:700 13px/1 Arial,Helvetica,sans-serif;height:32px;justify-content:center;min-width:72px;padding:0 14px;text-align:center}
      #${CONFIRM_ID} .hpc-no{background:rgba(13,27,39,.68);border:1px solid rgba(103,193,245,.32);color:#d6f4ff}
      #${CONFIRM_ID} .hpc-no:hover{background:rgba(26,52,70,.78);color:#fff}
      #${CONFIRM_ID} .hpc-yes{background:rgba(95,33,31,.78);border:1px solid rgba(217,75,63,.48);color:#ffe0dc}
      #${CONFIRM_ID} .hpc-yes:hover{background:rgba(112,42,39,.88);color:#fff1ef}
      @keyframes hubcap-library-fade-out{0%,45%{opacity:1}100%{opacity:0}}
    `;
    if (!style.parentElement) doc.head.appendChild(style);
  }

  function removeElements(doc, keepVersionPanel = false) {
    if (!doc) return;
    const selector = keepVersionPanel
      ? `#${VERSION_BUTTON_ID},#${CHECK_BUTTON_ID},#${BUTTON_ID},#${STATUS_ID},#${CONFIRM_ID}`
      : `#${VERSION_BUTTON_ID},#${CHECK_BUTTON_ID},#${BUTTON_ID},#${STATUS_ID},#${VERSION_PANEL_ID},#${CONFIRM_ID}`;
    doc.querySelectorAll(selector).forEach(el => el.remove());
  }

  function showRemoveConfirm(doc, state, onConfirm) {
    doc.getElementById(CONFIRM_ID)?.remove();
    const modal = doc.createElement("div");
    modal.id = CONFIRM_ID;
    modal.innerHTML = `<div class="hpc-box"><div class="hpc-title">Remove Lua?</div><div class="hpc-message"></div><div class="hpc-actions"><button class="hpc-yes" type="button">Yes</button><button class="hpc-no" type="button">No</button></div></div>`;
    const appId = state.appId || routeAppId();
    const gameName = state.gameName || `app ${appId}`;
    modal.querySelector(".hpc-message").textContent = `Remove Lua for ${gameName}? This will delete the local Lua file.`;
    const close = () => modal.remove();
    modal.querySelector(".hpc-no").addEventListener("click", event => { event.preventDefault(); event.stopPropagation(); close(); });
    modal.querySelector(".hpc-yes").addEventListener("click", event => { event.preventDefault(); event.stopPropagation(); close(); onConfirm(); });
    modal.addEventListener("click", event => { if (event.target === modal) close(); });
    doc.body.appendChild(modal);
    modal.querySelector(".hpc-no").focus();
  }

  function currentVersionPanelAppId() {
    const win = desktopWindow();
    return win?.document?.getElementById(VERSION_PANEL_ID)?.dataset?.appId
      || globalThis.document?.getElementById(VERSION_PANEL_ID)?.dataset?.appId
      || "";
  }

  function shouldKeepVersionPanel(state) {
    if (state?.luaVersion?.visible) return true;
    if (state?.removed) return false;
    const panelAppId = currentVersionPanelAppId();
    const stateAppId = state?.appId || routeAppId();
    return !!panelAppId && !!stateAppId && panelAppId === stateAppId;
  }

  function actionAnchorElement(doc) {
    const viewportWidth = Math.max(doc.documentElement.clientWidth || 0, desktopWindow()?.innerWidth || 0);
    const viewportHeight = Math.max(doc.documentElement.clientHeight || 0, desktopWindow()?.innerHeight || 0);
    return Array.from(doc.querySelectorAll("button, div, a"))
      .filter(el => !el.closest?.(".hp-settings,#hubcap-cdp-ui,#hubcap-cdp-top-stats,#hubcap-cdp-library-version-panel,#hubcap-cdp-library-remove-confirm"))
      .map(el => ({ el, r: el.getBoundingClientRect() }))
      .filter(x => x.r.width >= 24 && x.r.width <= 64 && x.r.height >= 24 && x.r.height <= 64)
      .filter(x => x.r.x > viewportWidth * .72 && x.r.y > 260 && x.r.y < Math.min(viewportHeight - 90, 560))
      .sort((a, b) => a.r.y - b.r.y || a.r.x - b.r.x)[0]?.el || null;
  }

  function actionRowForAnchor(anchor) {
    let node = anchor?.parentElement || null;
    while (node && node !== anchor.ownerDocument.body) {
      if (node.closest?.(".hp-settings,#hubcap-cdp-ui,#hubcap-cdp-top-stats,#hubcap-cdp-library-version-panel,#hubcap-cdp-library-remove-confirm")) {
        node = node.parentElement;
        continue;
      }
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
    if (row.closest?.(".hp-settings,#hubcap-cdp-ui,#hubcap-cdp-top-stats,#hubcap-cdp-library-version-panel,#hubcap-cdp-library-remove-confirm")) return false;
    element.style.position = "";
    element.style.left = "";
    element.style.top = "";
    element.style.zIndex = "";
    element.style.marginRight = "8px";
    const anchorX = anchor.getBoundingClientRect().x;
    const before = Array.from(row.children)
      .filter(child => child.id !== VERSION_BUTTON_ID && child.id !== CHECK_BUTTON_ID && child.id !== BUTTON_ID && child.id !== STATUS_ID)
      .filter(child => Math.abs(child.getBoundingClientRect().y - anchor.getBoundingClientRect().y) < 16)
      .filter(child => child.getBoundingClientRect().x >= anchorX - 8)
      .sort((a, b) => a.getBoundingClientRect().x - b.getBoundingClientRect().x)[0] || anchor;
    row.insertBefore(element, before);
    return true;
  }

  function showLuaVersionPanel(doc, state) {
    if (!state?.luaVersion?.visible) return;

    const version = state.luaVersion;
    const panel = doc.getElementById(VERSION_PANEL_ID) || doc.createElement("div");
    panel.id = VERSION_PANEL_ID;
    panel.dataset.appId = state.appId || routeAppId();

    const close = () => panel.remove();
    const gameName = version.gameName || state.gameName || panel.dataset.appId || "Unknown";
    const currentText = version.currentManifestId
      ? `${version.currentBuildId || "Unknown"} - ${version.currentManifestId}`
      : (version.loading ? "Loading..." : "Unknown");

    panel.innerHTML = `
      <div class="hpv-head">
        <div class="hpv-title">Lua Version</div>
        <button class="hpv-close" type="button" title="Close">x</button>
      </div>
      <div class="hpv-note">This changes the version for pinned games.</div>
      <div class="hpv-label">Game</div>
      <div class="hpv-value hpv-game"></div>
      <div class="hpv-label">Current Build ID - Manifest ID in LUA</div>
      <div class="hpv-value hpv-current"></div>
      <div class="hpv-label">Change to</div>
      <select class="hpv-select"></select>
      <div class="hpv-actions">
        <button class="hpv-apply" type="button">Apply</button>
      </div>
      <div class="hpv-status"></div>
    `;

    panel.querySelector(".hpv-close").addEventListener("click", close);
    panel.querySelector(".hpv-game").textContent = gameName;
    panel.querySelector(".hpv-current").textContent = currentText;

    const select = panel.querySelector(".hpv-select");
    const options = Array.isArray(version.options) ? version.options : [];
    if (version.loading || version.applying) {
      const option = doc.createElement("option");
      option.textContent = version.applying ? "Applying..." : "Loading manifests...";
      option.value = "";
      select.appendChild(option);
      select.disabled = true;
    } else if (options.length) {
      for (const item of options) {
        const option = doc.createElement("option");
        option.value = item.manifestId || "";
        option.dataset.buildId = item.buildId || "";
        option.dataset.date = item.date || "";
        option.dataset.label = item.label || item.manifestId || "";
        option.textContent = item.label || item.manifestId || "";
        option.selected = (item.manifestId || "") === (version.selectedManifestId || version.currentManifestId || "");
        select.appendChild(option);
      }
    } else {
      const option = doc.createElement("option");
      option.textContent = "No manifests available";
      option.value = "";
      select.appendChild(option);
      select.disabled = true;
    }

    const apply = panel.querySelector(".hpv-apply");
    const selectedManifest = () => {
      return select.value || "";
    };
    const refreshApply = () => {
      const selected = selectedManifest();
      const changed = /^\d{12,20}$/.test(selected) && selected !== (version.currentManifestId || "");
      apply.style.display = changed ? "inline-flex" : "none";
      apply.disabled = !changed || version.loading || version.applying;
    };
    select.addEventListener("change", refreshApply);
    apply.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      const selected = selectedManifest();
      if (!selected) return;
      const selectedOption = select.selectedOptions?.[0];
      apply.disabled = true;
      apply.textContent = "Applying...";
      send("libraryApplyLuaVersion", panel.dataset.appId, {
        manifestId: selected,
        buildId: selectedOption?.dataset?.buildId || "",
        date: selectedOption?.dataset?.date || "",
        label: selectedOption?.dataset?.label || selectedOption?.textContent || selected,
        options: options.map(item => ({
          manifestId: item.manifestId || "",
          buildId: item.buildId || "",
          date: item.date || "",
          label: item.label || item.manifestId || "",
          isCurrent: (item.manifestId || "") === selected
        }))
      });
    });
    refreshApply();

    const status = panel.querySelector(".hpv-status");
    status.textContent = version.statusText || "";
    status.dataset.tone = version.statusTone || "idle";

    if (!panel.parentElement) doc.body.appendChild(panel);
  }

  globalThis.__hubcapLibrarySetState = state => {
    const keepVersionPanel = shouldKeepVersionPanel(state);
    removeElements(globalThis.document, keepVersionPanel);
    const win = desktopWindow();
    const doc = win?.document;
    if (!doc) return;

    ensureStyle(doc);
    removeElements(doc, keepVersionPanel);

    if (!state.exists && !state.removed && !state.statusText) return;
    globalThis.__hubcapLastLibraryState = state.removed ? null : state;

    if (state.exists) {
      const versionButton = doc.createElement("button");
      versionButton.id = VERSION_BUTTON_ID;
      versionButton.type = "button";
      versionButton.dataset.appId = state.appId || routeAppId();
      versionButton.textContent = "Lua Version";
      versionButton.title = `Change Lua manifest for app ${versionButton.dataset.appId}`;
      versionButton.addEventListener("click", event => {
        event.preventDefault();
        event.stopPropagation();
        versionButton.disabled = true;
        versionButton.textContent = "Loading...";
        showLuaVersionPanel(doc, { ...state, luaVersion: { visible: true, loading: true, appId: versionButton.dataset.appId, gameName: state.gameName || "" } });
        send("libraryLoadLuaVersions", versionButton.dataset.appId);
        setTimeout(() => {
          if (versionButton.isConnected) {
            versionButton.disabled = false;
            versionButton.textContent = "Lua Version";
          }
        }, 1800);
      });
      placeInActionBar(doc, versionButton);

      const checkButton = doc.createElement("button");
      checkButton.id = CHECK_BUTTON_ID;
      checkButton.type = "button";
      checkButton.dataset.appId = state.appId || routeAppId();
      checkButton.textContent = "Edit Lua";
      checkButton.title = `Edit Lua for app ${checkButton.dataset.appId}`;
      checkButton.addEventListener("click", event => {
        event.preventDefault();
        event.stopPropagation();
        checkButton.disabled = true;
        checkButton.textContent = "Opening...";
        send("libraryCheck", checkButton.dataset.appId);
        setTimeout(() => {
          if (checkButton.isConnected) {
            checkButton.disabled = false;
            checkButton.textContent = "Edit Lua";
          }
        }, 1200);
      });
      placeInActionBar(doc, checkButton);

      const button = doc.createElement("button");
      button.id = BUTTON_ID;
      button.type = "button";
      button.dataset.appId = state.appId || routeAppId();
      button.textContent = "Remove Lua";
      button.title = `Remove Lua for app ${button.dataset.appId}`;
      button.addEventListener("click", event => {
        event.preventDefault();
        event.stopPropagation();
        showRemoveConfirm(doc, state, () => {
          button.disabled = true;
          button.textContent = "Removing...";
          send("libraryRemove", button.dataset.appId);
        });
      });
      placeInActionBar(doc, button);
      showLuaVersionPanel(doc, state);
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
      const current = win.document.getElementById(VERSION_BUTTON_ID) || win.document.getElementById(CHECK_BUTTON_ID) || win.document.getElementById(BUTTON_ID) || win.document.getElementById(STATUS_ID);
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
