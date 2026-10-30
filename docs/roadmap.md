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

To be verified before 0.1.0:

- [ ] Windows 11 File Explorer with several tabs
- [ ] Windows 10
- [ ] PowerShell 7.3+ argument passing
- [ ] Monitors with different scaling (mixed DPI)
- [ ] Elevated File Explorer is skipped
- [ ] Hotkey
- [ ] ARM64 build

**Done when** merging `dev` into `prod` produces a Windows release automatically.

## Phase 2 — Features (Windows)

- Recent projects ring and an in-menu session list (`--resume <id>`).
- File context: "ask about this file" with an @-mention of the file.
- New-project templates (git init, agent instruction file).
- A modifier key for an alternate mode (for example Shift for plan mode).
- WSL paths.

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
