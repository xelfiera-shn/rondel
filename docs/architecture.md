# Architecture

Rondel is a tray application. Every interaction follows the same path:

```
trigger (mouse gesture or hotkey) → context detection → radial menu → action → launch in a terminal
```

The platform-neutral parts (what the menu contains, how an agent is started, settings, sessions, strings) live in a core library. Everything that talks to the operating system lives in a platform project that implements the core's interfaces.

## Projects

| Project | Target | Contents |
|---|---|---|
| `src/Rondel.Core` | `net10.0` | The shared menu flow, agent profiles, menu model, launch planning, folder-name rules, session stores, settings schema and migrations, localization, logging, platform interfaces. No Win32 or WPF. |
| `src/Rondel.Windows` | `net10.0-windows` (WPF) | `Rondel.exe`: mouse hook, input re-injection, UI Automation and Shell detection, terminal launching, tray icon, radial menu and settings windows, autostart. |
| `tests/Rondel.Core.Tests` | `net10.0` | Unit tests for core logic that can break silently. |

```
src/Rondel.Core/
  Agents/         AgentProfile, AgentCapability, BuiltInAgents, ClaudeCode/ (session store)
  Context/        TriggerContext, ContextKind
  Flow/           MenuFlow, MenuDetails, InputSpec
  Menus/          ActionDefinition, MenuBuilder, MenuModel
  Launch/         LaunchPlan, LaunchPlanner, CommandLine, PowerShellScript, FolderNameRules
  Sessions/       ISessionStore, SessionStores, SessionHints, RelativeTime
  Settings/       AppSettings (schema and migrations), SettingsJson, SettingsStore
  Localization/   Loc, string tables (English, Turkish)
  Logging/        Log
  Platform/       IMenuHost, ITerminalLauncher, IFolderNameValidator, IAutostart
src/Rondel.Windows/
  Hook/ Interop/ Detection/ Shell/ Launch/ Settings/ UI/
```

The core builds and its tests run on Windows and macOS (CI checks both); the whole solution compiles on macOS and Linux too.

## Shared flow and platform interfaces

Each platform app owns the trigger, context detection and the menu window; everything between them is shared:

1. The platform turns a gesture or hotkey into a `TriggerContext`. With nothing useful under the pointer, the hotkey falls back to `MenuFlow.ProjectsContext`.
2. `MenuFlow.Build` returns the menu for the context (null when none is configured), and the platform shows it.
3. `MenuDetails.Load` reads the session stores off the UI thread; the platform fills in the hints and the items that open further when they arrive. `MenuDetails.Needed` says whether there is anything to look up.
4. The platform reports the chosen item to `MenuFlow.Invoke`, which starts the agent or asks for a folder name (and a prompt) first, creates the folder and launches.

| Interface | What the platform provides | Windows |
|---|---|---|
| `IMenuHost` | Closing the menu, the inline name/prompt card, warnings outside the menu | `AppHost` (radial menu window, tray balloon) |
| `ITerminalLauncher` | Running a `LaunchPlan` in a terminal | `WindowsTerminalLauncher` |
| `IFolderNameValidator` | The file system's rules for a new folder name | `FolderNameValidator` (`FolderNameRules`, long paths, write permission) |
| `IAutostart` | Starting with the user's session | `Autostart` (Run key) |

Some core parts carry Windows rules because they are pure and worth testing in the core: `FolderNameRules` (Windows naming rules) and `PowerShellScript` (the Windows launch script). The `Terminal` and `Shell` settings list Windows choices. Another platform adds its own counterparts next to them.

## Threads (Windows)

| Thread | Responsibility |
|---|---|
| Hook thread | Low-level mouse hook and the gesture state machine. Only cheap checks; decides whether a press is swallowed or given back. |
| Detection thread (STA) | UI Automation and Shell COM calls that turn a screen point into a `TriggerContext`. |
| UI thread (WPF) | Radial menu, settings window, tray icon, launching. |

Detection starts as soon as a Ctrl+press is swallowed, while the hold timer runs in parallel, so the menu can open the moment the hold threshold is reached. Session hints are looked up after the menu is shown and filled in when they arrive; they never delay the menu.

## Contexts

