# macOS plan

How Rondel comes to macOS. This is a plan to agree on before any code lands; questions that are still open are marked **Open**. The shared flow, the settings, the session store and the strings are already in the core (see [architecture.md](architecture.md#shared-flow-and-platform-interfaces)), so the macOS app only adds the trigger, context detection, the menu window and the platform interfaces.

## What the macOS app adds

| Piece | Windows | macOS (proposed) |
|---|---|---|
| Project | `src/Rondel.Windows` (WPF) | `src/Rondel.MacOS` (`net10.0`, Avalonia), an `.app` bundle named Rondel |
| Trigger | Low-level mouse hook | `CGEventTap` |
| Context detection | UI Automation + Shell COM | Accessibility API (`AXUIElement`), Apple Events as a fallback |
| `IMenuHost` | `AppHost` + radial menu window + tray balloon | `AppHost` + Avalonia menu window + notification |
| `ITerminalLauncher` | `WindowsTerminalLauncher` | `MacTerminalLauncher` (Terminal.app, iTerm2) |
| `IFolderNameValidator` | `FolderNameValidator` | `MacFolderNameValidator` |
| `IAutostart` | Run key | `SMAppService` (open at login) |
| Tray icon | Notification area | Menu bar extra, no Dock icon (`LSUIElement`) |
| Second start opens Settings | Mutex + event | The system's reopen event (macOS normally runs one copy of an app) |

Settings (`~/Library/Application Support/Rondel`), logs (`~/Library/Logs/Rondel`), the default projects folder (`~/Projects`) and the Claude Code session store already work on macOS.

## Trigger

**Hold ⌥ Option + the left mouse button** on the desktop or in a Finder window. Ctrl+click is a right-click on macOS and ⌘+click selects, so Option is the modifier that is free for a hold.

- A `CGEventTap` sees mouse presses before Finder does. It follows the Windows rules: only Option is down (no ⌘, ⌃, ⇧), the event is not one Rondel posted itself, and the window under the pointer belongs to Finder.
- **Swallow and give back**, as on Windows: a matching press is swallowed and detection starts at once. Released before the hold time, moved beyond the drag distance, or nothing to offer, the press is posted again with `CGEventPost`, so Option+click, Option+drag (copy) and Option+click on a disclosure triangle keep working. Posted events carry `0x524E444C` ("RNDL") in `kCGEventSourceUserData` and the tap ignores them.
- **The tap callback stays cheap.** macOS disables a tap whose callback is slow (`kCGEventTapDisabledByTimeout`); Rondel re-enables it and logs it. "Does the window under the pointer belong to Finder" is answered from a window list kept up to date off the tap thread, not by asking the window server in the callback.
- The keyboard shortcut uses `RegisterEventHotKey`, which needs no permission. The hold time setting is shared.

**Open:** whether other apps' synthetic clicks (remote desktop, automation tools) can be told apart reliably, as the pen/touch signature is on Windows.

## User interface

**Avalonia**, if a short spike confirms the points below. Avalonia is MIT-licensed; its paid offerings (tooling, XPF) are not needed and not used. Its XAML and layout model are close to WPF, so the radial menu's geometry, label fitting and arcs can be carried over idea by idea instead of being redesigned. It draws with Skia on Metal.

The spike is a throwaway window, not merged, and reports back here:

1. Fully transparent pixels let the mouse through, so only the ring and its arcs catch the pointer (Windows relies on this).
2. The menu opens above full-screen apps and on every Space, without taking focus from Finder until it is needed for the name and prompt card. This needs the window level and `collectionBehavior` of the underlying `NSWindow`, set through its native handle.
3. A menu bar extra works with `LSUIElement` (no Dock icon, no app menu).
4. The menu opens without visible delay from a warm start, on Retina and non-Retina monitors; idle memory is measured.

If any of these fails, the fallback is AppKit through .NET for macOS (`net10.0-macos`): the core stays shared, only the window and its drawing are written natively. Windows stays on WPF either way.

**Icons:** actions name Segoe Fluent Icons code points, a font that is not on macOS. The Mac app maps the built-in code points to its own vector icons (for example Fluent UI System Icons, MIT); an unknown code point gets the default icon.

## Folder detection in Finder

Detection runs on its own thread, like the Windows STA thread: Accessibility calls are synchronous requests to Finder and may wait.

- `AXUIElementCopyElementAtPosition` returns the element under the pointer, and its ancestor chain decides the context. As on Windows, only non-localized identifiers decide (`AXRole`, `AXSubrole`), never titles or descriptions. The spike records the roles for icon, list, column and gallery views and for the desktop, the way [windows-notes.md](windows-notes.md#context-detection) lists the UI Automation ones.
- **Folder item:** the item's file URL from its accessibility attributes. A folder must satisfy `Directory.Exists`; packages (`.app`, `.bundle`) and files are not folders; aliases and symbolic links are resolved.
- **Empty area of a Finder window:** the folder the window shows. If Accessibility does not expose it, the fallback asks Finder with Apple Events (`target` of the Finder window whose bounds contain the point), which needs the Automation permission.
- **Desktop:** an empty spot opens the desktop menu for the projects folder; a folder on the desktop is a folder item.
- Recents, AirDrop, Network, the Trash, iCloud search results and other virtual locations give `None`.
- The context kinds stay as they are. `ExplorerEmpty` is the key in everyone's `settings.json`, so on macOS it means "empty area of a Finder window".

**Open:** whether Finder's list and column views expose each row's URL, and how "Click wallpaper to reveal desktop" (macOS 14 and later) behaves when a press is given back.

## Launching in a terminal

The core already turns an action into a `LaunchPlan`; the Mac launcher runs it.

- **Script:** a POSIX counterpart of `PowerShellScript` in the core, unit-tested and round-trip tested on the macOS CI runner. Every value is a single-quoted literal (`'` becomes `'\''`), so quotes, `$`, backticks, `;`, line breaks and Turkish characters arrive unchanged. The script changes to the folder (`cd --`), sets the window title, checks the command with `command -v`, prints "command not found" in the user's language, and starts the agent.
- **Shell:** the user's login shell (zsh by default), started as a login and interactive shell, so `PATH` from `~/.zprofile` and aliases such as `cc` from `~/.zshrc` work. Apps started from Finder do not inherit the terminal's `PATH`; running inside the shell avoids that. The shell stays open when the agent exits, like `-NoExit` on Windows.
- **Terminal.app** (default): the script is written to a private temporary `.command` file (mode `0700`) that deletes itself when it starts, and opened with `open -a Terminal`. No Automation permission is needed and nothing has to be quoted for AppleScript.
- **iTerm2** (when chosen): a new window through its AppleScript interface, which needs the Automation permission for iTerm2.
- **Settings:** `Terminal` gains `TerminalApp` and `ITerm`; `Auto` means Terminal.app on macOS. The Settings window lists only the current platform's choices. No default changes, so no schema migration. `Shell` keeps `Auto` on macOS.

**Open:** other terminals (Ghostty, WezTerm, kitty) through their command-line options, later.

## Folder names

`FolderNameRules` holds the Windows rules. A `MacFolderNameRules` next to it, also unit-tested:

- `/` and `:` are not allowed (Finder shows `:` as `/`), nor control characters; `.` and `..` are not names;
- at most 255 bytes in UTF-8 (APFS);
- a leading `.` would hide the folder in Finder, so it is rejected with its own message;
- the folder must not exist yet. APFS is usually case-insensitive, so `Directory.Exists` answers that.

`MacFolderNameValidator` adds the write check (`access` with `W_OK | X_OK` on the nearest existing ancestor), without creating anything.

## Permissions and signing

| Permission | Why | When it is asked |
|---|---|---|
| Accessibility | The event tap that swallows presses, and reading elements under the pointer | First start; the trigger stays off until it is granted, and the menu bar icon says so |
| Automation (Finder) | Only for the Apple Events fallback in detection | The first time the fallback is used |
| Automation (iTerm2) | Only when iTerm2 is the chosen terminal | The first launch in iTerm2 |

- **Permission and signature:** macOS ties a granted permission to the app's code signature and bundle identifier. An unsigned or ad-hoc signed build loses its permissions on every rebuild, so development builds are signed with a fixed local certificate, and the bundle identifier is fixed from the first build on.
- **Distribution:** Developer ID signing with the hardened runtime, notarization and stapling. This needs an Apple Developer Program membership. .NET needs the `allow-jit` entitlement under the hardened runtime unless it is built with NativeAOT.
- **No App Sandbox:** the sandbox does not allow a global event tap or starting other apps' shells, so Rondel is not a Mac App Store app. It ships as a zipped `.app` on the GitHub release (later perhaps a Homebrew cask).
- **Without signing**, macOS blocks the first start; it can be allowed in System Settings → Privacy & Security → Open Anyway. The README would explain this the way it explains SmartScreen.
- **Release:** `release.yml` gains a macOS job that builds `osx-arm64` and `osx-x64`, signs and notarizes when the secrets are present, and adds the archives to `SHA256SUMS.txt` and the build provenance attestations.

## Strings

All new text is added in English and Turkish. Some current strings name Windows ("Start with Windows", "Automatic (Windows language)", the folder-name errors); they get platform-specific keys or neutral wording ("Open at login", "Automatic (system language)").

## Steps

Each step is its own pull request into `dev`:

1. This plan.
2. Spike: Avalonia window, event tap and Accessibility probe; results recorded here, nothing merged.
3. Core: the POSIX script, `MacFolderNameRules`, the new `Terminal` values and strings, with tests. Nothing changes on Windows.
4. `Rondel.MacOS`: menu bar icon, trigger, detection, menu, launching, settings window; built and tested in the macOS CI job.
5. Distribution: signing, notarization, the release job and the macOS part of the README.

## Decisions needed from the maintainer

- Who holds the Apple Developer Program membership and the signing certificate.
- The bundle identifier (it cannot change later without everyone granting permissions again).
- The oldest macOS version to support (at least what .NET 10 supports; `SMAppService` needs macOS 13).
- Whether Option is the final modifier, or Option is only the default and the modifier becomes a setting.
