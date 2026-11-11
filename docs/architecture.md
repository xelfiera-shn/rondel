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
| `src/Rondel.MacOS` | `net10.0` (Avalonia, NativeAOT) | `Rondel.app` ([macos.md](macos.md)): event tap, Finder detection (window list, Accessibility, Apple Events), menu bar extra, the radial menu with its arcs and input cards, launching in Terminal.app or iTerm2, the settings, welcome and update windows. |
| `tests/Rondel.Core.Tests` | `net10.0` | Unit tests for core logic that can break silently. |

```
src/Rondel.Core/
  Activity/       AgentActivity, ActiveAgent, HookEvent, HookServer (following the agents Rondel starts)
  Agents/         AgentProfile, AgentCapability, BuiltInAgents, ClaudeCode/ (session store)
  Context/        TriggerContext, ContextKind
  Flow/           MenuFlow, MenuDetails, InputSpec
  Menus/          ActionDefinition, MenuBuilder, MenuModel
  Launch/         LaunchPlan, LaunchPlanner, CommandLine, PowerShellScript, FolderNameRules, PosixShellScript, MacFolderNameRules
  Sessions/       ISessionStore, SessionStores, SessionHints, RelativeTime
  Settings/       AppSettings (schema and migrations), SettingsJson, SettingsStore
  Themes/         MenuTheme, BuiltInThemes, AppearanceOptions
  Localization/   Loc, string tables (English, Turkish)
  Logging/        Log
  Platform/       IMenuHost, ITerminalLauncher, IFolderNameValidator, IAutostart
src/Rondel.Windows/
  Hook/ Interop/ Detection/ Shell/ Launch/ Settings/ UI/
src/Rondel.MacOS/
  Trigger/ Interop/ Detection/ UI/
```

The core builds and its tests run on Windows and macOS (CI checks both); the whole solution compiles on macOS and Linux too.

## Shared flow and platform interfaces

Each platform app owns the trigger, context detection and the menu window; everything between them is shared:

1. The platform turns a gesture or hotkey into a `TriggerContext`. With nothing useful under the pointer, the hotkey falls back to `MenuFlow.ProjectsContext`.
2. `MenuFlow.Build` returns the menu for the context (null when none is configured), and the platform shows it.
3. `MenuDetails.Load` reads the session stores off the UI thread; the platform fills in the hints and the items that open further when they arrive. `MenuDetails.Needed` says whether there is anything to look up.
4. The platform reports the chosen item to `MenuFlow.Invoke`, which starts the agent or asks for a folder name (and a prompt) first, creates the folder and launches.

| Interface | What the platform provides | Windows | macOS |
|---|---|---|---|
| `IMenuHost` | Closing the menu, the inline name/prompt card, the New Project card, warnings outside the menu | `AppHost` (radial menu window, tray balloon) | `AppHost` (radial menu window, a warning in the menu bar menu) |
| `ITerminalLauncher` | Running a `LaunchPlan` in a terminal | `WindowsTerminalLauncher` | `MacTerminalLauncher` (a `.command` file opened in Terminal.app or iTerm2) |
| `IFolderNameValidator` | The file system's rules for a new folder name | `FolderNameValidator` (`FolderNameRules`, long paths, write permission) | `MacFolderNameValidator` (`MacFolderNameRules`, write permission) |
| `IAutostart` | Starting with the user's session | `Autostart` (Run key) | `Autostart` (`SMAppService`, open at login) |

Some core parts carry one platform's rules because they are pure and worth testing in the core: `FolderNameRules` and `PowerShellScript` for Windows, `MacFolderNameRules` and `PosixShellScript` for macOS. The `Terminal` setting has values for both platforms (`WindowsTerminal`, `Console`; `TerminalApp`, `ITerm`), and each platform treats the other's as `Auto`; the `Shell` setting lists Windows choices.

## Threads (Windows)

| Thread | Responsibility |
|---|---|
| Hook thread | Low-level mouse hook and the gesture state machine. Only cheap checks; decides whether a press is swallowed or given back. |
| Detection thread (STA) | UI Automation and Shell COM calls that turn a screen point into a `TriggerContext`. |
| UI thread (WPF) | Radial menu, settings window, tray icon, launching. |

Detection starts as soon as a Ctrl+press is swallowed, while the hold timer runs in parallel, so the menu can open the moment the hold threshold is reached. Session hints are looked up after the menu is shown and filled in when they arrive; they never delay the menu.

## Threads (macOS)

