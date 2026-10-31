# macOS plan

How Rondel comes to macOS. This is a plan to agree on before any code lands; questions that are still open are marked **Open**. The shared flow, the settings, the session store and the strings are already in the core (see [architecture.md](architecture.md#shared-flow-and-platform-interfaces)), so the macOS app only adds the trigger, context detection, the menu window and the platform interfaces.

The spike (step 2) has been done; its results are folded into the sections below and summarized in [Spike](#spike).

## What the macOS app adds

| Piece | Windows | macOS |
|---|---|---|
| Project | `src/Rondel.Windows` (WPF) | `src/Rondel.MacOS` (`net10.0`, Avalonia, NativeAOT), an `.app` bundle named Rondel |
| Trigger | Low-level mouse hook | `CGEventTap` |
| Context detection | UI Automation + Shell COM | Window list, Accessibility API (`AXUIElement`) for items, Apple Events for the folder a Finder window shows |
| `IMenuHost` | `AppHost` + radial menu window + tray balloon | `AppHost` + Avalonia menu window + notification |
| `ITerminalLauncher` | `WindowsTerminalLauncher` | `MacTerminalLauncher` (Terminal.app, iTerm2) |
| `IFolderNameValidator` | `FolderNameValidator` | `MacFolderNameValidator` |
| `IAutostart` | Run key | `SMAppService` (open at login) |
| Tray icon | Notification area | Menu bar extra, no Dock icon (`LSUIElement`) |
| Second start opens Settings | Mutex + event | The system's reopen event (macOS normally runs one copy of an app) |

Settings (`~/Library/Application Support/Rondel`), logs (`~/Library/Logs/Rondel`), the default projects folder (`~/Projects`) and the Claude Code session store already work on macOS.

## Trigger

**Hold ⌥ Option + the left mouse button** on the desktop or in a Finder window. Ctrl+click is a right-click on macOS and ⌘+click selects, so Option is the modifier that is free for a hold.

- A `CGEventTap` on its own thread (with its own run loop) sees mouse presses before Finder does. It follows the Windows rules: only Option is down (no ⌘, ⌃, ⇧) and the event is not one Rondel posted itself.
- **Swallow and give back**, as on Windows: a matching press is swallowed and detection starts at once. Released before the hold time, moved beyond the drag distance (4 pt), or nothing to offer, the press is posted again with `CGEventPost`, so Option+click and Option+drag (copy) keep working; in other apps Option+click is given back at once. Posted events carry `0x524E444C` ("RNDL") in `kCGEventSourceUserData` and the tap ignores them.
- **The tap callback stays cheap.** It only reads the flags and the tag and swallows; whether the window under the press is Finder's is decided on the detection thread, which then opens the menu or gives the press back. macOS disables a tap whose callback is slow (`kCGEventTapDisabledByTimeout`); Rondel re-enables it and logs it.
- **Held drags reach the menu through the tap.** The window that receives a press receives its drags; the press was swallowed, so the menu window sees no pointer events while the button is still down. The tap forwards them, and the menu lights and picks the slice under the pointer itself.
- While the menu is open, a press outside it is swallowed (down and up) and closes it, as on Windows.
- **Trackpad:** a hold is a one-finger press kept down. Tap to click cannot hold, and pressing harder triggers Force Click. Many Mac users only have a trackpad, so the keyboard shortcut matters more here than on Windows.
- The keyboard shortcut uses `RegisterEventHotKey`, which needs no permission. The hold time setting is shared.

**Open:** whether other apps' synthetic clicks (remote desktop, automation tools) can be told apart reliably, as the pen/touch signature is on Windows.

## User interface

**Avalonia** (MIT); its paid offerings (tooling, XPF) are not needed and not used. Its XAML and layout model are close to WPF, so the radial menu's geometry, label fitting and arcs can be carried over idea by idea instead of being redesigned. It draws with Skia on Metal. The spike confirmed it, so the AppKit fallback (`net10.0-macos`) is not needed.

| Spike criterion | Result |
|---|---|
| Fully transparent pixels let the mouse through | **No**: drawn with Metal, every pixel of the window catches the pointer. A press on the empty stage closes the menu, which is what a click outside the menu does on Windows too. The window is therefore no larger than the deepest arc the menu can open, as on Windows. |
| Above full-screen apps, on the current Space, keyboard without activating Rondel | **Yes**, with the native changes below. Finder keeps its active title bar and menu bar; Esc, digits and clicks work; the keyboard goes back to Finder at once when the menu closes. |
| Menu bar extra with `LSUIElement` | **Yes** (Avalonia `TrayIcon`). |
| Opens without visible delay; idle memory | First frame 8 ms the first time and 3 ms after (NativeAOT; 30 and 7–9 ms with the JIT). About 118 MB resident (160 MB with the JIT), of which 2 MB is the managed heap; the rest is Skia, Metal and the runtime. The app is 34 MB. Retina and non-Retina side by side not checked yet. |
| Automatic language started from Finder (no `LANG`) | **Yes**: `CultureInfo.CurrentUICulture` follows the system's preferred languages. |

Native changes on Avalonia's `AvnWindow`, through its `NSWindow` handle:

- `setLevel:` `NSPopUpMenuWindowLevel` and `setCollectionBehavior:` `moveToActiveSpace | transient | ignoresCycle | fullScreenAuxiliary`. Not `canJoinAllSpaces`: with it the open menu followed the user to another Space and stayed there without the keyboard.
- **Non-activating panel**: `_setPreventsActivation:` and an `_isNonactivatingPanel` method added to `AvnWindow` that answers YES for the menu window only; then `ShowActivated = false` and `makeKeyWindow`. Both are **private API**: fine outside the Mac App Store, but a future macOS may change them. The window's class must not be replaced: macOS may already have given it a KVO subclass, and after replacing it the window came back 0×0.
- Losing the key window (another Space, ⌘Tab, a click in another app) closes the menu.

Other findings:

- **System font.** Avalonia 12 draws text with its own embedded Inter by default, which showed Ğ İ Ü Ö as G I U O. Folder names and session titles are often Turkish, so the menu uses the system font (`.AppleSystemUIFont`), as the Windows menu uses Segoe UI.
- **NativeAOT** for the macOS app. It needs one core change: settings are read and written with reflection-based `JsonSerializer` calls, which NativeAOT turns off; they move to a source-generated `JsonSerializerContext` (Windows can use it too; WPF itself does not support NativeAOT, so Windows keeps ReadyToRun).
- **The window is prepared on screen**, fully transparent, then hidden, so the first show is fast. Prepared off screen, it came back 0×0.
- Positions are in points with a top-left origin, the same space as `CGEventGetLocation`.
- The `Avalonia.BuildServices` package sends anonymous build telemetry; CI sets `AVALONIA_TELEMETRY_OPTOUT=1`.

**Themes:** the menu's themes are data in the core (`Rondel.Core.Themes`): tile shape, highlight, decoration and a palette per mode. The Mac menu draws the same themes from them, with the same geometry.

**Icons:** actions name Segoe Fluent Icons code points, a font that is not on macOS. The Mac app maps the built-in code points to its own vector icons (for example Fluent UI System Icons, MIT); an unknown code point gets the default icon.

## Folder detection in Finder

Detection runs on its own thread, like the Windows STA thread: the window list, Accessibility and Apple Events are synchronous requests and may wait.

1. **Window list** (`CGWindowListCopyWindowInfo`, 1–2 ms, no permission needed for owner, layer and bounds): the topmost window under the press decides between a Finder window, the desktop and "not ours".
   - The Dock keeps a transparent window over the whole screen at layer 20 (seen on macOS 27), above every app window; clicks pass through it, so it is skipped. The Dock itself is a strip.
   - The desktop is not only Finder's: the wallpaper belongs to `WindowManager`, Finder's desktop icons are at layer -2147483603. Any window far below layer 0 counts as the desktop.
   - Owners are identified by bundle identifier (from the owner's process id), never by name: names are localized (the wallpaper's owner is "Duvar Kâğıdı" on a Turkish system).
2. **Accessibility** (`AXUIElementCopyElementAtPosition` and the parent chain, 5–12 ms) finds the item. Only non-localized identifiers decide: roles, subroles and `AXIdentifier`, never titles.

   | Under the press | Chain (innermost first) | Context |
   |---|---|---|
   | Folder in icon view | `AXImage` with `AXURL`, `AXGroup`, `AXList/AXSectionList`, `AXList/AXCollectionList` (`IconView`) | **FolderItem** |
   | Folder in list, column or gallery view | an element with `AXURL` under `AXOutline` (`ListView`), `AXBrowser` (`ColumnView`) … | **FolderItem** |
   | File (or a package such as `.app`) | `AXURL` of a file (not tried in the spike) | **FileItem** for the folder it is in |
   | Empty area of a Finder window | … up to `AXWindow/AXStandardWindow` (`FinderWindow`) | **ExplorerEmpty**, folder from Apple Events (3) |
   | Empty desktop | no window | **DesktopEmpty** (projects folder) |
   | Folder on the desktop | `AXURL` | **FolderItem** |

   `AXURL` is the real path, also for folders Finder shows with a localized name (İndirilenler is `~/Downloads`). A folder must satisfy `Directory.Exists`; aliases and symbolic links are resolved.
3. **Apple Events** for an empty area. Finder leaves the window's `AXDocument` empty on macOS 27, and the title bar's proxy icon only has a localized title, so Accessibility cannot tell which folder a window shows. Rondel asks Finder in **one** request for the bounds of every Finder window and the URL of its target, and takes the frontmost window that contains the press: 33–37 ms warm, 45–60 ms for the whole detection. Asking window by window was wrong twice: the per-window `target as alias` failed for every window, and with 17 windows and tabs open it took 2.5–5 s. `NSAppleScript` runs on the main thread, compiled once.
   - This needs the **Automation permission for Finder**, so the Finder-window actions (New Project, Open Here, Continue Here, Resume Here) depend on it. It is never asked in the middle of a gesture, where its prompt would hold the press: it is asked at first start or from Settings, and until it is granted an empty area of a Finder window is not offered.

- Recents, AirDrop, Network, the Trash, iCloud search results and other virtual locations give `None`.
- The context kinds stay as they are. `ExplorerEmpty` is the key in everyone's `settings.json`, so on macOS it means "empty area of a Finder window".

**Open:** in column view Finder reports the window's deepest selected folder, not the column under the pointer; the empty part of a left column could instead mean that column's folder (from the `AXURL` of an item in the same column). Also how "Click wallpaper to reveal desktop" (macOS 14 and later) behaves when a press is given back.

## Launching in a terminal

The core already turns an action into a `LaunchPlan`; the Mac launcher runs it.

- **Script:** a POSIX counterpart of `PowerShellScript` in the core, unit-tested and round-trip tested on the macOS CI runner. Every value is a single-quoted literal (`'` becomes `'\''`), so quotes, `$`, backticks, `$(…)`, `;`, line breaks and Turkish characters arrive unchanged. The script changes to the folder (`cd --`), sets the window title, runs `git init` first for a new project that asked for it (`LaunchPlan.InitGit`; without git it says so and goes on), checks the command with `command -v`, prints "command not found" in the user's language, and starts the agent:

  ```zsh
  #!/bin/zsh
  rm -f -- "$0"
  cd -- '<folder>' 2>/dev/null || { printf … '<Folder not found …>' >&2; exec '<shell>' -l; }
  printf '\033]0;%s\007' '<Claude Code · folder>'
  exec '<shell>' -l -i -c 'if command -v NAME >/dev/null 2>&1; then NAME EXTRA "$@"; else printf … >&2; fi; exec '<shell>' -l' rondel '<arg 1>' '<arg 2>' …
  ```

- **Shell:** the user's login shell (from the user database, zsh by default); one that does not understand POSIX syntax (fish) is replaced by `/bin/zsh` (`PosixShellScript.ChooseShell`). The agent runs inside it as a login and interactive shell, so `PATH` from `~/.zprofile` and aliases such as `cc` from `~/.zshrc` work; apps started from Finder do not inherit the terminal's `PATH`. When the agent exits, the shell replaces itself with a login shell, so the window stays usable in the project folder, like `-NoExit` on Windows.
  - **One process all the way (`exec`).** Terminal runs the file as a job of its own shell, so the script owns the terminal. In the spike a child `"$SHELL" -l -i -c …` moved the terminal to a process group of its own; when it exited, the script's final shell was stopped (`suspended (tty output)`) and the window fell back to Terminal's shell in the home folder.
  - **The command name is not quoted** when it is a plain word (`[A-Za-z0-9._+/-]`): zsh does not expand a quoted word as an alias, and in the spike `'cc'` ran the C compiler instead of the user's alias. Any other name is quoted. The user's own text after the name in the agent's command (`--model opus`) is inserted as shell text, as on Windows; the arguments always go through `"$@"`.
  - The user's rc files run twice (for the interactive shell and for the shell that stays), so a slow `.zshrc` is paid twice.
- **Terminal.app** (default) and **iTerm2** (when chosen): the script is written to a private `.command` file (mode `0700`) that deletes itself when it starts, and opened with `open -a Terminal` or `open -a iTerm`. Both run it (iTerm2 3.7.6, even on its first start): no AppleScript, no quoting for it and no Automation permission for either terminal. Both show the file's path and `; exit` as the window's first line, so the file lives under a short path (for example `~/Library/Caches/Rondel`).
- **Settings:** `Terminal` gains `TerminalApp` and `ITerm`; `Auto` means Terminal.app on macOS. The Settings window lists only the current platform's choices, and each launcher treats the other platform's values as `Auto`. No default changes, so no schema migration. `Shell` keeps `Auto` on macOS.

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
| Accessibility | The event tap that swallows presses, and reading elements under the pointer | First start; the trigger stays off until it is granted, and the menu bar icon says so. A grant is noticed while Rondel runs (`AXIsProcessTrusted` once a second), no restart needed. |
| Automation (Finder) | The folder an empty area of a Finder window shows | At first start or from Settings, never during a gesture |

- **Permission and signature:** macOS ties a granted permission to the app's bundle identifier (`app.rondel`, fixed from the first build on) and its code signature. An ad-hoc signed build gets a new signature with every build, so users would have to grant Accessibility again after every update. Every build is therefore signed with a fixed self-signed certificate, which costs nothing: release builds with one certificate kept in the repository secrets (the macOS release job fails without it), development builds with each developer's own local certificate. The spike confirmed it: the Accessibility permission survived every rebuild signed with the same certificate.
- **Not notarized:** releases are signed but not notarized, so macOS blocks the first start of a downloaded copy, and again after downloading an update; the user allows it in System Settings → Privacy & Security → Open Anyway. The README explains this the way it explains SmartScreen.
- **No App Sandbox:** the sandbox does not allow a global event tap or starting other apps' shells, so Rondel is not a Mac App Store app. It ships as a zipped `.app` on the GitHub release.
- **Release:** `release.yml` gains a macOS job that builds `osx-arm64` and `osx-x64`, signs them with the certificate from the repository secrets, and adds the zipped apps to the same release as the Windows executables, to `SHA256SUMS.txt` and to the build provenance attestations.

## Strings

All new text is added in English and Turkish. Some current strings name Windows ("Start with Windows", "Automatic (Windows language)", the folder-name errors); they get platform-specific keys or neutral wording ("Open at login", "Automatic (system language)").

## Spike

A throwaway app (not merged), run on an Apple Silicon MacBook Air with macOS 27.0.1 and its built-in display, with Avalonia 12.1.4 and .NET 10: a five-slice stand-in for the menu, the Option trigger, Finder detection, and a launch round trip in Terminal.app and iTerm2. Everything worked in the end; what it changed in this plan:

- Avalonia holds, through three native window changes (level and Spaces, non-activating panel, closing on losing the key window); the AppKit fallback is dropped.
- NativeAOT for the macOS app, which needs the settings JSON in the core to be source-generated.
- The system font instead of Avalonia's default.
- Apple Events, not Accessibility, tell which folder a Finder window shows, so the Automation permission for Finder is needed for the Finder-window actions. Files were not tried; they should come the same way (`AXURL`) as `FileItem`.
- The window list skips the Dock's full-screen overlay and treats anything far below layer 0 as the desktop.
- The launch script `exec`s into the interactive shell and leaves a plain command name unquoted. iTerm2 runs the same `.command` file, so no terminal needs the Automation permission.
- Permissions survive rebuilds signed with the same certificate.

## Steps

Each step is its own pull request into `dev`:

1. This plan.
2. Spike: done (see [Spike](#spike)).
3. Core: the POSIX script, `MacFolderNameRules`, the new `Terminal` values and strings, and the settings JSON through a source-generated `JsonSerializerContext`, with tests. No behavior change on Windows. The settings JSON came first (#34); the rest is tested end to end with zsh and bash on the macOS CI runner.
4. `Rondel.MacOS`, in three pull requests, each built and tested in the macOS CI job:
   1. Menu bar icon, trigger and Finder detection, with a `--probe X Y out.txt` diagnostic like the Windows one.
   2. The menu window, with a `--render-menu` diagnostic for checking the design without screenshots.
   3. Launching and the settings window.
5. Distribution: signing with the release certificate, the release job and the macOS part of the README.

## Decisions

- **Bundle identifier:** `app.rondel`. It never changes: macOS keeps permissions and preferences under it, and a new one would make everyone grant them again.
- **Oldest macOS:** 15, the oldest version .NET 10 supports; Apple Silicon and Intel. `SMAppService` (macOS 13) is available on all of them.
- **Modifier:** Option is fixed in the first version, as Ctrl is on Windows. If people ask for a different one, the modifier becomes a setting on both platforms together.
- **Distribution:** signed with the fixed certificate, not notarized (see [Permissions and signing](#permissions-and-signing)); macOS builds ship in the same releases as Windows.
