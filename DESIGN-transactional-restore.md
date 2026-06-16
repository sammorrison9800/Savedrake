# DESIGN — Transactional Restore (Bucket 2, kills R1 / R2 / R3)

> Synthesized from a multi-agent judge panel (Design 3 base: single `RestoreTransactional`
> orchestrator + temp-dir rollback, no recycle-bin dependency; grafted compile-safe directory-flatten
> from Design 2 + named rollback helper from Design 1). Grounded against the **real** `Savedrake v1.2.3/Main.cs`
> on `master` (08160e2). Target: **.NET 4.8 / C# 7.3 / DotNetZip (Ionic.Zip) 1.16.0**.
>
> **This is a reference spec + reference code.** Compile and smoke-test on the Windows laptop — the Mac
> cannot build WinForms/VisualBasic.FileIO/WindowsAPICodePack/Ionic.Zip, so **CI (or your local build)
> is the build oracle.** Line numbers below are from 08160e2 and may shift as you edit — match on symbol
> names, not absolute lines.

---

## The bug (why this exists)

Current restore (`button_res_Click` Main.cs:1458, `MoveFilesToRecycleBin` 1523, `UnzipFileWithDotNetZip`
1547) **recycles the live saves at line 1505 BEFORE validating the backup, with no rollback**, and
`ExtractAll` replays the stored ReadOnly attribute onto restored files. Failure modes:

- **R1** (restore → no saves): an empty-but-valid zip or a missing/corrupt zip means the live saves are
  already in the Recycle Bin when extraction yields nothing. `probe.Count` is read at 1495 but never
  checked `> 0`.
