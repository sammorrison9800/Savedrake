# CONTINUE-HERE — Savedrake work handoff (for a fresh session on the Windows laptop)

> You (Claude Code) are resuming the Savedrake project on a **new machine**. The prior session's memory
> does **not** travel between machines — **this file + [DESIGN-transactional-restore.md](DESIGN-transactional-restore.md)
> are the handoff.** Read both before doing anything. Summarize state back to the user, then continue with
> the current task below.

**Savedrake** = a .NET 4.8 WinForms save manager for **Dragon's Dogma 2** (Steam appid **2054970**). It
backs up / restores the DD2 `win64_save` folder. Repo: **`sammorrison9800/Savedrake`**. The app shipped on
Nexus Mods; the prior work was an audit of user-reported bugs (R1–R7b) + fixes shipped as CI-checked PRs.

---

## Why you're on Windows now

Mac **cannot compile** this project (WinForms + Microsoft.VisualBasic.FileIO + WindowsAPICodePack +
Ionic.Zip are Windows-only). The whole prior project leaned on CI as the build oracle and the user
hand-running the app for smoke tests. On Windows you can finally **build and run locally** — a much tighter
loop. Use it: compile every change, run the app, smoke-test before opening a PR.

### One-time Windows setup
1. **Claude Code** — installed (you're reading this in it).
2. **Git for Windows** — `winget install Git.Git` (or git-scm.com).
3. **Build/run toolchain** — Visual Studio **Build Tools 2022** with the *".NET desktop build tools"*
   workload (gives `msbuild`), or full Visual Studio 2022. Plus **NuGet** CLI (or VS restores automatically).
4. **GitHub CLI** — `winget install GitHub.cli`, then `gh auth login` (or `gh auth switch`) as
   **`sammorrison9800`** — this account **owns** the repo; every push/PR/merge must run as it.
5. Clone + check out this branch:
   ```
   gh repo clone sammorrison9800/Savedrake
   cd Savedrake
   git checkout fix/transactional-restore
   ```
6. Build + run:
   ```
   nuget restore Savedrake.sln
   msbuild Savedrake.sln /p:Configuration=Release
   ```
   then run `Savedrake v1.2.3\bin\Release\Savedrake.exe`.

---

## Repo state (as of the handoff, master @ 08160e2)

- **`master` is the single trunk.** Bucket 1 fully shipped: PRs #8, #9, #10, #12 merged; **#11 merged**
  (merge commit `08160e2`) after all 5 Windows smoke tests passed. #11 carried 4 reviewed fixes:
  Gap B (WMI→UI thread marshaling), Gap D (tray dispose), Gap E (settings-load hardening),
  Gap G (`version.txt` try/catch).
- **Branches:** `master` (trunk), `pre-claude` (preserved — the decompile-confirmed shipped-1.2.4 source;
  do NOT delete), and `fix/transactional-restore` (this branch, the Bucket-2 work).
- **Tags:** `1.2.3`, `1.2.4`, `v1.2.3`, `archive/experimental-theme-2024`,
  `archive/pre-claude-shipped-1.2.4`. **Do not recreate the deleted junk tags** `1.2.5`/`ttttt`/`tag_name`
  — `1.2.5` especially is dangerous (the updater does a GitHub-tag-based version check; a stray `1.2.5`
  would trigger phantom updates).
- **CI:** every push to `master` + every PR builds Debug + Release + runs CodeQL. A `savedrake-release`
  artifact is produced you can download instead of building.

### Conventions (follow these)
- **`gh auth switch --user sammorrison9800`** before any push/PR/merge.
- **Branch per fix**, `fix/...` naming, PR vs `master`, **CI must be green before merge.**
- **You cannot merge to `master`** via the auto-approver — the repo owner runs
  `gh pr merge <n> -R sammorrison9800/Savedrake --merge --delete-branch` (and destructive ref deletions).
  Additive ops (create branch, push, open PR) are fine.
- **Smoke-test discipline:** a green build is necessary but NOT sufficient — run the app and exercise the
  fix on throwaway / DD2-shaped data before declaring done.
- Watch the two compile traps this codebase has hit: **CS0246** (the `Ionic.Zip` using is commented out →
  fully-qualify every Ionic type) and **CS0160** (catch-clause ordering — specific before `Exception`).

---

## Audit verdicts (condensed — full detail was in the prior session's memory, the essentials are here)

The repo tip == shipped **1.2.4** behavior (`pre-claude` IS the shipped source, **confirmed by decompiling
the actual Nexus 1.2.4 binary**). Bug reports map to that behavior.

- **R1** (restore → no saves): wipe-before-validate; empty-zip restore not guarded; zero Steam Cloud handling.
- **R2** (can't save after restore): DotNetZip round-trips the **ReadOnly** attribute onto restored saves;
  no game-running guard on restore.
- **R3** (only inn/partial save left): no transactional restore / no rollback; nested
  `win64_save\win64_save` layout from wrong-folder backups.
- **R4** (mouse appears, some buttons dead): global `WH_KEYBOARD_LL` hook installed unconditionally at
  startup, never unhooked (only the OS reclaims it at process exit → "closing Savedrake fixes input");
  recording mode swallows Enter/Esc globally and can bind a gameplay key as a global hotkey; bare
  `RegisterHotKey` with zero modifiers. **The claimed 1.2.4 input fix never shipped** (confirmed from the
  decompiled binary).
- **R5** ("select an interval" while one is selected): `SortComboBoxItems` does `Items.Clear()+AddRange`,
  wiping `SelectedIndex` while `Text` persists; `SetAutoBackupInterval` needs `SelectedItem != null`.
- **R6** (empty + invisible backups): list filter requires the zip comment `== "SamMorrison9800"`, and
  DotNetZip 1.16 never sets a comment on zero-entry zips; shipped builds also had `Environment.Exit(0)` in
  the backup catch (a partial-write path). Note: shipped/pre-claude DO have an empty-FOLDER guard; only the
  genuine tag-1.2.3 source lacked it.
- **R7a** (autosave after game close): exit IS detected via the registry watcher, but the timer callback
  never re-checks game state and there's no fallback if a WMI event is lost — residual gap.
- **R7b** (hours treated as minutes): **NOT-EXPLAINED / not reproducible.** Every inspectable build
  (genuine 1.2.3, pre-claude, current, AND the decompiled shipped binary) correctly distinguishes hours
  (`*3600000`) from minutes (`*60000`). There was no bug and no "fix." Don't chase it.
- **Updater**: `VerifyVersionFile`/`VerifyUpdatePackage` are `return true;` stubs; verify runs before
  download; `Process.Kill` before download; `ExtractToFile(overwrite:true)`. Security-relevant.

---

## What's done vs. what's left

**DONE:** the full audit (multi-agent + adversarially verified + reconciled against the decompiled shipped
binary), repo hygiene (single trunk, junk tags removed, `pre-claude` preserved), and **Bucket 1** (the four
B/D/E/G defensive fixes, shipped in #11).

**CURRENT TASK (this branch): Bucket 2 item 1 — transactional restore (kills R1/R2/R3).**
The full implementation spec + reference code + smoke tests are in
**[DESIGN-transactional-restore.md](DESIGN-transactional-restore.md)**. Implement it in
`Savedrake v1.2.3/Main.cs`, build, run the smoke tests, then PR vs `master`. **Delete this file and the
DESIGN file (or move to `docs/`) before opening the PR** so scratch docs don't land on `master`.

**REMAINING Bucket 2 backlog (after transactional restore — each its own CI-checked PR + smoke test):**
1. ~~Transactional restore (R1/R2/R3)~~ ← in progress on this branch.
2. **Hook lifecycle (R4):** install the `WH_KEYBOARD_LL` hook only while recording; unhook on
   end-recording / form-close / toggle; require a modifier; don't swallow Enter/Esc; skip `RegisterHotKey`
   when vk==0.
3. **R5 + R6:** restore `SelectedIndex` after `SortComboBoxItems` (with a `Text` fallback); list ALL
   Savedrake zips (treat the `SamMorrison9800` comment as a provenance tag, not a hard filter); require a
   `data*.bin` before zipping a backup; do real userdata-folder detection.
4. **Timer thread marshaling (R7a residual):** `SynchronizingObject` on the autobackup timer;
   re-check game state in the timer callback; tidy `FormClosing` stop-ordering.
5. **run4 additions:** locale-tolerant interval parsing (regex currently only accepts English
   `minutes|hour|hours`, Main.cs ~1031); embed `success.wav`/`error.wav` in git+csproj and drop the
   `_isUsingHotkey` gate so button/autobackup backups also play sound; relocate `version.txt` /
   `savedrake-updater.xml` to `%APPDATA%` in lockstep with the updater; the updater hardening
   (`return true;` verifier stubs, overwrite-extract, tag-based update check).

---

## First actions for this session
1. Read this file + DESIGN-transactional-restore.md.
2. `gh auth switch --user sammorrison9800`; confirm you're on `fix/transactional-restore`.
3. Summarize the state back to the user in a few lines, confirm they want transactional restore next.
4. Implement per the DESIGN doc, build (`msbuild`), run the smoke tests, then push + open the PR.
