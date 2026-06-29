# HubcapLauncher

Standalone launcher executable based on Hubcap Plugin.

## About

HubcapLauncher is meant to be minimalistic, less cluttered, and direct to the point. It starts Steam with DevTools enabled, injects the Hubcap controls into Steam, and uses a native Windows launcher bridge for the file operations that browser JavaScript cannot safely do.

It brings Steam Store and Library Lua controls to users who want Hubcap functionality without running Millennium.

## Features

- Adds a `Download Lua` / `Remove Lua` button on Steam store game pages.
- Buttons automatically change depending on whether you already have the Lua installed.
- Uses Hubcap's manifest API route to install both `.lua` and `.manifest` files.
- Checks Hubcap availability first and shows `Unavailable` when no file exists.
- Shows `Checking...` with a small spinner while checking availability.
- Automatically detects DLC pages and gets the main game's Lua instead.
- Shows the base game name when a DLC page is detected.
- Shows a warning when the Steam page mentions Denuvo or anti-tamper.
- Shows the `Download Lua` button in orange when Denuvo or anti-tamper is detected.
- Shows the `Remove Lua` button in red when Lua is already installed.
- Shows a `Go to Library` button after Lua is downloaded.
- Shows a Library-side `Remove Lua` button for games that already have Lua installed.
- Shows a Hubcap usage panel with username, API key expiry, daily usage count, progress bar, and loading spinner.
- Refreshes Hubcap usage on page load, after download, and when clicked manually.
- Runs as a standalone Windows launcher executable without Millennium.
- Launches or restarts Steam with DevTools/CDP support, then exits automatically when Steam closes.

## Screenshots

Download only Lua:

![Download only Lua](HubcapLauncher/docs/images/store-download-lua.png)

Unavailable:

![Unavailable](HubcapLauncher/docs/images/store-unavailable.png)

Denuvo Download:

![Denuvo Download](HubcapLauncher/docs/images/store-denuvo-download.png)

DLC download only:

![DLC download only](HubcapLauncher/docs/images/store-dlc-download.png)

DLC remove:

![DLC remove](HubcapLauncher/docs/images/store-dlc-remove.png)

Library Remove:

![Library Remove](HubcapLauncher/docs/images/library-remove-lua.png)

## Requirements

- Windows
- Steam
- HubcapTool
- A HubcapLauncher executable, or the .NET 9 SDK if building from source
- Your own Hubcap API key configured in:

```text
%Steam%\config\hubcaptools\config.yaml
```

Required HubcapTool config keys:

```yaml
HubcapApiKey: your_hubcap_api_key
HubcapLuaDir: C:\path\for\lua\files
```

## How to Run

1. Install and set up HubcapTool.
2. Make sure your HubcapTool config exists at `%Steam%\config\hubcaptools\config.yaml`.
3. Download or build `HubcapLauncher.exe`.
4. Run `HubcapLauncher.exe`.
5. If Steam is closed, HubcapLauncher starts Steam in dev mode automatically.
6. If Steam is already open but not in dev mode, HubcapLauncher asks to restart Steam in dev mode.
7. If HubcapLauncher cannot find Steam automatically, move `HubcapLauncher.exe` into the same folder as `steam.exe`, then run it again.
8. Open a game page in the Steam Store or Library.
9. Use the Hubcap buttons:
   - `Download Lua` installs Lua when available.
   - `Unavailable` means Hubcap does not have Lua for that app yet.
   - `Remove Lua` removes installed Lua.
   - DLC pages automatically use the main game's Lua.

## API Key Safety

HubcapLauncher only reads your Hubcap API key locally from HubcapTool's `config.yaml` so it can call Hubcap.

It does not save, display, upload, log, or share your API key anywhere else.

The Hubcap usage check only shows your daily limit count, like `23/50`.

## How It Works

HubcapLauncher starts Steam with Chromium DevTools enabled and connects through Chrome DevTools Protocol on `127.0.0.1:8080`.

It reads the Steam app ID from the current store page. If the page is DLC, it uses Steam appdetails to find the base game app ID first.

It uses Hubcap's official API through your locally configured HubcapTool API key, checks whether a Lua package is available, downloads the package when requested, and copies the returned files into the correct HubcapTool/Steam locations.

Temporary download/extract files are cleaned up after install.

## Build Standalone Launcher

```powershell
cd HubcapLauncher
.\publish-win-x64.ps1
```

The published single-file executable is created at:

```text
HubcapLauncher\bin\Release\net9.0\win-x64\publish\HubcapLauncher.exe
```

## Disclaimer

HubcapLauncher is an independent community-made launcher and is not affiliated with, endorsed by, or officially connected to Millennium, Steam, Valve, or HubcapTool.

Millennium and HubcapTool are separate projects owned and maintained by their respective creators.