| Thread | Responsibility |
|---|---|
| Tap thread | The `CGEventTap` and its run loop. Reads only the flags and Rondel's tag, swallows or gives back; the gesture state is shared with the hold timers under a lock. |
| Detection thread | The window list and the Accessibility requests that turn a point into a `TriggerContext`; Apple Events to Finder are sent from the main thread (`NSAppleScript` is not thread-safe). |
| Main thread (Avalonia) | Menu bar extra, permissions, settings, the menu window and launching; Apple Events (`AppleScript`) to Finder, Terminal and iTerm2. |

## Contexts

`ContextKind` is one of `None`, `DesktopEmpty`, `ExplorerEmpty`, `FolderItem` or `FileItem`. A `TriggerContext` carries the kind, the target folder (for a file, the folder it is in, with the file in `FilePath`, and with `SelectedFiles` when it was held in a selection of several files: the selected files in its folder, see `FileSelection`), the name shown in the menu center, a short reason for the log and, in debug mode, the detection trace. `None` means "not ours": the click is passed through unchanged.

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
| `Plan` | `--permission-mode plan` |

Menu actions refer to a **capability**, not to a command line. An action uses the default agent unless it names one, and it may add extra arguments (for example `--permission-mode plan`). A slice is hidden when its agent does not support the capability.

An action may ask for input first (`Input`): a new folder's name (`FolderName`), a name and then a prompt (`FolderNameAndPrompt`), only a prompt for the menu's folder (`Prompt`, the Ask slices), or the New Project card (`NewProject`). An action with `MentionFile` starts its prompt with the file the menu was opened on, in the agent's `FileMention` syntax; the agent starts in the file's folder, so `{file}` is the file's name, in double quotes unless it consists only of letters, digits and `._-()` (Claude Code reads `@"My Notes.md"` as one path). Such an action is hidden where there is no file or the agent has no `FileMention`.

**Saved prompts** come from `SavedPrompts`: every `.md` file in the prompts folder is one, named after its file and sent as written (a file over 64 KB is not read). With `Prompts.Enabled`, `MenuBuilder` gives the slices of `Prompt` actions the pinned names (`MenuItemModel.PinnedPrompts`); `MenuDetails.Load` looks their files up off the UI thread and opens the slice into those still there, in the order they were pinned (items with `SavedPrompt`), which start the agent at once, the file mentions first. The Ask card's `InputSpec` carries the folder and every prompt's name; picking one fills the box. A platform that passes no `SavedPrompts` to `MenuDetails.Load` (macOS, for now) shows no arc.

### New projects

The New Project card asks for the folder name and how the project starts; the options combine:

- **Prompt** starts the agent with the `Prompt` capability and the first prompt instead of `Open`.
- **Git** sets `LaunchPlan.InitGit`: the launch script runs `git init` in the new folder before the agent, and says so and goes on when git is missing.
- **Plan mode** puts the agent's `Plan` arguments in front of the others.
- **Templates** come from `ProjectTemplates`: every folder in the templates folder is one, and the chosen ones are copied into the new folder in the order they are offered (sorted by name), so a later template's file wins. A template's own `.git` folder and links inside it are left out.

The choice is saved as `NewProject` in the settings, so the card opens with it next time. `NewProjectCard` turns options off: an option that is off is not offered and not applied, whatever was chosen before.

### Claude Code session store

Claude Code keeps each session as `~/.claude/projects/<encoded folder>/<session id>.jsonl` (the root moves to `CLAUDE_CONFIG_DIR` when that variable is set).

- The folder encoding replaces every UTF-16 code unit outside `[A-Za-z0-9]` with `-`. Names longer than 200 characters are cut to 200 characters plus `-` and a hash, so they are matched by prefix.
- The encoding is lossy: `a.b` and `a-b` collide, and every Turkish letter becomes `-`. Each session file's first recorded `"cwd"` is therefore read (and cached) and compared case-insensitively with the target folder.
- If the store does not exist at all, the result is *unknown* and nothing is dimmed. If it exists but has no sessions for the folder, Continue and Resume are dimmed.
- `RecentFolders` lists the folders worked in most recently across the whole store (for Recent Projects), each with its session count and latest session. Folders that no longer exist and folders under the temp folder or Quick Conversation's folder are left out.
- The Resume slice and a recent project expand into their five newest sessions, but only when there are at least two. Their titles come from the session file: a title the user gave (`custom-title`), else Claude Code's latest generated title (`ai-title`), else an old-style `summary`, else the first real prompt. Only the first and last 256 KB of a file are read.
- This is an internal format of another program: any failure means "no hints", never an error.

### Deleting sessions

