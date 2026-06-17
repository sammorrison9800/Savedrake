# Savedrake Roadmap

> **Status:** planning only — nothing in this document is implemented yet.
> The items here are *enhancements*, not bug fixes. The known defects were already fixed
> in PRs #13–#27 (transactional restore, hook/timer lifecycle, updater hardening, the full
> code audit, and dead-code cleanup); the restore/update **core is considered solid** and
> should be preserved.
>
> **Provenance:** this roadmap was produced from a multi-angle web-research pass and then
> independently validated by **three separate external research passes** on **2026-06-17**
> (one fact-check, one clean-room independent study, one validate-and-expand). All three
> converged on the same top priority and the same scoping. Where they corrected earlier
> assumptions, the corrections are recorded under [Factual notes](#factual-notes-correctionsdont-regress).

Savedrake is a single-game, local-first **Dragon's Dogma 2** (Steam appid `2054970`) save
backup/restore utility for Windows (.NET Framework 4.8, C# 7.3, WinForms, MIT). This roadmap
keeps that scope deliberately narrow — see [Explicitly out of scope](#explicitly-out-of-scope).

---

## Guiding principles

1. **A backup tool must never be the thing that loses data.** Bias every change toward
   fail-safe behavior, verification, and an audit trail.
2. **The auto-updater is a code-execution channel** to every user's machine. It deserves the
   same rigor as the restore path (it currently has less).
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
| **P0** | **Authentic, signed auto-updater** | The updater downloads and *runs* code while only checking "is this a valid zip containing `Savedrake.exe`" — integrity, not authenticity. This is the single highest-leverage security fix. |
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
| **2** | Trust & distribution — **needs maintainer decisions first** (see below) | signed update manifest **or** NetSparkle migration · Authenticode signing · Inno Setup per-user installer · GitHub Immutable Releases + artifact attestations · dependency audit / Dependabot | ~2–4 weeks |
| **3** | Bigger features | slot-aware restore (after the Aug-2026 update lands) · off-machine/configurable destinations · import/export with path remapping · optional encrypted exports · full DPI/accessibility pass · possible .NET 10 migration · package managers (winget/scoop/choco) — only **after** signing | ~1–3 months |

### Decisions required before Phase 2

1. **Signing key custody** — generate an Ed25519 release-signing key; keep it offline on the
   maintainer machine, or as a GitHub Actions environment secret gated by manual approval?
2. **Code-signing path** — apply to **SignPath Foundation** (free for qualifying OSS) or pay
   **Azure Artifact Signing** (≈ $9.99/month). Eligibility note: individual public-trust signing
   is currently **US/Canada only** — confirm the maintainer qualifies, else fall back to an OV
   certificate.
3. **Accept the bootstrap caveat** — adding update-signature verification protects only updates
   delivered *after* a user is already on the new (verifying) version; existing installs still
   take one more unverified update first.

---

## Detailed items

Each item: **What** · **Why** · **How** (concrete, .NET Framework 4.8) · **Effort** · **Phase**.

### Data safety

#### P0 — Authentic, signed auto-updater
- **What:** verify a cryptographic signature/attestation of each update before installing, not
  just that the archive is a well-formed zip containing the exe.
- **Why:** HTTPS protects transport only. A compromised GitHub account/token, a tampered release
  asset, or a TLS-terminating proxy can serve a malicious `update.zip` that passes the current
  check and is then extracted over the install and launched. `VerifyUpdatePackage` in
  `updater/UpdaterForm.cs` openly documents that it is **not** an authenticity check.
- **How (minimum viable, "TUF-lite"):**
  - Publish per release: `manifest.json`, `manifest.json.sig`, `update.zip` (and an optional
    human `SHA256SUMS.txt`).
  - Sign the **exact UTF-8 bytes** of `manifest.json` with an **Ed25519** key (not a
    re-serialized object, not just the zip). Embed the **public key** as a constant in the app.
  - Manifest fields: `version`, `channel`, `createdUtc`, `expiresUtc`,
    `minSupportedUpdaterVersion`, `releaseTag`, `keyId`, and `files[]` =
    `{ name, url, length, sha256 }`.
  - **Verification order:** download manifest + sig → verify Ed25519 signature → reject expired
    metadata → reject `version <= current` → reject `version < highestSeenVersion` (persist it)
    → download zip with a **length cap** → verify exact SHA-256 + length → zip-path / required-
    entry checks → stage → *optionally* verify Authenticode on the staged `Savedrake.exe` → swap
    transactionally (the existing rollback install is good).
  - **Key rotation:** support `keyId`; a new public key is accepted only if the *old* key signs a
    rotation manifest.
  - **Don't** pin GitHub TLS certificates (rotation would brick updates) — app-level signatures
    make pinning unnecessary. **Don't** build full TUF (delegated roles / offline root
    ceremonies) — disproportionate for this tool.
  - **Alternative:** migrate updater logic to **NetSparkle** (built-in Ed25519, supports
    .NET Framework 4.6.2+) or **Velopack**, retiring the bespoke updater.
  - Also enable **GitHub Immutable Releases** + **artifact attestations** (SLSA provenance) as
    publishing-side hardening.
- **Tests:** modified zip with same name; modified manifest with invalid sig; replayed old valid
  manifest; expired manifest; zip larger than declared length; missing exe; zip-slip path; valid
  zip with wrong SHA; downgrade attempt; network failure between stage and swap.
- **Effort:** medium. **Phase 2.**

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
- **What:** sign `Savedrake.exe`, the updater, and any installer (RFC-3161 timestamped).
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
- **Deprecated API:** replace `Uri.EscapeUriString` in the updater with `Uri.EscapeDataString`
  (per-segment) or a built `Uri`/`UriBuilder` (`EscapeUriString` is obsolete and can corrupt
  URIs). **Phase 1.**
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

## Self-updater threat model (summary)

| Attacker capability | Control that helps | Minimum for a hobby tool? |
|---|---|---|
| Network / hostile Wi-Fi | signed manifest + SHA-256 + length (keep HTTPS) | **Yes** |
| Tampered release asset after publish | signed manifest (offline key) + Immutable Releases | **Yes** |
| Compromised repo token / account | offline/controlled signing key + manual release approval + attestations | **Yes** |
| Old-version replay (rollback) | persist `highestSeenVersion`; reject downgrades | **Yes** |
| Freeze (hide updates) | manifest `expiresUtc`; warn on stale metadata | **Yes (simple expiry)** |
| Mix-and-match | manifest signs all file hashes/lengths together | **Yes** |
| Oversized/slow download | declared length + max-size cap + streaming hash | **Yes** |
| Zip-slip / path traversal | existing full-path checks + case-insensitive dup-dest tests | **Already mostly done** |
| Compromised signing key | key rotation + protected storage + release audit | **Partial; full TUF = overkill** |
| Local malware/admin | out of scope for a hobby updater | **No full mitigation** |

**Minimum worth doing:** Ed25519-signed manifest, zip SHA-256 + length, downgrade protection,
metadata expiry, Authenticode-signed binaries/installer, GitHub Immutable Releases, and the
existing transactional install/rollback.

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
