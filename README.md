# Rondel

**A radial launcher for AI coding agents · works with Claude Code**

English · [Türkçe](README.tr.md)

<p align="center">
  <img src="docs/images/menu-en.png" width="460" alt="Rondel's radial menu over a project folder, with the Resume slice opened into the five most recent sessions">
</p>

Hold **Ctrl + the left mouse button** for a moment on the Windows desktop or in File Explorer, and a radial menu opens under the cursor. Pick a slice and your coding agent starts in a terminal, already in the right folder.

## Install

1. Install [Claude Code](https://docs.anthropic.com/en/docs/claude-code) and check that `claude` runs in a terminal.
2. Download `Rondel-<version>-win-x64.exe` (or `-win-arm64.exe` for ARM PCs) from the [latest release](https://github.com/xelfiera-shn/rondel/releases/latest), put it anywhere and run it. The Rondel icon appears in the notification area.
   - The executable is not code-signed yet, so SmartScreen may warn on first start: **More info → Run anyway**. Each release lists SHA-256 checksums and carries a build provenance attestation.
3. Right-click the icon and tick **Start with Windows**.

There is no installer and Rondel needs no admin rights. Windows 10 and 11 are supported.

## Use

| Where you hold | Actions | Folder |
|---|---|---|
| Empty desktop | New Project, New + Prompt, Recent Projects | Your projects folder (default `%USERPROFILE%\Projects`), or the project you pick |
| Empty area of a folder in File Explorer | New Project, New + Prompt, Open Here, Ask Here, Continue Here, Resume Here | The folder that is open |
| A folder (on the desktop or in File Explorer) | Open, Ask, Continue, Resume | That folder |
| A file (on the desktop or in File Explorer) | Ask About This File, Open Here, Continue Here, Resume Here | The folder the file is in |

- **Open** starts a new session, **Continue** the most recent one, **Resume** lets you pick (`claude`, `claude --continue`, `claude --resume`).
- **Ask** and **Ask Here** ask only for a prompt and start a new session with it. **Ask About This File** does the same in the file's folder and puts the file first in the prompt (`@Program.cs`, or `@"My Notes.md"` for a name with spaces), so Claude Code reads it right away.
- **Resume opens outward** when the folder has two or more sessions: the five newest appear on an arc around it, the newest right outside the slice. Pick one to resume it directly.
- **Recent Projects** (desktop menu) opens into the five folders you worked in most recently. Pick one to resume its latest session; a project with two or more sessions opens further into them, the same way.

<p align="center">
  <img src="docs/images/recents-en.png" width="460" alt="The desktop menu with Recent Projects opened into five project folders, and one project opened further into its sessions">
</p>

- Continue and Resume show when the last session was and how many there are, and are dimmed when there are none.
- **New + Prompt** asks for a folder name (**Enter**), then a prompt (**Ctrl+Enter** starts, **Esc** goes back). The Ask slices go straight to the prompt (**Esc** cancels).
- Click a slice, press **1–N**, or use the arrow keys and **Enter**; **Tab** steps into the items of the selected slice or project (newest first), **Esc** steps back out and finally closes. You can also keep holding, move onto a slice and release.
- Ordinary Ctrl+click and Ctrl+drag keep working everywhere. The navigation pane, the address bar, virtual locations such as This PC, and File Explorer windows running as administrator don't open the menu.
- An optional keyboard shortcut (Settings) opens the menu at the mouse cursor.

## Settings and customization

Tray icon → **Settings…**: language (Automatic / English / Türkçe), projects folder, hold duration, keyboard shortcut, the agent's command (`claude`, or an alias such as `cc`), terminal (Windows Terminal or console window) and shell (PowerShell 7 or Windows PowerShell).

Menus, actions and agents live in `%APPDATA%\Rondel\settings.json` and apply as soon as the file is saved. For example, to add a slice that opens Claude Code in plan mode, add an action and put its id into a menu:

```jsonc
"Actions": [
  // ... the existing actions ...
  { "Id": "plan", "Label": "Plan", "Icon": "E8FD", "Capability": "Open", "ExtraArgs": ["--permission-mode", "plan"] }
],
"Menus": {
  "FolderItem": ["open", "ask", "continue", "resume", "plan"]
}
```

`Label` can be plain text; labels starting with `@` are built-in translations. `Icon` is a [Segoe Fluent Icons](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font) code point. Agents are profiles with a command and arguments per capability (`Open`, `Continue`, `Resume`, `ResumeById`, `Prompt`), so another coding agent can be added the same way — see [docs/architecture.md](docs/architecture.md#agent-profiles).

## Troubleshooting

- Turn on **Debug logging** in Settings, reproduce the problem, then tray icon → **Open log folder** (`%LOCALAPPDATA%\Rondel\logs`). Every trigger logs the UI Automation chain, the detected context and the folder.
- `Rondel.exe --probe X Y result.txt` classifies a single screen point (physical pixels) with the full trace.
- If the terminal says the command was not found, check the command in Settings and your `PATH`; aliases from your PowerShell profile work too.

Please include the log lines or the probe output when you [report a bug](https://github.com/xelfiera-shn/rondel/issues/new/choose).

## Privacy

Rondel has no telemetry. It reads the agent's local session data only to show the hints and the recent sessions, and it writes nothing except its own settings, its logs and, if you enable it, the start-with-Windows entry.

## Build from source

Requires Windows and the .NET 10 SDK.

```powershell
dotnet build
dotnet test
.\build\publish.ps1     # dist\win-x64\Rondel.exe, self-contained single file
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for branches, commit messages and releases, and [docs/](docs/) for the design notes and the [roadmap](docs/roadmap.md).

## License

[MIT](LICENSE) © Ahmet Şahin

---

Rondel is an independent project. It is not affiliated with, endorsed by or sponsored by Anthropic. Claude and Claude Code are trademarks of Anthropic, PBC.
