# Savedrake Roadmap

> **Status (updated 2026-06-22):** A large amount has shipped to `master` since this roadmap was written, all
> **unreleased** (it ships at the next major release):
> - **Phase 1 data-safety: largely DONE** — pre-restore safety checkpoint (P4); verify-on-create + in-zip integrity
>   manifest + re-verify on restore + "Validate all backups" UI (P1); change-aware autobackup with content-hash dedup
>   + tiered retention + pinning (P3); the three exception hooks + rolling logs (P2); disk-space preflight + operation
>   lock; and capping the retention-exempt (Pre-Restore)/(Pre-Load) safety checkpoints so they can't pile up.
> - **WinForms → WPF migration: DONE** — the app is now a themed WPF shell (`Savedrake.App`) over portable logic
>   (`Savedrake.Core`), with a first-run setup flow and a tabbed **Backups / Characters / Settings** layout.
> - **Characters / profiles: DONE** — per-character backup folders with a non-destructive migration, switch, rename,
>   and a "Load into game" save-swap.
> - **Update delivery: REWORKED (see P0 below)** — the bespoke self-updater was removed; the app now uses a dual-build
>   model (the Nexus build makes no automatic network call; an opt-in GitHub build carries a one-click updater).
>
> The items here are *enhancements*, not bug fixes. The known defects were fixed earlier in PRs #13–#27, and the
> restore **core is considered solid** and should be preserved.
>
> **Provenance:** this roadmap was produced from a multi-angle web-research pass and then
> independently validated by **three separate external research passes** on **2026-06-17**
> (one fact-check, one clean-room independent study, one validate-and-expand). All three
> converged on the same top priority and the same scoping. Where they corrected earlier
> assumptions, the corrections are recorded under [Factual notes](#factual-notes-correctionsdont-regress).

Savedrake is a single-game, local-first **Dragon's Dogma 2** (Steam appid `2054970`) save
backup/restore utility for Windows (.NET Framework 4.8, MIT; a WPF app over a portable core
library). This roadmap keeps that scope deliberately narrow — see [Explicitly out of scope](#explicitly-out-of-scope).

---

## Guiding principles

1. **A backup tool must never be the thing that loses data.** Bias every change toward
   fail-safe behavior, verification, and an audit trail.
2. **Anything that downloads and runs code is a code-execution channel.** The Nexus-distributed
   build therefore makes no automatic network call at all; only the opt-in GitHub build self-updates,
   over HTTPS with an integrity check, and that build is never the one hosted on Nexus.
3. **Stay small and DD2-focused.** Match the genuinely useful features of the dedicated DD2
   tools; do not grow into a general multi-game manager.
4. **Preserve what already works:** transactional restore (staged swap + rollback), Zip-Slip
   guard, ReadOnly clearing, same-volume moves, game-running refusal, Steam-Cloud warning,
   Recycle-Bin undo, atomic backup writes (temp + rename), and the auto-backup retention cap.

---

## Factual notes (corrections — don't regress)

These were wrong or stale in earlier drafts and were corrected by the validation passes:

- **DD2 save slots:** the expansion from **one → three** save slots (each holding Autosave /
  Interim / Last Inn Rest) is **announced for the end-of-August-2026 title update — not shipped
  yet** (Title Update 3.1 shipped 2026-06-10). Do **not** retire the "single save" framing yet;
  do design **slot-aware** so the August change is a small migration, not a rewrite.
- **Hotkey ≠ keyboard hook (for AV purposes):** the active global hotkey uses `RegisterHotKey`;
  the low-level `WH_KEYBOARD_LL` hook is installed **only while recording** a hotkey (PR #14).
  The "keyboard hook trips antivirus" argument does **not** apply. (Being an *unsigned* exe is
  still a real AV/SmartScreen concern.)
- **.NET migration target is .NET 10 LTS, not .NET 8** (.NET 8 LTS reaches end-of-support
  2026-11-10). .NET Framework 4.8 remains supported and is fine for now — migration is **low
  priority**.
- **EV code-signing certs no longer auto-bypass SmartScreen.** Don't pay the EV premium for that
  reason; OV / Azure Artifact Signing / SignPath is sufficient.
- **A plain `SHA256SUMS` file is integrity, not authenticity.** If an attacker can replace
  `update.zip` they can replace the checksum file too. It only helps if it is **signed** or the
  updater verifies an attestation.
- **An installer is additive, not a replacement** for the portable zip.

---

## Priority stack (consensus of all three validation passes)

| # | Item | Why it's ranked here |
|---|------|----------------------|
| ~~P0~~ → **done** | ~~Authentic, signed auto-updater~~ **Update delivery reworked** | The bespoke self-updater that downloaded and ran code is **removed**. Delivery is now dual-build: the Nexus build makes no network call; the opt-in GitHub build self-updates over HTTPS with an integrity check. The original "code-execution channel" risk is closed for the Nexus build. See the P0 detail. |
| **P1** | **Backup integrity verification** | A backup that looks fine but is corrupt when finally needed is the worst failure mode for a save tool. |
| **P2** | **Logging + global exception handling** | `Program.cs` has none; a crash mid-backup/restore currently vanishes with no diagnostic trail. |
| **P3** | **Change-aware auto-backup** | Timer-only can miss the exact overwrite moment or spam redundant snapshots that rotate the good one out. |
| **P4** | **Pre-restore safety checkpoint** | Restore deletes the prior save on success, so restoring backup A silently discards the player's current state B. |
| **P5+** | Steam-Cloud preflight · code signing · pin/tiered retention · annotated restore points · installer · OSS hygiene · off-machine destination · etc. | High value, but gated behind the above or behind a maintainer decision (a signing key/cert). |

---

## Phased plan

| Phase | Theme | Scope | Rough effort |
|-------|-------|-------|--------------|
| **1** | Data-safety quick wins — **pure code, no external dependencies; each its own reviewed PR** | backup manifest + verify-on-create + corrupt flag · pre-restore checkpoint · pin + tiered retention · `FileSystemWatcher` + debounce (behind a setting) · the three exception hooks + rolling logs · replace deprecated `Uri.EscapeUriString` · `SECURITY.md` + release checklist · README pitch update | ~1–2 weekends |
| **2** | Trust & distribution — **mostly parked** | code signing (Authenticode — parked by the maintainer) · Inno Setup per-user installer · GitHub Immutable Releases + artifact attestations · dependency audit / Dependabot (done) | ~2–4 weeks |
| **3** | Bigger features | slot-aware restore (after the Aug-2026 update lands) · off-machine/configurable destinations · import/export with path remapping · optional encrypted exports · full DPI/accessibility pass · possible .NET 10 migration · package managers (winget/scoop/choco) — only **after** signing | ~1–3 months |

### Decisions required before Phase 2

The Ed25519 signed-update-manifest decisions no longer apply (the self-updater was removed; see P0).
What remains, if/when the maintainer revisits trust & distribution:

1. **Code signing — parked.** If revisited: SignPath Foundation (free for qualifying OSS) vs Azure
   Artifact Signing (≈ $9.99/month); note individual public-trust signing is currently US/Canada only.
2. **Installer** — whether to ship an Inno Setup per-user installer alongside the portable zip.

---

## Detailed items

Each item: **What** · **Why** · **How** (concrete, .NET Framework 4.8) · **Effort** · **Phase**.

### Data safety

#### P0 — Update delivery (reworked; the original bespoke self-updater is removed)
- **Status: DONE / changed direction.** The original P0 here was "sign the bespoke auto-updater."
  That updater has since been **removed entirely** (the separate `Savedrake-Updater.exe` project is
  gone). Update delivery is now a **dual-build** model, which both closes the original risk and fits
  Nexus's file rules (Nexus does not allow a hosted file that pulls executables from external sources):
  - **Nexus build (default, the one uploaded to Nexus):** makes **no automatic network call**. The
    startup check is a no-op; "Check for Updates" is user-initiated only and, if a newer release
    exists, just opens the Nexus downloads page. The app downloads and installs nothing, so the
    original "downloads and runs code" channel does not exist in this build.
  - **GitHub build (opt-in, published only on GitHub; built with `-p:Channel=GitHub`):** a one-click
    in-app updater — on the user's confirmation it downloads `update.zip` from the matching release,
    verifies it over HTTPS as a readable zip containing `Savedrake.exe` (**integrity**), swaps the
    files in place, and relaunches. This build is never uploaded to Nexus.
- **Residual gap (integrity, not authenticity):** the GitHub build's check is integrity-only — it
  does not cryptographically prove the publisher. The mitigation is **code-signing the exe** (see
  "Code signing" below), currently **parked** by the maintainer. A signed-manifest / Ed25519 scheme
  (the old plan) is **not** being pursued — disproportionate now that the Nexus build carries no
  updater and the GitHub channel is opt-in.

#### P1 — Backup integrity verification (manifest + verify-on-create)
- **What:** prove a backup is restorable at creation time and flag corrupt ones in the UI.
- **Why:** DD2 saves are multi-file and save-corruption is a known DD2 complaint; a silently
  corrupt backup defeats the tool. Today restore only checks "staged dir is non-empty"
  (`VerifyStagedDir`).
- **How:**
  - Write a `_savedrake/manifest.json` **inside each zip**: `manifestVersion`, `toolVersion`,
    `createdUtc`, `gameAppId`, `sourceRoot`, a **salted-hash** of the Steam user id, and `files[]`
    = `{ relativePath, length, lastWriteUtc, sha256 }`.
  - After `zip.Save(tempZip)` (and before the `File.Move` publish), verify in two layers:
    1. `Ionic.Zip.ZipFile.IsZipFile(path, testExtract: true)` — opens, reads metadata, **expands
       every entry and checks CRCs** (DotNetZip is already a dependency).
    2. Extract to a temp dir (re-using the existing Zip-Slip guard) and compare each file's
       length + SHA-256 against the manifest.
  - Surface a per-backup state in the history list: **validated / legacy-unvalidated / corrupt**;
    add a "Validate all backups" action and, for corrupt ones, "open containing folder" instead
    of failing only at restore time.
  - On restore, re-verify the manifest before committing the swap.
- **Caveat:** SHA-256 is **integrity**, not authenticity, unless the manifest is signed — fine
  for backups (the threat is bit-rot/truncation, not a malicious actor).
- **Effort:** medium. **Phase 1.**

#### P4 — Pre-restore safety checkpoint
- **What:** auto-snapshot the *current* live save (pinned, ~7-day retention) before every restore.
- **Why:** the transactional restore moves the old save aside and **deletes it on success**
  (Undo is disabled after a successful restore), so restoring backup A discards the player's
  current state B unless they remembered to back it up. This is a confirmed real gap.
- **How:** before staging a restore, run the normal backup pipeline into a reserved
  "pre-restore" category, pinned and exempt from auto-pruning.
- **Effort:** small. **Phase 1.**

#### P3 — Change-aware auto-backup
- **What:** back up when the save *changes*, not merely on a fixed interval.
- **Why:** the timer can miss the exact moment a save is overwritten or spam redundant identical
  snapshots that evict the meaningful one. Every dedicated DD2 tool is change-driven.
- **How:** add a `FileSystemWatcher` on `win64_save` as a **trigger only**, then debounce
  (~4–5 s of no writes) and confirm a stable file set (same names/sizes/mtimes, files openable)
  before zipping. Handle `Error`/buffer-overflow by falling back to the timer + a full scan.
  Suppress the watcher during restore. Keep the interval timer as a fallback. Skip the backup if
  the content hash matches the last one.
- **Effort:** medium. **Phase 1** (behind a setting).

#### Pin / favorites + tiered retention
- **What:** protect chosen backups from auto-pruning; smarter retention than a flat max-count.
- **Why:** a flat cap can delete the one pre-disaster snapshot the user actually wants.
- **How (suggested policy):** manual backups → keep forever; pinned → never pruned; auto →
  e.g. last 24 hourly + 14 daily + 8 weekly; pre-restore checkpoints → pinned 7 days; corrupt →
  never auto-deleted without confirmation.
- **Effort:** medium. **Phase 1.**

#### Off-machine / configurable backup destination
- **What:** let the backup root be any folder (a OneDrive/Dropbox/Drive-synced folder, a second
  drive, a NAS path).
- **Why:** local-only violates 3-2-1 — one disk failure/ransomware/profile-loss takes the saves
  *and* every backup. This is the cheap path to off-site copies without building cloud sync.
- **How:** a folder picker + warnings if the destination is inside the live save folder, on the
  same physical drive, or low on space. Keep the atomic temp+rename write and run post-write
  verification regardless of destination. (Do **not** build OAuth cloud-account sync.)
- **Effort:** small–medium. **Phase 3.**

#### Disk-space preflight + operation lock
- **What:** check free space (incl. staging + rollback) before any backup/restore/update; add an
  internal lock so those operations can't overlap (we already have a single-instance mutex).
- **Why:** transactional safety is only as good as the preflight; a disk-full mid-staging turns a
  recoverable op into a mess.
- **Effort:** small. **Phase 1–2.**

### Trust & distribution

#### Code signing (Authenticode)
- **Status: parked** (deferred by the maintainer for now). Listed for completeness; not an active
  priority.
- **What:** sign `Savedrake.exe` and any installer (RFC-3161 timestamped). There is no separate
  updater to sign anymore.
- **Why:** unsigned exes trigger SmartScreen "unknown publisher", **reset reputation on every
  release**, can be blocked outright by Windows 11 **Smart App Control**, and draw AV false
  positives. Signing lets reputation accrue to a stable identity.
- **How:** try **SignPath Foundation** (free for qualifying OSS, HSM-stored key, verifies the
  binary is built from the public repo) first; else **Azure Artifact Signing** (≈ $9.99/mo).
  Use **RSA** Authenticode (Smart App Control does **not** accept ECC). Don't buy EV solely for
  SmartScreen (it no longer bypasses). Keep publishing a portable zip + `SHA256SUMS` and label
  it "unsigned build" until signing exists.
- **Effort:** medium. **Phase 2.**

#### Installer (additive)
- **What:** offer a signed per-user installer alongside the portable zip.
- **How:** **Inno Setup 6.7.x stable** (Inno 7 was beta as of mid-2026), per-user install to
  `%LOCALAPPDATA%\Programs\Savedrake`, no admin, signed setup + uninstaller, Start-Menu shortcut,
  optional launch-at-login. (Velopack could fold installer + updater + delta updates together.)
- **Effort:** medium. **Phase 2.**

### Reliability & observability

#### P2 — Logging + global exception handling
- **What:** a rolling file log + all three WinForms exception hooks.
- **Why:** for a tool guarding irreplaceable saves, a silent crash or a failed restore with no
  log is unacceptable; bug reports (currently a personal email) become unactionable.
- **How:** early in `Program.Main`, before the first form:
  - `Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)`
  - `Application.ThreadException` (UI-thread exceptions)
  - `AppDomain.CurrentDomain.UnhandledException` (non-UI; log-then-exit)
  - `TaskScheduler.UnobservedTaskException` (**required** — the updater and auto-backup use
    `Task.Run`)
  - Serilog rolling file in `%APPDATA%\Savedrake\Logs` (daily, ~1 MB, retain ~14 files).
  - Log **events**, not save contents: backup/restore/update start-end, paths, backup id, zip
    validation result, game/Steam process state, rollback result, updater decisions, exceptions.
    **Redact/hash Steam IDs and paths.** Add an "Open log folder / Copy diagnostics" affordance.
  - Don't oversell it as "recover from all crashes" — for corrupted state, **log and exit** is
    correct.
- **Effort:** small–medium. **Phase 1.**

#### Crash reporting (optional, opt-in only)
- Prefer local logs + a "copy diagnostic bundle" button. If added later, make it **opt-in** and
  redact Steam IDs/paths (this is a privacy-sensitive local tool). Free zero-infra option:
  Windows Error Reporting LocalDumps. **Phase 3.**

### DD2-specific UX

- **Steam-Cloud preflight wizard:** turn the single pre-restore warning into a short guided flow
  (exit DD2 → optionally disable Steam Cloud for DD2 → restore → launch → choose **Local** at the
  conflict prompt), plus a post-restore reminder that Steam may re-sync. Don't automate
  destructive Cloud deletion.
- **Named / annotated restore points:** optional note/label per backup (manual or auto), shown as
  a column; optional tags ("pre-NG+", "before Art of Metamorphosis", "pre-Dragonsplague"); pin =
  favorite. Optional, privacy-conscious screenshots are a competitor feature.
- **All-profile discovery:** enumerate every `userdata\*\2054970\remote\win64_save`, show the
  detected Steam id/account, and make the active profile explicit before backup/restore.
- **Save-set fingerprinting / future-proofing:** back up the whole `win64_save` folder and
  manifest every relative path; don't hard-code today's filenames so the August 3-slot layout
  doesn't break the tool. Support "restore entire folder" first, "restore one slot" only after
  the on-disk mapping is confirmed.
- **"Corrupt save after death/restore" docs:** document the known recovery path (close game →
  set the intended save active → restart) as troubleshooting.
- **Ban-risk scoping (docs):** DD2 ships **Denuvo Anti-tamper**, not a competitive anti-cheat;
  there is no official ban policy for restoring your own *unedited* local saves. Document that
  Savedrake only copies/restores the user's own saves and never edits pawns/items/identity, and
  recommends disabling Steam Cloud during restore.

### OSS / supply-chain hygiene

- Add `SECURITY.md` (private vulnerability reporting — important for a tool that auto-downloads
  and runs code), `CONTRIBUTING.md`, a Keep-a-Changelog `CHANGELOG.md`, issue/PR templates, and a
  release checklist.
- Single SemVer source of truth — reconcile README (`v1.2.4`), the project folder name
  (`Savedrake v1.2.3`), the 3-part git tag, and the 4-part AssemblyVersion (the updater already
  special-cases this drift).
- CI/supply-chain: **pin GitHub Actions by full commit SHA**, least-privilege workflow
  permissions, Dependabot/NuGet audit, an SBOM, and track health via **OpenSSF Scorecard**.
  (CodeQL is already wired — keep it.) Note `DotNetZip 1.16.0` / the `Ionic.Zip` lineage is
  legacy — review the dependency.

### Polish

- **High-DPI:** reconcile the legacy `app.manifest` `<dpiAware>true</dpiAware>` with the
  `App.config` `PerMonitorV2` setting; test 100/150/200 % and multi-monitor moves; use
  `AutoScaleMode.Dpi`, avoid fixed pixel layouts.
- **Accessibility:** keyboard-only backup/restore paths, accessible control names, high-contrast,
  no sound-only alerts.
- **Deprecated API (done):** the only `Uri.EscapeUriString` use was in the now-removed updater; the
  current update-check code uses `Uri.EscapeDataString`. Nothing left to do here.
- **Encryption (later, conditional):** only for cloud/export destinations. DPAPI is **not**
  portable (CurrentUser-only); use a maintained AEAD (e.g. BouncyCastle) if portable encrypted
  archives are needed.

---

## Explicitly out of scope

All three validation passes agreed these add complexity without DD2-specific value and should
**not** be built: multi-game support, registry-save support, enterprise/cloud-account sync
(OAuth), account login, differential-backup complexity, default/SaaS telemetry, a full TUF
implementation with delegated roles and offline root ceremonies, GitHub-TLS certificate pinning,
and a .NET rewrite "for fashion." Package-manager distribution (winget/scoop/choco) is fine, but
only **after** code signing and a stable installer/update story exist.

---

## Update-delivery threat model (current dual-build model)

The bespoke self-updater is gone, so most of the old self-updater threat surface no longer applies.
What remains is scoped to the two channels:

| Channel / capability | Exposure today | Note |
|---|---|---|
| **Nexus build, any network attacker** | None — the build makes no network call (no download, no install) | The original "downloads and runs code" channel does not exist here |
| **GitHub build, network / hostile Wi-Fi** | Mitigated by HTTPS + an integrity check (readable zip containing `Savedrake.exe`) | Integrity, not authenticity |
| **GitHub build, tampered release asset / compromised repo token** | **Not** cryptographically detected (integrity-only) | Closed only by code-signing (parked) or a signed manifest (not pursued) |
| **Zip-slip / path traversal in a downloaded zip** | Guarded (the same full-path checks used on restore) | Already done |
| **Local malware / admin** | Out of scope for a local hobby tool | No full mitigation |

**Where this lands:** the highest-risk piece — an always-on auto-updater that runs code on every
user — is removed. The GitHub build's updater is opt-in and integrity-checked; raising it from
integrity to authenticity is a code-signing decision the maintainer has parked. A full
Ed25519-signed-manifest / TUF-lite scheme is intentionally **not** pursued (disproportionate for
the current shape).

---

## Sources

DD2 / saves: Capcom August Update Notice (dragonsdogma.com); RPG Site DD2 save-backup guide &
2026-06-10 patch notes; SteamDB app/patchnotes `2054970`; Steamworks Steam Cloud docs.
Competing tools: Ludusavi (mtkennerly/ludusavi); DD2 Save Manager (Nexus `mods/52`); Simple
SaveGame Manager (`mods/79`); Scum Bag (`mods/269`); PakL/savegame_manager; Game Backup Monitor.
Windows trust / signing: Microsoft Learn — SmartScreen reputation, Code-signing options,
Smart App Control code-signing (RSA, not ECC); SignPath Foundation; Azure Artifact Signing.
Updater security: The Update Framework spec & security docs; GitHub artifact attestations &
Immutable Releases; NetSparkleUpdater; Velopack.
.NET / WinForms: Microsoft Learn — `Application.ThreadException` / `AppDomain.UnhandledException`
/ `FileSystemWatcher` / High-DPI; .NET Framework support policy; .NET release lifecycle.
Backups: DotNetZip `IsZipFile(testExtract)`; 3-2-1 backup guidance (US Chamber / NIST NCCoE).
Hygiene: OpenSSF Scorecard; GitHub community health files & Actions secure-use (SHA pinning).
