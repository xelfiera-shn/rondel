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
2. The menu opens above full-screen apps and on every Space, and takes the keyboard (Esc, Tab, arrow keys and digits, as on Windows) without activating Rondel or taking Finder's active state: a non-activating panel that can become the key window, as Spotlight does. This needs the window level, `collectionBehavior` and style of the underlying `NSWindow`/`NSPanel`, set through its native handle.
3. A menu bar extra works with `LSUIElement` (no Dock icon, no app menu).
4. The menu opens without visible delay from a warm start, on Retina and non-Retina monitors; idle memory is measured.
5. With the language on Automatic, a Turkish macOS shows Turkish when Rondel is started from Finder or at login, where no `LANG` variable is set.

If any of these fails, the fallback is AppKit through .NET for macOS (`net10.0-macos`): the core stays shared, only the window and its drawing are written natively. Windows stays on WPF either way. `net10.0-macos` needs the macOS workload, which does not build on Windows, so the solution and CI would then build that project on macOS only; Avalonia on `net10.0` builds everywhere.

**Icons:** actions name Segoe Fluent Icons code points, a font that is not on macOS. The Mac app maps the built-in code points to its own vector icons (for example Fluent UI System Icons, MIT); an unknown code point gets the default icon.

## Folder detection in Finder

Detection runs on its own thread, like the Windows STA thread: Accessibility calls are synchronous requests to Finder and may wait.

- `AXUIElementCopyElementAtPosition` returns the element under the pointer, and its ancestor chain decides the context. As on Windows, only non-localized identifiers decide (`AXRole`, `AXSubrole`), never titles or descriptions. The spike records the roles for icon, list, column and gallery views and for the desktop, the way [windows-notes.md](windows-notes.md#context-detection) lists the UI Automation ones.
- **Folder item:** the item's file URL from its accessibility attributes. A folder must satisfy `Directory.Exists`; packages (`.app`, `.bundle`) and files are not folders; aliases and symbolic links are resolved.
- **Empty area of a Finder window:** the folder the window shows, from the window's `AXDocument` (a file URL). If Accessibility does not expose it, the fallback asks Finder with Apple Events (`target` of the Finder window whose bounds contain the point), which needs the Automation permission. That permission is never asked in the middle of a gesture, where its prompt would hold the press until it times out: it is asked at first start or from Settings, and until it is granted the fallback is skipped.
- **Desktop:** an empty spot opens the desktop menu for the projects folder; a folder on the desktop is a folder item.
- Recents, AirDrop, Network, the Trash, iCloud search results and other virtual locations give `None`.
- The context kinds stay as they are. `ExplorerEmpty` is the key in everyone's `settings.json`, so on macOS it means "empty area of a Finder window".

**Open:** whether Finder's list and column views expose each row's URL, and how "Click wallpaper to reveal desktop" (macOS 14 and later) behaves when a press is given back.

## Launching in a terminal

The core already turns an action into a `LaunchPlan`; the Mac launcher runs it.

- **Script:** a POSIX counterpart of `PowerShellScript` in the core, unit-tested and round-trip tested on the macOS CI runner. Every value is a single-quoted literal (`'` becomes `'\''`), so quotes, `$`, backticks, `;`, line breaks and Turkish characters arrive unchanged. The script changes to the folder (`cd --`), sets the window title, runs `git init` first for a new project that asked for it (`LaunchPlan.InitGit`; without git it says so and goes on), checks the command with `command -v`, prints "command not found" in the user's language, and starts the agent.
- **Shell:** the user's login shell (from the user database, zsh by default). The agent runs inside it as a login and interactive shell (`"$SHELL" -l -i -c …`), so `PATH` from `~/.zprofile` and aliases such as `cc` from `~/.zshrc` work; a script on its own would not see the aliases. Apps started from Finder do not inherit the terminal's `PATH`; running inside the shell avoids that. When the agent exits, the script replaces itself with a login shell (`exec "$SHELL" -l`), so the window stays usable, like `-NoExit` on Windows. The round-trip test also starts the command through an alias.
- **Terminal.app** (default): the script is written to a private temporary `.command` file (mode `0700`) that deletes itself when it starts, and opened with `open -a Terminal`. No Automation permission is needed and nothing has to be quoted for AppleScript.
- **iTerm2** (when chosen): the same `.command` file opened with `open -a iTerm`, if the spike confirms that iTerm2 runs it; that needs no AppleScript, no quoting and no Automation permission. Otherwise a new window through its AppleScript interface, which needs the Automation permission for iTerm2.
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
| Accessibility | The event tap that swallows presses, and reading elements under the pointer | First start; the trigger stays off until it is granted, and the menu bar icon says so |
| Automation (Finder) | Only for the Apple Events fallback in detection | At first start or from Settings, never during a gesture |
| Automation (iTerm2) | Only if iTerm2 cannot run the `.command` file directly | The first launch in iTerm2 |

- **Permission and signature:** macOS ties a granted permission to the app's bundle identifier (`app.rondel`, fixed from the first build on) and its code signature. An ad-hoc signed build gets a new signature with every build, so users would have to grant Accessibility again after every update. Every build is therefore signed with a fixed self-signed certificate, which costs nothing: release builds with one certificate kept in the repository secrets (the macOS release job fails without it), development builds with each developer's own local certificate. **Open:** the spike confirms that a permission granted to one build survives an update signed with the same certificate.
- **Not notarized:** releases are signed but not notarized, so macOS blocks the first start of a downloaded copy, and again after downloading an update; the user allows it in System Settings → Privacy & Security → Open Anyway. The README explains this the way it explains SmartScreen.
- **No App Sandbox:** the sandbox does not allow a global event tap or starting other apps' shells, so Rondel is not a Mac App Store app. It ships as a zipped `.app` on the GitHub release.
- **Release:** `release.yml` gains a macOS job that builds `osx-arm64` and `osx-x64`, signs them with the certificate from the repository secrets, and adds the zipped apps to the same release as the Windows executables, to `SHA256SUMS.txt` and to the build provenance attestations.

## Strings

All new text is added in English and Turkish. Some current strings name Windows ("Start with Windows", "Automatic (Windows language)", the folder-name errors); they get platform-specific keys or neutral wording ("Open at login", "Automatic (system language)").

## Steps

Each step is its own pull request into `dev`:

1. This plan.
2. Spike: Avalonia window, event tap and Accessibility probe, a permission surviving a re-signed build, iTerm2 opening a `.command` file; results recorded here, nothing merged.
3. Core: the POSIX script, `MacFolderNameRules`, the new `Terminal` values and strings, with tests. Nothing changes on Windows.
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
