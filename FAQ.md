# Frequently Asked Questions

## Where are my Dragon's Dogma 2 save files?

Under your Steam install, at:

```
<Steam>\userdata\<your_id>\2054970\remote\win64_save
```

A common path is `C:\Program Files (x86)\Steam\userdata\...`, but it depends on where Steam is
installed. The folder holds several files (`data*.bin` and `system.bin`).

## How does Savedrake name backups?

By default it uses random Dragon's Dogma themed names like "Archer Drake.zip". You can switch to
timestamps in the settings menu. Names are made unique, so two backups never overwrite each other.

## Is restoring safe? Can it lose my save?

Restore is transactional. Savedrake extracts the backup to a staging area, checks it contains save
data, swaps it into place, and rolls back to your previous save if any step fails. Your previous
save is kept until the new one is provably in place.

## Should I disable Steam Cloud when restoring?

Yes. Exit Dragon's Dogma 2 and either close Steam or turn off Steam Cloud for the game before
restoring. Otherwise Steam can re-upload the old cloud save over your restored files. If Steam
shows a cloud conflict on the next launch, choose the local copy.

## Can I get banned for using Savedrake?

Savedrake only copies and restores your own local saves. It does not edit save contents, pawns, or
items. There is no official statement that backing up or restoring your own unedited saves is
bannable. Use it on your own saves only.

## Does it support multiple characters?

There is no dedicated multi-character feature yet. You can keep separate, clearly named backups per
character and restore the one you want. Planned improvements are in the [roadmap](ROADMAP.md).

## How do I change the notification sounds?

Replace `success.wav` and `error.wav` next to `Savedrake.exe` with your own files, keeping the same
names.

## How do I organize backups?

Right-click a backup in the list to rename or delete it. F2 renames, Del deletes. Deleted backups
go to the Recycle Bin, and the last deletion can be undone.

## I updated, but the updater still seems old.

The updater program cannot replace itself while it applies an update, so it stays on the version you
installed. To get the latest updater, download the release zip and extract it over your install
once.

## How do I report a bug or request a feature?

Open an issue on the [GitHub repository](https://github.com/sammorrison9800/Savedrake/issues), or
email sammorrison9800@gmail.com. For security reports, see [SECURITY.md](SECURITY.md).

## Where can I see what changed or what is planned?

See [CHANGELOG.md](CHANGELOG.md) for releases and [ROADMAP.md](ROADMAP.md) for planned work.
