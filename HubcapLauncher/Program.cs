using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
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

static class SteamDbManifestPicker
{
    public static List<LuaManifestOptionState> Pick(string depotId, string currentManifestId)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return PickOnSta(depotId, currentManifestId);

        List<LuaManifestOptionState>? options = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                options = PickOnSta(depotId, currentManifestId);
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
        return options ?? [];
    }

    private static List<LuaManifestOptionState> PickOnSta(string depotId, string currentManifestId)
    {
        var result = new List<LuaManifestOptionState>();
        using var form = new System.Windows.Forms.Form
        {
            Text = $"SteamDB Manifests - Depot {depotId}",
            Width = 900,
            Height = 660,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = true,
            ShowInTaskbar = true,
            TopMost = true
        };

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
            Text = "Loading SteamDB...",
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        };
        var useButton = new System.Windows.Forms.Button
        {
            Text = "Use visible manifests",
            Dock = System.Windows.Forms.DockStyle.Right,
            Width = 150,
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

        var latest = new List<LuaManifestOptionState>();
        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        var timeout = new System.Windows.Forms.Timer { Interval = 120000 };

        async Task ExtractAsync(bool closeOnSuccess)
        {
            if (webView.CoreWebView2 is null) return;
            try
            {
                var json = await webView.CoreWebView2.ExecuteScriptAsync("""
(() => {
  const text = document.body?.innerText || "";
  const rows = [];
  const seen = new Set();
  const datePattern = "\\d{1,2}\\s+[A-Za-z]+\\s+\\d{4}(?:\\s+[\\u2013\\u2014-]\\s+\\d{2}:\\d{2}(?::\\d{2})?\\s+UTC|,\\s+[A-Za-z]{3},\\s+\\d{2}:\\d{2}|\\s+[A-Za-z]{3}\\s+\\d{2}:\\d{2})";
  const dateRe = new RegExp(datePattern, "i");
  const manifestRe = /\b\d{12,20}\b/g;
  const add = (manifestId, date) => {
    manifestId = String(manifestId || "").trim();
    date = String(date || "").trim();
    if (!/^\d{12,20}$/.test(manifestId) || seen.has(manifestId)) return;
    seen.add(manifestId);
    rows.push({ manifestId, date });
  };
  const dateFrom = value => (String(value || "").match(dateRe) || [""])[0];
  const display = text.match(/Displaying manifest\s+(\d{12,20})\s+dated\s+(\d{1,2}\s+[A-Za-z]+\s+\d{4}\s+[–—-]\s+\d{2}:\d{2}:\d{2}\s+UTC)/i);
  if (display) add(display[1], display[2]);
  const displayFallback = text.match(new RegExp("Displaying manifest\\s+(\\d{12,20})\\s+dated\\s+(" + datePattern + ")", "i"));
  if (displayFallback) add(displayFallback[1], displayFallback[2]);

  for (const link of document.querySelectorAll("a[href], a")) {
    const combined = `${link.textContent || ""} ${link.getAttribute("href") || ""}`;
    const manifestIds = combined.match(manifestRe) || [];
    if (!manifestIds.length) continue;
    const row = link.closest("tr") || link.closest("li") || link.parentElement;
    let date = dateFrom(row?.innerText || "");
    if (!date && row?.previousElementSibling) date = dateFrom(row.previousElementSibling.innerText || "");
    for (const manifestId of manifestIds) add(manifestId, date);
  }

  for (const row of document.querySelectorAll("tr")) {
    const rowText = row.innerText || "";
    const date = dateFrom(rowText);
    const manifestIds = rowText.match(manifestRe) || [];
    for (const manifestId of manifestIds) add(manifestId, date);
  }
  const start = text.search(/Previously seen manifests/i);
  const section = start >= 0 ? text.slice(start) : text;
  const re = /(\d{1,2}\s+[A-Za-z]+\s+\d{4}\s+[–—-]\s+\d{2}:\d{2}:\d{2}\s+UTC)\s+(\d{12,20})/gi;
  let match;
  while ((match = re.exec(section)) !== null) add(match[2], match[1]);
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
                var rows = JsonSerializer.Deserialize<List<SteamDbManifestRow>>(json, JsonOptions.Default) ?? [];
                latest = RowsToOptions(rows, currentManifestId);
                if (latest.Count > 0)
                {
                    useButton.Enabled = true;
                    status.Text = $"Found {latest.Count} manifest rows. Importing...";
                    if (closeOnSuccess && latest.Count > 1)
                    {
                        result = latest;
                        timer.Stop();
                        form.Close();
                    }
                }
                else
                {
                    status.Text = "No manifest rows found yet. Finish any browser check, then use visible manifests.";
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
                timeout.Start();
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HubcapLauncher",
                    "SteamDbWebView2");
                Directory.CreateDirectory(userDataFolder);
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
        return result;
    }

    private static List<LuaManifestOptionState> RowsToOptions(List<SteamDbManifestRow> rows, string currentManifestId)
    {
        var options = new Dictionary<string, LuaManifestOptionState>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var manifestId = row.ManifestId.Trim();
            if (!Regex.IsMatch(manifestId, @"^\d{12,20}$")) continue;
            var isCurrent = string.Equals(manifestId, currentManifestId, StringComparison.OrdinalIgnoreCase);
            var date = row.Date.Trim();
            options[manifestId] = new LuaManifestOptionState
            {
                ManifestId = manifestId,
                Date = date,
                Label = string.IsNullOrWhiteSpace(date) ? $"{manifestId}{(isCurrent ? " (current)" : "")}" : $"{manifestId} - {date}{(isCurrent ? " (current)" : "")}",
                IsCurrent = isCurrent
            };
        }

        if (!string.IsNullOrWhiteSpace(currentManifestId) && !options.ContainsKey(currentManifestId))
        {
            options[currentManifestId] = new LuaManifestOptionState
            {
                ManifestId = currentManifestId,
                Label = $"{currentManifestId} (current)",
                IsCurrent = true
            };
        }

        return options.Values.ToList();
    }

    private sealed class SteamDbManifestRow
    {
        public string ManifestId { get; set; } = "";
        public string Date { get; set; } = "";
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
        var options = new Dictionary<string, LuaManifestOptionState>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in manifestRows)
        {
            var manifestId = row.ManifestId.Trim();
            if (!Regex.IsMatch(manifestId, @"^\d{12,20}$")) continue;
            var isCurrent = string.Equals(manifestId, currentManifestId, StringComparison.OrdinalIgnoreCase);
            var date = row.Date.Trim();
            buildByDate.TryGetValue(SteamDbTimestampKey(date), out var buildId);
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

            switch (evt.Action)
            {
                case "settings":
                    await SetStateAsync(cdp, new UiState { SettingsOnly = true, Settings = _hubcap.GetSettings() });
                    return;

                case "openLuaFolder":
                    var currentSettings = _hubcap.GetSettings();
                    var chosenFolder = _hubcap.ChooseLuaFolder();
                    var folderChanged = string.IsNullOrWhiteSpace(chosenFolder.Error) && chosenFolder.LuaDir != currentSettings.LuaDir;
                    await SetStateAsync(cdp, new UiState
                    {
                        SettingsOnly = true,
                        Settings = chosenFolder,
                        SettingsDraft = folderChanged,
                        StatusText = folderChanged ? "Folder selected. Save to apply." : chosenFolder.Error,
                        StatusTone = string.IsNullOrWhiteSpace(chosenFolder.Error) ? "idle" : "error",
                        StatusError = !string.IsNullOrWhiteSpace(chosenFolder.Error)
                    });
                    return;

                case "saveSettings":
                    var savedSettings = _hubcap.SaveSettings(evt.LuaDir, evt.ApiKey);
                    await SetStateAsync(cdp, new UiState
                    {
                        SettingsOnly = true,
                        Settings = savedSettings,
                        SettingsDraft = !string.IsNullOrWhiteSpace(savedSettings.Error),
                        StatusText = string.IsNullOrWhiteSpace(savedSettings.Error) ? "Saved." : savedSettings.Error,
                        StatusTone = string.IsNullOrWhiteSpace(savedSettings.Error) ? "success" : "error",
                        StatusError = !string.IsNullOrWhiteSpace(savedSettings.Error)
                    });
                    if (string.IsNullOrWhiteSpace(savedSettings.Error) &&
                        savedSettings.LuaDirChanged &&
                        UserPrompts.ConfirmRestartLauncherForLuaFolder())
                    {
                        LauncherRestarter.RestartFullLauncherAndExit();
                    }
                    return;

                case "copyApiKey":
                    var copyKey = _hubcap.CopyApiKeyToClipboard();
                    if (!copyKey.Success)
                        await SetStateAsync(cdp, new UiState { SettingsOnly = true, Settings = _hubcap.GetSettings(), StatusText = copyKey.Error, StatusTone = "error", StatusError = true });
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

                case "openSteamDb":
                    _hubcap.OpenSteamDb(evt.Url);
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
            await SetStateAsync(cdp, new UiState { UsageOnly = true, Usage = new UsageState { Error = ex.Message, ErrorLabel = "Stats Error" } });
        }
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

    public async Task<LuaVersionState> GetLuaVersionAsync(string appId, string gameName = "")
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
                return LuaVersionState.Error(appId, depotId, "", "No active Windows base depot manifest found in this Lua.", gameName);

            depotId = current.DepotId;
            var state = LuaVersionState.ForCurrent(appId, depotId, current.ManifestId, gameName);
            try
            {
                state.Options = await GetSteamDbManifestOptionsAsync(depotId, current.ManifestId);
                var currentOption = state.Options.FirstOrDefault(option => string.Equals(option.ManifestId, current.ManifestId, StringComparison.OrdinalIgnoreCase));
                if (currentOption is not null) state.CurrentDate = currentOption.Date;
                state.StatusText = state.Options.Count > 1
                    ? ""
                    : "SteamDB did not return older manifests for this depot.";
            }
            catch (Exception ex)
            {
                state.Options = [new LuaManifestOptionState
                {
                    ManifestId = current.ManifestId,
                    Date = "",
                    Label = $"{current.ManifestId} (current)",
                    IsCurrent = true
                }];
                state.StatusText = $"SteamDB manifest list unavailable: {ex.Message}";
                state.StatusTone = "error";
            }

            return state;
        }
        catch (Exception ex)
        {
            return LuaVersionState.Error(appId, MainDepotId(appId), "", ex.Message, gameName);
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
                return LuaVersionState.Error(appId, depotId, "", "No active Windows base depot manifest found in this Lua.", gameName);

            return LuaVersionState.ForCurrent(appId, current.DepotId, current.ManifestId, gameName);
        }
        catch (Exception ex)
        {
            return LuaVersionState.Error(appId, MainDepotId(appId), "", ex.Message, gameName);
        }
    }

    public async Task<LuaVersionState> GetLuaVersionWithBrowserAsync(string appId, string gameName = "")
    {
        var state = await GetLuaVersionAsync(appId, gameName);
        if (string.IsNullOrWhiteSpace(state.DepotId) || string.IsNullOrWhiteSpace(state.CurrentManifestId))
        {
            state.StatusText = string.IsNullOrWhiteSpace(state.StatusText) ? "No current Lua manifest to match against." : state.StatusText;
            state.StatusTone = "error";
            return state;
        }

        try
        {
            var options = SteamDbVersionPicker.Pick(state.AppId, state.DepotId, state.CurrentManifestId);
            if (options.Count == 0)
            {
                state.StatusText = "No manifest rows were readable from the SteamDB window.";
                state.StatusTone = "error";
                return state;
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
            return state;
        }
        catch (Exception ex)
        {
            state.StatusText = $"SteamDB browser picker failed: {ex.Message}";
            state.StatusTone = "error";
            return state;
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
                return new ActionResult(false, "No active Windows base depot manifest found in this Lua.");
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

    public ActionResult OpenSteamDb(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Host, "steamdb.info", StringComparison.OrdinalIgnoreCase))
                return new ActionResult(false, "Invalid SteamDB URL.");

            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
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
            var configPath = GetConfigPath();
            if (!File.Exists(configPath))
                return MissingConfigSettings(configPath);

            return ReadSettings(configPath);
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
                ApiKey = settings.ApiKey
            };
        }
        catch (Exception ex)
        {
            var settings = GetSettings();
            settings.Error = ex.Message;
            return settings;
        }
    }

    public SettingsState SaveSettings(string luaDir, string apiKey)
    {
        var luaDirChanged = false;
        try
        {
            var configPath = GetConfigPath();
            if (!File.Exists(configPath))
                throw new InvalidOperationException(ConfigMissingMessage);

            var previousSettings = ReadSettings(configPath);
            var previousLuaDir = NormalizeBackslashes(previousSettings.LuaDir);

            luaDir = NormalizeBackslashes(Environment.ExpandEnvironmentVariables((luaDir ?? "").Trim()));
            apiKey = (apiKey ?? "").Trim();
            var luaDirWasBlank = string.IsNullOrWhiteSpace(luaDir);
            if (luaDirWasBlank)
                luaDir = DefaultLuaDir();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("API key is required.");
            if (!luaDirWasBlank && !Directory.Exists(luaDir))
                throw new InvalidOperationException("Lua folder does not exist.");
            luaDirChanged = !string.Equals(previousLuaDir, luaDir, StringComparison.OrdinalIgnoreCase);

            var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : [];
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
            File.WriteAllLines(configPath, lines);

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

    private async Task<List<LuaManifestOptionState>> GetSteamDbManifestOptionsAsync(string depotId, string currentManifestId)
    {
        var url = $"https://steamdb.info/depot/{Uri.EscapeDataString(depotId)}/manifests/";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126 Safari/537.36");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.Referrer = new Uri("https://steamdb.info/");
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SteamDB returned {(int)response.StatusCode}.");

        var html = await response.Content.ReadAsStringAsync();
        var options = ParseSteamDbManifestOptions(html, currentManifestId);
        if (options.Count == 0)
            throw new InvalidOperationException("no manifests found on SteamDB.");
        return options;
    }

    private static List<LuaManifestEntry> ReadLuaManifestEntries(string luaPath)
    {
        var entries = new List<LuaManifestEntry>();
        var depotMeta = new Dictionary<string, LuaDepotMeta>(StringComparer.OrdinalIgnoreCase);
        var section = LuaDepotSection.Unknown;

        foreach (var line in File.ReadLines(luaPath))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                section = NextLuaDepotSection(section, trimmed);
                continue;
            }

            var addMatch = Regex.Match(line, @"^\s*addappid\s*\(\s*(?<depot>\d+)[^)]*\)\s*(?:--\s*(?<comment>.*))?$", RegexOptions.IgnoreCase);
            if (addMatch.Success)
            {
                var depotId = addMatch.Groups["depot"].Value;
                var comment = addMatch.Groups["comment"].Value.Trim();
                depotMeta[depotId] = LuaDepotMeta.From(section, comment);
                continue;
            }

            var manifestMatch = Regex.Match(line, @"^\s*setManifestid\s*\(\s*(?<depot>\d+)\s*,\s*[""'](?<manifest>\d+)[""']\s*(?:,\s*(?<size>\d+))?\s*\)", RegexOptions.IgnoreCase);
            if (!manifestMatch.Success) continue;

            var manifestDepotId = manifestMatch.Groups["depot"].Value;
            if (!depotMeta.TryGetValue(manifestDepotId, out var meta))
                meta = LuaDepotMeta.From(section, "");
            entries.Add(new LuaManifestEntry
            {
                DepotId = manifestDepotId,
                ManifestId = manifestMatch.Groups["manifest"].Value,
                Size = manifestMatch.Groups["size"].Value,
                Comment = meta.Comment,
                Section = meta.Section,
                IsWindows = meta.IsWindows,
                IsNonWindows = meta.IsNonWindows,
                IsBase = meta.IsBase,
                IsDlc = meta.IsDlc,
                IsShared = meta.IsShared
            });
        }

        return entries;
    }

    private static LuaManifestEntry? SelectLuaVersionEntry(List<LuaManifestEntry> entries, string appId)
    {
        var active = entries
            .Where(entry => !entry.IsShared && !entry.IsDlc && !entry.IsNonWindows)
            .ToList();
        var mainDepotId = MainDepotId(appId);

        var explicitWindowsBase = active
            .Where(entry => entry.IsBase && entry.IsWindows)
            .ToList();
        var current = explicitWindowsBase.FirstOrDefault(entry => string.Equals(entry.DepotId, mainDepotId, StringComparison.OrdinalIgnoreCase))
            ?? explicitWindowsBase.FirstOrDefault();
        if (current is not null) return current;

        var inferredBase = active.Where(entry => entry.IsBase).ToList();
        current = inferredBase.FirstOrDefault(entry => string.Equals(entry.DepotId, mainDepotId, StringComparison.OrdinalIgnoreCase));
        if (current is not null) return current;
        if (inferredBase.Count == 1) return inferredBase[0];

        current = active.FirstOrDefault(entry => string.Equals(entry.DepotId, mainDepotId, StringComparison.OrdinalIgnoreCase));
        if (current is not null) return current;
        return active.Count == 1 ? active[0] : null;
    }

    private static LuaDepotSection NextLuaDepotSection(LuaDepotSection current, string commentLine)
    {
        var text = commentLine.TrimStart('-').Trim();
        if (text.Contains("MAIN APP DEPOTS", StringComparison.OrdinalIgnoreCase)) return LuaDepotSection.Base;
        if (text.Contains("DLCS WITH DEDICATED DEPOTS", StringComparison.OrdinalIgnoreCase)) return LuaDepotSection.Dlc;
        if (text.Contains("SHARED DEPOTS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("REDISTRIBUTABLES", StringComparison.OrdinalIgnoreCase)) return LuaDepotSection.Shared;
        if (text.Contains("EMPTY DEPOTS", StringComparison.OrdinalIgnoreCase)) return LuaDepotSection.Empty;
        if (text.Contains("MAIN APPLICATION", StringComparison.OrdinalIgnoreCase)) return LuaDepotSection.MainApplication;
        return current;
    }

    private static List<LuaManifestOptionState> ParseSteamDbManifestOptions(string html, string currentManifestId)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "\n"));
        text = Regex.Replace(text, @"[ \t\r\f\v]+", " ");
        text = Regex.Replace(text, @"\n\s+", "\n");

        var options = new Dictionary<string, LuaManifestOptionState>(StringComparer.OrdinalIgnoreCase);
        var displayMatch = Regex.Match(
            text,
            @"Displaying manifest\s+(?<manifest>\d{12,20})\s+dated\s+(?<date>\d{1,2}\s+[A-Za-z]+\s+\d{4}\s+\p{Pd}\s+\d{2}:\d{2}:\d{2}\s+UTC)",
            RegexOptions.IgnoreCase);
        if (displayMatch.Success)
            AddSteamDbOption(options, displayMatch.Groups["manifest"].Value, displayMatch.Groups["date"].Value, currentManifestId);

        var sectionStart = text.IndexOf("Previously seen manifests", StringComparison.OrdinalIgnoreCase);
        if (sectionStart >= 0)
        {
            var section = text[sectionStart..];
            var historyStart = section.IndexOf("\n##", StringComparison.OrdinalIgnoreCase);
            if (historyStart > 0) section = section[..historyStart];

            foreach (Match match in Regex.Matches(
                section,
                @"(?<date>\d{1,2}\s+[A-Za-z]+\s+\d{4}\s+\p{Pd}\s+\d{2}:\d{2}:\d{2}\s+UTC)\s+(?<manifest>\d{12,20})",
                RegexOptions.IgnoreCase))
            {
                AddSteamDbOption(options, match.Groups["manifest"].Value, match.Groups["date"].Value, currentManifestId);
            }
        }

        if (!string.IsNullOrWhiteSpace(currentManifestId) && !options.ContainsKey(currentManifestId))
            AddSteamDbOption(options, currentManifestId, "", currentManifestId);

        return options.Values
            .OrderByDescending(option => ParseSteamDbDate(option.Date))
            .ThenByDescending(option => option.IsCurrent)
            .ToList();
    }

    private static void AddSteamDbOption(Dictionary<string, LuaManifestOptionState> options, string manifestId, string date, string currentManifestId)
    {
        if (string.IsNullOrWhiteSpace(manifestId)) return;
        var isCurrent = string.Equals(manifestId, currentManifestId, StringComparison.OrdinalIgnoreCase);
        options[manifestId] = new LuaManifestOptionState
        {
            ManifestId = manifestId,
            Date = date,
            Label = string.IsNullOrWhiteSpace(date) ? $"{manifestId}{(isCurrent ? " (current)" : "")}" : $"{manifestId} - {date}{(isCurrent ? " (current)" : "")}",
            IsCurrent = isCurrent
        };
    }

    private static DateTimeOffset ParseSteamDbDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTimeOffset.MinValue;
        var normalized = value.Replace("–", "-", StringComparison.Ordinal).Replace("—", "-", StringComparison.Ordinal);
        return DateTimeOffset.TryParse(normalized, out var parsed) ? parsed : DateTimeOffset.MinValue;
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
        var configPath = GetConfigPath();
        if (!File.Exists(configPath))
            throw new InvalidOperationException(ConfigMissingMessage);

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
        return new HubcapConfig(apiKey, luaDir);
    }

    private static SettingsState ReadSettings(string configPath)
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

        if (string.IsNullOrWhiteSpace(luaDir)) luaDir = DefaultLuaDir();
        return new SettingsState
        {
            LuaDir = ToConfigPath(luaDir),
            ApiKey = apiKey,
            Error = string.IsNullOrWhiteSpace(apiKey) ? "API Key missing." : ""
        };
    }

    private static string GetConfigPath()
    {
        var steamRoot = FindSteamRoot();
        return Path.Combine(steamRoot, "config", "hubcaptools", "config.yaml");
    }

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
sealed record ResolvedApp(string AppId, string VisibleAppId, string GameName, string ParentName, bool IsDlc);
sealed record HubcapConfig(string ApiKey, string LuaDir);
sealed record StatusResult(bool Success, bool Available, string Error);
sealed record CachedStatusResult(StatusResult Result, DateTimeOffset ExpiresAt);
sealed record ActionResult(bool Success, string Error);
sealed record HubcapEvent(
    string Action,
    string AppId,
    string Href,
    string LuaDir = "",
    string ApiKey = "",
    string ManifestId = "",
    string BuildId = "",
    string Date = "",
    string Label = "",
    string Url = "",
    List<LuaManifestOptionState>? Options = null);
sealed record StoreWatchdogState(string AppId, bool HasUi, bool HasSetter);

enum LuaDepotSection
{
    Unknown,
    MainApplication,
    Base,
    Dlc,
    Shared,
    Empty
}

sealed class LuaDepotMeta
{
    public LuaDepotSection Section { get; private init; }
    public string Comment { get; private init; } = "";
    public bool IsWindows { get; private init; }
    public bool IsNonWindows { get; private init; }
    public bool IsBase => Section == LuaDepotSection.Base;
    public bool IsDlc => Section == LuaDepotSection.Dlc;
    public bool IsShared => Section == LuaDepotSection.Shared || Comment.Contains("Shared from", StringComparison.OrdinalIgnoreCase);

    public static LuaDepotMeta From(LuaDepotSection section, string comment)
    {
        var normalized = comment ?? "";
        var isWindows = Regex.IsMatch(normalized, @"\b(win|win32|win64|windows)\b", RegexOptions.IgnoreCase);
        var isNonWindows = Regex.IsMatch(normalized, @"\b(linux|osx|macos|mac)\b", RegexOptions.IgnoreCase);
        return new LuaDepotMeta
        {
            Section = section,
            Comment = normalized,
            IsWindows = isWindows,
            IsNonWindows = isNonWindows
        };
    }
}

sealed class LuaManifestEntry
{
    public string DepotId { get; init; } = "";
    public string ManifestId { get; init; } = "";
    public string Size { get; init; } = "";
    public string Comment { get; init; } = "";
    public LuaDepotSection Section { get; init; }
    public bool IsWindows { get; init; }
    public bool IsNonWindows { get; init; }
    public bool IsBase { get; init; }
    public bool IsDlc { get; init; }
    public bool IsShared { get; init; }
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
}

sealed class SettingsState
{
    public string LuaDir { get; set; } = "";
    public string ApiKey { get; set; } = "";
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
    public string BuildUrl { get; set; } = "";
    public string ManifestUrl { get; set; } = "";
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
        SelectedManifestId = manifestId,
        BuildUrl = $"https://steamdb.info/app/{appId}/patchnotes/",
        ManifestUrl = $"https://steamdb.info/depot/{depotId}/manifests/"
    };

    public static LuaVersionState Error(string appId, string depotId, string manifestId, string error, string gameName = "") => new()
    {
        Visible = true,
        AppId = appId,
        GameName = gameName,
        DepotId = depotId,
        CurrentManifestId = manifestId,
        SelectedManifestId = manifestId,
        BuildUrl = $"https://steamdb.info/app/{appId}/patchnotes/",
        ManifestUrl = string.IsNullOrWhiteSpace(depotId) ? "" : $"https://steamdb.info/depot/{depotId}/manifests/",
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
  const CONFIRM_ID = "hubcap-cdp-remove-confirm";
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
    #${ROOT_ID} button[data-state="disabled"]{background:rgba(13,27,39,.32);border-color:rgba(180,198,210,.18);color:rgba(214,244,255,.56);opacity:1}
    #${ROOT_ID} button[data-state="disabled"]:hover{background:rgba(13,27,39,.32);border-color:rgba(180,198,210,.18);color:rgba(214,244,255,.56)}
    #${ROOT_ID} .hp-status{color:#acdbf5;font:12px Arial,Helvetica,sans-serif;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
    #${ROOT_ID} .hp-status[data-tone="error"]{color:#ff9b8f;font-weight:700}
    #${ROOT_ID} .hp-status[data-tone="success"]{color:#a4d007;font-weight:700}
    #${ROOT_ID} .hp-warning{color:#f7c46c;display:none;font:12px Arial,Helvetica,sans-serif;font-weight:700;white-space:nowrap}
    #${ROOT_ID} .hp-warning[data-visible="true"]{display:inline-flex}
    #${ROOT_ID} .hp-usage{background:rgba(13,27,39,.48);border:1px solid rgba(103,193,245,.26);border-radius:3px;color:#d6f4ff;cursor:pointer;font-family:Arial,Helvetica,sans-serif;min-width:190px;padding:7px 10px 8px;position:relative}
    #${ROOT_ID} .hp-usage-row,#${ROOT_ID} .hp-usage-bottom{align-items:center;display:flex;gap:8px;justify-content:space-between}
    #${ROOT_ID} .hp-usage-row{font-size:12px}
    #${ROOT_ID} .hp-usage-bottom{font-size:12px;justify-content:space-between;margin-top:5px}
    #${ROOT_ID} .hp-usage-count-wrap{align-items:center;display:inline-flex;gap:6px;margin-left:auto}
    #${ROOT_ID} .hp-usage-name{color:#fff;font-weight:700}
    #${ROOT_ID} .hp-usage-expiry{color:#9fc9e0;font-size:11px}
    #${ROOT_ID} .hp-usage-bar{background:rgba(0,0,0,.26);border-radius:999px;height:4px;margin-top:6px;overflow:hidden}
    #${ROOT_ID} .hp-usage-fill{background:linear-gradient(90deg,#a4d007 0%,#67c1f5 100%);display:block;height:100%;width:0%}
    #${ROOT_ID} .hp-usage-spinner{animation:hubcap-cdp-spin .8s linear infinite;border:2px solid rgba(214,244,255,.28);border-top-color:#d6f4ff;border-radius:50%;display:none;height:10px;width:10px}
    #${ROOT_ID} .hp-usage-spinner[data-visible="true"]{display:inline-flex}
    #${ROOT_ID} .hp-settings-button{align-items:center;background:rgba(13,27,39,.38);border:1px solid rgba(103,193,245,.18);border-radius:3px;color:#9fc9e0;cursor:pointer;display:inline-flex;font:13px Arial,Helvetica,sans-serif;height:18px;justify-content:center;min-height:18px;min-width:18px;padding:0;width:18px}
    #${ROOT_ID} .hp-settings-button:hover{background:rgba(26,52,70,.72);border-color:rgba(103,193,245,.42);color:#fff}
    #${ROOT_ID} .hp-settings{background:rgba(13,27,39,.96);border:1px solid rgba(103,193,245,.3);border-radius:3px;box-shadow:0 8px 24px rgba(0,0,0,.38);color:#d6f4ff;display:none;font-family:Arial,Helvetica,sans-serif;min-width:360px;padding:10px;position:absolute;right:0;top:calc(100% + 8px);z-index:999999}
    #${ROOT_ID} .hp-settings[data-visible="true"]{display:block}
    #${ROOT_ID} .hp-settings-head{align-items:center;display:flex;font-size:12px;font-weight:700;justify-content:space-between;margin-bottom:9px}
    #${ROOT_ID} .hp-settings-close{align-items:center;background:transparent;border:0;box-shadow:none;color:#9fc9e0;cursor:pointer;display:inline-flex;font:16px Arial,Helvetica,sans-serif;height:20px;justify-content:center;min-height:20px;min-width:20px;padding:0;width:20px}
    #${ROOT_ID} .hp-settings-close:hover{background:rgba(255,255,255,.08);color:#fff}
    #${ROOT_ID} .hp-field{display:grid;gap:4px;margin-top:8px}
    #${ROOT_ID} .hp-field label{color:#9fc9e0;font-size:11px;font-weight:700}
    #${ROOT_ID} .hp-field-row{align-items:center;display:flex;gap:6px}
    #${ROOT_ID} .hp-field input{background:rgba(0,0,0,.24);border:1px solid rgba(103,193,245,.2);border-radius:2px;color:#d6f4ff;font:12px Consolas,monospace;height:28px;min-width:0;padding:0 8px;width:100%}
    #${ROOT_ID} .hp-icon-button{align-items:center;background:rgba(13,27,39,.48);border:1px solid rgba(103,193,245,.26);border-radius:3px;color:#d6f4ff;cursor:pointer;display:inline-flex;font:13px Arial,Helvetica,sans-serif;height:28px;justify-content:center;min-height:28px;min-width:30px;padding:0;width:30px}
    #${ROOT_ID} .hp-icon-button:hover{background:rgba(26,52,70,.62);border-color:rgba(103,193,245,.42);color:#fff}
    #${ROOT_ID} .hp-config-missing{display:none;gap:10px;margin-top:8px}
    #${ROOT_ID} .hp-config-missing[data-visible="true"]{display:grid}
    #${ROOT_ID} .hp-config-missing-text{color:#ff9b8f;font-size:12px;font-weight:700;line-height:1.35}
    #${ROOT_ID} .hp-settings-note{color:#9fc9e0;font-size:11px;min-height:14px;margin-top:8px}
    #${ROOT_ID} .hp-settings-note[data-tone="error"]{color:#ff9b8f;font-weight:700}
    #${ROOT_ID} .hp-settings-note[data-tone="success"]{color:#a4d007;font-weight:700}
    #${ROOT_ID} .hp-settings-actions{align-items:center;display:flex;justify-content:flex-end;margin-top:8px}
    #${ROOT_ID} .hp-save-settings{background:rgba(13,27,39,.48);border:1px solid rgba(103,193,245,.26);border-radius:3px;color:#d6f4ff;cursor:pointer;display:none;font:12px Arial,Helvetica,sans-serif;height:28px;min-height:28px;min-width:64px;padding:0 12px}
    #${ROOT_ID} .hp-save-settings[data-visible="true"]{display:inline-flex}
    #${ROOT_ID} .hp-save-settings:hover{background:rgba(26,52,70,.62);border-color:rgba(103,193,245,.42);color:#fff}
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
  if (!existingRoot || !root.querySelector(".hp-settings-button") || !root.querySelector(".hp-config-missing") || root.querySelector(".hp-open-config-file")) root.innerHTML = `
    <div class="hp-left"><button class="hp-main" type="button" data-state="checking" disabled>Checking...</button><button class="hp-library" type="button" style="display:none">Go to Library</button><span class="hp-status"></span><span class="hp-warning"></span></div>
    <div class="hp-right"><div class="hp-usage"><div class="hp-usage-row"><span class="hp-usage-name">Hubcap</span><span class="hp-usage-expiry">Expires --</span></div><div class="hp-usage-bar"><span class="hp-usage-fill"></span></div><div class="hp-usage-bottom"><button class="hp-settings-button" type="button" title="Hubcap settings">&#9881;</button><span class="hp-usage-count-wrap">Daily Usage: <strong class="hp-usage-count">--/--</strong><span class="hp-usage-spinner"></span></span></div><div class="hp-settings"><div class="hp-settings-head"><span>Hubcap Settings</span><button class="hp-settings-close" type="button" title="Close">&times;</button></div><div class="hp-config-missing"><div class="hp-config-missing-text">Config file not found. Make sure HubcapTools is installed.</div></div><div class="hp-field"><label>Lua Folder</label><div class="hp-field-row"><input class="hp-lua-dir" type="text"><button class="hp-icon-button hp-open-lua-folder" type="button" title="Select Lua folder">&#128193;</button></div></div><div class="hp-field"><label>API Key</label><div class="hp-field-row"><input class="hp-api-key" type="password"><button class="hp-icon-button hp-toggle-api-key" type="button" title="Show API key">&#128065;</button><button class="hp-icon-button hp-copy-api-key" type="button" title="Copy API key">&#128203;</button></div></div><div class="hp-settings-note"></div><div class="hp-settings-actions"><button class="hp-save-settings" type="button">Save</button></div></div></div></div>`;
  const appIdFromText = value => (String(value || "").match(/\/app\/(\d+)(?:\/|$)/)?.[1] || String(value || "").match(/store\.steampowered\.com\/app\/(\d+)(?:\/|$)/)?.[1] || "");
  const appIdFromUrl = () => appIdFromText(location.href) || appIdFromText(location.pathname) || appIdFromText(document.URL) || appIdFromText(document.querySelector('link[rel="canonical"]')?.href) || appIdFromText(document.querySelector('meta[property="og:url"]')?.content) || appIdFromText(globalThis.MainWindowBrowserManager?.m_lastLocation?.pathname || "") || appIdFromText(globalThis.MainWindowBrowserManager?.m_lastLocation?.href || "");
  function headerHost(){return document.querySelector(".apphub_HeaderStandardTop") || document.querySelector(".apphub_AppName")?.parentElement || null;}
  function earlyHost(){return headerHost() || document.querySelector("#game_highlights")?.parentElement || document.querySelector(".game_page_background") || document.querySelector(".game_background_glow") || null;}
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
    else if (host.classList?.contains("game_page_background") && root.parentElement !== host) host.prepend(root);
    else if (root.parentElement !== host) host.appendChild(root);
    return true;
  }
  placeRoot();
  const send = (action, extra = {}) => {
    const payload = JSON.stringify({ action, appId: appIdFromUrl(), href: location.href, ...extra });
    if (typeof window.hubcapNative === "function") window.hubcapNative(payload);
    else console.warn("[Hubcap CDP] native binding unavailable", payload);
  };
  function showRemoveConfirm(onConfirm) {
    document.getElementById(CONFIRM_ID)?.remove();
    const modal = document.createElement("div");
    modal.id = CONFIRM_ID;
    modal.innerHTML = `<div class="hp-confirm-box"><div class="hp-confirm-title">Remove Lua?</div><div class="hp-confirm-message"></div><div class="hp-confirm-actions"><button class="hp-confirm-no" type="button">No</button><button class="hp-confirm-yes" type="button">Yes</button></div></div>`;
    const gameName = document.querySelector(".apphub_AppName")?.textContent?.trim() || `app ${appIdFromUrl()}`;
    modal.querySelector(".hp-confirm-message").textContent = `Remove Lua for ${gameName}? This will delete the local Lua file.`;
    const close = () => modal.remove();
    modal.querySelector(".hp-confirm-no").addEventListener("click", event => { event.preventDefault(); event.stopPropagation(); close(); });
    modal.querySelector(".hp-confirm-yes").addEventListener("click", event => { event.preventDefault(); event.stopPropagation(); close(); onConfirm(); });
    modal.addEventListener("click", event => { if (event.target === modal) close(); });
    document.body.appendChild(modal);
    modal.querySelector(".hp-confirm-no").focus();
  }
  function setSettingsNote(message, tone = "idle") {
    const note = root.querySelector(".hp-settings-note");
    if (!note) return;
    note.textContent = message || "";
    note.dataset.tone = tone;
  }
  function updateSaveState() {
    const luaDir = root.querySelector(".hp-lua-dir");
    const apiKey = root.querySelector(".hp-api-key");
    const save = root.querySelector(".hp-save-settings");
    if (!luaDir || !apiKey || !save) return;
    save.dataset.visible = hasSettingsChanges() ? "true" : "false";
  }
  function hasSettingsChanges() {
    const luaDir = root.querySelector(".hp-lua-dir");
    const apiKey = root.querySelector(".hp-api-key");
    return !!luaDir && !!apiKey && (luaDir.value !== (root.dataset.luaDir || "") || apiKey.value !== (root.dataset.apiKey || ""));
  }
  function showSettings(settings, draft = false) {
    const panel = root.querySelector(".hp-settings");
    const luaDir = root.querySelector(".hp-lua-dir");
    const apiKey = root.querySelector(".hp-api-key");
    if (!panel || !luaDir || !apiKey) return;
    panel.dataset.visible = "true";
    const missing = !!settings?.configMissing;
    root.querySelector(".hp-config-missing").dataset.visible = missing ? "true" : "false";
    root.querySelectorAll(".hp-field").forEach(field => field.style.display = missing ? "none" : "grid");
    root.querySelector(".hp-settings-actions").style.display = missing ? "none" : "flex";
    if (missing) {
      luaDir.value = "";
      apiKey.value = "";
      root.dataset.luaDir = "";
      root.dataset.apiKey = "";
      setSettingsNote("");
      updateSaveState();
      return;
    }
    luaDir.value = settings?.luaDir || "";
    apiKey.value = settings?.apiKey || "";
    if (!draft) {
      root.dataset.luaDir = luaDir.value;
      root.dataset.apiKey = apiKey.value;
    }
    apiKey.type = "password";
    setSettingsNote(settings?.error || "", settings?.error ? "error" : "idle");
    updateSaveState();
  }
  window.__hubcapCdpSetState = state => {
    placeRoot();
    const button=root.querySelector(".hp-main"), library=root.querySelector(".hp-library"), status=root.querySelector(".hp-status"), warning=root.querySelector(".hp-warning"), usage=root.querySelector(".hp-usage"), name=root.querySelector(".hp-usage-name"), expiry=root.querySelector(".hp-usage-expiry"), count=root.querySelector(".hp-usage-count"), fill=root.querySelector(".hp-usage-fill"), spinner=root.querySelector(".hp-usage-spinner");
    if(state.settingsOnly){showSettings(state.settings||{},!!state.settingsDraft);if(state.statusText)setSettingsNote(state.statusText,state.statusTone||"idle");return;}
    const denuvo=/denuvo|anti[-\s]?tamper/i.test(document.body?.innerText||"");
    warning.textContent="Warning: Denuvo / 3rd-party anti-tamper detected";
    warning.dataset.visible=denuvo?"true":"false"; button.dataset.denuvo=denuvo?"true":"false";
    if(state.busy){button.disabled=true;button.dataset.state="checking";button.dataset.busy="true";button.textContent=state.busyText||"Working...";status.textContent=state.statusText||"";status.dataset.tone="idle";spinner.dataset.visible=state.usageBusy?"true":"false";return;}
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
  if (!root.dataset.bound) {
    root.querySelector(".hp-main").addEventListener("click",()=>{const state=root.querySelector(".hp-main").dataset.state;if(state==="download")send("download");if(state==="remove")showRemoveConfirm(()=>send("remove"));});
    root.querySelector(".hp-library").addEventListener("click",()=>send("library"));
    root.querySelector(".hp-usage").addEventListener("click",()=>send("refresh"));
    root.querySelector(".hp-settings-button").addEventListener("click",event=>{event.preventDefault();event.stopPropagation();setSettingsNote("Loading...");root.querySelector(".hp-settings").dataset.visible="true";send("settings");});
    root.querySelector(".hp-settings").addEventListener("click",event=>event.stopPropagation());
    root.querySelector(".hp-settings-close").addEventListener("click",event=>{event.preventDefault();event.stopPropagation();root.querySelector(".hp-settings").dataset.visible="false";});
    root.querySelector(".hp-open-lua-folder").addEventListener("click",event=>{event.preventDefault();event.stopPropagation();setSettingsNote("Selecting Lua folder...");send("openLuaFolder");});
    root.querySelector(".hp-lua-dir").addEventListener("input",updateSaveState);
    root.querySelector(".hp-api-key").addEventListener("input",updateSaveState);
    root.querySelector(".hp-save-settings").addEventListener("click",event=>{event.preventDefault();event.stopPropagation();setSettingsNote("Saving...");send("saveSettings",{luaDir:root.querySelector(".hp-lua-dir").value||"",apiKey:root.querySelector(".hp-api-key").value||""});});
    root.querySelector(".hp-toggle-api-key").addEventListener("click",event=>{event.preventDefault();event.stopPropagation();const input=root.querySelector(".hp-api-key");input.type=input.type==="password"?"text":"password";});
    root.querySelector(".hp-copy-api-key").addEventListener("click",async event=>{event.preventDefault();event.stopPropagation();const value=root.querySelector(".hp-api-key").value||"";try{await navigator.clipboard.writeText(value);setSettingsNote("API key copied.");}catch{send("copyApiKey");setSettingsNote("API key copied.");}});
    document.addEventListener("click",event=>{const panel=root.querySelector(".hp-settings"),button=root.querySelector(".hp-settings-button");if(panel?.dataset.visible==="true"&&!panel.contains(event.target)&&!button?.contains(event.target)&&!hasSettingsChanges())panel.dataset.visible="false";},true);
    root.dataset.bound = "true";
  }
  if(window.__hubcapCdpRouteTimer)clearInterval(window.__hubcapCdpRouteTimer);
  let lastHubcapAppId=appIdFromUrl();
  window.__hubcapCdpSetState({busy:true,busyText:"Checking...",statusText:"",usageBusy:true});
  hydrateCachedUsage();
  if(lastHubcapAppId)setTimeout(()=>send("route"),0);
  window.__hubcapCdpRouteTimer=setInterval(()=>{const nextAppId=appIdFromUrl();placeRoot();if(!nextAppId&&lastHubcapAppId){lastHubcapAppId="";window.__hubcapCdpSetState({busy:true,busyText:"Checking...",statusText:""});return;}if(nextAppId&&nextAppId!==lastHubcapAppId){lastHubcapAppId=nextAppId;send("route");}},150);
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
    modal.innerHTML = `<div class="hpc-box"><div class="hpc-title">Remove Lua?</div><div class="hpc-message"></div><div class="hpc-actions"><button class="hpc-no" type="button">No</button><button class="hpc-yes" type="button">Yes</button></div></div>`;
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