`ClaudeCodeStorage` lists every project folder of the store with its sessions and auto memory for the Sessions window, and deletes what is asked for. A session is a GUID-named transcript and what Claude Code keeps under the same id: `<id>.orphaned-*.jsonl` and `<id>.jsonl.superseded-*` beside it, the `<id>/` folder (subagents, tool results, MCP tasks), `file-history`, `session-env`, `image-cache`, `uploads`, `dev-mods`, `tasks` and `debug` entries for the id, and its temporary folder `%TEMP%\claude\<project>\<id>` (images, scratchpad and tasks; under `CLAUDE_CODE_TMPDIR` when set), where `<project>` is the encoded folder for older Claude Code versions and the first 16 hex digits of the SHA-256 of the encoded folder in lower case for newer ones; both are looked at. Its size is the sum of all of them. Nothing whose name is not a GUID is taken for a session, so no other path can be reached. A session is in use, and not deleted, while `~/.claude/sessions/<pid>.json` names it for a running process, or when its transcript was written to in the last two minutes; this is checked again just before deleting. `projects/<folder>/memory/` goes only when the project's memory is asked for, and a project folder (and its temporary folders) left empty is removed. The Recycle Bin is `SHFileOperation` with undo, on a thread of its own (STA, for Windows' question about files too large for the bin); on macOS the Trash is `NSFileManager trashItemAtURL` for each path (`Interop/Trash`), and the temporary folders are `/tmp/claude-<uid>`; without it the files are deleted for good. The window shows `cleanupPeriodDays` from `~/.claude/settings.json` (30 when unset) and never changes it. It leaves out Quick Conversation's folder.

**Quick Conversation** is an `Ephemeral` action. `MenuFlow` runs it in `AppInfo.QuickDirectory` whatever the menu's folder, opens the prompt card unless `Quick.PromptCard` is off, sets the agent profile's `EphemeralEnvironment` in the plan (`CLAUDE_CODE_SKIP_PROMPT_HISTORY=1` for Claude Code: no transcript, no prompt history) and names the window `… · Quick Conversation (not saved)`. `MenuBuilder` leaves the action out for an agent without `EphemeralEnvironment`. `ClaudeCodeStorage.Sweep` then deletes for good what is left: the ids found under the folder's project and temporary folders, and those whose end was reported, each with everything `PathsOf` names. A reported end whose session another project keeps a transcript of is ignored: that session is not the folder's. A session is kept while a running Claude Code has it open, while Rondel still follows it, or while what it has was written in the last minute and its end was not reported (one that has just started may not be registered as running yet). The folder's memory goes once nothing was kept, and the emptied folders go too. So does what is in the folder itself (files Claude wrote there, a `.claude` folder of permissions), once nothing was kept, no followed session works there and no running Claude Code has it as its folder; an entry written in the last minute stays unless an end was reported. The folder itself stays. Windows sweeps when a followed session ends, at its start and before each Quick Conversation; macOS, which does not follow agents yet, sweeps at its start, before each Quick Conversation and from the first one on every two minutes (`QuickSweeper`), so what a session leaves goes within a few minutes of its end. It also names Claude Code's temporary folder there, `/tmp/claude-<uid>`, which the core knows only on Windows; a `CLAUDE_CODE_TMPDIR` set in the shell does not reach Rondel, so a folder under it stays. Without `~/.claude/sessions` (or under `CLAUDE_CONFIG_DIR`) nothing tells a running Quick Conversation from one that ended (on Windows, one Rondel does not follow), so neither platform sweeps then. Claude Code's `~/.claude.json` keeps an entry for the folder (its trust, the last session's id and figures); Rondel never writes that file.

**The first answer on a card** (Windows). When `MenuFlow` was given an `IAnswerLauncher` and the agent profile has `AnswerArgs` (Claude Code: `-p --output-format stream-json --verbose --include-partial-messages --tools Read,WebSearch,WebFetch --allowedTools WebSearch,WebFetch --permission-mode default`, with `ModelArgs` for `Quick.Model`; not `--bare`, which ignores the subscription login; Manual mode, not `dontAsk`, because a session continued in a terminal resumes in the mode it ran in, and print mode denies whatever would ask anyway), the prompt card's question does not start a terminal: `QuickAnswer` runs the agent without a window, the question on its standard input, and reads its stream-json events (`ClaudeCodeAnswerStream`: the session id, text pieces, tools, the result). The platform shows it on a card of its own (`AnswerCardWindow`, topmost, not activated when it opens, dragged by its header) and the prompt card closes, so nothing waits for the answer. This first step writes its transcript (no `EphemeralEnvironment`), so it can be resumed: **Continue in terminal** starts `ResumeById` for that session as a Quick Conversation (with the ephemeral environment and the model), and a failed answer offers the question in a terminal instead. While a card is open, its session is in `MenuFlow.QuickSessions` and the folder counts as in use for the sweep; closed, `QuickSessionEnded` lets the sweep take it at once, and one continued in a terminal stays kept until that session ends. The Windows launcher runs only a program found on PATH (`.exe`, or a `.cmd`/`.bat` such as npm's shim) with plain arguments; an alias or PowerShell syntax in the command starts in a terminal as before.

