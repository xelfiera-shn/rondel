# Changelog

Every version of Rondel, newest first. Until 1.0 only the two newest [releases](https://github.com/xelfiera-shn/rondel/releases) are kept for download; the notes of all versions are here.

## 0.8.0 (2026-11-01)

### Features

- **windows:** Announce only the releases that change the Windows app (#54)

### macOS

- **macos:** Open the radial menu on Option + hold (#45)
- **macos:** Start the agent in Terminal or iTerm2 (#49)
- **macos:** Ask for a folder name and a prompt on the input cards (#50)
- **macos:** Open sessions and recent projects on arcs around the ring (#51)
- **macos:** Add the settings window and open at login (#52)
- **macos:** Ship Rondel.app in the releases (#53)

## 0.7.0 (2026-11-01)

### Features

- **core:** Look for newer releases and verify them with the release key (#46)
- **windows:** Update Rondel from within the app (#47)

## 0.6.0 (2026-11-01)

### Features

- **core:** Follow the agents Rondel starts through their hooks (#37)
- **windows:** Notify when an agent asks or is done, and focus its terminal (#38)
- **windows:** Show the agent's state in its notification's icon (#39)
- **menu:** Open the active agents from the desktop menu or a shortcut (#40)

### Bug fixes

- **settings:** Keep reading settings.json written by a newer version (#34)

### macOS

- **macos:** Add the launch script and folder-name rules (#36)
- **macos:** Add the menu bar app with the Option trigger and Finder detection (#41)

## 0.5.0 (2026-10-31)

### Features

- **ui:** Choose the menu's theme, mode and accent colors (#30)

## 0.4.0 (2026-10-31)

### Features

- **menu:** Start new projects from one card with options and templates (#27)

### Bug fixes

- **ui:** Keep the input card on the screen when it grows (#28)

## 0.3.0 (2026-10-31)

### Features

- **menu:** Ask about a file, or in a folder, with a prompt (#25)

### Bug fixes

- **ui:** Keep long project names inside their tiles (#24)

## 0.2.0 (2026-10-31)

### Features

- **core:** List recent projects and open them on the desktop menu (#11)
- **ui:** Open recent projects into their sessions on a second arc (#12)

### Bug fixes

- **ui:** Give session titles on the outermost arc room for three lines (#13)
- **ui:** Close a project's sessions when the pointer goes back to Recent Projects (#15)

### Performance

- **ui:** Make the menu lighter to draw, especially on battery (#16)

## 0.1.0 (2026-10-31)

### Features

- **core:** Add agent profiles, menu model, launch planning and settings (#1)
- **windows:** Add the Windows tray app on top of the core (#2)
- **ui:** New radial menu design with an expandable Resume slice (#4)
- **ui:** Bring the settings window in line with Rondel's look (#7)

### Bug fixes

- **ui:** Keep the session arc on screen near monitor edges (#5)
