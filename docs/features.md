# Features by platform

English · [Türkçe](features.tr.md)

Which features Rondel has on Windows and on macOS, and since which version. Download both from the [latest release](https://github.com/xelfiera-shn/rondel/releases/latest); the [README](../README.md) explains how to install and use them.

✅ available, with the version it came in · ⬜ not yet · — not needed on this platform

| | Windows | macOS |
|---|---|---|
| **Runs on** | Windows 10 and 11, x64 and ARM64 | macOS 15 and later, Apple Silicon and Intel |
| **Download** | `Rondel-<version>-win-x64.exe` or `-win-arm64.exe` | `Rondel-<version>-osx-arm64.zip` or `-osx-x64.zip` |
| **Opens the menu** | Ctrl + hold the left mouse button | ⌥ Option + hold the left mouse button |
| **First start** | Not code-signed yet: SmartScreen asks once (More info → Run anyway) | Signed and notarized by Apple: macOS asks once whether to open it |

## The menu

| Feature | Windows | macOS |
|---|---|---|
| Hold the modifier and the left mouse button to open the menu; ordinary clicks and drags keep working | ✅ 0.1.0 | ✅ 0.8.0 |
| A menu for each place: the desktop, a folder window, a folder, a file | ✅ 0.3.0 | ✅ 0.8.0 |
| Session hints on the slices: when the last session was, how many there are | ✅ 0.1.0 | ✅ 0.8.0 |
| Keyboard in the menu: digits, arrows, Tab, Space, Enter, Esc | ✅ 0.1.0 | ✅ 0.8.0 |
| Long project names fit their tiles | ✅ 0.3.0 | ✅ 0.8.0 |
| Five themes, dark and light, accent colors | ✅ 0.5.0 | ✅ 0.8.0 |
| A color picker for your own accent colors | ✅ 0.10.0 | ✅ 0.15.0 |
| A keyboard shortcut that opens the menu at the mouse cursor | ✅ 0.1.0 | ⬜ |

## Starting the agent

| Feature | Windows | macOS |
|---|---|---|
| **Open**, **Continue** and **Resume** in a folder | ✅ 0.1.0 | ✅ 0.8.0 |
| **Resume** opens into the newest sessions | ✅ 0.1.0 | ✅ 0.8.0 |
| **Recent Projects** on the desktop, opening into the projects and their sessions | ✅ 0.2.0 | ✅ 0.8.0 |
| **Ask**, **Ask Here** and **Ask About This File** | ✅ 0.3.0 | ✅ 0.8.0 |
| **Ask About These Files**: several selected files in one prompt | ✅ 0.10.0 | ✅ 0.15.0 |
| Saved prompts: up to five on an arc of the Ask slices, all of them on the Ask card | ✅ 0.11.0 | ⬜ |
| **Quick Conversation** on the desktop: a talk in a folder of its own, not saved, with what is left of it deleted once it ends | ✅ 0.13.0 | ✅ 0.15.0 |
| **Ask About a Screenshot**: a frozen screen to pick an area or a window from, asked about in a Quick Conversation (tray menu, optional shortcut) | ✅ 0.14.0 | ⬜ |
| A Quick Conversation's first answer on a card of its own that blocks nothing, with Copy, Close and Continue in terminal, and a choice of model | ✅ 0.16.0 | ✅ 0.17.0 |
| **New Project** card: a first prompt, `git init`, plan mode and templates | ✅ 0.4.0 | ✅ 0.8.0 |
| Choice of terminal | ✅ 0.1.0: Windows Terminal or a console window | ✅ 0.8.0: Terminal or iTerm2 |
| Choice of shell | ✅ 0.1.0: PowerShell 7 or Windows PowerShell | — the agent runs in your login shell, with your `PATH` and aliases |

## Active agents

| Feature | Windows | macOS |
|---|---|---|
| A notification when an agent asks for something, is done or stops with an error; a click brings its terminal to the front | ✅ 0.6.0 | ⬜ |
| **Active Agents** on the desktop menu, with a status dot for each agent, and a shortcut that opens it | ✅ 0.6.0 | ⬜ |
| The agents card: the running agents in sight, above other windows or on the desktop, collapsed to a badge with the number that need you | ✅ 0.9.0 | ⬜ |
| **Sessions**: every saved Claude Code session with the space it takes, to delete into the Recycle Bin or for good | ✅ 0.12.0 | ⬜ |

## The app

| Feature | Windows | macOS |
|---|---|---|
| An icon in the notification area or the menu bar; opening Rondel again opens its settings | ✅ 0.1.0 | ✅ 0.8.0 |
| Settings window | ✅ 0.1.0 | ✅ 0.8.0 |
| Start with Windows / Open at login | ✅ 0.1.0 | ✅ 0.8.0 |
| Permissions in Settings (Accessibility, Finder) | — | ✅ 0.8.0 |
| Menus, actions and agents in `settings.json`, applied as soon as it is saved | ✅ 0.1.0 | ✅ 0.8.0 |
| English and Turkish | ✅ 0.1.0 | ✅ 0.8.0 |
| Updates from within the app, verified with Rondel's release key | ✅ 0.7.0 | ✅ 0.15.0 |
| Diagnostics: debug logging and `--probe` | ✅ 0.1.0 | ✅ 0.8.0 |
| A welcome window at the first start that shows how the menu opens, again from the tray menu | ✅ 0.11.0 | ✅ 0.15.0 |

## Planned

On neither platform yet; see the [roadmap](roadmap.md).

| Feature | Windows | macOS |
|---|---|---|
| A modifier key for another mode, for example Shift for plan mode | ⬜ | ⬜ |

The ARM64 Windows build and the Intel Mac build are released, but neither has been tried on such a computer yet. If you use one, please [report](https://github.com/xelfiera-shn/rondel/issues/new/choose) how it goes.
