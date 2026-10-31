# Windows implementation notes

Behaviors the Windows implementation depends on, and the reasons behind them. Most of these were learned the hard way; please keep them intact when changing the code, and update this page when a behavior changes on purpose.

## Trigger and mouse hook

**Cheap checks on the hook thread.** A left-button press is only considered when all of these hold:

- Ctrl is down and Shift, Alt and Win are not;
- the input is physical: not injected, and not carrying the pen/touch signature (`0xFF515700` in `dwExtraInfo`);
- the root window under the cursor is `Progman`, `WorkerW` (desktop) or `CabinetWClass` (File Explorer), and the window that was hit has a `SHELLDLL_DefView` ancestor;
- the owning process is **not elevated** — UIPI would block the re-injected click, so elevated Explorer windows are left alone.

**Swallow and re-inject.** A matching press is swallowed and detection starts at once on the STA thread while the hold timer runs:

| What happens next | Rondel does |
|---|---|
| Button released before the hold threshold | Re-injects press + release (an ordinary Ctrl+click). |
| Mouse moves beyond `SM_CXDRAG` / `SM_CYDRAG` | Re-injects the press at the original point, then the move (Ctrl+drag keeps working). |
| Detection returns `None` | Re-injects the press; the button is still physically held. |
| Detection takes longer than hold time + 1200 ms | Gives the click back. |
| Hold threshold reached with a context | Opens the menu. |

Re-injected events are tagged with `dwExtraInfo = 0x524E444C` and the hook ignores them. While the menu is open, a click outside Rondel's windows is swallowed (down and up) and closes the menu.

**Focus.** Taking the foreground needs an empty `SendInput` call before `SetForegroundWindow`. When the menu is cancelled, focus returns to the previous window.

## Context detection

**Overlay trap.** Some click-through overlays cover the whole screen — NVIDIA's GeForce overlay (`CEF-OSC-WIDGET`), Discord, Game Bar. `WindowFromPoint` skips them, but UI Automation's `ElementFromPoint` returns the overlay. When the UIA ancestor chain does not contain the `WindowFromPoint` window, detection starts again from `ElementFromHandle(hit)` and descends to the smallest child whose `BoundingRectangle` contains the point.

**Only non-localized identifiers drive decisions:**

| Element | UIA control type / class | Meaning |
|---|---|---|
| Explorer items view | `List` / `UIItemsView` | Container |
| Explorer item | `ListItem` / `UIItem` | Item |
| Details view cell | `Edit` / `UIProperty` | Still the item |
| Group header | `Group` / `UIGroupItem` | Empty space |
| Column header (Windows 11) | `SplitButton` / `UIColumnHeader` | Blocked |
| Desktop | `List` / `SysListView32` | Container; items carry no `ItemType` |

Windows 11 Home's start page has no `UIItemsView`, so it does not trigger.

**Folder resolution.**

- The Explorer window is found in `Shell.Application.Windows()` by its top-level HWND. Windows 11 tabs share that HWND, so the active tab is matched through `IServiceProvider → IShellBrowser → GetWindow` against the `ShellTabWindowClass` ancestor of the window that was hit.
- An item is resolved with `ParseName` first; otherwise `Items()` is scanned by display name (up to 2000 items), so localized names such as "Belgeler" work. Items with the same name are told apart by comparing the UIA `ItemType` with the shell `Type`; if it is still ambiguous, the result is `None`.
- ZIP files report `IsFolder`, so a folder must also satisfy `Directory.Exists`. Shortcuts resolve through `GetLink.Path`, and drives in This PC count as folders.
- Virtual containers (`::{GUID}` paths), the Recycle Bin and search results get no empty-area menu. Items inside them that resolve to real folders still work.

**Shell COM through `dynamic`.** Cast results to `object` or a concrete type early (for example `Convert.ToInt64((object)w.HWND)`); mixing `dynamic` with `nint` comparisons throws at run time.

## Launching

