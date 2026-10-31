# Roadmap

Rondel is built Windows first. Each phase ends with something usable; later phases are refined when the earlier ones are done. Who is working on what, per platform, is in the [task list](https://github.com/xelfiera-shn/rondel/blob/tasks/TODO.md) on the `tasks` branch.

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
- **0.2.0 — Recents** (done, ships with 0.2.0): a desktop-menu slice that opens into the five most recently worked-on project folders. Clicking a project resumes its latest session; a project with two or more sessions opens further into them. The Resume slice and project items only expand when there are at least two sessions.
- **0.3.0 — Ask and files** (done): a menu for files (Ask About This File, which starts the prompt with an @-mention of the file, plus Open, Continue and Resume in its folder), and Ask slices that start a session in an existing folder with a prompt. Selecting several files is left for later.
- **0.4.0 — New Project card** (done): one card for the folder name and how a project starts — a first prompt, `git init`, plan mode, and templates that combine (a language's files plus a field's agent instructions). It remembers the last choice; options can be turned off in Settings. New + Prompt moved into it, so the desktop and Explorer menus lost a slice.
- A modifier key for an alternate mode (for example Shift for plan mode).
- WSL paths.
- **0.5.0 — Themes** (done): five menu themes (Rondel, Line, Spotlight, Orbit, Petal), each dark and light, chosen in Settings from pictures of the menu; the mode can follow Windows or stay fixed, and the accent can be a ready-made pair or the user's own colors.
- **0.6.0 — Active agents** (done): Rondel follows the agents it starts through Claude Code's hooks. A notification, with Rondel's mark and a badge for what happened, says when one asks for a permission or a question, is done or stopped with an error, and a click brings its terminal to the front. The desktop menu's Active Agents slice opens into the followed agents with a dot for each one's state; a shortcut opens it anywhere.

## Phase 3 — Trust and distribution

- Code signing (SignPath for open source or Azure Trusted Signing).
- winget manifest.
- Update notification.
- First-run hint that explains the gesture.

## macOS (in progress)

The macOS app is built on the shared core, step by step, following [macos.md](macos.md): a spike first, then the core additions, the app itself and distribution. Its builds ship in the same releases as Windows.

## Later — Linux

Started with small experiments first:

- Wayland has no global mouse hook and no window positioning, so triggers there are file-manager entries (Nautilus, Dolphin) and a global shortcut through the desktop portal.
- On X11 the gesture can work with `XGrabButton` and `ReplayPointer`.
- Terminal adapters per desktop; no Flatpak at first.
- Kando's per-desktop backends are a useful reference.
