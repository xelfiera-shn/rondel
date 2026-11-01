# Contributing to Rondel

Thanks for helping. This page explains how the repository is organized and what a change needs before it is merged. Issues and pull requests can be written in English or Turkish; code, comments and commit messages are in English.

## Branches

| Branch | Purpose |
|---|---|
| `dev` | Default branch. All work is integrated here through pull requests. |
| `prod` | Release branch. Every push to `prod` builds Rondel and publishes a GitHub release. |
| `tasks` | Only the task list (`TODO.md`). Edited directly, without pull requests, and never merged. |

Work branches are created from `dev` and named after the kind of change:

```
feat/<short-name>   fix/<short-name>   refactor/<short-name>
docs/<short-name>   ci/<short-name>    chore/<short-name>
```

An urgent fix for a released version goes on `hotfix/<short-name>`, created from `prod` and merged back into `prod`. Afterwards `prod` is merged into `dev` so the fix is not lost.

## Taking a task

The [task list](https://github.com/xelfiera-shn/rondel/blob/tasks/TODO.md) shows who is working on what, per platform. It lives on its own branch, `tasks`, so it can be edited without a pull request. Before you start, take the task there: set its mark to 🔄 with your GitHub user name and commit straight to `tasks`. Once your pull request is merged, mark the task ✅. The marks are explained at the top of the list.

## Commit messages and pull requests

Rondel follows [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<optional scope>): <summary>
```

- **Types:** `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`, `chore`.
- **Scopes (optional):** `core`, `windows`, `menu`, `detection`, `launch`, `sessions`, `settings`, `ui`, `l10n`, `ci`, `docs`.
- **Summary:** imperative, lower case, no trailing period, at most 72 characters — `fix(launch): keep quotes in prompts on Windows PowerShell`.
- **Breaking changes:** add `!` after the type (`feat(settings)!: …`) or a `BREAKING CHANGE:` footer.

How pull requests are merged:

| Pull request | Merge method | Why |
|---|---|---|
| Work branch → `dev` | **Squash and merge** | The PR title becomes the commit message, so it must follow the convention above. |
| `dev` → `prod` | **Create a merge commit** (never squash) | The release reads every commit since the last version. |
| `hotfix/*` → `prod`, `prod` → `dev` | **Create a merge commit** | Keeps both branches' history connected. Hotfix commits must follow the convention, because they are kept as they are. |

A pull request into `dev` needs the required checks and one maintainer approval; a new push dismisses the approval. Only maintainers can merge into `prod`, because every merge there is a release.

## Versions and releases

Releases are automatic. On every push to `prod`, the next version is computed from the commits since the last version tag:

- `feat` → minor version, `fix` and `perf` → patch version.
- A breaking change raises the minor version while Rondel is below 1.0, and the major version from 1.0 on.
- Commits of other types alone (`docs`, `chore`, …) do not produce a release.

The release notes are generated from the same commits, so a clear PR title is also a clear changelog line. A release contains `Rondel-<version>-win-x64.exe`, `Rondel-<version>-win-arm64.exe`, a `SHA256SUMS.txt` and build provenance attestations.

Running the Release workflow by hand is always a dry run: it shows the next version and its notes and builds the executables, but publishes nothing.

Every push to `dev` also produces a test build: the CI run's **Rondel-dev.N-win-x64** artifact (kept for 14 days).

## Building and checking

Requirements: the .NET 10 SDK. The Windows app runs on Windows 10 or 11; the macOS app, in progress (see [docs/macos.md](docs/macos.md)), on macOS 15 and later. The whole solution, including the Windows project, also builds on macOS and Linux, and the core tests run there.

```powershell
dotnet build                                   # must finish with zero warnings
dotnet test                                    # Core unit tests
dotnet format --verify-no-changes              # code style (.editorconfig)
.\build\publish.ps1                            # single-file Rondel.exe in .\dist (Windows)
build/publish-macos.sh                         # Rondel.app in dist/osx-arm64 (macOS), see below
```

macOS ties the Accessibility and Automation permissions to the app's signature, so `publish-macos.sh` signs every build with the same certificate, by default one named **Rondel Development**. Create it once: Keychain Access → Certificate Assistant → Create a Certificate, name `Rondel Development`, identity type *Self-Signed Root*, certificate type *Code Signing*, in the login keychain. The first build asks for your login password to use it; choose *Always Allow*. Then grant Rondel the Accessibility permission once; it survives later builds.

CI builds and tests on Windows and on macOS; both must pass.

## Rules for changes

- **Every user-visible text in English and Turkish.** No UI text is hard-coded; both string tables are updated together.
- **Supported systems:** Windows 10 and 11, and macOS 15 and later for the macOS app. A change must keep working on every system it touches; say in the pull request which ones you tested on.
- **Nothing machine-specific** in code, tests or docs: no personal paths, user names or local settings.
- **The core stays platform-neutral.** No OS-specific APIs in `Rondel.Core`; what differs per OS goes behind an interface in `Core/Platform` and is implemented in the platform project. Core tests build paths with `Path.Combine` from real roots (such as the temp folder), never from literals like `C:\work`.
- **Ordinal or invariant comparisons** for identifiers, paths and keys (the Turkish dotless/dotted i breaks culture-sensitive comparisons).
- **Context detection decides only on non-localized identifiers** (window classes and control types on Windows, `AXRole`/`AXSubrole` on macOS), never on displayed text.
- **Changing a settings default needs a schema migration.** People already have a saved `settings.json`; the migration updates it only where the old default is still in place.
- **Keep the mouse hook callback cheap** (the event tap callback on macOS). No I/O, COM or Accessibility calls or other heavy work there; everything else happens on the detection or UI thread.
- **Tests stay light.** Core logic that can break silently (path encoding, folder-name rules, argument escaping, settings migration) gets a unit test. UI and Win32 behavior is checked by hand.

## Reporting a bug

Please use the bug report form. The most helpful details are:

1. Rondel version (shown in Settings) and Windows version with build number (`winver`).
2. Where you held the mouse (desktop, Explorer folder, folder item…).
3. The debug log: Settings → **Debug logging** on, reproduce the problem, then tray icon → **Open log folder**. Remove any paths you do not want to share.
4. For "the menu does not open" or "wrong folder": the output of `Rondel.exe --probe X Y result.txt`, where X and Y are the screen coordinates of the spot.
