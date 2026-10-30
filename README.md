# Rondel

**A radial launcher for AI coding agents · works with Claude Code**

English · [Türkçe](README.tr.md)

> **Status: early development.** There is no release yet. The first Windows build will be published as 0.1.0.

Hold **Ctrl + the left mouse button** for a moment on the Windows desktop or in File Explorer, and a radial menu opens under the cursor. Pick a slice and your coding agent starts in a terminal, already in the right folder.

| Where you hold | Actions | Folder |
|---|---|---|
| Empty desktop | New Project, New + Prompt | Your projects folder (default `%USERPROFILE%\Projects`) |
| Empty area of a folder in File Explorer | New Project, New + Prompt, Open Here, Continue Here, Resume Here | The folder that is open |
| A folder (on the desktop or in File Explorer) | Open, Continue, Resume | That folder |

Ordinary Ctrl+click and Ctrl+drag keep working everywhere. Rondel only steps in when you hold.

## Platforms and agents

- **Windows 10 and 11** come first. Linux and macOS are planned once Windows is complete (see the [roadmap](docs/roadmap.md)).
- **Agents are profiles.** Claude Code is the first supported agent. Each profile declares what the agent can do (open, continue, resume, start with a prompt), and the menu only shows what the selected agent supports.

## Privacy

Rondel has no telemetry. It reads the agent's local session data only to show hints such as "last session 2 h ago", and it writes nothing except its own settings, its logs and, if you enable it, the start-with-Windows entry.

## Contributing

Bug reports and ideas are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). Design notes live in [docs/](docs/).

## License

[MIT](LICENSE) © Ahmet Şahin

---

Rondel is an independent project. It is not affiliated with, endorsed by or sponsored by Anthropic. Claude and Claude Code are trademarks of Anthropic, PBC.