**Ask About a Screenshot** is `MenuFlow.AskAboutScreenshot(png)`: the platform picks the picture and hands over its PNG bytes, and the flow shows the Quick Conversation card by itself (`IMenuHost.ShowCard`) with `InputSpec.Image`, whatever `Quick.PromptCard` says. Until the card is submitted the picture is only in memory. Then it is written into the Quick Conversation folder as `Screenshot-<time>.png` and the agent starts as a Quick Conversation with the file mentioned first (`@Screenshot-….png …`); the sweep is raised after the agent is followed, so the new file stays while it runs. An agent without `EphemeralEnvironment`, the `Prompt` capability or a `FileMention` cannot do it.

## Settings

- `%APPDATA%\Rondel\settings.json` (macOS: `~/Library/Application Support/Rondel/settings.json`). The file is watched, so edits apply without a restart; saves are atomic, and an unreadable file is backed up before it is overwritten. Keys are read regardless of case; of two that differ only in case, the later one wins. A file that cannot be read or repaired means the defaults, never a failed start; and if starting fails anyway, Windows says so in a message and exits, instead of running on without its icon and holding the single-instance mutex.
- Comments and trailing commas are accepted. Non-ASCII characters are written as they are, so Turkish paths stay readable. Icons are Segoe Fluent Icons code points in hex (`"E8F4"`).
- Enum values are written as names. A value this version does not know (written by a newer Rondel) reads as the default, with a warning in the log, instead of making the whole file unreadable.
- The serializer is generated at build time (`SettingsJsonContext`), without reflection, so the core works in a NativeAOT build; `IsAotCompatible` makes the build warn about anything that would break that.
- The schema is versioned and is at **7**. **Changing a default requires a migration**, applied only where the user still has the old default: version 2 added Recent Projects to the desktop menu, version 3 added Ask to the folder and Explorer menus, version 4 removed New + Prompt from them once the New Project card took it over (and gave built-in agents their `Plan` arguments), version 5 added Active Agents to the desktop menu and version 6 added Quick Conversation to it, each only where that menu was still the previous default, and version 7 let the card's answer search the web (Claude Code's `AnswerArgs`, only where they were still the 0.16.0 default). A menu for a new context (`FileItem` in version 3) needs no migration: a missing menu gets its default.

Shape of version 7 (abridged):

```jsonc
{
  "SchemaVersion": 7,
  "Language": "auto",                       // "auto", "en" or "tr"
  "ProjectsRoot": "%USERPROFILE%\\Projects",       // "~/Projects" on macOS and Linux; ~ and %NAME% are expanded
  "HoldMs": 300,
  "Hotkey": "",                             // e.g. "Ctrl+Alt+Space"
  "Terminal": "Auto",                       // Auto, WindowsTerminal, Console (Windows); TerminalApp, ITerm (macOS)
  "Shell": "Auto",                          // Auto, Pwsh, WindowsPowerShell
  "DefaultAgent": "claude-code",
  "Agents": [
    { "Id": "claude-code", "Name": "Claude Code", "Command": "claude", "Sessions": "claude-code", "FileMention": "@{file}",
      "Capabilities": { "Open": [], "Continue": ["--continue"], "Prompt": ["{prompt}"], "Plan": ["--permission-mode", "plan"], "...": [] },
      "ActivityArgs": ["--settings", "{hooks}"],    // [] turns following off for the agent
      "EphemeralEnvironment": { "CLAUDE_CODE_SKIP_PROMPT_HISTORY": "1" } }   // Quick Conversation; without it the agent has none
  ],
  "NewProject": { "Prompt": true, "Git": true, "Plan": false, "Templates": ["Python"] },   // the last choice
  "NewProjectCard": { "Prompt": true, "Git": true, "Plan": true, "Templates": true },     // options the card offers
  "Appearance": { "Theme": "orbit", "Mode": "Auto", "AccentFrom": "#22D3EE", "AccentTo": "#3B82F6" },  // no accent: the theme's own
  "ActiveAgents": { "Follow": true, "NotifyAsks": true, "NotifyFinished": true, "Hotkey": "Ctrl+Alt+A" },
  "Updates": { "Check": true },              // the daily check; "Check now" works either way
  "Prompts": { "Enabled": true, "Pinned": ["Review my changes", "Write tests"] },  // saved prompts on the Ask slices' arc
  "Quick": { "PromptCard": true, "ScreenshotHotkey": "" },   // the card first; Ask About a Screenshot's shortcut (none by default)
  "Actions": [
    { "Id": "continue", "Label": "@Action.Continue", "Icon": "E768", "Capability": "Continue", "Hint": "LastSession" },
    { "Id": "plan", "Label": "Plan", "Icon": "E8FD", "Capability": "Open", "ExtraArgs": ["--permission-mode", "plan"] },
    { "Id": "recents", "Label": "@Action.Recents", "Icon": "E823", "Capability": "ResumeById", "Source": "RecentProjects" },
    { "Id": "agents", "Label": "@Action.ActiveAgents", "Icon": "E99A", "Capability": "Open", "Source": "ActiveAgents" },
    { "Id": "quick", "Label": "@Action.Quick", "Icon": "E945", "Input": "Prompt", "Capability": "Prompt", "Ephemeral": true },
    { "Id": "ask-file", "Label": "@Action.AskFile", "Icon": "E8F2", "Input": "Prompt", "Capability": "Prompt", "MentionFile": true }
  ],
  "Menus": {
    "DesktopEmpty": ["new-project", "recents", "agents", "quick"],
    "ExplorerEmpty": ["new-project", "open-here", "ask-here", "continue-here", "resume-here"],
    "FolderItem": ["open", "ask", "continue", "resume"],
    "FileItem": ["ask-file", "open-here", "continue-here", "resume-here"]
  }
}
```