`ContextKind` is one of `None`, `DesktopEmpty`, `ExplorerEmpty`, `FolderItem` or `FileItem`. A `TriggerContext` carries the kind, the target folder (for a file, the folder it is in, with the file in `FilePath`), the name shown in the menu center, a short reason for the log and, in debug mode, the detection trace. `None` means "not ours": the click is passed through unchanged.

## Agent profiles

An agent profile describes one coding agent:

- an id and a display name,
- a base command, which the user can override (aliases defined in the shell profile work too),
- the capabilities the agent supports, each with an argument template,
- optionally a session store, used for the hints under Continue and Resume,
- optionally how a prompt mentions a file (`FileMention`, `@{file}` for Claude Code).

| Capability | Claude Code arguments |
|---|---|
| `Open` | *(none)* |
| `Continue` | `--continue` |
| `Resume` | `--resume` |
| `ResumeById` | `--resume {id}` |
| `Prompt` | `{prompt}` |

Menu actions refer to a **capability**, not to a command line. An action uses the default agent unless it names one, and it may add extra arguments (for example `--permission-mode plan`). A slice is hidden when its agent does not support the capability.

An action may ask for input first (`Input`): a new folder's name (`FolderName`), a name and then a prompt (`FolderNameAndPrompt`), or only a prompt for the menu's folder (`Prompt`, the Ask slices). An action with `MentionFile` starts its prompt with the file the menu was opened on, in the agent's `FileMention` syntax; the agent starts in the file's folder, so `{file}` is the file's name, in double quotes unless it consists only of letters, digits and `._-()` (Claude Code reads `@"My Notes.md"` as one path). Such an action is hidden where there is no file or the agent has no `FileMention`.

### Claude Code session store

Claude Code keeps each session as `~/.claude/projects/<encoded folder>/<session id>.jsonl` (the root moves to `CLAUDE_CONFIG_DIR` when that variable is set).

- The folder encoding replaces every UTF-16 code unit outside `[A-Za-z0-9]` with `-`. Names longer than 200 characters are cut to 200 characters plus `-` and a hash, so they are matched by prefix.
- The encoding is lossy: `a.b` and `a-b` collide, and every Turkish letter becomes `-`. Each session file's first recorded `"cwd"` is therefore read (and cached) and compared case-insensitively with the target folder.
- If the store does not exist at all, the result is *unknown* and nothing is dimmed. If it exists but has no sessions for the folder, Continue and Resume are dimmed.
- `RecentFolders` lists the folders worked in most recently across the whole store (for Recent Projects), each with its session count and latest session. Folders that no longer exist and folders under the temp folder are left out.
- The Resume slice and a recent project expand into their five newest sessions, but only when there are at least two. Their titles come from the session file: a title the user gave (`custom-title`), else Claude Code's latest generated title (`ai-title`), else an old-style `summary`, else the first real prompt. Only the first and last 256 KB of a file are read.
- This is an internal format of another program: any failure means "no hints", never an error.

## Settings

- `%APPDATA%\Rondel\settings.json` (macOS: `~/Library/Application Support/Rondel/settings.json`). The file is watched, so edits apply without a restart; saves are atomic, and an unreadable file is backed up before it is overwritten.
- Comments and trailing commas are accepted. Non-ASCII characters are written as they are, so Turkish paths stay readable. Icons are Segoe Fluent Icons code points in hex (`"E8F4"`).
- The schema is versioned and is at **3**. **Changing a default requires a migration**, applied only where the user still has the old default: version 2 added Recent Projects to the desktop menu, and version 3 added Ask to the folder and Explorer menus, each only where that menu was still the previous default. A menu for a new context (`FileItem` in version 3) needs no migration: a missing menu gets its default.

Shape of version 3 (abridged):