- Everything travels in **one PowerShell script** passed with `-EncodedCommand` (Base64 of UTF-16LE). Quotes, `;`, `$`, backticks, line breaks and Turkish characters survive both Windows Terminal's and PowerShell's parsing.
- Windows Terminal is started as `wt -d . "<shell>" -NoLogo -NoExit -EncodedCommand …` with the process working directory set to the target. The folder never appears on wt's command line, because `;` is wt's command separator. The script itself runs `Set-Location -LiteralPath` and then calls the command with `@args`.
- `CreateProcess` cannot start in a directory longer than `MAX_PATH`; in that case the process starts in the user profile and the script's `Set-Location` still goes to the target.
- **Single-quoted literals:** double every `'` **and** the typographic quotes `‘ ’ ‚ ‛`, which PowerShell also treats as quotes.
- **Embedded double quotes:** Windows PowerShell 5.1 (and pwsh below 7.3, or in `Legacy` mode) does not escape `"` inside arguments passed to native programs. The script escapes them at run time following `CommandLineToArgvW` rules; pwsh 7.3+ does it itself.
- **npm `.cmd` shims:** `cmd.exe` cannot carry line breaks or quotes in one argument and may expand `%VAR%`. For such a shim the prompt is simplified and a warning is printed in the terminal. The native `claude.exe` has no such limitation.
- `PATH` is re-read from the registry on every launch, so tools installed after Rondel started are found.
- `AllowSetForegroundWindow(ASFW_ANY)` lets an already-running Windows Terminal bring its new tab to the front.
- "Command not found" is reported inside the terminal in the user's language. There is no hard pre-check, because aliases from the PowerShell profile are valid commands.
- The whole command line must stay below `CreateProcess`'s 32,767-character limit (a margin is kept).

## Radial menu window

- One per-pixel transparent WPF window (600 × 600 DIP stage, ring radius 168), created at startup and rendered once off-screen so the first open is instant. Rondel uses its own palette (ink surfaces, teal → violet brand gradient for whatever is active), not the Windows accent; light and dark follow the app mode.
- Placement is computed in physical pixels on the cursor's monitor and clamped so the ring (not the whole stage) stays in the work area. `SetWindowPos` is applied again after `Show`, because WPF rescales the window when the DPI changes.
- Fully transparent pixels of a layered window let the mouse through. Areas that must keep the pointer without being visible (the bridge between the ring and an expanded slice's items) are painted with alpha 1.
- **Expandable slices**: a Resume slice whose agent can resume by id gets an arrow outside the ring once its recent sessions are known. Hovering it opens the sessions on an outer arc centered on the slice: the newest right outside it, the next ones alternating clockwise and counterclockwise. Leaving the ring keeps the arc open; hovering another slice closes it. Keyboard: Tab steps into the items, arrows move, Esc/Backspace steps back.
- Labels are measured and narrowed until every corner stays inside the tile and its wedge, but never below the width of the longest word (expanded items may shrink their font a little instead). Long single-word folder names in the hub break at camel-case and separator boundaries. The hint line under a label is reserved in the layout, so hints arriving later do not move anything.
- `--render-menu` draws the menu for a folder into a PNG without showing it, for checking the design.
- The text box placeholder is aligned with `GetRectFromCharacterIndex(0)`; fixed padding was off by one or two pixels.
- "Animation effects" turned off in Windows (`SystemParameters.ClientAreaAnimation`) disables Rondel's animations too.
- The native tray menu follows dark mode through undocumented uxtheme ordinals 133, 135 and 136, guarded to build 18362 and later.

## Folder names

New project folders are validated before anything is created:

- invalid characters, a trailing dot or space;
- reserved device names, checked on the part before the first dot, including `COM0`/`LPT0` and the superscript forms `COM¹ COM² COM³ LPT¹ LPT² LPT³`;
- at most 255 characters per name, and a full path of at most 247 characters unless `LongPathsEnabled` is set;
- write permission, probed with `CreateFile(FILE_ADD_SUBDIRECTORY)` on the nearest existing ancestor — no temporary folders are created.

All identifier, path and key comparisons are ordinal or invariant (the Turkish i).

## Manual testing tips

- PowerShell helper scripts that use UI Automation must run in a fresh `powershell -NoProfile -File …`; DPI awareness has to be set before anything else in the process.
- Do not end a quoted command-line argument with `\` — `"C:\x\"` swallows the closing quote.
- Only one Rondel instance runs at a time; a second start just opens Settings in the running one.
- `WScript.Shell` writes `.lnk` targets through the ANSI code page (Ğ becomes G, ş becomes s). Create test shortcuts with `IShellLinkW` instead.