- **R2** (can't save after restore): DotNetZip round-trips the **ReadOnly** attribute onto restored
  saves → the game can't overwrite them. Plus no guard against restoring while the game runs.
- **R3** (only inn save left / partial): no transactional swap, no rollback; wrong-folder backups create
  a nested `win64_save\win64_save` layout that lands saves where DD2 doesn't read them.

## The fix (one sentence)

Touch **no** live save until the backup is proven good **and** fully staged on disk; extract to a
same-volume temp staging dir with a per-entry zip-slip guard; flatten a nested layout; verify staged
content; **then** move live saves aside into a same-volume rollback temp dir, move staged into live,
clear ReadOnly; on **any** failure restore the moved-aside originals; the recycle bin is never used.

---

## Control flow

### `button_res_Click` — keep all existing guards; change only the `Count==1` body

Keep verbatim: the two path guards (`textbox1`/`textbox2` empty or `!Directory.Exists`), the
`listView.SelectedItems.Count == 1 / > 1 / else` branch structure, and inside `==1` the
`fileName`/`filePath` computation + the `if (!File.Exists(filePath)) { … LoadBackupHistory(); return; }`
guard. Then **replace** the old probe block (1490–1502) and the
`MoveFilesToRecycleBin(...); Status.Text=...; UnzipFileWithDotNetZip(...)` (1505–1509) with:

```csharp
// STEP 1 — game-running guard (R2). Fresh synchronous read; do NOT use the cached isGameRunning field.
if (CheckGameRunningStatus())
{
    MessageBox.Show("Please quit Dragon's Dogma 2 before restoring.", "Game Running",
        MessageBoxButtons.OK, MessageBoxIcon.Warning);
    return;
}

// STEP 2 — Steam Cloud warning (run4 C). Declining costs nothing; no live file touched yet.
DialogResult cloud = MessageBox.Show(
    "Before restoring, fully EXIT Steam (or disable Dragon's Dogma 2 Cloud Saves in " +
    "Steam > Properties). Otherwise Steam may re-upload your OLD save and overwrite this restore." +
    "\n\nContinue with the restore?",
    "Exit Steam Before Restoring", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
if (cloud != DialogResult.Yes) { Status.Text = "Restore cancelled."; return; }

// STEP 3 — validate the backup BEFORE touching live saves (R1).
if (!ValidateBackup(filePath, out string reason))
{
    MessageBox.Show(reason + "\n\nYour current save files have not been touched.",
        "Restore Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return;
}

// STEP 4 — delegate ALL destructive work (staging, swap, rollback, cleanup) to the transaction.
bool ok = RestoreTransactional(filePath, textbox1.Text);

// STEP 5 — post-success UI. Undo is DISABLED on success: old saves went to a temp dir, not the
// recycle bin, so a recycle-bin Undo would silently no-op. Honest = disable it.
if (ok)
{
    deletedFiles.Clear();
    UpdateUndoButtonState();
    LoadBackupHistory();
    SortComboBoxItems();
    listView.Sort();
}
```

`MoveFilesToRecycleBin` / `RecordDeletion` / `RestoreDeletedFiles` / `deletedFiles` stay **untouched** and
keep serving the right-click Delete feature. `UnzipFileWithDotNetZip` is removed from the restore path
(delete it or leave it unreferenced — it has no other caller).

---

## Reference implementations (verify on compile — see Compile-safety checklist)

```csharp
private bool RestoreTransactional(string filePath, string liveDir)
{
    Status.Text = "Restore started... Please wait.";
    string stagingDir  = CreateSiblingTempDir(liveDir, "savedrake_stage");
    string rollbackDir = CreateSiblingTempDir(liveDir, "savedrake_rollback");
    bool movedLiveAside = false;
    bool stagedIntoLive = false;
    try
    {
        ExtractZipToStaging(filePath, stagingDir);                 // T1 stage + zip-slip guard
        if (DetectNestedPrefix(stagingDir)) FlattenNestedLayout(stagingDir); // T1b flatten win64_save\win64_save
        if (!VerifyStagedDir(stagingDir))                          // T2 verify on disk
            throw new System.IO.IOException(
                "The backup extracted but does not contain Dragon's Dogma 2 save data. Restore aborted.");

        MoveDirContents(liveDir, rollbackDir); movedLiveAside = true;  // T3 move live aside (same-volume rename)
        MoveDirContents(stagingDir, liveDir);  stagedIntoLive = true;  // T4 move staged into live
        ClearReadOnlyRecursive(liveDir);                              // T5 strip ReadOnly (R2)

        Status.Text = "Restore successful.";                          // T6 commit
        MessageBox.Show("Restore successful.", "Information",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }
    // CS0160: order is load-bearing. ZipException : Exception, UnauthorizedAccessException : SystemException,
    // IOException : SystemException — none derives from another, so the first three are order-free; Exception MUST be last.
    catch (Ionic.Zip.ZipException ze)   { HandleRestoreFailure(ze,  rollbackDir, liveDir, movedLiveAside, stagedIntoLive); return false; }
    catch (UnauthorizedAccessException uae) { HandleRestoreFailure(uae, rollbackDir, liveDir, movedLiveAside, stagedIntoLive); return false; }
    catch (System.IO.IOException ioEx)  { HandleRestoreFailure(ioEx, rollbackDir, liveDir, movedLiveAside, stagedIntoLive); return false; }
    catch (Exception ex)                { HandleRestoreFailure(ex,  rollbackDir, liveDir, movedLiveAside, stagedIntoLive); return false; }
    finally
    {
        TryDeleteDir(stagingDir);
        TryDeleteDir(rollbackDir);  // after success: stale originals; after rollback: already emptied
    }
}

private void HandleRestoreFailure(Exception ex, string rollbackDir, string liveDir,
                                  bool movedLiveAside, bool stagedIntoLive)
{
    bool recovered = Rollback(liveDir, rollbackDir, movedLiveAside, stagedIntoLive);
    Status.Text = "Restore failed. Your saves were restored to their previous state.";
    string tail = recovered
        ? "\n\nYour previous save files have been restored."
        : "\n\nWARNING: automatic recovery also failed. Your ORIGINAL saves are safe in:\n" +
          rollbackDir + "\nMove their contents back into your save folder manually.";
    MessageBox.Show(ex.Message + tail, "Restore Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
}

private bool Rollback(string liveDir, string rollbackDir, bool movedLiveAside, bool stagedIntoLive)
{
    try
    {
        if (stagedIntoLive) EmptyDir(liveDir);                 // drop partially-moved staged files
        if (movedLiveAside) MoveDirContents(rollbackDir, liveDir); // put originals back byte-for-byte
        return true;
    }
    catch (Exception) { return false; } // never throw out of a catch; dialog will name rollbackDir
}

private bool ValidateBackup(string filePath, out string reason)
{
    reason = null;
    try
    {
        using (Ionic.Zip.ZipFile zip = Ionic.Zip.ZipFile.Read(filePath))
        {
            if (zip.Count == 0) { reason = "The backup file contains no files."; return false; }
            foreach (Ionic.Zip.ZipEntry entry in zip.Entries)
            {
                if (entry.IsDirectory) continue;
                if (IsRealSaveEntry(entry.FileName)) return true; // GetFileName() inside ignores any win64_save\ prefix
            }
            reason = "The backup does not contain Dragon's Dogma 2 save data (no data*.bin / system.bin).";
            return false;
        }
    }
    catch (Ionic.Zip.ZipException ze) { reason = "The backup file is not a valid zip: " + ze.Message; return false; }
    catch (Exception ex)              { reason = "The backup file could not be read: " + ex.Message; return false; }
}

private static bool IsRealSaveEntry(string entryFileName)
{
    string leaf = Path.GetFileName(entryFileName.Replace('/', Path.DirectorySeparatorChar));
    return System.Text.RegularExpressions.Regex.IsMatch(
               leaf, "^data.*\\.bin$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        || string.Equals(leaf, "system.bin", StringComparison.OrdinalIgnoreCase);
}

private void ExtractZipToStaging(string filePath, string stagingDir)
{
    string stagingRoot = Path.GetFullPath(stagingDir);
    if (!stagingRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        stagingRoot += Path.DirectorySeparatorChar;

    using (Ionic.Zip.ZipFile zip = Ionic.Zip.ZipFile.Read(filePath))
    {
        foreach (Ionic.Zip.ZipEntry entry in zip.Entries)
        {
            // zip-slip guard — ported from the HARDENED updater/UpdaterForm.cs:361-391 (GetFullPath both
            // sides + trailing separator + OrdinalIgnoreCase). Runs on directory entries too (before the
            // IsDirectory skip), because '..' can appear in a directory entry name.
            string rel = entry.FileName.Replace('/', Path.DirectorySeparatorChar);
            string destFull = Path.GetFullPath(Path.Combine(stagingRoot, rel));
            if (!destFull.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                throw new System.IO.IOException(
                    "Backup contains an unsafe path entry: '" + entry.FileName + "'. Restore aborted.");
            if (entry.IsDirectory) continue;
            // ONLY use this overload (proven equivalent to ExtractAll at 1555). NOT OpenReader/stream-copy,
            // NOT ExtractToFile (System.IO.Compression only). Ionic recreates parent subdirs automatically.
            entry.Extract(stagingDir, Ionic.Zip.ExtractExistingFileAction.OverwriteSilently);
        }
    }
}

private static bool DetectNestedPrefix(string stagingDir)
{
    return Directory.GetFiles(stagingDir).Length == 0
        && Directory.GetDirectories(stagingDir).Length == 1
        && string.Equals(new DirectoryInfo(Directory.GetDirectories(stagingDir)[0]).Name,
                         "win64_save", StringComparison.OrdinalIgnoreCase);
}

private static void FlattenNestedLayout(string stagingDir)
{
    string nested = Directory.GetDirectories(stagingDir)[0]; // the single win64_save husk
    MoveDirContents(nested, stagingDir);
    Directory.Delete(nested, false);
}

private static bool VerifyStagedDir(string stagingDir)
{
    string[] all = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories);
    return all.Length > 0 && all.Any(p => IsRealSaveEntry(Path.GetFileName(p))); // System.Linq present (line 12)
}

private static string CreateSiblingTempDir(string liveDir, string tag)
{
    // Parent-of-live keeps temp on the SAME volume → every File.Move/Directory.Move is a near-atomic rename.
    // For a real DD2 path (...\2054970\remote\win64_save) the parent 'remote' always exists. %TEMP% is a
    // last-resort fallback only when GetParent is null (drive-root edge).
    string root = Directory.GetParent(liveDir.TrimEnd(Path.DirectorySeparatorChar))?.FullName
                  ?? Path.GetTempPath();
    string dir = Path.Combine(root, "._" + tag + "_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
}

private static void MoveDirContents(string sourceDir, string destDir)
{
    Directory.CreateDirectory(destDir);
    foreach (string f in Directory.GetFiles(sourceDir))
        File.Move(f, Path.Combine(destDir, Path.GetFileName(f)));
    foreach (string d in Directory.GetDirectories(sourceDir))            // moves subdirs too (fixes the old
        Directory.Move(d, Path.Combine(destDir, new DirectoryInfo(d).Name)); // top-level-only MoveFilesToRecycleBin gap)
}

private static void EmptyDir(string dir)
{
    if (!Directory.Exists(dir)) return;
    ClearReadOnlyRecursive(dir); // so a ReadOnly partial file can't block deletion
    foreach (string f in Directory.GetFiles(dir)) File.Delete(f);
    foreach (string d in Directory.GetDirectories(dir)) Directory.Delete(d, true);
}

private static void ClearReadOnlyRecursive(string root)
{
    DirectoryInfo dirInfo = new DirectoryInfo(root);
    if (!dirInfo.Exists) return;
    if ((dirInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
        dirInfo.Attributes &= ~FileAttributes.ReadOnly;
    foreach (FileInfo fi in dirInfo.GetFiles("*", SearchOption.AllDirectories))
    {
        try { if ((fi.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                  fi.Attributes &= ~FileAttributes.ReadOnly; }
        catch (UnauthorizedAccessException) { }
        catch (System.IO.IOException) { }
    }
    foreach (DirectoryInfo di in dirInfo.GetDirectories("*", SearchOption.AllDirectories))
    {
        try { if ((di.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                  di.Attributes &= ~FileAttributes.ReadOnly; }
        catch (UnauthorizedAccessException) { }
        catch (System.IO.IOException) { }
    }
}

private static void TryDeleteDir(string dir)
{
    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
    try { ClearReadOnlyRecursive(dir); Directory.Delete(dir, true); }
    catch (Exception) { } // best-effort temp cleanup; at worst leaves a hidden ._savedrake_* husk
}
```

---

## Compile-safety checklist (Mac cannot compile — this is where it breaks)

1. **CS0246 — fully qualify every Ionic type.** Line 1 `//using Ionic.Zip;` is commented out, so use
   `Ionic.Zip.ZipFile`, `Ionic.Zip.ZipFile.Read(string)`, `Ionic.Zip.ZipEntry`,
   `Ionic.Zip.ExtractExistingFileAction.OverwriteSilently`, `Ionic.Zip.ZipException` — exactly like the
   existing call sites (1492/1553/1555/1562/1625).
2. **CS0160 — catch ordering.** This codebase already hit CS0160 once (PR #11). In `RestoreTransactional`
   keep `ZipException → UnauthorizedAccessException → IOException → Exception`. `catch(IOException)` does
   **not** catch a `ZipException` (DotNetZip's `ZipException : System.Exception`, not `IOException`), so the
   explicit `ZipException` catch is required.
3. **Extraction API.** Use **only** `entry.Extract(string baseDir, Ionic.Zip.ExtractExistingFileAction)`.
   Do **not** use `entry.OpenReader()`/stream-copy (unproven here) or `entry.ExtractToFile`
   (System.IO.Compression only). Nested layout is fixed by post-extract `Directory.Move`, not custom paths.
4. **Entry name separators.** DotNetZip may emit `\` or `/` in `entry.FileName`. Always
   `Replace('/', Path.DirectorySeparatorChar)` then `Path.GetFullPath` (canonicalizes both + resolves `..`).
   `IsRealSaveEntry` uses `Path.GetFileName`, which discards any `win64_save\` prefix — so nested backups
   still validate.
5. **C# 7.3 only.** No `using` declarations, switch expressions, or target-typed `new`. `out string reason`
   (out-var) and `?.`/`??` are fine. Use classic `using` statements + explicit types.
6. **No new `using` directives needed.** `System.IO` (11), `System.Linq` (12),
   `System.Text.RegularExpressions` (19), `System.Windows.Forms` (22), `Microsoft.Win32` (2), `System` (6)
   already cover everything (`File`/`Directory`/`Path`/`FileAttributes`/`SearchOption`/`.Any()`/`Regex`/
   `MessageBox`/`DialogResult`/`Guid`/`UnauthorizedAccessException`).
7. **Restore NuGet first** so DotNetZip 1.16.0 resolves (`Savedrake.csproj:72-73`, `packages.config`).

---

## Grounded snippets (the originals you're porting from / integrating with)

**Synchronous game-running helper to reuse (Main.cs:579-595) — do NOT write a new reg read:**
```csharp
private bool CheckGameRunningStatus()  // fresh blocking read of HKCU\Software\Valve\Steam\Apps\2054970 'Running' DWORD; true iff ==1
```
Already called synchronously on the UI thread at lines 240 and 865. Don't branch on the cached
`isGameRunning` field (can be stale if the WMI watcher failed to start); don't call `OnGameStatusChanged`
(it has autobackup side effects).

**Hardened zip-slip guard ported into `ExtractZipToStaging` (from updater/UpdaterForm.cs:361-391):**
```csharp
string installRoot = Path.GetFullPath(extractPath);
if (!installRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
    installRoot += Path.DirectorySeparatorChar;
string destinationPath = Path.GetFullPath(Path.Combine(installRoot, entry.FullName));
if (destinationPath.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase)) { /* extract */ }
```
Use **this** hardened variant (GetFullPath-normalized root + trailing separator + OrdinalIgnoreCase),
**not** the weaker `Savedrake v1.2.3/updater/updater.cs:17-26` one (raw path, no trailing sep,
case-sensitive Ordinal — vulnerable to sibling-prefix + case bypass). Our port **throws** on violation
(the updater silently skips); throwing is correct for restore so a tampered backup aborts the whole thing.

**Backup entry structure (why flatten works):** `BackupOperation` does `zip.AddDirectory(textbox1.Text)`
(Main.cs:1390), which roots the folder's **contents** at the zip root. Correct backup
(`textbox1 = ...\remote\win64_save`) → entries are `data000.bin`, `system.bin`, … at root. Wrong-folder
backup (`textbox1 = ...\remote`) → entries are `win64_save\data000.bin` (one extra prefix) = the R3 nest.
The wrong-folder warning at 1314–1322 is click-through-bypassable, so nested backups **do** exist in the wild.

---

## Edge cases (each handled by the flow above)

| Case | Handling |
|---|---|
| Empty zip (Count==0) | `ValidateBackup` false → refuse, live untouched |
| Zip readable but no `data*.bin`/`system.bin` | `ValidateBackup` false; defense-in-depth `VerifyStagedDir` re-checks on disk |
| Corrupt / non-zip | `ValidateBackup` catch → false; if it parses but fails mid-extract, `ExtractZipToStaging` throws → rollback no-op (live untouched), temp cleaned |
| Zip-slip (`..\` or `C:\evil`) | per-entry containment throws `IOException` before any live move → live untouched |
| Nested `win64_save\win64_save` (R3) | `DetectNestedPrefix` + `FlattenNestedLayout`; only flattens when the **sole** top-level item is a `win64_save` dir, so legit root backups are never flattened |
| Game running (R2) | `CheckGameRunningStatus()` true → refuse before any work |
| Steam Cloud (run4 C) | YesNo warning; No → "Restore cancelled", zero side effects |
| ReadOnly saves (R2) | `ClearReadOnlyRecursive(liveDir)` after swap, best-effort per item |
| Saves with subfolders | `MoveDirContents` moves subdirs too (fixes old top-level-only gap) |
| Partial extract / disk full mid-archive | `ExtractZipToStaging` throws before move-aside → live untouched |
| Disk full DURING swap (after move-aside) | `Rollback` empties live + moves originals back; if even that fails, dialog names `rollbackDir` for manual recovery, originals remain on disk |
| Live-dir parent not writable | `CreateSiblingTempDir` throws at T0 before any live move → clean refusal |
| Backup deleted between listing and restore | existing `File.Exists` guard → message + `LoadBackupHistory` + return |

**Invariant:** at every instant the user's saves exist intact in exactly one place — either `liveDir` or
`rollbackDir`.

---

## Windows smoke tests (run after a clean build)

1. **Build** — zero errors; specifically watch CS0246 (unqualified Ionic) + CS0160 (catch order). Restore NuGet first.
2. **Happy path** — DD2 closed + Steam exited, point at a real `...\remote\win64_save`, restore a known-good
   backup: cloud dialog → Yes; Status `Restore started…` → `Restore successful.`; `data*.bin` at win64_save
   **root** (not nested); no leftover `._savedrake_*` dirs under `remote`; Undo **disabled**; launch DD2 →
   save loads and can be overwritten (proves ReadOnly cleared).
3. **Game-running guard (R2)** — launch DD2, click Restore → "Please quit Dragon's Dogma 2…", no live file
   changes (check timestamps). Quit DD2, retry → proceeds.
4. **Corrupt/empty backup (R1)** — craft a 0-entry zip and a truncated `.zip` (give each the
   `SamMorrison9800` comment so they list); each → refusal + "your current save files have not been touched",
   live byte-for-byte unchanged (hash before/after).
5. **Wrong-folder / no-save backup (R1)** — zip with files but no `data*.bin` → refusal, live untouched.
6. **Nested layout (R3)** — back up with source pointed at `...\remote` (parent) so entries are
   `win64_save\…`, then restore → files land directly in `win64_save` (data at root, not `win64_save\win64_save`),
   DD2 reads them.
7. **Zip-slip** — hand-edit a backup to add `..\evil.txt` (and an absolute-path entry), restore → ABORTS with
   the unsafe-path message, nothing written outside `win64_save`, live untouched (abort is during staging).
8. **ReadOnly (R2)** — set ReadOnly on source saves before backup, restore, confirm every restored file has
   ReadOnly **cleared** (`attrib`) and DD2 can overwrite.
9. **Rollback / data-loss safety (R7) — the critical one** — force a mid-swap failure (e.g. hold a staged file
   open exclusively, or mark the live dir read-only at the dir level) so `MoveDirContents(staging→live)` throws
   after move-aside. Confirm: error dialog "Your previous save files have been restored.", Status `Restore
   failed…`, ORIGINAL saves back in `win64_save` intact (hash-match pre-test), no `._savedrake_rollback_` husk
   (or, if rollback was blocked, husk present + dialog named its path).
10. **Subfolder saves** — backup containing a subdir → subdir + files restored under `win64_save`; rollback test
    still restores them.
11. **Undo coherence** — after a successful restore, Undo disabled; then use right-click Delete on a backup →
    Undo still works for Delete (proves restore didn't entangle the shared `deletedFiles`/Shell32 path).

---

## When done

- Commit on this branch (`fix/transactional-restore`), push, let CI build (Debug + Release + CodeQL) go green.
- Run the smoke tests above on real DD2-shaped data.
- Open the PR vs `master`. **Before opening it, delete `DESIGN-transactional-restore.md` and `CONTINUE-HERE.md`**
  (or move them into a `docs/` folder) so the scratch handoff doesn't land on `master`.
- The merge to `master` is the repo owner's to run (`gh pr merge … --merge --delete-branch`) — the auto-approver
  blocks merges to the default branch.
