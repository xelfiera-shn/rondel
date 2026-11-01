# Features by platform

English · [Türkçe](features.tr.md)

Which features Rondel has on Windows and on macOS, and since which version. Download both from the [latest release](https://github.com/xelfiera-shn/rondel/releases/latest); the [README](../README.md) explains how to install and use them.

✅ available, with the version it came in · ⬜ not yet · — not needed on this platform

| | Windows | macOS |
|---|---|---|
| **Runs on** | Windows 10 and 11, x64 and ARM64 | macOS 15 and later, Apple Silicon and Intel |
| **Download** | `Rondel-<version>-win-x64.exe` or `-win-arm64.exe` | `Rondel-<version>-osx-arm64.zip` or `-osx-x64.zip` |
| **Opens the menu** | Ctrl + hold the left mouse button | ⌥ Option + hold the left mouse button |
| **First start** | Not code-signed yet: SmartScreen asks once (More info → Run anyway) | Signed, but not notarized by Apple: allow it once in System Settings → Privacy & Security → Open Anyway |

## The menu

| Feature | Windows | macOS |
|---|---|---|
| Hold the modifier and the left mouse button to open the menu; ordinary clicks and drags keep working | ✅ 0.1.0 | ✅ 0.8.0 |
| A menu for each place: the desktop, a folder window, a folder, a file | ✅ 0.3.0 | ✅ 0.8.0 |
| Session hints on the slices: when the last session was, how many there are | ✅ 0.1.0 | ✅ 0.8.0 |
| Keyboard in the menu: digits, arrows, Enter, Tab, Esc | ✅ 0.1.0 | ✅ 0.8.0 |
| Long project names fit their tiles | ✅ 0.3.0 | ✅ 0.8.0 |
| Five themes, dark and light, accent colors | ✅ 0.5.0 | ✅ 0.8.0 |
| A keyboard shortcut that opens the menu at the mouse cursor | ✅ 0.1.0 | ⬜ |

## Starting the agent

| Feature | Windows | macOS |
|---|---|---|
| **Open**, **Continue** and **Resume** in a folder | ✅ 0.1.0 | ✅ 0.8.0 |
| **Resume** opens into the newest sessions | ✅ 0.1.0 | ✅ 0.8.0 |
| **Recent Projects** on the desktop, opening into the projects and their sessions | ✅ 0.2.0 | ✅ 0.8.0 |
| **Ask**, **Ask Here** and **Ask About This File** | ✅ 0.3.0 | ✅ 0.8.0 |
| **New Project** card: a first prompt, `git init`, plan mode and templates | ✅ 0.4.0 | ✅ 0.8.0 |
| Choice of terminal | ✅ 0.1.0: Windows Terminal or a console window | ✅ 0.8.0: Terminal or iTerm2 |
| Choice of shell | ✅ 0.1.0: PowerShell 7 or Windows PowerShell | — the agent runs in your login shell, with your `PATH` and aliases |

## Active agents

| Feature | Windows | macOS |
|---|---|---|
| A notification when an agent asks for something, is done or stops with an error; a click brings its terminal to the front | ✅ 0.6.0 | ⬜ |
| **Active Agents** on the desktop menu, with a status dot for each agent, and a shortcut that opens it | ✅ 0.6.0 | ⬜ |

## The app

| Feature | Windows | macOS |
|---|---|---|
| An icon in the notification area or the menu bar; opening Rondel again opens its settings | ✅ 0.1.0 | ✅ 0.8.0 |
| Settings window | ✅ 0.1.0 | ✅ 0.8.0 |
| Start with Windows / Open at login | ✅ 0.1.0 | ✅ 0.8.0 |
| Permissions in Settings (Accessibility, Finder) | — | ✅ 0.8.0 |
| Menus, actions and agents in `settings.json`, applied as soon as it is saved | ✅ 0.1.0 | ✅ 0.8.0 |
| English and Turkish | ✅ 0.1.0 | ✅ 0.8.0 |
| Updates from within the app, verified with Rondel's release key | ✅ 0.7.0 | ⬜ download the new zip from the release page |
| Diagnostics: debug logging and `--probe` | ✅ 0.1.0 | ✅ 0.8.0 |

## Planned

On neither platform yet; see the [roadmap](roadmap.md).

| Feature | Windows | macOS |
|---|---|---|
| A modifier key for another mode, for example Shift for plan mode | ⬜ | ⬜ |
| Ask about several selected files | ⬜ | ⬜ |
| A first-run hint that shows the gesture | ⬜ | ⬜ |

The ARM64 Windows build and the Intel Mac build are released, but neither has been tried on such a computer yet. If you use one, please [report](https://github.com/xelfiera-shn/rondel/issues/new/choose) how it goes.
