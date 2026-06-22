# Savedrake

A save game manager for Dragon's Dogma 2 on Windows. Savedrake backs up and restores your
Dragon's Dogma 2 saves so a bad decision, a corrupt save, or an accidental overwrite does not
cost you your progress.

Windows only. Built on .NET Framework 4.8 (WinForms).

[![Latest release](https://img.shields.io/github/v/release/sammorrison9800/Savedrake?sort=semver)](https://github.com/sammorrison9800/Savedrake/releases/latest)
[![Build](https://github.com/sammorrison9800/Savedrake/actions/workflows/build.yml/badge.svg)](https://github.com/sammorrison9800/Savedrake/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/github/license/sammorrison9800/Savedrake)](LICENSE)

[Download the latest release](https://github.com/sammorrison9800/Savedrake/releases/latest) ·
[Changelog](CHANGELOG.md) · [FAQ](FAQ.md) · [Roadmap](ROADMAP.md)

## Features

- Manual backups. Zip your save folder on demand, named with random Dragon's Dogma themes or with
  timestamps. Names are made unique so backups never overwrite each other.
- Transactional restore. A backup is staged, checked for save data, and swapped into place, and it
  rolls back to your previous save automatically if any step fails, so a failed restore cannot
  leave your saves half-replaced.
- Autobackup. Take a backup on a timer while the game is running, with a cap on how many to keep.
- Global hotkey. Trigger a backup with a key combination without leaving the game.
- Backup history. Open, rename, or delete backups from the list. Deleted backups go to the Recycle
  Bin, and the last deletion can be undone.
- Sound feedback on every backup (manual, hotkey, and autobackup).
- Minimize to the system tray.
- Update checks. Savedrake checks GitHub for new releases and tells you when one is out. It does not
  download or install anything itself; it points you to the Nexus Mods page to update by hand.

## Install

1. Download `Savedrake.v<version>.zip` from the
   [latest release](https://github.com/sammorrison9800/Savedrake/releases/latest).
2. Extract it anywhere.
3. Run `Savedrake.exe`, then set your Dragon's Dogma 2 save folder and a backup folder.

The build is not code-signed, so Windows SmartScreen may warn about an unknown publisher. Choose
"More info" and then "Run anyway". Each release lists SHA-256 checksums you can verify against.

## Usage

- Backup: zips your save folder into the backup folder.
- Restore: replaces your current save with a selected backup (staged, with automatic rollback on
  failure).
- Autobackup: tick the checkbox and set an interval. Backups run while the game is open.
- Hotkey: enable "Custom Hotkey" and follow the prompts to set a key combination.
- Undo: recovers the most recently deleted backup from the Recycle Bin (one use per deletion).
- Right-click a backup to rename or delete it. F2 renames, Del deletes.
- Sounds: replace `success.wav` and `error.wav` next to the exe to change them, keeping the names.

## Steam Cloud note

Exit Dragon's Dogma 2 and either close Steam or turn off Steam Cloud for the game before you
restore. Otherwise Steam can re-upload the old cloud save over your restored files. If Steam shows
a cloud conflict on the next launch, choose the local copy. Savedrake backs up and restores your
own local saves and does not edit save contents.

## Reporting issues and security

- Bugs and feature requests: open a [GitHub issue](https://github.com/sammorrison9800/Savedrake/issues).
- Security reports: see [SECURITY.md](SECURITY.md).
- You can also reach the author at sammorrison9800@gmail.com or on Nexus Mods.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to build, test, and submit changes.

## License

MIT. See [LICENSE](LICENSE).
