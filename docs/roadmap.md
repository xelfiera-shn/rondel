# Roadmap

Rondel is built Windows first. Each phase ends with something usable; later phases are refined when the earlier ones are done.

## Phase 0 — Repository

License, README, contribution guide, issue and pull request templates, code style, branch rules and design notes.

**Done when** the repository has its rules in place and `dev` and `prod` are protected.

## Phase 1 — Windows app

- Core library with agent profiles, a menu model built on capabilities, launch planning, settings schema 1, English and Turkish strings.
- Windows app with all behaviors described in [windows-notes.md](windows-notes.md): gesture, context detection, radial menu, inline name and prompt input, session hints, tray icon, settings window, hotkey, autostart, diagnostics.
- Light unit tests for the core.
- `ci.yml`: build with zero warnings, format check and tests on every pull request and on `dev`; a test build of `Rondel.exe` from every push to `dev`.
- `release.yml`: on every push to `prod`, compute the version from the commits, build single-file x64 and ARM64 executables, attach checksums and build provenance, tag and publish a GitHub release.

Verified for 0.1.0:

- [x] Windows 11 File Explorer with several tabs
- [x] Windows 10
- [x] PowerShell 7.3+ argument passing (automated: launch round-trip test on CI, Windows PowerShell and pwsh)
- [x] Monitors with different scaling (mixed DPI)
- [x] Elevated File Explorer is skipped
- [x] Hotkey
- [x] ARM64 build (compiles and publishes in the release workflow; not run on ARM hardware yet)

**Done when** merging `dev` into `prod` produces a Windows release automatically.

## Phase 2 — Features (Windows)

- ~~In-menu session list (`--resume <id>`)~~ — shipped in 0.1.0 as the Resume arc.
- **0.2.0 — Recents**: a desktop-menu slice that opens into the five most recently worked-on project folders. Clicking a project resumes its latest session; a project with two or more sessions opens further into them. The Resume slice and project items only expand when there are at least two sessions.
- File context: "ask about this file" with an @-mention of the file.
- New-project templates (git init, agent instruction file).
- A modifier key for an alternate mode (for example Shift for plan mode).
- WSL paths.
- Customizable theme: user-chosen colors on top of Rondel's palette.

## Phase 3 — Trust and distribution

- Code signing (SignPath for open source or Azure Trusted Signing).
- winget manifest.
- Update notification.
- First-run hint that explains the gesture.

## Later — Linux and macOS

Started only after Windows is complete, with small experiments first:

- Wayland has no global mouse hook and no window positioning, so triggers there are file-manager entries (Nautilus, Dolphin) and a global shortcut through the desktop portal.
- On X11 the gesture can work with `XGrabButton` and `ReplayPointer`.
- Terminal adapters per desktop; no Flatpak at first.
- Kando's per-desktop backends are a useful reference.
