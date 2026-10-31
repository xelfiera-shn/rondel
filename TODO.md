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
| C-1 | Holding the mouse with the modifier opens the menu on the desktop and in folder windows; ordinary clicks and drags with the modifier keep working | ✅ 0.1.0 | ⬜ | Ctrl + hold on Windows, Option + hold on macOS |
| C-2 | Menus by place: desktop, folder window, folder, file | ✅ 0.3.0 | ⬜ | The file menu came in 0.3.0 |
| C-3 | Open, Continue and Resume, with session hints (when, how many; dimmed when there are none) | ✅ 0.1.0 | ⬜ | |
| C-4 | Resume opens into the five newest sessions | ✅ 0.1.0 | ⬜ | Only with two or more sessions |
| C-5 | Recent Projects on the desktop, opening into projects and their sessions | ✅ 0.2.0 | ⬜ | |
| C-6 | Ask, Ask Here and Ask About This File (`@file` mention) | ✅ 0.3.0 | ⬜ | |
| C-7 | New Project card: prompt, `git init`, plan mode and combinable templates; remembers the last choice | ✅ 0.4.0 | ⬜ | |
| C-8 | Themes, dark and light mode, accent colors | ✅ 0.5.0 | ⬜ | |
| C-9 | Keyboard in the menu: 1–N, arrows, Enter, Tab, Esc | ✅ 0.1.0 | ⬜ | |
| C-10 | Long project names fit their tiles | ✅ 0.3.0 | ⬜ | |
| C-11 | Tray icon; starting Rondel again opens Settings | ✅ 0.1.0 | ⬜ | A menu bar icon without a Dock icon on macOS |
| C-12 | Settings window | ✅ 0.1.0 | ⬜ | |
| C-13 | Keyboard shortcut that opens the menu at the mouse cursor | ✅ 0.1.0 | ⬜ | |
| C-14 | Start at sign-in | ✅ 0.1.0 | ⬜ | Run key on Windows, `SMAppService` on macOS |
| C-15 | Choice of terminal | ✅ 0.1.0 | ⬜ | Windows Terminal or console window; Terminal.app or iTerm2 |
| C-16 | Menus, actions and agents in `settings.json`, applied when the file is saved | ✅ 0.1.0 | ⬜ | |
| C-17 | English and Turkish | ✅ 0.1.0 | ⬜ | |
| C-18 | Diagnostics: debug log, `--probe`, `--render-menu` | ✅ 0.1.0 | ⬜ | |
| C-19 | A modifier key for an alternate mode (for example Shift for plan mode) | ⬜ | ⬜ | |
| C-20 | Ask about several selected files | ⬜ | ⬜ | |
| C-21 | Update notification | ⬜ | ⬜ | |
| C-22 | First-run hint that explains the gesture | ⬜ | ⬜ | |
| C-23 | Pick up where you left off: a desktop slice that resumes the latest project's last session | ⏸️ | ⏸️ | Parked; to be decided later |

## Windows

| ID | Task | Status | Notes |
|---|---|---|---|
| W-1 | Release builds: single-file x64 and ARM64 executables, checksums, build provenance | ✅ 0.1.0 | |
| W-2 | File Explorer tabs, monitors with different scaling; File Explorer running as administrator is skipped | ✅ 0.1.0 | |
| W-3 | Choice of shell: PowerShell 7 or Windows PowerShell | ✅ 0.1.0 | |
| W-4 | Lighter menu on battery (no animations) | ✅ 0.2.0 | |
| W-5 | WSL folders (`\\wsl.localhost\…`) | ⬜ | |
| W-6 | Code signing (SignPath for open source, or Azure Trusted Signing) | ⬜ | So that SmartScreen stops warning |
| W-7 | winget manifest | ⬜ | |
| W-8 | Run the ARM64 build on ARM hardware | ⬜ | It is built and released, but has not run on ARM yet |

## macOS

Kept by the macOS side. The first rows are the steps of [macos.md](https://github.com/xelfiera-shn/rondel/blob/dev/docs/macos.md#steps); split them or add rows as the work needs.

| ID | Task | Status | Notes |
|---|---|---|---|
| M-1 | Plan | ✅ #21 | |
| M-2 | Spike | ✅ #32 | |
| M-3 | Core: POSIX launch script, `MacFolderNameRules`, terminal values and strings, source-generated settings JSON | 🔄 burakseyitkara1 | Settings JSON done in #34; the launch script, folder-name rules and terminal values are next |
| M-4 | Menu bar icon, trigger and Finder detection, `--probe` | ⬜ | |
| M-5 | Menu window, `--render-menu` | ⬜ | A fallback when the non-activating panel API is missing (review of #32) |
| M-6 | Launching and the settings window | ⬜ | |
| M-7 | Distribution: signing with the release certificate, the release job, the macOS part of the README | ⬜ | |

## Ideas

Not agreed yet. Once an idea is agreed, it moves into one of the lists above with an ID.

- Saved prompts: an arc of ready-made prompts (review, write tests, explain) next to Ask.
- Notifications when an agent finishes or waits for input, through Claude Code's hooks.
- Parallel sessions: start in a git worktree or in the background, and open the agents view.
- More agent profiles: Codex CLI, Gemini CLI.
- A menu editor in Settings, instead of editing `settings.json`.
- Ask with the clipboard or a screenshot.
- Open the folder in an editor (VS Code).