```jsonc
{
  "SchemaVersion": 3,
  "Language": "auto",                       // "auto", "en" or "tr"
  "ProjectsRoot": "%USERPROFILE%\\Projects",       // "~/Projects" on macOS and Linux; ~ and %NAME% are expanded
  "HoldMs": 300,
  "Hotkey": "",                             // e.g. "Ctrl+Alt+Space"
  "Terminal": "Auto",                       // Auto, WindowsTerminal, Console
  "Shell": "Auto",                          // Auto, Pwsh, WindowsPowerShell
  "DefaultAgent": "claude-code",
  "Agents": [
    { "Id": "claude-code", "Name": "Claude Code", "Command": "claude", "Sessions": "claude-code", "FileMention": "@{file}",
      "Capabilities": { "Open": [], "Continue": ["--continue"], "Prompt": ["{prompt}"], "...": [] } }
  ],
  "Actions": [
    { "Id": "continue", "Label": "@Action.Continue", "Icon": "E768", "Capability": "Continue", "Hint": "LastSession" },
    { "Id": "plan", "Label": "Plan", "Icon": "E8FD", "Capability": "Open", "ExtraArgs": ["--permission-mode", "plan"] },
    { "Id": "recents", "Label": "@Action.Recents", "Icon": "E823", "Capability": "ResumeById", "Source": "RecentProjects" },
    { "Id": "ask-file", "Label": "@Action.AskFile", "Icon": "E8F2", "Input": "Prompt", "Capability": "Prompt", "MentionFile": true }
  ],
  "Menus": {
    "DesktopEmpty": ["new-project", "new-prompt", "recents"],
    "ExplorerEmpty": ["new-project", "new-prompt", "open-here", "ask-here", "continue-here", "resume-here"],
    "FolderItem": ["open", "ask", "continue", "resume"],
    "FileItem": ["ask-file", "open-here", "continue-here", "resume-here"]
  }
}
```

Labels starting with `@` are built-in translated strings; anything else is shown as written.

## Launching

The core turns an action into a `LaunchPlan`: the folder, the agent command, the literal argument list and the window title. The Windows project executes the plan as a single PowerShell script in Windows Terminal (or a console window when Windows Terminal is missing). The escaping rules are described in [windows-notes.md](windows-notes.md#launching).

## Localization

English and Turkish string tables live in the core. The language follows the Windows display language unless the user picks one in Settings; switching applies immediately.

## Logging and diagnostics

Logs go to `%LOCALAPPDATA%\Rondel\logs` (macOS: `~/Library/Logs/Rondel`): one file per day, rolled at 5 MB, the newest 20 kept. Writing happens on a background task, so logging is safe from the hook thread. With **Debug logging** on, every trigger writes the UI Automation chain, the detected context and the resolved folder.

Command-line diagnostics run instead of the tray app, never touch a running instance and write their result to a file:

| Switch | Result |
|---|---|
| `--probe X Y out.txt` | Classifies a screen point (physical pixels) exactly like a trigger would, with the full trace. |
| `--sessions FOLDER out.txt` | The session hints the menu would show for a folder. |
| `--launch-script FOLDER COMMAND prompt.txt out.ps1` | The PowerShell script a launch would run. |
| `--launch-now FOLDER COMMAND prompt.txt` | Launches like the menu does, with COMMAND instead of the configured one. |
| `--export-icon out.ico` | Writes the application icon. |
| `--show-menu [input [text]]` | Starts normally and opens the menu at the cursor without the gesture; `input` continues into the New + Prompt card. |
| `--render-menu out.png [dark\|light] [en\|tr] [desktop\|item] [idle] [deep] [edge] [transparent] [FOLDER\|FILE]` | Draws the Explorer menu for a folder (the desktop menu with `desktop`, the folder's own menu with `item`, a file's menu when given a file) with its real session hints and recent sessions into a PNG, the first expandable slice opened unless `idle`; `edge` places the menu against the left edge of the screen, `transparent` leaves out the background. |
| `--render-settings out.png [en\|tr]` | Draws the settings window (current Windows theme) into a PNG; a running Rondel is not touched. |

## Fixed identifiers

| What | Value |
|---|---|
| Settings folder | `%APPDATA%\Rondel` (macOS: `~/Library/Application Support/Rondel`) |
| Log folder | `%LOCALAPPDATA%\Rondel\logs` (macOS: `~/Library/Logs/Rondel`) |
| Single-instance mutex | `Local\Rondel.SingleInstance` (a second start opens Settings in the running instance) |
| Activation event | `Local\Rondel.Activate` |
| Start with Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Rondel` |
| Tag on re-injected input | `dwExtraInfo = 0x524E444C` ("RNDL") |
