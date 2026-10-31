# Contributing to Rondel

Thanks for helping. This page explains how the repository is organized and what a change needs before it is merged. Issues and pull requests can be written in English or Turkish; code, comments and commit messages are in English.

## Branches

| Branch | Purpose |
|---|---|
| `dev` | Default branch. All work is integrated here through pull requests. |
| `prod` | Release branch. Every push to `prod` builds Rondel and publishes a GitHub release. |

Work branches are created from `dev` and named after the kind of change:

```
feat/<short-name>   fix/<short-name>   refactor/<short-name>
docs/<short-name>   ci/<short-name>    chore/<short-name>
```

An urgent fix for a released version goes on `hotfix/<short-name>`, created from `prod` and merged back into `prod`. Afterwards `prod` is merged into `dev` so the fix is not lost.

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

Requirements: Windows 10 or 11 and the .NET 10 SDK. Once the solution is in place:

```powershell
dotnet build                                   # must finish with zero warnings
dotnet test                                    # Core unit tests
dotnet format --verify-no-changes              # code style (.editorconfig)
.\build\publish.ps1                            # single-file Rondel.exe in .\dist
```

## Rules for changes

- **Every user-visible text in English and Turkish.** No UI text is hard-coded; both string tables are updated together.
- **Windows 10 and 11.** Both must keep working; say in the pull request which one you tested on.
- **Nothing machine-specific** in code, tests or docs: no personal paths, user names or local settings.
- **Ordinal or invariant comparisons** for identifiers, paths and keys (the Turkish dotless/dotted i breaks culture-sensitive comparisons).
- **Context detection decides only on non-localized identifiers** (window classes, control types), never on displayed text.
- **Changing a settings default needs a schema migration.** People already have a saved `settings.json`; the migration updates it only where the old default is still in place.
- **Keep the mouse hook callback cheap.** No I/O, COM calls or heavy work on the hook thread; everything else happens on the detection or UI thread.
- **Tests stay light.** Core logic that can break silently (path encoding, folder-name rules, argument escaping, settings migration) gets a unit test. UI and Win32 behavior is checked by hand.

## Reporting a bug

Please use the bug report form. The most helpful details are:

1. Rondel version (shown in Settings) and Windows version with build number (`winver`).
2. Where you held the mouse (desktop, Explorer folder, folder item…).
3. The debug log: Settings → **Debug logging** on, reproduce the problem, then tray icon → **Open log folder**. Remove any paths you do not want to share.
4. For "the menu does not open" or "wrong folder": the output of `Rondel.exe --probe X Y result.txt`, where X and Y are the screen coordinates of the spot.