Labels starting with `@` are built-in translated strings; anything else is shown as written.

## Themes

The menu's look is data in the core, so every platform draws the same themes. A `MenuTheme` describes:

- the tile shape: sectors, rounded petals, or none (icons float over the ring's base);
- the highlight: a wash with a lit edge, an accent fill, a lit outline, or a soft glow behind the icon;
- a decoration (the orbit: an accent ring broken at every slice by a tick), gaps, corner radius, the hub ring;
- a `ThemePalette` for dark and one for light mode, colors as 0xAARRGGBB.

Geometry (radii, arcs, where labels go) is the same for every theme, so a theme never moves anything. `BuiltInThemes` has Rondel's own look and four others; `AppearanceOptions` in the settings picks one, a mode (`Auto` follows the system) and optionally the accent pair. The ring's base must never be fully transparent: the menu's window only catches the mouse where it is drawn.

## Launching

The core turns an action into a `LaunchPlan`: the folder, the agent command, the literal argument list, the variables set for the agent and the window title. The Windows project executes the plan as a single PowerShell script in Windows Terminal (or a console window when Windows Terminal is missing); the escaping rules are described in [windows-notes.md](windows-notes.md#launching). On macOS, `PosixShellScript` turns the plan into a `.command` file for Terminal.app or iTerm2 (see [macos.md](macos.md#launching-in-a-terminal)).

## Following agents

Rondel follows the agents it starts, so it can say when one asks for something or is done, and bring its terminal to the front.

- **Hooks.** An agent profile's `ActivityArgs` make the agent report its events; for Claude Code that is `--settings <hooks file>`. The file (`agent-hooks.json` next to `settings.json`) adds an HTTP hook for each followed event, which runs next to the user's own hooks; the user's Claude Code settings are not changed. An agent without `ActivityArgs` starts as before.
- **Which session.** The launch script sets `RONDEL_SESSION` to the session's id, and the hooks send it back in a header (`allowedEnvVars`). The platform launcher also names the terminal window after it. On macOS, which does not let one process read another's environment, the launcher puts a first line into the script that writes its tty to `~/Library/Caches/Rondel/agents/<id>.tty`, with the terminal app next to it, so a restarted Rondel still finds the tab (`AgentTerminals`); bringing the agent to the front asks Terminal or iTerm2 through AppleScript for the tab on that tty (the Automation permission for that terminal, asked the first time), and a tab no process uses any more counts as closed (`ps`, every 15 s).
- **The server.** `HookServer` listens on the loopback address only and accepts a request only with the token from the hooks file, in a header, so a web page can neither guess it nor send one (a custom header needs a CORS preflight, which is never allowed). It answers at once with an empty body, which leaves every decision (a permission prompt, stopping) to the agent. The port and the token stay the same across restarts, so agents started before a restart keep reporting; when the port has been taken in between, the new one gets a new token, since whoever holds the old port has seen it; a session Rondel does not know yet is followed again from its folder. While Rondel is not running, Claude Code shows a hook error under each prompt and goes on.
- **The menu.** An action with `"Source": "ActiveAgents"` (the desktop menu's Active Agents slice) opens into the followed agents, those waiting for the user first (`MenuBuilder.AgentItems`); its line counts the waiting ones, with a dot in the most urgent state's color. Choosing an agent asks the platform to bring its terminal to the front (`IMenuHost.FocusAgent`). The Active Agents shortcut opens the desktop menu with that slice open (`MenuFlow.AgentsMenu`), adding the slice for that time if the menu has none.
- **The card (Windows).** `AgentsCard` shows `AgentsCardWindow` while the card is turned on and an agent runs (its shortcut also shows it with none). It never takes the focus (`WS_EX_NOACTIVATE`) and is not in Alt+Tab, so its quick settings close by themselves when the mouse leaves them or another window comes to the front. A card the user hid comes back with its shortcut, the tray menu's **Show the agents card**, or the next agents. Collapsed, it is a badge counting the agents that need the user (waiting or failed; red as soon as one failed); it opens while the mouse is over it, toward the free side of the screen, and stays open when pinned. On top of other windows it is topmost; on the desktop it stays at the bottom of the z-order and is raised while the desktop is in front (Win+D), which a foreground WinEvent hook tells. Over a full-screen app (`SHQueryUserNotificationState`, checked on every foreground change and every two seconds) it hides, or, with **Show it over full-screen apps too**, stays open and click-through (`WS_EX_TRANSPARENT`). Its options are in `ActiveAgents.Card` in settings.json; its place on each monitor is in `%LOCALAPPDATA%\Rondel\agents-card.json`. While it is shown, notifications are skipped if `MuteNotifications` is on.
- **The card (macOS).** `AgentsCard` keeps the Windows behavior (it appears with the first agent, collapses to its badge, opens toward the free side, keeps its place per screen in `agents-card.json` next to `settings.json`). A click on it neither activates Rondel nor makes it the key window (`MenuPanel.ConfigureCard`), so the keyboard stays with the app in front; notifications are kept quiet only while macOS draws some of it (`occlusionState`); above other windows it is at the floating level, on the desktop right above the desktop icons (`MenuPanel.SetCardLayer`), on every Space, and over full-screen apps only with **Also over full-screen apps** (`fullScreenAuxiliary`), so there is no full-screen detection or click-through. Its options open inside the card, where its agents were: a menu of its own would need the focus the card never takes. A drag is the window's own (`BeginMoveDrag`); its place is kept once it rests. No shortcut yet.
- **States.** `AgentActivity` turns events into a state per session: a prompt or a tool at work → **working**; a permission request or a question (`AskUserQuestion`) → **waiting**; the end of a turn (`Stop`) → **ready**; an error (`StopFailure`) → **failed**. A permission is announced by Claude Code's own reminder a few seconds later, so a prompt the user answers at once is not announced; a question is announced at once. `SessionEnd` ends following, except for `/clear` and `/resume`, which go on in the same window. Claude Code sends no `SessionStart` to these hooks, so Rondel follows a session from the moment it starts it.

## Updates

- **Checking.** `UpdateService` asks GitHub's API for the latest release about a minute after Rondel starts, then once a day, and soon after the connection comes back when a check is due; a failed check is tried again at the next hourly tick. The request carries only Rondel's version, in its User-Agent. Local and CI test builds (0.0.0) never check. What the last check found, the version the user skipped and the last announcement are kept in `%LOCALAPPDATA%\Rondel\updates\state.json` (macOS: `update-state.json` in the settings folder), not in settings.json. A newer release is announced once a day until the user skips it; **Check now** in Settings always offers it. A release whose notes have only a macOS section (the release workflow puts `macos`-scoped features and fixes there) does not concern Windows: it is neither announced nor offered in the tray, only shown by **Check now**. The macOS app does the same with a release whose every line is `windows`-scoped (`ReleaseNotes.OnlyScoped`), and leaves those lines out of the notes it shows.
- **Trust.** The release workflow signs `SHA256SUMS.txt` with Rondel's release key (ECDSA P-256 with SHA-256) into `SHA256SUMS.txt.sig`. The private key is the `RELEASE_SIGNING_KEY` secret of the `release` environment, which only the `prod` branch may use; its public half is `src/Rondel.Core/Updates/release-key.pem`, built into the app. `UpdateDownloader` takes a file only when that signature is valid and the file matches its checksum (hashed while it arrives); anything else is deleted. A file the app downloads carries no mark of the web, so SmartScreen never looks at it: this check takes its place.
- **Replacing the exe (Windows).** `SelfUpdate` renames the running exe to `Rondel.exe.old` (a running exe can be renamed, but not overwritten), moves the new one to its path and starts it with `--after-update <process id> <old version>`. The new version signals `Local\Rondel.UpdateStarted`, waits for the old one to exit, starts as usual and deletes the `.old` file; the old one exits as soon as it hears the signal, or puts itself back when the new one does not start within 30 seconds. The path stays the same, so the Start-with-Windows entry and shortcuts keep working. When Rondel's folder is not writable, the update window offers the release page instead.
- **Replacing the app (macOS).** `MacSelfUpdate` unpacks the release's zip (`Rondel-<version>-osx-arm64.zip` or `-osx-x64.zip`) in `~/Library/Caches/Rondel/updates`, checks the new Rondel.app with `codesign --verify --strict --deep` and against the running app's designated requirement (the same signer, so macOS keeps the permissions), and starts a small script that waits for Rondel to exit, swaps the two bundles and opens the new one with `--updated-from <old version>`. Rondel downloads the zip itself, so it carries no quarantine and Gatekeeper does not ask again. A bundle macOS runs from a read-only place (an app opened from Downloads without moving it) or a folder Rondel may not write in gets the release page instead. The announcement is a notification (`Interop/Notifications`, UserNotifications); when Rondel may not notify, the update window opens instead. A click on the notification reopens the app too, which is not taken for "open Settings".
- **Testing.** `RONDEL_UPDATE_FEED` names another "latest release" address, such as a local test feed (http is allowed on the loopback address only). What it offers is still taken only with the release key's signature.

## Localization

English and Turkish string tables live in the core. The language follows the Windows display language unless the user picks one in Settings; switching applies immediately.

## Logging and diagnostics

Logs go to `%LOCALAPPDATA%\Rondel\logs` (macOS: `~/Library/Logs/Rondel`): one file per day, rolled at 5 MB, the newest 20 kept. Writing happens on a background task, so logging is safe from the hook thread. With **Debug logging** on, every trigger writes the UI Automation chain, the detected context and the resolved folder.

Command-line diagnostics run instead of the tray app, never touch a running instance and write their result to a file:

| Switch | Result |
|---|---|
| `--probe X Y out.txt` | Classifies a screen point (physical pixels; points on macOS) exactly like a trigger would, with the full trace. On macOS start it through `open -n -W Rondel.app --args --probe …`, so that Rondel's permissions apply, not the terminal's. |
| `--sessions FOLDER out.txt` | The session hints the menu would show for a folder. |
| `--launch-script FOLDER COMMAND prompt.txt out.ps1` | The PowerShell script a launch would run. |
| `--launch-now FOLDER COMMAND prompt.txt` | Launches like the menu does, with COMMAND instead of the configured one. |
| `--export-icon out.ico` | Writes the application icon. |
| `--show-menu [input [text]]` | Starts normally and opens the menu at the cursor without the gesture; `input` continues into the New + Prompt card. |
| `--render-menu out.png [dark\|light] [en\|tr] [desktop\|item] [idle] [deep] [edge] [transparent] [theme=ID] [accent=#RRGGBB[,#RRGGBB]] [hover=N] [agents] [prompts] [FOLDER\|FILE...]` | Draws the Explorer menu for a folder (the desktop menu with `desktop`, the folder's own menu with `item`, a file's menu when given a file, held in a selection of them when given several) with its real session hints and recent sessions into a PNG, the first expandable slice opened unless `idle`; `edge` places the menu against the left edge of the screen, `transparent` leaves out the background; `theme`/`accent` pick a theme and accent, `hover=N` only highlights slice N, `agents` opens the Active Agents slice into made-up agents in every state, `prompts` pins made-up saved prompts to the Ask slices. |
| `--render-settings out.png [en\|tr] [bottom\|scroll=DIP] [picker] [prompts]` | Draws the settings window (current Windows theme) into a PNG, scrolled to the end or by DIP; `picker` sets a custom accent and draws its color picker open; `prompts` turns saved prompts on with made-up ones, scrolled to them. It works on a copy of settings.json: a running Rondel, and the real file, are not touched. On macOS it takes `[dark\|light] [en\|tr] [custom]` and draws the whole page; `custom` sets a custom accent, with its color dots. |
| `--render-card out.png [dark\|light] [en\|tr] [open] [theme=ID] [accent=…]` | Draws the agents card with made-up agents: open, collapsed, its quick settings and empty side by side, or with `open` the open card alone (the README's picture). On macOS it takes `[dark\|light] [en\|tr] [collapsed] [options] [empty]` (`options`: the card's own options open), without the shadows. |
| `--card-demo [dark\|light] [en\|tr] [desktop] [report.txt]` | The live agents card with made-up agents, its options in memory and its place in a temporary file, so the real settings are left alone; Settings… on the card ends it. With `report.txt` it runs a short script (collapsed, pinned open, on the desktop, no agents, its shortcut) and writes the window's state at each step. |
| `--render-notice-icons out.png` | Draws the icons of the notifications about agents and updates at 32, 48 and 64 px, on a dark and a light notification. |
| `--render-sessions out.png [dark\|light] [en\|tr] [confirm] [forgood]` | Draws the Sessions window with made-up projects and sessions, some selected, and with `confirm` the question before deleting (`forgood`: with Delete permanently ticked). The real sessions are never drawn: their titles are the user's. The same on macOS. |
| `--render-welcome out.png [dark\|light] [en\|tr] [stage] [t=SECONDS] [frames=N]` | Draws the welcome window at one moment of its picture (by default the still one shown when Windows' animations are off), with `stage` the picture alone, and with `frames=N` N moments over one loop (`out-000.png`, …). The README's `gesture-*.gif` are 72 such frames of `stage`, at three quarters of their size, with one palette for all frames and 100 ms each. On macOS it takes `[dark\|light] [en\|tr] [t=SECONDS]`, and the still moment is the one shown when Reduce Motion is on. |
| `--render-update out.png [dark\|light] [en\|tr] [working\|failed]` | Draws the update window for a made-up release: the offer, the download under way (`working`) or a failed download (`failed`). |
| `--install-update Rondel.app new.zip 0.13.0` (macOS) | Swaps a locally built zip into the bundle once the tool exits and opens it, as an update does. |
| `--export-app-icon out.icns` (macOS) | Writes the app icon, `build/Rondel.icns`, which `publish-macos.sh` puts into the bundle. |
| `--render-new out.png [dark\|light] [en\|tr] [prompt] [empty] [transparent] [theme=ID] [accent=…]` | Draws the New Project card with made-up templates (`prompt`: with Prompt on; `empty`: without templates). |
| `--render-ask out.png [dark\|light] [en\|tr] [empty] [picked] [quick] [shot] [transparent] [theme=ID] [accent=…]` | Draws the Ask card with made-up saved prompts (`empty`: none in the folder; `picked`: one of them picked into the box; `quick`: Quick Conversation's card; `shot`: Ask About a Screenshot's card with a made-up picture). |
| `--render-shot-picker out.png [dark\|light] [en\|tr] [drag]` | Draws the screen picker over a made-up desktop with a window outlined (`drag`: an area being dragged). |
| `--render-answer out.png [dark\|light] [en\|tr] [starting\|search\|writing\|done\|stopped\|failed] [shot]` | Draws a Quick Conversation's answer card with a made-up question and answer (Markdown with a code block, a list and a table), in the state given; `shot` adds a made-up screenshot. |

## Fixed identifiers

| What | Value |
|---|---|
| Settings folder | `%APPDATA%\Rondel` (macOS: `~/Library/Application Support/Rondel`) |
| Log folder | `%LOCALAPPDATA%\Rondel\logs` (macOS: `~/Library/Logs/Rondel`) |
| Templates folder | `Templates` in the settings folder |
| Saved prompts folder | `Prompts` in the settings folder |
| Quick Conversation folder | `%LOCALAPPDATA%\Rondel\Quick` (macOS: `~/Library/Application Support/Rondel/Quick`) |
| Single-instance mutex | `Local\Rondel.SingleInstance` (a second start opens Settings in the running instance) |
| Activation event | `Local\Rondel.Activate` |
| Start with Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Rondel` |
| Tag on re-injected input | `dwExtraInfo = 0x524E444C` ("RNDL"); on macOS `kCGEventSourceUserData` |
| Updates folder | `%LOCALAPPDATA%\Rondel\updates`: the checks' `state.json` and a download until it is installed (macOS: `~/Library/Caches/Rondel/updates` for the download, removed at the next start) |
| Update handshake event | `Local\Rondel.UpdateStarted` |
| Agents card's place | `%LOCALAPPDATA%\Rondel\agents-card.json` (per monitor) |
| Release key | public half `src/Rondel.Core/Updates/release-key.pem`; private half the `RELEASE_SIGNING_KEY` secret of the `release` environment |
| macOS bundle identifier | `app.rondel` |
| macOS single-instance lock | `.instance` in the settings folder |
