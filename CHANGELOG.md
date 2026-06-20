# Changelog

All notable changes to Savedrake are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Pre-restore safety checkpoint: before restoring a backup, Savedrake now automatically
  snapshots your current save into a `(Pre-Restore)` backup first, so restoring one backup
  no longer discards the save you currently have. The snapshot is exempt from the autobackup
  limit and is never auto-pruned; if it cannot be created you are asked whether to continue
  without it. (#34)
- Backup integrity check on creation: every backup (and pre-restore checkpoint) is now
  verified right after it is written, by expanding and checksum-checking every file inside
  it. A corrupt or unreadable backup is rejected with an error instead of being saved and
  only failing later when you try to restore it. (#35)
- Backups now carry an internal integrity manifest (the list of save files with each one's
  size and checksum). Every new backup is verified against it at creation, so a backup that
  is missing a file or has silently corrupted is caught and rejected up front. The manifest
  is metadata only and is never restored into your save folder. (#36)
- Restore now re-checks a backup against its integrity manifest before replacing your live
  save, so a backup that has corrupted on disk since it was created is caught and the restore
  is refused with your current save left untouched. Older backups without a manifest are
  unaffected. (#37)
- The backup list now has an Integrity column: "Protected" for backups that carry the new
  checksum manifest and "Legacy" for older ones. A new right-click "Validate all backups"
  action runs a full check and marks each backup Validated, Legacy, or Corrupt, so you can
  find a bad backup before you ever need it. (#38)
- Diagnostic logging: Savedrake now writes a rolling log to `%APPDATA%\Savedrake\Logs` (one
  file per day, the last 14 kept) recording key events and any unexpected error, with file
  paths and your Steam ID redacted. Crashes that used to disappear silently are now caught
  and logged. Open the folder from Help > Open log folder. (#39)
- Disk-space preflight: before a backup or restore, Savedrake checks the target drive has room
  (including the staging copy a restore needs) and refuses up front with a clear message instead
  of failing partway through. A backup and a restore can also no longer overlap. (#41)
- Change-aware autobackup (part 1 of 4): autobackup no longer writes a redundant identical backup when
  your save has not changed since the last one. Each backup records a content fingerprint of the save, and
  a timer tick that finds no change is skipped instead of consuming one of your autobackup slots. A
  just-restored save, and a save folder that is momentarily locked or mid-write, are handled safely. (#44)
- Automatically clean up old autobackups (optional, off by default): a new Files > Settings option that, once
  turned on, keeps all of your recent autobackups and a spread of older ones and removes the extra older
  autobackups so they no longer just stop at the limit and so old restore points are not lost the way a plain
  "keep the newest N" limit loses them. Your manual backups and the pre-restore checkpoint are never removed,
  and a backup that fails its integrity check is never removed. Removed backups are deleted by default, or sent
  to the Recycle Bin if you tick the second option. (#45)
- Pin a backup (part 3 of 4): right-click any backup and choose "Pin backup" to protect it. A pinned backup is
  never removed by the automatic cleanup and does not count toward your autobackup limit, so you can keep an
  important restore point (for example, before a boss) for as long as you like. Pinned backups are marked with
  "[PINNED]" in the file name, so you can see them in the folder too; right-click again to unpin. (#46)
- Back up the moment the game saves (part 4 of 4, optional, off by default): a new Files > Settings option that
  watches your save folder and takes an autobackup shortly after the game writes a save, instead of waiting for the
  timer interval. The interval timer keeps running as a fallback, and the watcher pauses while you restore so it never
  backs up the save you just restored. Combine it with the automatic cleanup above for a tidy, up-to-the-moment
  history. (#47)
- Undo last restore: a new File > Undo last restore that rolls your save back to the snapshot Savedrake takes
  automatically just before every restore, so a restore you regret is one click to reverse. It runs through the same
  safety checks as a normal restore (and snapshots your current save first, so you can redo). The restore prompt now
  also tells you the snapshot is taken. (#48)
- Auto-detect your save folder: on first run (when no save folder is set yet) Savedrake offers the Dragon's Dogma 2
  save folder it finds via Steam, so new users no longer have to hunt down the cryptic `…\userdata\<id>\2054970\
  remote\win64_save` path. If you have more than one Steam profile it picks the most recently used and tells you.
  There is also a File > Detect save folder you can run any time. It only ever fills the folder in after you confirm. (#49)
- The backup list now shows friendly times ("just now", "5 min ago", "yesterday", "3 days ago") instead of a raw
  timestamp; older backups still show the date. Sorting by date is unchanged. (#50)
- Backup-location heads-up: when you pick a backup folder that is in a cloud-synced folder (OneDrive, Dropbox, etc.)
  or on the same drive as your saves, Savedrake now gives a one-time, non-blocking note about the trade-off. (#50)

### Fixed
- The updater builds its download URL with `Uri.EscapeDataString` on the version segment
  instead of the obsolete `Uri.EscapeUriString` (which can corrupt URLs). No change for
  normal version tags. (#40)

## [1.3.0] - 2026-06-17

This release is a large batch of reliability and data-safety fixes on top of 1.2.4,
plus a few small features. The backup and restore flows are now transactional, and
the auto-updater installs updates safely with rollback.

### Added
- Settings, the version file, and updater data are now stored in
  `%APPDATA%\Savedrake`, so they save correctly even when Savedrake is installed in a
  read-only folder such as Program Files. Existing files are migrated automatically. (#18)
- Backup success and error sounds now play for every backup (manual, hotkey, and
  autobackup), not only hotkey backups. (#20)
- A restore regression test that runs in CI on every change. (#13)
- `ROADMAP.md` describing planned work, and this `CHANGELOG.md`. (#28)

### Changed
- Restore is now transactional. It extracts the backup to a staging area, checks it
  contains save data, swaps it into place, and rolls back automatically if any step
  fails, so a failed restore can no longer leave your saves half-replaced. (#13)
- The auto-updater installs updates transactionally. It downloads and verifies the
  package first, then replaces files one at a time while keeping a backup of each,
  rolls back on any failure, and relaunches the app. (#17, #21)
- Autobackup intervals accept more input. Entries like "5 min", "1 Hr", and "2 hrs"
  are now valid, and parsing no longer depends on your Windows display language. (#19)
- "Check for Updates" now runs an in-app version check and tells you when you are
  already up to date, instead of always launching the updater. (#24)

### Fixed
- Data loss: a failed restore could delete the only copy of your saves while trying to
  recover. Restore now keeps your originals until the new save is provably in place. (#13)
- A timestamped backup could overwrite an existing backup made in the same second.
  Backup names are now made unique. (#22)
- A backup interrupted partway through could leave a corrupt `.zip`. Backups are now
  written to a temporary file and renamed into place only after they finish. (#22)
- The autobackup limit counter no longer drifts. A failed backup no longer counts
  toward the limit, and the count tracks the real number of backups on disk. (#22)
- Settings could be corrupted if the app was closed while saving them. Settings are now
  written atomically. (#23)
- "Restore Default" now clears all settings reliably and reports if a file could not be
  removed, instead of always claiming success. (#23)
- The global hotkey and the autobackup timer are now set up and cleaned up correctly
  across the app's lifetime. (#14, #16)
- Fixed several crashes: an empty interval list, a double-click in empty space in the
  backup list, and a stale right-click menu. (#25)
- Combobox selection and backup listing handle more edge cases. (#15)
- The updater is launched by its full path, and the update check no longer hangs or
  shows an unexpected error when you are offline. (#24)
- Fixed resource leaks in the sound player and the recycle-bin undo. (#25)

### Removed
- Dead code and unused files left over from earlier versions. (#26, #27)

## [1.2.4] - 2024-05-10

Previous release. Earlier history is not tracked in this file.

[1.3.0]: https://github.com/sammorrison9800/Savedrake/compare/1.2.4...1.3.0
[1.2.4]: https://github.com/sammorrison9800/Savedrake/releases/tag/1.2.4
