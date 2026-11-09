# Tasks

Who is working on what, and how far each platform is. The phases and the reasons behind them are in [the roadmap](https://github.com/xelfiera-shn/rondel/blob/dev/docs/roadmap.md); the macOS plan is in [macos.md](https://github.com/xelfiera-shn/rondel/blob/dev/docs/macos.md).

This list lives on its own branch, `tasks`, which holds only this file. Anyone with write access edits it directly, without a pull request or an approval. The branch is never merged into `dev`, pushing to it runs no build and makes no release, and its history cannot be rewritten or deleted.

## How to use this list

| Mark | Meaning |
|---|---|
| ⬜ | Open: nobody has taken it |
| 🔄 | In progress: followed by the GitHub user name of whoever took it |
| ✅ | Done: followed by the pull request, or the version it shipped in |
| ⏸️ | On hold: parked or waiting on something; the note says why |
| ❌ | Dropped: not going to happen; the note says why |
| — | Does not apply to this platform |

1. **Take a task:** set its mark to 🔄, add your GitHub user name (`🔄 burakseyitkara1`) and commit straight to `tasks`, for example as `chore(todo): take W-5`.
2. **Work on it** in a branch from `dev` as usual and name the task in the pull request (`Task: W-5`).
3. **Finish it** once the pull request is merged: set the mark to ✅ with its number (`✅ #41`).
4. **Stop:** set the mark back to ⬜ and remove your name, or use ⏸️ with a reason.
5. **A new task** gets the next free ID of its list. IDs are never reused or renumbered, so pull requests and commits can refer to them.
6. **Shared features** are taken per platform: every platform's cell has its own mark and name.

On GitHub: switch to the `tasks` branch, open this file, edit it and choose **Commit directly to the `tasks` branch**. With git, a separate folder keeps it apart from the code: `git worktree add ../rondel-tasks tasks`, then pull before you edit and push after.

Done rows stay, so the lists also show what each platform already has. Change only the rows you touch and don't pad the tables to line up; two people editing different rows then rarely conflict.

## Shared features

What every platform gets, with a mark per platform. A Linux column is added when Linux work starts.

| ID | Feature | Windows | macOS | Notes |
|---|---|---|---|---|
| C-1 | Holding the mouse with the modifier opens the menu on the desktop and in folder windows; ordinary clicks and drags with the modifier keep working | ✅ 0.1.0 | ✅ 0.8.0 | Ctrl + hold on Windows, Option + hold on macOS |
| C-2 | Menus by place: desktop, folder window, folder, file | ✅ 0.3.0 | ✅ 0.8.0 | The file menu came in 0.3.0 |
| C-3 | Open, Continue and Resume, with session hints (when, how many; dimmed when there are none) | ✅ 0.1.0 | ✅ 0.8.0 | |
| C-4 | Resume opens into the five newest sessions | ✅ 0.1.0 | ✅ 0.8.0 | Only with two or more sessions |
| C-5 | Recent Projects on the desktop, opening into projects and their sessions | ✅ 0.2.0 | ✅ 0.8.0 | |
| C-6 | Ask, Ask Here and Ask About This File (`@file` mention) | ✅ 0.3.0 | ✅ 0.8.0 | |
| C-7 | New Project card: prompt, `git init`, plan mode and combinable templates; remembers the last choice | ✅ 0.4.0 | ✅ 0.8.0 | |
| C-8 | Themes, dark and light mode, accent colors | ✅ 0.5.0 | ✅ 0.8.0 | |
| C-9 | Keyboard in the menu: 1–N, arrows, Enter, Tab, Esc | ✅ 0.1.0 | ✅ 0.8.0 | |
| C-10 | Long project names fit their tiles | ✅ 0.3.0 | ✅ 0.8.0 | |
| C-11 | Tray icon; starting Rondel again opens Settings | ✅ 0.1.0 | ✅ 0.8.0 | A menu bar icon without a Dock icon on macOS |
| C-12 | Settings window | ✅ 0.1.0 | ✅ 0.8.0 | |
| C-13 | Keyboard shortcut that opens the menu at the mouse cursor | ✅ 0.1.0 | 🔄 burakseyitkara1 | Mac: none by default; recorded in Settings, ⌃⌥Space suggested |
| C-14 | Start at sign-in | ✅ 0.1.0 | ✅ 0.8.0 | Run key on Windows, `SMAppService` on macOS |
| C-15 | Choice of terminal | ✅ 0.1.0 | ✅ 0.8.0 | Windows Terminal or console window; Terminal.app or iTerm2 |
| C-16 | Menus, actions and agents in `settings.json`, applied when the file is saved | ✅ 0.1.0 | ✅ 0.8.0 | |
| C-17 | English and Turkish | ✅ 0.1.0 | ✅ 0.8.0 | |
| C-18 | Diagnostics: debug log, `--probe`, `--render-menu` | ✅ 0.1.0 | ✅ 0.8.0 | |
| C-19 | A modifier key for an alternate mode (for example Shift for plan mode) | ⬜ | ⬜ | |
| C-20 | Ask about several selected files | ✅ 0.10.0 | ✅ 0.15.0 | |
| C-21 | Updates: a daily check (on by default, also when the connection comes back), asking before updating, and the update itself from within the app, verified with Rondel's release key | ✅ 0.7.0 | ✅ 0.15.0 | Windows: the exe replaces itself and restarts. macOS picks its own way with M-7 |
| C-22 | First-run hint that explains the gesture | ✅ 0.11.0 | ✅ 0.15.0 | macOS: the welcome window also asks for Accessibility and Finder |
| C-23 | Pick up where you left off: a desktop slice that resumes the latest project's last session | ⏸️ | ⏸️ | Parked; to be decided later |
| C-24 | Active agents: a notification when an agent asks something or finishes, and clicking it brings that session's terminal to the front; an Active Agents arc on the desktop with a status dot per session (working, waiting for you, done) and a shortcut that opens it anywhere | ✅ 0.6.0 | 🔄 burakseyitkara1 | Sessions started from Rondel, through Claude Code's hooks. Spike first: hooks reaching Rondel, bringing the right terminal window to the front |
| C-25 | Agents card: an optional floating card (on in Settings, with a shortcut) that lists the active agents with their state, how long and what they wait for; a click brings an agent's terminal to the front. It can be dragged anywhere and remembers its place per monitor. It collapses to Rondel's mark with the number of waiting agents as a badge and opens while the mouse is over it. On the card, a pin keeps it open, and a menu button next to it opens quick settings: on top of other windows or on the desktop (like Sticky Notes), and more. Over full-screen apps it hides, unless Settings keeps it there; then it stays open (a game has the focus, so it could not be opened), never takes the focus and lets clicks through. It appears when the first agent starts and hides when none is left. A notification setting turns notifications off while the card is shown. | ✅ 0.9.0 | 🔄 burakseyitkara1 | Windows first, on top of C-24; macOS once it follows agents |
| C-26 | Color picker for the custom accent colors: in Settings → Appearance, each of the two colors (start and end of the gradient) can be picked from a palette instead of typing a hex code; the hex box stays and follows the picker | ✅ 0.10.0 | ✅ 0.15.0 | WPF has no color picker of its own; Avalonia has one (`Avalonia.Controls.ColorPicker`) |
| C-27 | Fix: one key, one job in the menu. Tab (Shift+Tab back) only moves between slices, and selecting an expandable slice with the keyboard no longer opens its arc. Space only opens the arc of the selected slice and steps in; on a slice without one it does nothing. Enter only runs the selected slice; on Recent Projects and Active Agents, which run nothing themselves, it does nothing. The mouse and the number keys are unchanged | ✅ 0.10.0 | ✅ 0.15.0 | Today Tab on Resume, Recent Projects or Active Agents opens the arc and moves into it, Space runs a slice like Enter, and Enter on Recent Projects steps into it. The README describes the keys |
| C-28 | Saved prompts, turned on in Settings (off by default; off, Ask is unchanged): every `.md` file in `%APPDATA%\Rondel\Prompts` is a prompt, named after its file, and the folder starts empty. Up to five prompts pinned in Settings sit on an arc around the Ask slices (Ask, Ask Here, Ask About This File and These Files), in the order they were pinned; picking one starts the agent with it at once. Clicking an Ask slice opens the prompt card as today, now with a list of every saved prompt: picking one fills the box, to edit before starting. On a file, its mention still comes first | ✅ 0.11.0 | 🔄 burakseyitkara1 | Moved here from the ideas |
| C-29 | Session cleanup: a Sessions window (tray menu and Settings) lists every saved Claude Code session by project, with its title, last activity and size on disk, sorted by size or date, with a search and filters (older than some days, projects whose folder is gone). Selected sessions are deleted with everything that belongs to them (transcript, subagent transcripts, tool results, file history, debug logs, temporary images and scratchpad) after a confirmation that says how much space comes free; they leave Rondel's menu (Resume, Recent Projects, hints) and Claude Code's own resume list at once. Running sessions cannot be selected. Deleted files go to the Recycle Bin unless the confirmation's "Delete permanently" is ticked. A project's auto memory (`memory/`) stays, unless its own "Delete the memory too" is chosen | ✅ 0.12.0 | 🔄 burakseyitkara1 | Claude Code deletes sessions only after 30 days (`cleanupPeriodDays`), and `claude purge` only a whole project; the window shows that period without changing Claude Code's settings |
| C-30 | Quick Conversation that leaves no trace: a slice of the desktop menu (added to untouched desktop menus by a migration) starts the agent in a folder of its own (`%LOCALAPPDATA%\Rondel\Quick`) with `CLAUDE_CODE_SKIP_PROMPT_HISTORY=1`, so no transcript or prompt history is written. It opens the prompt card first, or the bare terminal when that is turned off in Settings. When the session ends (SessionEnd, or its terminal closes), Rondel deletes what is left of it for good (temporary folder, file history, environment, memory), and sweeps the folder again at its next start and before each new one. It never shows in Recent Projects, Resume or the Sessions window | ✅ 0.13.0 | ✅ 0.15.0 | Local only: Anthropic's own retention follows the account. Claude Code asks once to trust the folder and keeps a small entry for it in `~/.claude.json` |
| C-31 | Ask About a Screenshot (TR Ekran Görüntüsünü Sor): a keyboard shortcut (none by default; the user picks one in Settings) or the tray menu freezes the screen. Drag a region, click a window, or Enter for the whole screen; Esc cancels, and Rondel's own windows are hidden first. The Quick Conversation card opens with a preview of the shot (always, even with the card turned off for Quick Conversation) and starts a Quick Conversation with the image in the first message (`@file`). The image never touches the clipboard: it is written to the Quick Conversation folder and goes with the sweep, or at once when the card is cancelled | ✅ 0.14.0 | ⬜ | Nothing is sent before Ctrl+Enter. macOS needs the screen recording permission |
| C-32 | Quick Conversation answers in the card: its first question is answered right in the prompt card, without a terminal (Claude Code in print mode, `-p`, streaming), with the text shown as it is written (Markdown: paragraphs, lists, emphasis, code blocks). Under the answer: **Copy**, **Close** and **Continue in terminal**, which opens the conversation in a terminal (`--resume`) where it goes on as a Quick Conversation; there is no follow-up question in the card. In the card Claude may only read and search the web (no commands, no edits, no permission prompts). Settings → Quick Conversation picks the model (Claude Code's default, or Opus, Sonnet, Haiku). Ask About a Screenshot answers the same way. With the card turned off, the agent starts in a terminal as today. What the answer writes goes with the sweep, also when the card is closed | ✅ 0.16.0 | 🔄 burakseyitkara1 | Not `--bare` (it ignores the subscription login). Spike 2026-11-05: first text ~2 s; `-p` writes the transcript but no prompt history and no `~/.claude.json` entry; `--resume` with `CLAUDE_CODE_SKIP_PROMPT_HISTORY=1` continues it without writing; the sweep removes it |

## Windows

| ID | Task | Status | Notes |
|---|---|---|---|
| W-1 | Release builds: single-file x64 and ARM64 executables, checksums, build provenance | ✅ 0.1.0 | |
| W-2 | File Explorer tabs, monitors with different scaling; File Explorer running as administrator is skipped | ✅ 0.1.0 | |
| W-3 | Choice of shell: PowerShell 7 or Windows PowerShell | ✅ 0.1.0 | |
| W-4 | Lighter menu on battery (no animations) | ✅ 0.2.0 | |
| W-5 | WSL folders (`\\wsl.localhost\…`) | ⬜ | |
| W-6 | Code signing (SignPath for open source, or Azure Trusted Signing) | ⏸️ | So that SmartScreen stops warning. Parked: no budget for a paid certificate. SignPath Foundation signs open source projects for free (MIT and a public repo qualify; the publisher shown is SignPath Foundation; MFA for everyone on the team; an application that must be approved) |
| W-7 | winget manifest | ⬜ | |
| W-8 | Run the ARM64 build on ARM hardware | ⬜ | It is built and released, but has not run on ARM yet |

## macOS

Kept by the macOS side. The first rows are the steps of [macos.md](https://github.com/xelfiera-shn/rondel/blob/dev/docs/macos.md#steps); split them or add rows as the work needs.

| ID | Task | Status | Notes |
|---|---|---|---|
| M-1 | Plan | ✅ #21 | |
| M-2 | Spike | ✅ #32 | |
| M-3 | Core: POSIX launch script, `MacFolderNameRules`, terminal values and strings, source-generated settings JSON | ✅ #34, #36 | |
| M-4 | Menu bar icon, trigger and Finder detection, `--probe` | ✅ #41 | |
| M-5 | Menu window, `--render-menu` | ✅ #45, #50, #51 | A fallback when the non-activating panel API is missing (review of #32) |
| M-6 | Launching and the settings window | ✅ #49, #52 | |
| M-7 | Distribution: signing with the release certificate, the release job, the macOS part of the README | ✅ #53 | |
| M-8 | Signing with a Developer ID and notarizing, so a downloaded copy opens after a single confirmation | ✅ #81 | The certificate and the notary key come from burakseyitkara1's Apple Developer membership (team R2333A9MFR); the certificate secrets change in the same session as the merge |

## Ideas

Not agreed yet. Once an idea is agreed, it moves into one of the lists above with an ID.

- Parallel sessions: start in a git worktree or in the background, and open the agents view.
- More agent profiles: Codex CLI, Gemini CLI.
- A menu editor in Settings, instead of editing `settings.json`.
- Ask with the clipboard or a screenshot.
- Open the folder in an editor (VS Code).
- Answer a permission request from the agents card (Allow / Deny) through Claude Code's PermissionRequest hook: optional, and it shows exactly what would be allowed.
