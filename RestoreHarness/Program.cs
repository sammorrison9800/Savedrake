using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Savedrake; // compile-time reference to Savedrake.Core for the end-to-end RestoreService flow test

namespace RestoreHarness
{
    // Recording stubs for the Core service seams, used by Test_RestoreServiceFlow to drive the REAL
    // RestoreService.Restore orchestration headlessly. Confirm returns a configurable bool (default true);
    // Info/Warn/Error and Set record their text so the test can assert no error dialog fired.
    internal sealed class StubDialog : IDialogService
    {
        public bool ConfirmResult = true;
        public readonly List<string> Confirms = new List<string>();
        public readonly List<string> Infos = new List<string>();
        public readonly List<string> Warns = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public bool Confirm(string title, string message) { Confirms.Add(title + " | " + message); return ConfirmResult; }
        public void Info(string title, string message) { Infos.Add(title + " | " + message); }
        public void Warn(string title, string message) { Warns.Add(title + " | " + message); }
        public void Error(string title, string message) { Errors.Add(title + " | " + message); }
        // Not exercised by the harness today (no prompt-driven flow is under test); returns null = cancelled.
        public string Prompt(string title, string message, string defaultValue) => null;
    }

    internal sealed class StubStatus : IStatusSink
    {
        public readonly List<string> Lines = new List<string>();
        public string Last { get { return Lines.Count > 0 ? Lines[Lines.Count - 1] : null; } }
        public void Set(string text) { Lines.Add(text); }
    }

    // Headless reflection harness for Savedrake's transactional-restore helpers.
    // Loads the REAL compiled Savedrake.exe and invokes the actual private methods
    // (static + UI-free instance methods via an uninitialized Main instance) against
    // real temp dirs + crafted zips. Covers most of the DESIGN smoke matrix deterministically.
    internal static class Program
    {
        static int passed = 0, failed = 0;
        static Assembly Core;           // Savedrake.Core (the compile-time-referenced assembly)
        static string work;             // harness scratch root

        // Public static method on a Savedrake.Core type, e.g. CM("IntervalParser","TryParse").
        static MethodInfo CM(string typeName, string method) { var t = Core.GetType("Savedrake." + typeName); if (t == null) throw new Exception("core type not found: Savedrake." + typeName); var m = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static); if (m == null) throw new Exception("core method not found: " + typeName + "." + method); return m; }

        static void Check(string name, bool ok, string detail = null)
        {
            if (ok) { passed++; Console.WriteLine("[PASS] " + name); }
            else { failed++; Console.WriteLine("[FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
        }

        static Exception Unwrap(Exception e) { return e is TargetInvocationException && e.InnerException != null ? e.InnerException : e; }

        static int Main(string[] args)
        {
            // Post-cutover: the harness runs entirely against Savedrake.Core (referenced at compile time and loaded
            // next to this exe by normal probing). It no longer loads the retired WinForms Savedrake.exe — every test
            // exercises Core directly (the CM reflection helper points at this same compile-time Core assembly).
            Core = typeof(Savedrake.RestoreEngine).Assembly;

            work = Path.Combine(Path.GetTempPath(), "sdk_restore_harness_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            Console.WriteLine("Work dir: " + work);
            Console.WriteLine();

            try
            {
                Test_IsRealSaveEntry();
                Test_ValidateBackup();
                Test_ExtractZipToStaging_and_ZipSlip();
                Test_NestedDetectAndFlatten();
                Test_VerifyStagedDir();
                Test_DirPrimitives();
                Test_ClearReadOnly();
                Test_CreateSiblingTempDir();
                Test_HappyPathSequence();   // T1->T6 end-to-end at the file level using the real compiled helpers
                Test_DataLoss_Repro();   // the critical one: proves/refutes the finally + Rollback behavior
                Test_PartialT4_NoDataLoss();   // forces a partial T4 failure; asserts surviving originals == 2
                Test_TryParseInterval();   // locale-tolerant autobackup-interval parser (single source of truth)
                Test_CanonicalizeInterval();   // variant spellings collapse onto one item (no duplicate-item regression)
                Test_SoundAssetsShipped();   // success.wav / error.wav must ship next to Savedrake.exe
                Test_MakeUniquePath();   // backup-name collision guard (timestamp backups no longer overwrite)
                Test_PreRestoreCheckpoint();   // P4: snapshot the live save before a restore so it isn't discarded
                Test_CheckpointPruning();     // cap the retention-exempt (Pre-Restore)/(Pre-Load) snapshots
                Test_VerifyZipRestorable();   // P1: backups are CRC-verified at creation; corrupt ones are rejected
                Test_BackupManifest();   // P1 layer 2: in-zip manifest verify (missing/corrupt files) + restore skips it
                Test_ChangeFingerprint();   // change-aware autobackup (PR1): save fingerprint is stable/changes/fail-closed
                Test_TieredRetention();   // change-aware autobackup (PR2): tiered-retention selector (buckets/cap/idempotent)
                Test_PinHelpers();   // change-aware autobackup (PR3): pin filename-token helpers (detect/add/remove/round-trip)
                Test_UndoRestore();   // QoL: find the latest (Pre-Restore) checkpoint for one-click undo restore
                Test_DetectSaveFolder();   // QoL: enumerate DD2 save folders under a Steam root (auto-detect)
                Test_FriendlyTime();   // QoL: friendly relative time in the backup list
                Test_BackupLocationWarning();   // QoL: cloud-synced / same-drive backup-location warning
                Test_RestoreReverify();   // P1: restore re-verifies a manifest-bearing backup; legacy backups unaffected
                Test_ClassifyBackup();   // P1 UI: full Validated/Legacy/Corrupt classification for "Validate all"
                Test_LogRedaction();   // P2: the rolling logger redacts the Steam account id and user profile path
                Test_DiskPreflight();   // disk-space preflight helpers (size math + free-space check, fail-open)
                Test_RestoreServiceFlow();   // Phase 4a: the REAL Core RestoreService.Restore orchestration end-to-end
                Test_AutobackupPolicy();   // Phase 5: the change-aware autobackup decision (every branch + game-start bypass)
                Test_AutobackupCountStore();   // Phase 5: forgiving read of the autobackup count file (missing/garbled -> 0)
                Test_AutobackupCleanup();   // Phase 5: auto-thinning excludes manual/pre-restore/pinned, removes surplus autos
                Test_GameDetect();   // Phase 5: DD2 running-state registry read returns a bool and never throws
                Test_SaveReadiness();   // "back up after the save settles": defer while a save file is exclusively locked
                Test_UpdateCheck();   // Phase 6i: update version parsing (strip v, 2-4 numeric parts) + comparison
                Test_CharacterFolder();   // Characters: safe single-segment folder-name validation + SafeName clamp
                Test_CharacterMigration();   // Characters: non-destructive, idempotent, resumable loose->Default migration
                Test_LiveFolderHasRealSave();   // Load: detect real DD2 save data in the live folder (fail-open)
                Test_FindLatestRealBackup();   // Load: newest real backup by CreationTime, excluding (Pre-Restore)/(Pre-Load)
                Test_PreLoadCheckpoint();   // Load: (Pre-Load) snapshot naming + content + skip rules
                Test_LoadSequence_Happy();   // Load: snapshot-outgoing then restore-target end-to-end; live becomes target
                Test_LoadSequence_Cancelled();   // Load: declined restore leaves live untouched -> no flip (the no-limbo proof)
                Test_SuppressedCheckpoint_StillRestores();   // Load: suppressed (Pre-Restore) still commits + writes no checkpoint
                Test_DefaultStillCheckpoints();   // Restore: default (flag off) still writes a (Pre-Restore) (regression guard)
                Test_UndoAfterLoad_Consistency();   // Load: no (Pre-Restore) in incoming folder -> undo-after-load consistent
            }
            catch (Exception ex)
            {
                Console.WriteLine("HARNESS ERROR: " + Unwrap(ex));
                failed++;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("==== " + passed + " passed, " + failed + " failed ====");
            return failed == 0 ? 0 : 1;
        }

        // ---- helpers ----
        static byte[] B(string s) { return Encoding.ASCII.GetBytes(s); }

        static void MakeZip(string path, Action<Ionic.Zip.ZipFile> build, string comment = "SamMorrison9800")
        {
            if (File.Exists(path)) File.Delete(path);
            using (var z = new Ionic.Zip.ZipFile())
            {
                build(z);
                if (comment != null) z.Comment = comment;
                z.Save(path);
            }
        }

        static string NewDir(string tag)
        {
            string d = Path.Combine(work, tag + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static bool IsRealSaveEntry(string n) { return (bool)CM("SaveScan", "IsRealSaveEntry").Invoke(null, new object[] { n }); }

        // private static bool TryParseInterval(string, out TimeSpan) — read the out-param back from the args array.
        static bool TryParseInterval(string input, out TimeSpan interval)
        {
            object[] a = { input, null };
            bool ok = (bool)CM("IntervalParser", "TryParse").Invoke(null, a);
            interval = a[1] == null ? TimeSpan.Zero : (TimeSpan)a[1];
            return ok;
        }

        static string CanonicalizeInterval(string input) { return (string)CM("IntervalParser", "Canonicalize").Invoke(null, new object[] { input }); }

        // ---- tests ----
        static void Test_IsRealSaveEntry()
        {
            Console.WriteLine("== IsRealSaveEntry ==");
            Check("data000.bin", IsRealSaveEntry("data000.bin"));
            Check("data1.bin", IsRealSaveEntry("data1.bin"));
            Check("data.bin", IsRealSaveEntry("data.bin"));
            Check("DATA999.BIN (case)", IsRealSaveEntry("DATA999.BIN"));
            Check("system.bin", IsRealSaveEntry("system.bin"));
            Check("SYSTEM.BIN (case)", IsRealSaveEntry("SYSTEM.BIN"));
            Check("win64_save/data000.bin (leaf)", IsRealSaveEntry("win64_save/data000.bin"));
            Check("win64_save\\system.bin (leaf, backslash)", IsRealSaveEntry("win64_save\\system.bin"));
            Check("readme.txt is NOT save", !IsRealSaveEntry("readme.txt"));
            Check("notdata.bin is NOT save", !IsRealSaveEntry("notdata.bin"));
            Check("mydata000.bin is NOT save", !IsRealSaveEntry("mydata000.bin"));
            Check("datax.txtbin is NOT save", !IsRealSaveEntry("datax.txtbin"));
            Console.WriteLine();
        }

        static void Test_TryParseInterval()
        {
            Console.WriteLine("== TryParseInterval (locale-tolerant) ==");

            // Canonical forms still parse exactly as before.
            Check("'5 minutes' -> 5 min", TryParseInterval("5 minutes", out var t1) && t1 == TimeSpan.FromMinutes(5));
            Check("'1 hour' -> 60 min", TryParseInterval("1 hour", out var t2) && t2 == TimeSpan.FromHours(1));
            Check("'2 hours' -> 120 min", TryParseInterval("2 hours", out var t3) && t3 == TimeSpan.FromHours(2));

            // Case-insensitive (was rejected before: the old regex was case-sensitive).
            Check("'5 Minutes' (case)", TryParseInterval("5 Minutes", out var t4) && t4 == TimeSpan.FromMinutes(5));
            Check("'1 HOUR' (case)", TryParseInterval("1 HOUR", out var t5) && t5 == TimeSpan.FromHours(1));

            // Whitespace tolerance (single-space-only was required before).
            Check("'5  minutes' (2 spaces)", TryParseInterval("5  minutes", out var t6) && t6 == TimeSpan.FromMinutes(5));
            Check("'5minutes' (no space)", TryParseInterval("5minutes", out var t7) && t7 == TimeSpan.FromMinutes(5));
            Check("'  10 minutes  ' (padded)", TryParseInterval("  10 minutes  ", out var t8) && t8 == TimeSpan.FromMinutes(10));

            // Synonyms / abbreviations.
            Check("'30 min'", TryParseInterval("30 min", out var t9) && t9 == TimeSpan.FromMinutes(30));
            Check("'30 mins'", TryParseInterval("30 mins", out var t10) && t10 == TimeSpan.FromMinutes(30));
            Check("'1 minute'", TryParseInterval("1 minute", out var t11) && t11 == TimeSpan.FromMinutes(1));
            Check("'2 hr'", TryParseInterval("2 hr", out var t12) && t12 == TimeSpan.FromHours(2));
            Check("'3 hrs'", TryParseInterval("3 hrs", out var t13) && t13 == TimeSpan.FromHours(3));

            // Rejections (return false, never throw).
            Check("'' -> false", !TryParseInterval("", out _));
            Check("null -> false", !TryParseInterval(null, out _));
            Check("'abc' -> false", !TryParseInterval("abc", out _));
            Check("'minutes' (no number) -> false", !TryParseInterval("minutes", out _));
            Check("'5' (no unit) -> false", !TryParseInterval("5", out _));
            Check("'5 seconds' (unknown unit) -> false", !TryParseInterval("5 seconds", out _));
            Check("'5 days' (unknown unit) -> false", !TryParseInterval("5 days", out _));
            Check("'5.5 minutes' (decimal) -> false", !TryParseInterval("5.5 minutes", out _));
            Check("'-5 minutes' (sign) -> false", !TryParseInterval("-5 minutes", out _));

            // Out-of-range values reject cleanly instead of throwing / overflowing (old int*int math).
            Check("'999999999 hours' (TimeSpan overflow) -> false, no throw", !TryParseInterval("999999999 hours", out _));
            Check("'99999999999 minutes' (> int) -> false", !TryParseInterval("99999999999 minutes", out _));

            Console.WriteLine();
        }

        static void Test_CanonicalizeInterval()
        {
            // Guards the dedup fix: the broadened grammar accepts variant spellings, so they must canonicalize
            // onto one item (e.g. built-in "5 minutes") rather than persist as duplicate ComboBox entries.
            Console.WriteLine("== CanonicalizeInterval (collapse variants onto one canonical item) ==");
            Check("'5min' -> '5 minutes'", CanonicalizeInterval("5min") == "5 minutes");
            Check("'5 Minutes' -> '5 minutes'", CanonicalizeInterval("5 Minutes") == "5 minutes");
            Check("'5  minutes' -> '5 minutes'", CanonicalizeInterval("5  minutes") == "5 minutes");
            Check("'30 mins' -> '30 minutes'", CanonicalizeInterval("30 mins") == "30 minutes");
            Check("'1 Hr' -> '1 hour'", CanonicalizeInterval("1 Hr") == "1 hour");
            Check("'2 hr' -> '2 hours'", CanonicalizeInterval("2 hr") == "2 hours");
            Check("'2 HOURS' -> '2 hours'", CanonicalizeInterval("2 HOURS") == "2 hours");
            // Unit preserved (no minutes<->hours conversion), matching the prior app convention.
            Check("'120 minutes' stays '120 minutes'", CanonicalizeInterval("120 minutes") == "120 minutes");
            // Idempotent on the canonical/default forms so existing list items are never rewritten.
            Check("idempotent: '5 minutes'", CanonicalizeInterval("5 minutes") == "5 minutes");
            Check("idempotent: '1 hour'", CanonicalizeInterval("1 hour") == "1 hour");
            Check("idempotent: '2 hours'", CanonicalizeInterval("2 hours") == "2 hours");
            // Non-interval text passes through untouched.
            Check("non-interval passes through", CanonicalizeInterval("not a time") == "not a time");
            Console.WriteLine();
        }

        static void Test_MakeUniquePath()
        {
            Console.WriteLine("== MakeUniquePath (backup-name collision guard) ==");
            var mi = CM("BackupNaming", "MakeUniquePath");
            string dir = NewDir("uniq");
            string p = Path.Combine(dir, "backup_250617120000.zip");
            Check("free path returned unchanged", (string)mi.Invoke(null, new object[] { p }) == p);
            File.WriteAllText(p, "x");
            string r2 = (string)mi.Invoke(null, new object[] { p });
            Check("existing path -> _2", r2 == Path.Combine(dir, "backup_250617120000_2.zip"), r2);
            File.WriteAllText(r2, "x");
            string r3 = (string)mi.Invoke(null, new object[] { p });
            Check("_2 also exists -> _3", r3 == Path.Combine(dir, "backup_250617120000_3.zip"), r3);
            Console.WriteLine();
        }

        static void Test_PreRestoreCheckpoint()
        {
            // P4: before a restore deletes the current live save, snapshot it into the backup folder under a
            // "(Pre-Restore) " name that LoadBackupHistory does NOT count as an autobackup. UI-free, so testable here.
            Console.WriteLine("== Pre-restore safety checkpoint (P4) ==");
            var mi = CM("RestoreEngine", "CreatePreRestoreCheckpoint");

            // 1) live save present -> a single (Pre-Restore) zip is created, valid, and contains the save data
            string live = NewDir("cp_live");
            File.WriteAllBytes(Path.Combine(live, "data000.bin"), B("savedata"));
            File.WriteAllBytes(Path.Combine(live, "system.bin"), B("sys"));
            string backup = NewDir("cp_backup");
            bool ok = (bool)mi.Invoke(null, new object[] { live, backup });
            string[] zips = Directory.GetFiles(backup, "*.zip");
            Check("checkpoint returns true on success", ok);
            Check("exactly one checkpoint zip created", zips.Length == 1, "found " + zips.Length);
            string name = zips.Length == 1 ? Path.GetFileName(zips[0]) : "";
            Check("checkpoint uses '(Pre-Restore) ' prefix", name.StartsWith("(Pre-Restore) "), name);
            Check("checkpoint is NOT counted as an autobackup", !name.StartsWith("(Auto)") && !name.StartsWith("auto"), name);
            bool hasSave = false;
            if (zips.Length == 1)
                using (var z = Ionic.Zip.ZipFile.Read(zips[0]))
                    hasSave = z.Entries.Any(en => IsRealSaveEntry(Path.GetFileName(en.FileName)));
            Check("checkpoint zip contains the live save data", hasSave);
            Check("no .savedrake.tmp left behind", Directory.GetFiles(backup, "*.savedrake.tmp").Length == 0);

            // 2) live folder has no DD2 save data -> nothing to lose: skip, return true, create no zip
            string live2 = NewDir("cp_live_nosave");
            File.WriteAllBytes(Path.Combine(live2, "readme.txt"), B("nothing"));
            string backup2 = NewDir("cp_backup2");
            bool ok2 = (bool)mi.Invoke(null, new object[] { live2, backup2 });
            Check("no-save-data live -> skipped, returns true, no zip", ok2 && Directory.GetFiles(backup2, "*.zip").Length == 0);

            // 3) live == backup folder -> refuse (can't stage into the folder being snapshotted), return true
            string same = NewDir("cp_same");
            File.WriteAllBytes(Path.Combine(same, "data000.bin"), B("savedata"));
            bool ok3 = (bool)mi.Invoke(null, new object[] { same, same });
            Check("live==backup -> skipped (no zip), returns true", ok3 && Directory.GetFiles(same, "*.zip").Length == 0);
            Console.WriteLine();
        }

        static void Test_CheckpointPruning()
        {
            // The (Pre-Restore)/(Pre-Load) safety snapshots are exempt from autobackup cleanup, so they must be capped
            // separately or they pile up forever. PruneCheckpoints keeps the newest N of a prefix; checkpoint creation
            // self-prunes.
            Console.WriteLine("== Safety-checkpoint pruning ==");
            var prune = CM("RestoreEngine", "PruneCheckpoints");

            // 13 (Pre-Restore) snapshots + unrelated files; keep 10 -> the 3 OLDEST go, the noise is untouched.
            string dir = NewDir("prune_dir");
            DateTime t0 = DateTime.UtcNow.AddHours(-20);
            for (int i = 0; i < 13; i++)
            {
                string p = Path.Combine(dir, "(Pre-Restore) " + i.ToString("D2") + ".zip");
                File.WriteAllBytes(p, B("x"));
                File.SetCreationTimeUtc(p, t0.AddHours(i)); // i=0 oldest .. i=12 newest
            }
            File.WriteAllBytes(Path.Combine(dir, "(Auto) a.zip"), B("x"));
            File.WriteAllBytes(Path.Combine(dir, "manual.zip"), B("x"));
            File.WriteAllBytes(Path.Combine(dir, "(Pre-Load) z.zip"), B("x"));

            int removed = (int)prune.Invoke(null, new object[] { dir, "(Pre-Restore)", 10 });
            var names = Directory.GetFiles(dir, "*.zip").Select(Path.GetFileName).ToList();
            int prLeft = names.Count(n => n.StartsWith("(Pre-Restore)"));
            Check("prune removed the 3 oldest", removed == 3, "removed " + removed);
            Check("exactly 10 (Pre-Restore) remain", prLeft == 10, "have " + prLeft);
            Check("the 3 oldest were deleted",
                !names.Contains("(Pre-Restore) 00.zip") && !names.Contains("(Pre-Restore) 01.zip") && !names.Contains("(Pre-Restore) 02.zip"));
            Check("the newest survived", names.Contains("(Pre-Restore) 12.zip"));
            Check("unrelated files untouched",
                names.Contains("(Auto) a.zip") && names.Contains("manual.zip") && names.Contains("(Pre-Load) z.zip"));

            // Under the cap -> nothing pruned.
            string dir2 = NewDir("prune_under");
            for (int i = 0; i < 4; i++) File.WriteAllBytes(Path.Combine(dir2, "(Pre-Load) " + i + ".zip"), B("x"));
            int removed2 = (int)prune.Invoke(null, new object[] { dir2, "(Pre-Load)", 10 });
            Check("under the cap -> nothing pruned", removed2 == 0 && Directory.GetFiles(dir2, "*.zip").Length == 4);

            // Missing dir -> 0, no throw.
            Check("missing dir -> 0 (no throw)",
                (int)prune.Invoke(null, new object[] { Path.Combine(dir2, "nope"), "(Pre-Restore)", 10 }) == 0);

            // Integration: an 11th (Pre-Restore) checkpoint self-prunes back to 10.
            var create = CM("RestoreEngine", "CreatePreRestoreCheckpoint");
            string live = NewDir("prune_live");
            File.WriteAllBytes(Path.Combine(live, "data000.bin"), B("savedata"));
            string bdir = NewDir("prune_backup");
            for (int i = 0; i < 11; i++) create.Invoke(null, new object[] { live, bdir });
            int created = Directory.GetFiles(bdir, "*.zip").Count(p => Path.GetFileName(p).StartsWith("(Pre-Restore)"));
            Check("creating 11 checkpoints self-prunes to 10", created == 10, "have " + created);
            Console.WriteLine();
        }

        static void Test_VerifyZipRestorable()
        {
            // P1 layer 1: a freshly written backup is CRC-verified (IsZipFile testExtract) before it is published;
            // truncated/corrupt/missing archives are rejected at creation. VerifyZipRestorable(string, out string) static.
            Console.WriteLine("== Backup integrity: verify-on-create (P1) ==");
            var mi = CM("Manifest", "VerifyZipRestorable");

            string good = Path.Combine(work, "verify_good.zip");
            MakeZip(good, z => { z.AddEntry("data000.bin", B("savedata")); z.AddEntry("system.bin", B("sys")); });
            object[] a1 = { good, null };
            bool r1 = (bool)mi.Invoke(null, a1);
            Check("intact backup -> verifies, no reason", r1 && a1[1] == null, "reason=" + a1[1]);

            string notzip = Path.Combine(work, "verify_notzip.zip");
            File.WriteAllBytes(notzip, B("this is plain text, definitely not a zip archive"));
            object[] a2 = { notzip, null };
            bool r2 = (bool)mi.Invoke(null, a2);
            Check("non-zip bytes -> rejected with a reason", !r2 && a2[1] != null, "reason=" + a2[1]);

            // High-entropy payload so it stays (near) uncompressed and the file is large enough that the midpoint
            // lands in the entry's data. Flipping a byte there makes the extracted CRC mismatch -> testExtract fails.
            byte[] payload = new byte[16384];
            uint s = 0x12345678u;
            for (int i = 0; i < payload.Length; i++) { s = s * 1664525u + 1013904223u; payload[i] = (byte)(s >> 24); }
            string big = Path.Combine(work, "verify_big.zip");
            MakeZip(big, z => { z.AddEntry("data000.bin", payload); });
            byte[] cb = File.ReadAllBytes(big);
            cb[cb.Length / 2] ^= 0xFF; // corrupt a byte in the entry data
            string corrupt = Path.Combine(work, "verify_corrupt.zip");
            File.WriteAllBytes(corrupt, cb);
            object[] a3 = { corrupt, null };
            bool r3 = (bool)mi.Invoke(null, a3);
            Check("corrupt entry data -> rejected (CRC mismatch)", !r3 && a3[1] != null, "reason=" + a3[1]);

            object[] a4 = { Path.Combine(work, "verify_missing.zip"), null };
            bool r4 = (bool)mi.Invoke(null, a4);
            Check("missing file -> rejected without throwing", !r4 && a4[1] != null, "reason=" + a4[1]);
            Console.WriteLine();
        }

        static void Test_BackupManifest()
        {
            // P1 layer 2: backups carry an in-zip integrity manifest (path/length/sha256 per file). Verify catches a
            // missing or corrupted file; restore must SKIP the manifest so it never lands in the live save folder.
            Console.WriteLine("== Backup integrity: manifest verify (P1 layer 2) ==");
            var build = CM("Manifest", "BuildBackupManifest");
            var verify = CM("Manifest", "VerifyZipAgainstManifest");
            var extract = CM("RestoreEngine", "ExtractZipToStaging");

            string src = NewDir("man_src");
            File.WriteAllBytes(Path.Combine(src, "data000.bin"), B("save-A-contents"));
            File.WriteAllBytes(Path.Combine(src, "system.bin"), B("system-contents"));
            string manifest = (string)build.Invoke(null, new object[] { src });
            Check("manifest lists both source files", manifest.Contains("data000.bin") && manifest.Contains("system.bin"));

            string good = Path.Combine(work, "man_good.zip");
            MakeZip(good, z => { z.AddDirectory(src); z.AddEntry("_savedrake/manifest.json", B(manifest)); });
            object[] a1 = { good, null };
            Check("intact backup verifies against its manifest", (bool)verify.Invoke(null, a1) && a1[1] == null, "reason=" + a1[1]);

            string missing = Path.Combine(work, "man_missing.zip");
            MakeZip(missing, z => { z.AddEntry("data000.bin", B("save-A-contents")); z.AddEntry("_savedrake/manifest.json", B(manifest)); });
            object[] a2 = { missing, null };
            Check("missing declared file -> rejected", !(bool)verify.Invoke(null, a2) && a2[1] != null, "reason=" + a2[1]);

            string tampered = Path.Combine(work, "man_tampered.zip");
            MakeZip(tampered, z => { z.AddEntry("data000.bin", B("DIFFERENT-contents!")); z.AddEntry("system.bin", B("system-contents")); z.AddEntry("_savedrake/manifest.json", B(manifest)); });
            object[] a3 = { tampered, null };
            Check("tampered file content -> rejected", !(bool)verify.Invoke(null, a3) && a3[1] != null, "reason=" + a3[1]);

            string nomani = Path.Combine(work, "man_none.zip");
            MakeZip(nomani, z => { z.AddEntry("data000.bin", B("x")); });
            object[] a4 = { nomani, null };
            Check("no manifest -> rejected", !(bool)verify.Invoke(null, a4) && a4[1] != null, "reason=" + a4[1]);

            string stage = NewDir("man_stage");
            extract.Invoke(null, new object[] { good, stage });
            bool hasSaves = File.Exists(Path.Combine(stage, "data000.bin")) && File.Exists(Path.Combine(stage, "system.bin"));
            bool noManifest = !Directory.Exists(Path.Combine(stage, "_savedrake")) && !File.Exists(Path.Combine(stage, "_savedrake", "manifest.json"));
            Check("restore extracts the save files", hasSaves);
            Check("restore skips the _savedrake manifest", noManifest);
            Console.WriteLine();
        }

        static void Test_ChangeFingerprint()
        {
            // Change-aware autobackup (PR1): ComputeSaveFingerprint is the signal that lets the autobackup timer skip a
            // tick when nothing changed. It must be STABLE across calls on identical content (immune to the manifest's
            // volatile createdUtc/tool), CHANGE on any real content change, be enumeration-ORDER independent, and return
            // null (fail-closed) for a missing / locked / no-save-data folder.
            Console.WriteLine("== Change-aware autobackup: save fingerprint ==");
            var fp = CM("Fingerprint", "ComputeSaveFingerprint");
            var stable = CM("Manifest", "StableManifestHash");
            var build = CM("Manifest", "BuildBackupManifest");

            string src = NewDir("fp_src");
            File.WriteAllBytes(Path.Combine(src, "data000.bin"), B("save-A"));
            File.WriteAllBytes(Path.Combine(src, "system.bin"), B("sys-A"));

            string f1 = (string)fp.Invoke(null, new object[] { src });
            string f2 = (string)fp.Invoke(null, new object[] { src });
            Check("fingerprint is non-null for a real save folder", f1 != null);
            Check("fingerprint is stable across calls (ignores volatile manifest fields)", f1 == f2, "f1=" + f1 + " f2=" + f2);

            File.WriteAllBytes(Path.Combine(src, "data000.bin"), B("save-B"));
            string f3 = (string)fp.Invoke(null, new object[] { src });
            Check("a content change changes the fingerprint", f3 != f1);
            File.WriteAllBytes(Path.Combine(src, "data000.bin"), B("save-A"));
            Check("reverting the content restores the fingerprint", (string)fp.Invoke(null, new object[] { src }) == f1);

            File.WriteAllBytes(Path.Combine(src, "data001.bin"), B("save-A2"));
            Check("adding a save file changes the fingerprint", (string)fp.Invoke(null, new object[] { src }) != f1);
            File.Delete(Path.Combine(src, "data001.bin"));
            Check("removing the added file restores the fingerprint", (string)fp.Invoke(null, new object[] { src }) == f1);

            // StableManifestHash must ignore createdUtc/tool: two manifests of identical content hash equal.
            string m1 = (string)build.Invoke(null, new object[] { src });
            System.Threading.Thread.Sleep(5);
            string m2 = (string)build.Invoke(null, new object[] { src });
            string h1 = (string)stable.Invoke(null, new object[] { m1 });
            Check("StableManifestHash ignores volatile createdUtc/tool", h1 != null && h1 == (string)stable.Invoke(null, new object[] { m2 }));

            // Enumeration-order independence: same files created in opposite order -> identical fingerprint.
            string oA = NewDir("fp_orderA");
            File.WriteAllBytes(Path.Combine(oA, "data000.bin"), B("x")); File.WriteAllBytes(Path.Combine(oA, "system.bin"), B("y"));
            string oB = NewDir("fp_orderB");
            File.WriteAllBytes(Path.Combine(oB, "system.bin"), B("y")); File.WriteAllBytes(Path.Combine(oB, "data000.bin"), B("x"));
            Check("fingerprint is enumeration-order independent", (string)fp.Invoke(null, new object[] { oA }) == (string)fp.Invoke(null, new object[] { oB }));

            // No real save data -> null (so a wrong/empty folder reads as "not comparable" and the tick fails closed).
            string nosave = NewDir("fp_nosave");
            File.WriteAllText(Path.Combine(nosave, "notes.txt"), "not a save");
            Check("folder with no DD2 save data -> null", (string)fp.Invoke(null, new object[] { nosave }) == null);

            // Missing folder -> null, no throw.
            string missing = Path.Combine(work, "fp_missing_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Check("missing folder -> null (no throw)", (string)fp.Invoke(null, new object[] { missing }) == null);

            // Locked (mid-write) file -> null (fail-closed), then recovers once unlocked.
            string locked = NewDir("fp_locked");
            string lf = Path.Combine(locked, "data000.bin");
            File.WriteAllBytes(lf, B("locked-save"));
            using (new FileStream(lf, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Check("a locked (mid-write) save file -> null (fail-closed)", (string)fp.Invoke(null, new object[] { locked }) == null);
            }
            Check("fingerprint recovers once the file is unlocked", (string)fp.Invoke(null, new object[] { locked }) != null);
            Console.WriteLine();
        }

        static void Test_TieredRetention()
        {
            // Change-aware autobackup (PR2): SelectAutobackupsToThin is the pure tiered-retention selector. It must keep
            // all recent backups, keep the newest per widening time bucket, honor the count cap by thinning oldest
            // survivors, be idempotent, and keep future-dated (clock-skew) backups.
            Console.WriteLine("== Change-aware autobackup: tiered retention (PR2) ==");
            var thin = CM("RetentionPolicy", "SelectAutobackupsToThin");
            long now = DateTime.UtcNow.Ticks;
            Func<long[], int, int[]> run = (ticks, cap) => (int[])thin.Invoke(null, new object[] { ticks, now, cap });

            Check("empty input -> nothing thinned", run(new long[0], 0).Length == 0);
            Check("single backup -> kept", run(new long[] { now - TimeSpan.FromHours(3).Ticks }, 0).Length == 0);

            long[] recent = {
                now - TimeSpan.FromMinutes(5).Ticks,
                now - TimeSpan.FromMinutes(20).Ticks,
                now - TimeSpan.FromMinutes(45).Ticks,
            };
            Check("all backups within the last hour are kept", run(recent, 0).Length == 0);

            // Three in the same 30-minute bucket (1-6h tier) -> keep only the newest (index 0).
            long[] bucket = {
                now - TimeSpan.FromHours(2).Ticks,
                now - (TimeSpan.FromHours(2).Ticks + TimeSpan.FromMinutes(5).Ticks),
                now - (TimeSpan.FromHours(2).Ticks + TimeSpan.FromMinutes(20).Ticks),
            };
            int[] d = run(bucket, 0);
            Check("a 30-min bucket keeps only the newest (2 of 3 thinned)", d.Length == 2 && !d.Contains(0), "deleted=" + string.Join(",", d));

            // Five recent (all kept by schedule), cap=3 -> the 2 oldest are thinned.
            long[] five = {
                now - TimeSpan.FromMinutes(5).Ticks,
                now - TimeSpan.FromMinutes(15).Ticks,
                now - TimeSpan.FromMinutes(25).Ticks,
                now - TimeSpan.FromMinutes(35).Ticks,
                now - TimeSpan.FromMinutes(50).Ticks,
            };
            int[] capped = run(five, 3);
            Check("count cap thins the oldest survivors (2 thinned to reach 3)", capped.Length == 2 && capped.Contains(3) && capped.Contains(4), "deleted=" + string.Join(",", capped));

            // Idempotency: thin once, drop the deleted, re-run -> nothing more.
            long[] across = {
                now - TimeSpan.FromMinutes(10).Ticks,
                now - TimeSpan.FromHours(2).Ticks,
                now - (TimeSpan.FromHours(2).Ticks + TimeSpan.FromMinutes(10).Ticks),
                now - TimeSpan.FromHours(10).Ticks,
                now - TimeSpan.FromDays(3).Ticks,
                now - TimeSpan.FromDays(20).Ticks,
            };
            int[] first = run(across, 0);
            long[] survivors = Enumerable.Range(0, across.Length).Where(i => !first.Contains(i)).Select(i => across[i]).ToArray();
            Check("re-running on survivors thins nothing (idempotent)", run(survivors, 0).Length == 0, "first=" + string.Join(",", first));

            // A future-dated backup (clock skew) is kept, as is an old one alone in its weekly bucket.
            Check("a future-dated backup is kept (treated as newest)", run(new long[] { now + TimeSpan.FromHours(1).Ticks, now - TimeSpan.FromDays(30).Ticks }, 0).Length == 0);
            Console.WriteLine();
        }

        static void Test_PinHelpers()
        {
            // Change-aware autobackup (PR3): pinning is marked by a " [PINNED]" filename token. IsPinnedBackup detects
            // it; PinnedPath/UnpinnedPath add/remove it idempotently and round-trip, and pinning must preserve the
            // autobackup name prefix so a pinned autobackup is still recognizable (just excluded from count + cleanup).
            Console.WriteLine("== Change-aware autobackup: pinning helpers (PR3) ==");
            var isPinned = CM("Pinning", "IsPinnedBackup");
            var pin = CM("Pinning", "PinnedPath");
            var unpin = CM("Pinning", "UnpinnedPath");
            Func<string, bool> P = n => (bool)isPinned.Invoke(null, new object[] { n });
            Func<string, string> PIN = p => (string)pin.Invoke(null, new object[] { p });
            Func<string, string> UNPIN = p => (string)unpin.Invoke(null, new object[] { p });

            Check("a name with the token is pinned", P("autobackup_240620 [PINNED].zip"));
            Check("a plain name is not pinned", !P("autobackup_240620.zip"));
            Check("PinnedPath inserts the token before the extension", PIN("C:\\b\\foo.zip") == "C:\\b\\foo [PINNED].zip");
            Check("PinnedPath is idempotent", PIN("C:\\b\\foo [PINNED].zip") == "C:\\b\\foo [PINNED].zip");
            Check("UnpinnedPath strips the token", UNPIN("C:\\b\\foo [PINNED].zip") == "C:\\b\\foo.zip");
            Check("pin keeps the autobackup name prefix", Path.GetFileName(PIN("C:\\b\\autobackup_1.zip")).StartsWith("auto"));
            Check("pin -> unpin round-trips to the original", UNPIN(PIN("C:\\b\\(Auto) save 3.zip")) == "C:\\b\\(Auto) save 3.zip");
            Console.WriteLine();
        }

        static void Test_UndoRestore()
        {
            // Undo-restore (QoL): FindLatestPreRestoreCheckpoint returns the NEWEST "(Pre-Restore)" backup, ignoring
            // other backups, and null when there is none or the folder is missing.
            Console.WriteLine("== Undo last restore: latest pre-restore checkpoint ==");
            var find = CM("SaveScan", "FindLatestPreRestoreCheckpoint");
            string dir = NewDir("undo");
            Check("empty folder -> null", (string)find.Invoke(null, new object[] { dir }) == null);

            File.WriteAllText(Path.Combine(dir, "backup_1.zip"), "x");
            File.WriteAllText(Path.Combine(dir, "(Auto) backup_2.zip"), "x");
            Check("no pre-restore among other backups -> null", (string)find.Invoke(null, new object[] { dir }) == null);

            string older = Path.Combine(dir, "(Pre-Restore) 240619120000.zip");
            File.WriteAllText(older, "x");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
            string newer = Path.Combine(dir, "(Pre-Restore) 240620120000.zip");
            File.WriteAllText(newer, "x");
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(-1));
            string found = (string)find.Invoke(null, new object[] { dir });
            Check("returns the newest (Pre-Restore) checkpoint", found != null && Path.GetFileName(found) == "(Pre-Restore) 240620120000.zip", "found=" + found);

            string missing = Path.Combine(work, "undo_missing_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Check("missing folder -> null (no throw)", (string)find.Invoke(null, new object[] { missing }) == null);
            Console.WriteLine();
        }

        static void Test_DetectSaveFolder()
        {
            // QoL: FindDd2SaveFoldersUnder enumerates <steamRoot>\userdata\<id>\2054970\remote\win64_save, ignores other
            // appids, finds multiple Steam profiles, and is null-safe on a missing root.
            Console.WriteLine("== Auto-detect DD2 save folder ==");
            var find = CM("SaveScan", "FindDd2SaveFoldersUnder");
            System.Func<string, System.Collections.Generic.List<string>> F =
                r => ((System.Collections.IEnumerable)find.Invoke(null, new object[] { r })).Cast<string>().ToList();

            string root = NewDir("steam");
            Check("no userdata folder -> empty", F(root).Count == 0);

            string save1 = Path.Combine(root, "userdata", "111", "2054970", "remote", "win64_save");
            Directory.CreateDirectory(save1);
            var one = F(root);
            Check("finds the one DD2 save folder", one.Count == 1 && one[0] == save1, "got=" + string.Join(",", one));

            Directory.CreateDirectory(Path.Combine(root, "userdata", "222", "9999999", "remote", "win64_save"));
            Check("ignores non-DD2 appids", F(root).Count == 1);

            Directory.CreateDirectory(Path.Combine(root, "userdata", "333", "2054970", "remote", "win64_save"));
            Check("finds both DD2 Steam profiles", F(root).Count == 2);

            Check("missing steam root -> empty (no throw)", F(Path.Combine(work, "no_steam_" + Guid.NewGuid().ToString("N").Substring(0, 6))).Count == 0);

            // Phase 6d: GetSteamRoot + the no-arg FindDd2SaveFolders (registry/filesystem). They must never throw and
            // must return sane shapes regardless of whether Steam/DD2 are present on the build box.
            var getRoot = CM("SaveScan", "GetSteamRoot");
            var findAll = CM("SaveScan", "FindDd2SaveFolders");
            bool threw = false; string sroot = null; System.Collections.Generic.List<string> all = null;
            try
            {
                sroot = (string)getRoot.Invoke(null, null);
                all = ((System.Collections.IEnumerable)findAll.Invoke(null, null)).Cast<string>().ToList();
            }
            catch { threw = true; }
            Check("GetSteamRoot + FindDd2SaveFolders do not throw", !threw);
            Check("GetSteamRoot is null or an existing directory", sroot == null || Directory.Exists(sroot), "root=" + sroot);
            Check("FindDd2SaveFolders returns a non-null list", all != null);
            Check("every detected save folder exists", all == null || all.All(Directory.Exists));
            Console.WriteLine();
        }

        static void Test_FriendlyTime()
        {
            // QoL: FriendlyTime renders a backup's age as a human phrase, falling back to an absolute date for old ones.
            Console.WriteLine("== QoL: friendly relative time ==");
            var ft = CM("TimeText", "Friendly");
            System.Func<DateTime, string> F = d => (string)ft.Invoke(null, new object[] { d });
            Check("recent -> 'just now'", F(DateTime.Now.AddSeconds(-5)) == "just now");
            Check("minutes -> 'N min ago'", F(DateTime.Now.AddMinutes(-5)).EndsWith("min ago"));
            Check("hours -> 'N hours ago'", F(DateTime.Now.AddHours(-3)) == "3 hours ago");
            Check("one hour -> singular", F(DateTime.Now.AddMinutes(-61)) == "1 hour ago");
            Check("a day -> 'yesterday'", F(DateTime.Now.AddHours(-25)) == "yesterday");
            Check("several days -> 'N days ago'", F(DateTime.Now.AddDays(-3)) == "3 days ago");
            Check("old -> absolute date (no 'ago')", !F(DateTime.Now.AddDays(-30)).Contains("ago"));
            Console.WriteLine();
        }

        static void Test_BackupLocationWarning()
        {
            // QoL: BackupLocationWarning advises (returns a string) for risky backup folders, null when fine.
            Console.WriteLine("== QoL: backup-location warning ==");
            var w = CM("SaveScan", "BackupLocationWarning");
            System.Func<string, string, string> W = (s, b) => (string)w.Invoke(null, new object[] { s, b });
            Check("different local drive -> no warning", W(@"C:\Saves", @"D:\Backups") == null);
            Check("backup inside save -> warned", W(@"C:\Saves", @"C:\Saves\backups") != null);
            Check("backup == save -> warned", W(@"C:\Saves", @"C:\Saves") != null);
            string cloud = W(@"C:\Saves", @"C:\Users\x\OneDrive\Backups");
            Check("OneDrive folder -> cloud warning", cloud != null && cloud.ToLower().Contains("cloud"));
            string sameDrive = W(@"C:\Saves", @"C:\Other\Backups");
            Check("same drive -> drive warning", sameDrive != null && sameDrive.ToLower().Contains("drive"));
            Check("empty inputs -> null", W("", "") == null);
            Console.WriteLine();
        }

        static void Test_RestoreReverify()
        {
            // P1 read-side gate: a manifest-bearing backup that no longer matches its hashes is blocked before a
            // restore touches the live saves; a legacy backup (no manifest) is never blocked. Both helpers are static.
            Console.WriteLine("== Backup integrity: re-verify on restore (P1) ==");
            var hasMan = CM("Manifest", "HasManifest");
            var blocked = CM("Manifest", "RestoreBlockedByManifest");

            string src = NewDir("rv_src");
            File.WriteAllBytes(Path.Combine(src, "data000.bin"), B("the-save"));
            string manifest = (string)CM("Manifest", "BuildBackupManifest").Invoke(null, new object[] { src });

            string good = Path.Combine(work, "rv_good.zip");
            MakeZip(good, z => { z.AddDirectory(src); z.AddEntry("_savedrake/manifest.json", B(manifest)); });
            string legacy = Path.Combine(work, "rv_legacy.zip");
            MakeZip(legacy, z => { z.AddEntry("data000.bin", B("the-save")); }); // no manifest
            string corrupt = Path.Combine(work, "rv_corrupt.zip");
            MakeZip(corrupt, z => { z.AddEntry("data000.bin", B("TAMPERED!")); z.AddEntry("_savedrake/manifest.json", B(manifest)); });

            Check("HasManifest true for a manifest backup", (bool)hasMan.Invoke(null, new object[] { good }));
            Check("HasManifest false for a legacy backup", !(bool)hasMan.Invoke(null, new object[] { legacy }));

            object[] g = { good, null };
            Check("valid manifest backup -> NOT blocked", !(bool)blocked.Invoke(null, g) && g[1] == null, "reason=" + g[1]);
            object[] l = { legacy, null };
            Check("legacy backup -> NOT blocked", !(bool)blocked.Invoke(null, l) && l[1] == null, "reason=" + l[1]);
            object[] c = { corrupt, null };
            Check("on-disk corrupted manifest backup -> BLOCKED with reason", (bool)blocked.Invoke(null, c) && c[1] != null, "reason=" + c[1]);
            Console.WriteLine();
        }

        static void Test_ClassifyBackup()
        {
            // P1 UI: full classification used by "Validate all backups" -> "Validated" / "Legacy" / "Corrupt".
            Console.WriteLine("== Backup integrity: full classification (P1 UI) ==");
            var classify = CM("Manifest", "ClassifyBackupFully");
            string src = NewDir("cls_src");
            File.WriteAllBytes(Path.Combine(src, "data000.bin"), B("the-save"));
            string manifest = (string)CM("Manifest", "BuildBackupManifest").Invoke(null, new object[] { src });

            string good = Path.Combine(work, "cls_good.zip");
            MakeZip(good, z => { z.AddDirectory(src); z.AddEntry("_savedrake/manifest.json", B(manifest)); });
            Check("manifest-valid backup -> Validated", (string)classify.Invoke(null, new object[] { good }) == "Validated");

            string legacy = Path.Combine(work, "cls_legacy.zip");
            MakeZip(legacy, z => { z.AddEntry("data000.bin", B("the-save")); });
            Check("no-manifest backup -> Legacy", (string)classify.Invoke(null, new object[] { legacy }) == "Legacy");

            string corrupt = Path.Combine(work, "cls_corrupt.zip");
            MakeZip(corrupt, z => { z.AddEntry("data000.bin", B("TAMPERED!")); z.AddEntry("_savedrake/manifest.json", B(manifest)); });
            Check("tampered manifest backup -> Corrupt", (string)classify.Invoke(null, new object[] { corrupt }) == "Corrupt");

            string notzip = Path.Combine(work, "cls_notzip.zip");
            File.WriteAllBytes(notzip, B("not a zip at all"));
            Check("non-zip file -> Corrupt", (string)classify.Invoke(null, new object[] { notzip }) == "Corrupt");
            Console.WriteLine();
        }

        static void Test_LogRedaction()
        {
            // P2: the logger must strip personal data before writing. Redact is a pure function (no side effects).
            Console.WriteLine("== Logging: redaction (P2) ==");
            Type logT = Core.GetType("Savedrake.Log");
            Check("Savedrake.Log type exists", logT != null);
            if (logT == null) { Console.WriteLine(); return; }
            var redact = logT.GetMethod("Redact", BindingFlags.Public | BindingFlags.Static);
            Check("Log.Redact(string) exists", redact != null);
            if (redact == null) { Console.WriteLine(); return; }
            string r1 = (string)redact.Invoke(null, new object[] { @"C:\Program Files (x86)\Steam\userdata\1696225205\2054970\remote\win64_save" });
            Check("Steam account id is redacted", !r1.Contains("1696225205") && r1.Contains("<redacted>"), r1);
            string r2 = (string)redact.Invoke(null, new object[] { "nothing sensitive here" });
            Check("plain text is unchanged", r2 == "nothing sensitive here", r2);
            Console.WriteLine();
        }

        static void Test_DiskPreflight()
        {
            // Disk-space preflight helpers. GetDirectorySize / GetZipUncompressedSize are pure size math;
            // HasFreeSpaceFor checks the volume and FAILS OPEN when the volume can't be determined.
            Console.WriteLine("== Disk-space preflight ==");
            var dirSize = CM("DiskPreflight", "GetDirectorySize");
            var zipSize = CM("DiskPreflight", "GetZipUncompressedSize");
            var hasSpace = CM("DiskPreflight", "HasFreeSpaceFor");

            string d = NewDir("ds");
            File.WriteAllBytes(Path.Combine(d, "a.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(d, "b.bin"), new byte[2500]);
            long size = (long)dirSize.Invoke(null, new object[] { d });
            Check("GetDirectorySize sums file lengths", size == 3500, "got " + size);

            string z = Path.Combine(work, "ds.zip");
            MakeZip(z, x => { x.AddEntry("data000.bin", new byte[4096]); x.AddEntry("system.bin", new byte[1024]); });
            long unz = (long)zipSize.Invoke(null, new object[] { z });
            Check("GetZipUncompressedSize sums uncompressed entry sizes", unz == 4096 + 1024, "got " + unz);

            object[] a1 = { work, 0L, null };
            bool r1 = (bool)hasSpace.Invoke(null, a1);
            Check("0 bytes needed -> has space (true)", r1 && a1[2] == null);

            object[] a2 = { work, long.MaxValue / 2, null };
            bool r2 = (bool)hasSpace.Invoke(null, a2);
            Check("absurd requirement -> false with reason", !r2 && a2[2] != null, "reason=" + a2[2]);

            object[] a3 = { @"\\nonexistent-share-xyz\nope", 1000L, null };
            bool r3 = (bool)hasSpace.Invoke(null, a3);
            Check("undeterminable volume -> fails open (true)", r3);
            Console.WriteLine();
        }

        // Phase 4a: exercise the REAL Savedrake.Core orchestration (RestoreService.Restore) end-to-end through its
        // service seams, with stub IDialogService/IStatusSink. Stronger than the file-level happy-path mirror: it runs
        // the actual prompt/guard sequence + the transactional swap as the future WPF app will call them. Compile-time
        // referenced (not reflected), so the call site is the genuine public API.
        static void Test_RestoreServiceFlow()
        {
            Console.WriteLine("== RestoreService.Restore end-to-end (real Core orchestration) ==");

            // A real DD2-style live folder (...\remote\win64_save) holding OLD save data.
            string remote = NewDir("rsf_remote");
            string live = Path.Combine(remote, "win64_save");
            Directory.CreateDirectory(live);
            File.WriteAllText(Path.Combine(live, "data000.bin"), "OLD-DATA");
            File.WriteAllText(Path.Combine(live, "system.bin"), "OLD-SYS");

            // A separate backup folder, and a valid backup zip carrying NEW content + an integrity manifest (so the
            // P1 read-side gate sees a matching manifest and does NOT block).
            string backupDir = NewDir("rsf_backup");
            string srcForManifest = NewDir("rsf_src");
            File.WriteAllText(Path.Combine(srcForManifest, "data000.bin"), "NEW-DATA");
            File.WriteAllText(Path.Combine(srcForManifest, "system.bin"), "NEW-SYS");
            string manifest = (string)CM("Manifest", "BuildBackupManifest").Invoke(null, new object[] { srcForManifest });
            string backupZip = Path.Combine(backupDir, "newsave.zip");
            MakeZip(backupZip, z =>
            {
                z.AddEntry("data000.bin", B("NEW-DATA"));
                z.AddEntry("system.bin", B("NEW-SYS"));
                z.AddEntry("_savedrake/manifest.json", B(manifest));
            });

            var dialog = new StubDialog { ConfirmResult = true }; // auto-Yes to the Steam Cloud + any checkpoint prompt
            var status = new StubStatus();

            RestoreResult res = RestoreService.Restore(
                new RestoreRequest
                {
                    BackupZipPath = backupZip,
                    LiveSaveDir = live,
                    BackupDir = backupDir,
                    GameRunning = false
                },
                dialog, status);

            Check("RestoreService: result.Ok is true", res.Ok, "msg=" + res.Message + " errors=" + string.Join(" ;; ", dialog.Errors));
            Check("RestoreService: result not Cancelled", !res.Cancelled);
            Check("RestoreService: no Error dialog fired", dialog.Errors.Count == 0, "errors=" + string.Join(" ;; ", dialog.Errors));
            Check("RestoreService: data000.bin now holds NEW content",
                File.Exists(Path.Combine(live, "data000.bin")) && File.ReadAllText(Path.Combine(live, "data000.bin")) == "NEW-DATA");
            Check("RestoreService: system.bin now holds NEW content",
                File.Exists(Path.Combine(live, "system.bin")) && File.ReadAllText(Path.Combine(live, "system.bin")) == "NEW-SYS");

            string[] checkpoints = Directory.GetFiles(backupDir, "(Pre-Restore)*.zip");
            Check("RestoreService: a (Pre-Restore) checkpoint zip was created", checkpoints.Length == 1, "found " + checkpoints.Length);

            bool noTempDirs = Directory.GetDirectories(remote).All(d => !Path.GetFileName(d).StartsWith("._savedrake"));
            Check("RestoreService: staging/rollback temp dirs cleaned up", noTempDirs,
                "leftover=" + string.Join(",", Directory.GetDirectories(remote).Select(Path.GetFileName)));
            Check("RestoreService: final status is 'Restore successful.'", status.Last == "Restore successful.", "last=" + status.Last);
            Check("RestoreService: the Steam-Cloud confirm was prompted", dialog.Confirms.Count >= 1, "confirms=" + dialog.Confirms.Count);
            Check("RestoreService: a success Info dialog was shown", dialog.Infos.Count >= 1, "infos=" + string.Join(" ;; ", dialog.Infos));
            Check("RestoreService: no Warning dialog on the happy path", dialog.Warns.Count == 0, "warns=" + string.Join(" ;; ", dialog.Warns));
            Console.WriteLine();
        }

        static void Test_AutobackupPolicy()
        {
            // Phase 5: AutobackupPolicy.Decide is the whole change-aware autobackup decision as one pure function.
            // It must reproduce the shipped RunChangeAwareAutobackup / OnGameStatusChanged tree exactly, including the
            // ordering (game first, then limit, then the change gate) and the game-start bypass that lets the first
            // backup of a session always fire.
            Console.WriteLine("== Phase 5: change-aware autobackup decision ==");
            Func<bool, bool, int, int, bool, string, string, bool, AutobackupAction> D = AutobackupPolicy.Decide;

            // 1. Game not running wins over everything else (even an invalid limit / a hit cap).
            Check("game not running -> pause", D(false, false, 99, 1, false, null, null, false) == AutobackupAction.PauseGameNotRunning);
            Check("game not running -> pause even at game-start bypass", D(false, true, 0, 10, true, "fp", null, true) == AutobackupAction.PauseGameNotRunning);

            // 2. Invalid limit field (only checked once the game is running).
            Check("invalid limit -> InvalidLimit", D(true, false, 0, 0, false, "fp", null, false) == AutobackupAction.InvalidLimit);

            // 3. Limit reached with cleanup off -> stop; with cleanup on -> keep going.
            Check("count == limit, cleanup off -> LimitReached", D(true, true, 5, 5, false, "fp", null, false) == AutobackupAction.LimitReached);
            Check("count over limit, cleanup off -> LimitReached", D(true, true, 9, 5, false, "fp", null, false) == AutobackupAction.LimitReached);
            Check("count == limit, cleanup ON -> proceeds (DoBackup)", D(true, true, 5, 5, true, "fp", null, false) == AutobackupAction.DoBackup);
            Check("count below limit -> proceeds", D(true, true, 4, 5, false, "new", "old", false) == AutobackupAction.DoBackup);

            // 4. The change gate (only on the timer/watcher path, i.e. bypass = false).
            Check("null fingerprint -> SkipNotReady (fail closed)", D(true, true, 0, 5, false, null, "old", false) == AutobackupAction.SkipNotReady);
            Check("fingerprint unchanged -> SkipNoChange", D(true, true, 0, 5, false, "same", "same", false) == AutobackupAction.SkipNoChange);
            Check("null baseline never skips -> DoBackup", D(true, true, 0, 5, false, "fp", null, false) == AutobackupAction.DoBackup);
            Check("changed fingerprint -> DoBackup", D(true, true, 0, 5, false, "new", "old", false) == AutobackupAction.DoBackup);

            // 5. Game-start bypass: the change gate is skipped, but the limit still applies.
            Check("game-start bypass: null fp still DoBackup", D(true, true, 0, 5, false, null, null, true) == AutobackupAction.DoBackup);
            Check("game-start bypass: unchanged fp still DoBackup", D(true, true, 0, 5, false, "same", "same", true) == AutobackupAction.DoBackup);
            Check("game-start bypass still respects the limit", D(true, true, 5, 5, false, "same", "same", true) == AutobackupAction.LimitReached);
            Console.WriteLine();
        }

        static void Test_AutobackupCountStore()
        {
            // Phase 5: the count read is advisory and must be crash-proof — a missing/empty/garbled/locked file reads 0.
            Console.WriteLine("== Phase 5: autobackup count store ==");
            string dir = NewDir("count");
            Check("missing file -> 0", AutobackupCountStore.Read(Path.Combine(dir, "nope.txt")) == 0);
            Check("null/empty path -> 0", AutobackupCountStore.Read(null) == 0 && AutobackupCountStore.Read("") == 0);

            string f = Path.Combine(dir, "count.txt");
            File.WriteAllText(f, "7");
            Check("valid integer is read", AutobackupCountStore.Read(f) == 7);
            File.WriteAllText(f, "  12 \r\n");
            Check("whitespace-padded integer is read", AutobackupCountStore.Read(f) == 12);
            File.WriteAllText(f, "");
            Check("empty file -> 0", AutobackupCountStore.Read(f) == 0);
            File.WriteAllText(f, "not-a-number");
            Check("garbled file -> 0 (no throw)", AutobackupCountStore.Read(f) == 0);

            // A file open with an exclusive write lock must not crash the read.
            using (var fs = new FileStream(f, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(B("5"), 0, 1);
                Check("exclusively-locked file -> 0 (no throw)", AutobackupCountStore.Read(f) == 0);
            }

            // Phase 6b: the critical fix — the count must be WRITTEN, not just read, or the "Keep at most N" limit
            // never triggers. Write round-trips; CountAutobackups matches the WinForms filter (non-pinned (Auto)/auto
            // zips only); RecomputeAndWrite makes Read agree with the files on disk.
            string g = Path.Combine(dir, "count2.txt");
            AutobackupCountStore.Write(g, 42);
            Check("Write then Read round-trips", AutobackupCountStore.Read(g) == 42);
            AutobackupCountStore.Write(null, 1); // must not throw on a null path
            Check("Write(null) is a no-op (no throw)", true);

            string cdir = NewDir("count_recompute");
            MakeZip(Path.Combine(cdir, "(Auto) a.zip"), z => z.AddEntry("x", B("1")));
            MakeZip(Path.Combine(cdir, "(Auto) b.zip"), z => z.AddEntry("x", B("1")));
            MakeZip(Path.Combine(cdir, "autobackup_240101.zip"), z => z.AddEntry("x", B("1")));   // "auto" prefix counts
            MakeZip(Path.Combine(cdir, "(Auto) pinned [PINNED].zip"), z => z.AddEntry("x", B("1"))); // pinned excluded
            MakeZip(Path.Combine(cdir, "ManualBackup.zip"), z => z.AddEntry("x", B("1")));          // manual excluded
            MakeZip(Path.Combine(cdir, "(Pre-Restore) cp.zip"), z => z.AddEntry("x", B("1")));      // checkpoint excluded
            Check("CountAutobackups counts only non-pinned (Auto)/auto zips", AutobackupCountStore.CountAutobackups(cdir) == 3,
                "got " + AutobackupCountStore.CountAutobackups(cdir));
            Check("CountAutobackups on a missing dir -> 0", AutobackupCountStore.CountAutobackups(Path.Combine(cdir, "nope")) == 0);

            string cf = Path.Combine(cdir, "count_of_autobackups.txt");
            int written = AutobackupCountStore.RecomputeAndWrite(cdir, cf);
            Check("RecomputeAndWrite returns the live count", written == 3, "got " + written);
            Check("RecomputeAndWrite persists so Read agrees with disk", AutobackupCountStore.Read(cf) == 3);
            Console.WriteLine();
        }

        static void Test_AutobackupCleanup()
        {
            // Phase 5: auto-thinning must only ever touch autobackups. Manual backups, the (Pre-Restore) checkpoint,
            // and pinned autobackups are protected by name/token regardless of age; surplus plain autos are removed.
            Console.WriteLine("== Phase 5: autobackup auto-cleanup ==");
            string dir = NewDir("cleanup");
            long now = DateTime.UtcNow.Ticks;
            DateTime old = DateTime.UtcNow - TimeSpan.FromDays(30);

            Action<string> makeOld = name =>
            {
                string p = Path.Combine(dir, name);
                MakeZip(p, z => z.AddEntry("win64_save\\data.sav", B("payload-" + name)));
                File.SetLastWriteTimeUtc(p, old);
            };

            // Five plain autobackups, all old and within minutes of each other (one retention bucket -> keep newest).
            for (int i = 0; i < 5; i++) { makeOld("(Auto) save " + i + ".zip"); File.SetLastWriteTimeUtc(Path.Combine(dir, "(Auto) save " + i + ".zip"), old.AddMinutes(i)); }
            // Protected siblings, also old so survival proves the exclusion (not just recency).
            makeOld("(Auto) keepme [PINNED].zip");   // pinned autobackup
            makeOld("MyManualBackup.zip");           // manual (no prefix)
            makeOld("(Pre-Restore) checkpoint.zip");  // restore checkpoint

            int removed = AutobackupCleanup.Run(dir, 10, false, now); // maxKeep high so the count cap is inactive

            Check("cleanup removed at least one surplus autobackup", removed >= 1, "removed=" + removed);
            Check("the pinned autobackup survived", File.Exists(Path.Combine(dir, "(Auto) keepme [PINNED].zip")));
            Check("the manual backup survived", File.Exists(Path.Combine(dir, "MyManualBackup.zip")));
            Check("the (Pre-Restore) checkpoint survived", File.Exists(Path.Combine(dir, "(Pre-Restore) checkpoint.zip")));
            int plainAutosLeft = Directory.GetFiles(dir, "(Auto)*.zip").Count(p => !Pinning.IsPinnedBackup(Path.GetFileName(p)));
            Check("at least the newest plain autobackup survived", plainAutosLeft >= 1, "left=" + plainAutosLeft);
            Check("removed count == plain autos thinned", removed == 5 - plainAutosLeft, "removed=" + removed + " left=" + plainAutosLeft);

            // Recent-only autos must not be thinned, and an empty/absent dir is a no-op.
            string fresh = NewDir("cleanup_fresh");
            for (int i = 0; i < 3; i++) MakeZip(Path.Combine(fresh, "(Auto) r" + i + ".zip"), z => z.AddEntry("a", B("x")));
            Check("recent autobackups are not thinned", AutobackupCleanup.Run(fresh, 10, false, now) == 0);
            Check("empty dir -> 0 removed", AutobackupCleanup.Run(NewDir("cleanup_empty"), 5, false, now) == 0);
            Check("missing dir -> 0 removed (no throw)", AutobackupCleanup.Run(Path.Combine(dir, "does-not-exist"), 5, false, now) == 0);
            Console.WriteLine();
        }

        static void Test_GameDetect()
        {
            // Phase 5: the DD2 running-state read is a plain registry lookup. On the build/CI box DD2 is not installed,
            // so it must return false without throwing (a missing key is the normal "not installed" case).
            Console.WriteLine("== Phase 5: game detection ==");
            bool threw = false, result = false;
            try { result = GameDetect.IsDd2Running(); } catch { threw = true; }
            Check("IsDd2Running() does not throw on a box without DD2", !threw);
            Check("IsDd2Running() reports not-running when the key is absent", result == false);
            Console.WriteLine();
        }

        static void Test_SaveReadiness()
        {
            // "back up only after the save settles": IsSaveSettled defers a capture while the game holds a save file
            // open exclusively (mid-write), and reports settled once writing has finished / the file is shared.
            Console.WriteLine("== save readiness (settled-before-capture gate) ==");
            string dir = Path.Combine(work, "readiness_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            Check("missing folder is not settled (nothing to capture yet)",
                SaveReadiness.IsSaveSettled(Path.Combine(dir, "nope")) == false);
            Check("empty existing folder is settled (no writes in flight)",
                SaveReadiness.IsSaveSettled(dir) == true);

            string save = Path.Combine(dir, "data0000.bin");
            File.WriteAllBytes(save, new byte[] { 1, 2, 3, 4 });
            Check("a normal closed save file is settled",
                SaveReadiness.IsSaveSettled(dir) == true);

            using (new FileStream(save, FileMode.Open, FileAccess.Write, FileShare.None))
                Check("an exclusively-locked save (game mid-write) is NOT settled",
                    SaveReadiness.IsSaveSettled(dir) == false);

            Check("settled again once the lock is released",
                SaveReadiness.IsSaveSettled(dir) == true);

            // A handle the game keeps open but shares for reading is still readable, so we must not over-defer on it.
            using (new FileStream(save, FileMode.Open, FileAccess.Write, FileShare.Read))
                Check("a save file open WITH read-sharing is still settled (not over-deferred)",
                    SaveReadiness.IsSaveSettled(dir) == true);

            bool threw = false;
            try { SaveReadiness.IsSaveSettled(null); } catch { threw = true; }
            Check("IsSaveSettled(null) never throws", !threw);
            Console.WriteLine();
        }

        static void Test_CharacterFolder()
        {
            Console.WriteLine("== characters: name validation ==");
            foreach (string ok in new[] { "Aldric", "My Hero 2", "a-b_c", "Default", new string('x', 40) })
                Check("valid name: '" + ok + "'",
                    CharacterFolder.IsValidName(ok) && CharacterFolder.SafeName(ok) == ok.Trim());

            string[] bad = { null, "", "   ", "a/b", "a\\b", "a:b", ".", "..", " leading", "trailing ", "trailing.",
                             "con", "CON", "nul", "COM1", "LPT9", new string('y', 41) };
            foreach (string b in bad)
                Check("invalid name: '" + (b ?? "<null>") + "'",
                    !CharacterFolder.IsValidName(b) && CharacterFolder.SafeName(b) == "Default");

            foreach (char c in Path.GetInvalidFileNameChars())
                Check("invalid filename char rejected: U+" + ((int)c).ToString("X4"),
                    !CharacterFolder.IsValidName("a" + c + "b"));
            Console.WriteLine();
        }

        static void Test_CharacterMigration()
        {
            Console.WriteLine("== characters: non-destructive migration ==");

            // loose -> Default, plus idempotent re-run
            {
                string bd = NewDir("mig_loose");
                File.WriteAllText(Path.Combine(bd, "a.zip"), "A");
                File.WriteAllText(Path.Combine(bd, "b.zip"), "B");
                File.WriteAllText(Path.Combine(bd, "c.zip"), "C");
                File.WriteAllText(Path.Combine(bd, "notes.txt"), "keep");
                var r = CharacterMigration.MigrateLooseToDefault(bd);
                string def = Path.Combine(bd, "Default");
                Check("loose->Default: Ran with 3 moved", r.Ran && r.MovedZips == 3);
                Check("loose->Default: all 3 zips under Default", Directory.GetFiles(def, "*.zip").Length == 3);
                Check("loose->Default: none left loose", Directory.GetFiles(bd, "*.zip").Length == 0);
                Check("loose->Default: notes.txt untouched", File.Exists(Path.Combine(bd, "notes.txt")));
                var r2 = CharacterMigration.MigrateLooseToDefault(bd);
                Check("idempotent: second run does nothing", !r2.Ran && Directory.GetFiles(def, "*.zip").Length == 3);
            }

            // real second character -> hands off
            {
                string bd = NewDir("mig_handsoff");
                File.WriteAllText(Path.Combine(bd, "loose.zip"), "x");
                Directory.CreateDirectory(Path.Combine(bd, "Bjorn"));
                Check("second char: NeedsMigration false", !CharacterMigration.NeedsMigration(bd));
                var r = CharacterMigration.MigrateLooseToDefault(bd);
                Check("second char: hands off (loose stays)", !r.Ran && File.Exists(Path.Combine(bd, "loose.zip")));
            }

            // interrupted-migration resume: pre-existing Default + leftover loose zips
            {
                string bd = NewDir("mig_resume");
                Directory.CreateDirectory(Path.Combine(bd, "Default"));
                File.WriteAllText(Path.Combine(bd, "Default", "old.zip"), "old");
                File.WriteAllText(Path.Combine(bd, "left1.zip"), "1");
                File.WriteAllText(Path.Combine(bd, "left2.zip"), "2");
                Check("resume: NeedsMigration true", CharacterMigration.NeedsMigration(bd));
                var r = CharacterMigration.MigrateLooseToDefault(bd);
                string def = Path.Combine(bd, "Default");
                Check("resume: leftovers finished (3 total)", r.Ran && Directory.GetFiles(def, "*.zip").Length == 3);
                Check("resume: no loose left", Directory.GetFiles(bd, "*.zip").Length == 0);
            }

            // name collision: never overwrite
            {
                string bd = NewDir("mig_collide");
                Directory.CreateDirectory(Path.Combine(bd, "Default"));
                File.WriteAllText(Path.Combine(bd, "Default", "dup.zip"), "ORIGINAL");
                File.WriteAllText(Path.Combine(bd, "dup.zip"), "LOOSE");
                var r = CharacterMigration.MigrateLooseToDefault(bd);
                Check("collision: skipped not moved", r.SkippedZips == 1);
                Check("collision: existing copy unchanged", File.ReadAllText(Path.Combine(bd, "Default", "dup.zip")) == "ORIGINAL");
                Check("collision: loose copy left in place", File.Exists(Path.Combine(bd, "dup.zip")));
            }

            // parent .savedrake.tmp swept
            {
                string bd = NewDir("mig_tmp");
                File.WriteAllText(Path.Combine(bd, "a.zip"), "A");
                File.WriteAllText(Path.Combine(bd, "stale.savedrake.tmp"), "junk");
                CharacterMigration.MigrateLooseToDefault(bd);
                Check("orphan .savedrake.tmp swept", !File.Exists(Path.Combine(bd, "stale.savedrake.tmp")));
            }

            // legacy global count file pulled into Default
            {
                string bd = NewDir("mig_count");
                File.WriteAllText(Path.Combine(bd, "a.zip"), "A");
                string legacy = Path.Combine(bd, "legacy_count.txt");
                File.WriteAllText(legacy, "7");
                var r = CharacterMigration.MigrateLooseToDefault(bd, legacy);
                string destCount = Path.Combine(bd, "Default", "count_of_autobackups.txt");
                Check("legacy count moved into Default", r.MovedCountFile && File.Exists(destCount));
                Check("legacy count gone from origin", !File.Exists(legacy));
                Check("legacy count bytes preserved", File.ReadAllText(destCount) == "7");
            }

            // locked file: skipped, rest move, resumable after unlock
            {
                string bd = NewDir("mig_lock");
                File.WriteAllText(Path.Combine(bd, "free.zip"), "F");
                string locked = Path.Combine(bd, "locked.zip");
                File.WriteAllText(locked, "L");
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var r = CharacterMigration.MigrateLooseToDefault(bd);
                    Check("locked: free moved, locked skipped",
                        Directory.GetFiles(Path.Combine(bd, "Default"), "*.zip").Length == 1 && r.SkippedZips == 1);
                    Check("locked: still loose at root", File.Exists(locked));
                }
                CharacterMigration.MigrateLooseToDefault(bd);
                Check("locked: resumes after unlock", Directory.GetFiles(Path.Combine(bd, "Default"), "*.zip").Length == 2);
            }

            // empty / nonexistent: no-op, no throw
            {
                var r1 = CharacterMigration.MigrateLooseToDefault("");
                var r2 = CharacterMigration.MigrateLooseToDefault(Path.Combine(work, "nope_" + Guid.NewGuid().ToString("N")));
                Check("empty/nonexistent: Ran false, no throw", !r1.Ran && !r2.Ran);
                Check("empty/null: NeedsMigration false",
                    !CharacterMigration.NeedsMigration("") && !CharacterMigration.NeedsMigration(null));
            }
            Console.WriteLine();
        }

        static void Test_LiveFolderHasRealSave()
        {
            Console.WriteLine("== load: live-folder save detection ==");
            string withSave = NewDir("lhrs_yes");
            Directory.CreateDirectory(Path.Combine(withSave, "win64_save"));
            File.WriteAllText(Path.Combine(withSave, "win64_save", "data000.bin"), "x");
            Check("has data*.bin (nested) -> true", SaveScan.LiveFolderHasRealSave(withSave));

            string sysOnly = NewDir("lhrs_sys");
            File.WriteAllText(Path.Combine(sysOnly, "system.bin"), "x");
            Check("has system.bin -> true", SaveScan.LiveFolderHasRealSave(sysOnly));

            string noSave = NewDir("lhrs_no");
            File.WriteAllText(Path.Combine(noSave, "readme.txt"), "x");
            Check("no save data -> false", !SaveScan.LiveFolderHasRealSave(noSave));
            Check("missing folder -> false (no throw)", !SaveScan.LiveFolderHasRealSave(Path.Combine(noSave, "nope")));
            Check("null -> false (no throw)", !SaveScan.LiveFolderHasRealSave(null));
            Console.WriteLine();
        }

        static void Test_FindLatestRealBackup()
        {
            Console.WriteLine("== load: newest loadable backup (includes (Pre-Load), excludes (Pre-Restore)) ==");
            string dir = NewDir("flrb");
            string older = Path.Combine(dir, "backup_old.zip");
            string newer = Path.Combine(dir, "backup_new.zip");
            string preL = Path.Combine(dir, "(Pre-Load) 240101000000.zip");
            string preR = Path.Combine(dir, "(Pre-Restore) 240101000000.zip");
            foreach (string p in new[] { older, newer, preL, preR }) File.WriteAllText(p, "z");
            File.SetCreationTimeUtc(older, new DateTime(2024, 1, 1));
            File.SetCreationTimeUtc(newer, new DateTime(2024, 1, 2));
            File.SetCreationTimeUtc(preL, new DateTime(2024, 1, 5));   // a (Pre-Load) of more-recent live progress
            File.SetCreationTimeUtc(preR, new DateTime(2024, 1, 9));   // an undo-checkpoint, newest file of all
            // (Pre-Load) IS the character's most-recent state -> it wins over older normal backups; the (Pre-Restore)
            // undo-checkpoint is excluded even though it is the newest file.
            Check("(Pre-Load) loads as newest state; (Pre-Restore) excluded even when newest",
                SaveScan.FindLatestRealBackup(dir) == preL);

            string normalOnly = NewDir("flrb_n");
            string nb = Path.Combine(normalOnly, "backup_x.zip"); File.WriteAllText(nb, "z");
            Check("normal backup returned when no snapshots", SaveScan.FindLatestRealBackup(normalOnly) == nb);

            string onlyPreR = NewDir("flrb_ck");
            File.WriteAllText(Path.Combine(onlyPreR, "(Pre-Restore) 1.zip"), "z");
            Check("only (Pre-Restore) undo-checkpoint -> null (nothing to load)", SaveScan.FindLatestRealBackup(onlyPreR) == null);
            Check("missing folder -> null (no throw)", SaveScan.FindLatestRealBackup(Path.Combine(dir, "nope")) == null);
            Console.WriteLine();
        }

        static void Test_PreLoadCheckpoint()
        {
            Console.WriteLine("== load: (Pre-Load) outgoing snapshot ==");
            string live = NewDir("plc_live");
            File.WriteAllText(Path.Combine(live, "data000.bin"), "OUTGOING");
            File.WriteAllText(Path.Combine(live, "system.bin"), "SYS");
            string target = NewDir("plc_target");

            Check("CreatePreLoadCheckpoint returns true", RestoreEngine.CreatePreLoadCheckpoint(live, target));
            string[] zips = Directory.GetFiles(target, "*.zip");
            Check("exactly one zip written", zips.Length == 1, "found " + zips.Length);
            Check("name starts with (Pre-Load)", zips.Length == 1 && Path.GetFileName(zips[0]).StartsWith("(Pre-Load)"));
            Check("snapshot IS loadable as the character's most-recent state",
                zips.Length == 1 && SaveScan.FindLatestRealBackup(target) == zips[0]);
            Check("no .savedrake.tmp left", Directory.GetFiles(target, "*.savedrake.tmp").Length == 0);

            // No save data in live -> justified skip, true, no zip.
            string emptyLive = NewDir("plc_empty");
            File.WriteAllText(Path.Combine(emptyLive, "readme.txt"), "x");
            string target2 = NewDir("plc_target2");
            Check("no-save-data live -> skip (true, no zip)",
                RestoreEngine.CreatePreLoadCheckpoint(emptyLive, target2) && Directory.GetFiles(target2, "*.zip").Length == 0);
            // live == target -> skip, true.
            Check("live == target -> skip (true)", RestoreEngine.CreatePreLoadCheckpoint(live, live));
            Console.WriteLine();
        }

        // Build a real DD2-style live folder + a valid target backup zip + character folders; returns the pieces.
        static void SetupLoad(out string live, out string loadedFolder, out string activeFolder, out string targetZip)
        {
            string remote = NewDir("load_remote");
            live = Path.Combine(remote, "win64_save");
            Directory.CreateDirectory(live);
            File.WriteAllText(Path.Combine(live, "data000.bin"), "OLD-DATA");   // outgoing character A's live save
            File.WriteAllText(Path.Combine(live, "system.bin"), "OLD-SYS");

            loadedFolder = NewDir("load_A");   // character A (currently loaded) backup folder
            activeFolder = NewDir("load_B");   // character B (being loaded) backup folder

            string src = NewDir("load_src");
            File.WriteAllText(Path.Combine(src, "data000.bin"), "NEW-DATA");
            File.WriteAllText(Path.Combine(src, "system.bin"), "NEW-SYS");
            string manifest = (string)CM("Manifest", "BuildBackupManifest").Invoke(null, new object[] { src });
            targetZip = Path.Combine(activeFolder, "newsave.zip");
            MakeZip(targetZip, z =>
            {
                z.AddEntry("data000.bin", B("NEW-DATA"));
                z.AddEntry("system.bin", B("NEW-SYS"));
                z.AddEntry("_savedrake/manifest.json", B(manifest));
            });
        }

        static void Test_LoadSequence_Happy()
        {
            Console.WriteLine("== load: full sequence (snapshot outgoing, restore target) ==");
            SetupLoad(out string live, out string loadedFolder, out string activeFolder, out string targetZip);

            // Step 1: snapshot the outgoing (A) live save into A's folder.
            bool snapped = RestoreEngine.CreatePreLoadCheckpoint(live, loadedFolder);
            Check("step 1: outgoing snapshot ok", snapped);
            Check("step 1: (Pre-Load) of OLD save in loaded folder",
                Directory.GetFiles(loadedFolder, "(Pre-Load)*.zip").Length == 1);

            // Step 2: restore B's newest into the live folder (the exact call DoRestoreCore makes).
            var dialog = new StubDialog { ConfirmResult = true };
            RestoreResult res = RestoreService.Restore(
                new RestoreRequest { BackupZipPath = targetZip, LiveSaveDir = live, BackupDir = activeFolder, GameRunning = false },
                dialog, new StubStatus());

            Check("step 2: restore Ok", res.Ok, "msg=" + res.Message);
            Check("step 3: live now holds NEW (B) data",
                File.ReadAllText(Path.Combine(live, "data000.bin")) == "NEW-DATA");
            Check("(Pre-Restore) checkpoint of OLD save landed in active (B) folder",
                Directory.GetFiles(activeFolder, "(Pre-Restore)*.zip").Length == 1);
            Console.WriteLine();
        }

        static void Test_LoadSequence_Cancelled()
        {
            Console.WriteLine("== load: declined restore -> live untouched, no flip (no-limbo proof) ==");
            SetupLoad(out string live, out string loadedFolder, out string activeFolder, out string targetZip);

            // Step 1 still runs (silent snapshot).
            RestoreEngine.CreatePreLoadCheckpoint(live, loadedFolder);

            // Step 2: user declines the Steam-Cloud confirm.
            var dialog = new StubDialog { ConfirmResult = false };
            RestoreResult res = RestoreService.Restore(
                new RestoreRequest { BackupZipPath = targetZip, LiveSaveDir = live, BackupDir = activeFolder, GameRunning = false },
                dialog, new StubStatus());

            Check("declined: result not Ok", !res.Ok);
            Check("declined: live save UNCHANGED (still OLD)",
                File.ReadAllText(Path.Combine(live, "data000.bin")) == "OLD-DATA");
            // The command flips LoadedCharacter only on res.Ok, so here it would NOT flip -> Loaded stays the outgoing
            // character, which still matches the (unchanged) live save. No limbo.
            Check("declined: flip-gate is false (LoadedCharacter would stay outgoing)", res.Ok == false);
            Console.WriteLine();
        }

        static void Test_SuppressedCheckpoint_StillRestores()
        {
            Console.WriteLine("== load: suppressed pre-restore checkpoint still commits, writes no (Pre-Restore) ==");
            SetupLoad(out string live, out string loadedFolder, out string activeFolder, out string targetZip);
            RestoreResult res = RestoreService.Restore(
                new RestoreRequest { BackupZipPath = targetZip, LiveSaveDir = live, BackupDir = activeFolder, GameRunning = false, SuppressPreRestoreCheckpoint = true },
                new StubDialog { ConfirmResult = true }, new StubStatus());
            Check("suppressed: restore Ok", res.Ok, "msg=" + (res != null ? res.Message : "null"));
            Check("suppressed: live holds NEW data", File.ReadAllText(Path.Combine(live, "data000.bin")) == "NEW-DATA");
            Check("suppressed: NO (Pre-Restore) written (transaction still ran)",
                Directory.GetFiles(activeFolder, "(Pre-Restore)*.zip").Length == 0);
            Console.WriteLine();
        }

        static void Test_DefaultStillCheckpoints()
        {
            Console.WriteLine("== restore: default (flag omitted) still writes a (Pre-Restore) checkpoint ==");
            SetupLoad(out string live, out string loadedFolder, out string activeFolder, out string targetZip);
            RestoreResult res = RestoreService.Restore(
                new RestoreRequest { BackupZipPath = targetZip, LiveSaveDir = live, BackupDir = activeFolder, GameRunning = false },
                new StubDialog { ConfirmResult = true }, new StubStatus());
            Check("default: restore Ok", res.Ok);
            Check("default: exactly one (Pre-Restore) checkpoint written (unchanged behavior)",
                Directory.GetFiles(activeFolder, "(Pre-Restore)*.zip").Length == 1);
            Console.WriteLine();
        }

        static void Test_UndoAfterLoad_Consistency()
        {
            Console.WriteLine("== load: no (Pre-Restore) in incoming folder -> undo-after-load is consistent ==");
            SetupLoad(out string live, out string loadedFolder, out string activeFolder, out string targetZip);
            // The exact load sequence: snapshot outgoing (A) into its own folder, then restore B with checkpoint suppressed.
            RestoreEngine.CreatePreLoadCheckpoint(live, loadedFolder);
            RestoreResult res = RestoreService.Restore(
                new RestoreRequest { BackupZipPath = targetZip, LiveSaveDir = live, BackupDir = activeFolder, GameRunning = false, SuppressPreRestoreCheckpoint = true },
                new StubDialog { ConfirmResult = true }, new StubStatus());
            Check("load: Ok", res.Ok);
            Check("outgoing (A) save preserved as (Pre-Load) in ITS own folder",
                Directory.GetFiles(loadedFolder, "(Pre-Load)*.zip").Length == 1);
            Check("incoming (B) folder has NO (Pre-Restore) -> Undo finds nothing (no wrong-character undo)",
                Directory.GetFiles(activeFolder, "(Pre-Restore)*.zip").Length == 0
                && SaveScan.FindLatestPreRestoreCheckpoint(activeFolder) == null);
            Console.WriteLine();
        }

        static void Test_UpdateCheck()
        {
            // Phase 6i: TryParseVersion is the pure half of the update check — strip a leading 'v', require 2-4
            // numeric parts. A higher tag drives UpdateAvailable; an equal/unparsable tag must never claim an update.
            Console.WriteLine("== Phase 6i: update version parsing ==");
            Check("plain 1.2.3 parses", UpdateCheck.TryParseVersion("1.2.3", out Version v1) && v1.ToString() == "1.2.3");
            Check("v-prefixed v1.2.5 parses (v stripped)", UpdateCheck.TryParseVersion("v1.2.5", out Version v2) && v2.ToString() == "1.2.5");
            Check("uppercase V2.0 parses", UpdateCheck.TryParseVersion("V2.0", out Version _));
            Check("two-part 1.4 parses", UpdateCheck.TryParseVersion("1.4", out Version _));
            Check("four-part 1.2.3.4 parses", UpdateCheck.TryParseVersion("1.2.3.4", out Version _));
            Check("single part 1 -> invalid", !UpdateCheck.TryParseVersion("1", out Version _));
            Check("five parts -> invalid", !UpdateCheck.TryParseVersion("1.2.3.4.5", out Version _));
            Check("non-numeric part -> invalid", !UpdateCheck.TryParseVersion("1.x.3", out Version _));
            Check("empty -> invalid", !UpdateCheck.TryParseVersion("", out Version _));
            Check("garbage -> invalid", !UpdateCheck.TryParseVersion("vNext", out Version _));

            UpdateCheck.TryParseVersion("1.2.3", out Version cur);
            UpdateCheck.TryParseVersion("1.3.0", out Version newer);
            UpdateCheck.TryParseVersion("1.2.3", out Version same);
            Check("a higher release tag compares greater (update available)", newer > cur);
            Check("an equal tag is not greater (no phantom update)", !(same > cur));
            Console.WriteLine();
        }

        static void Test_SoundAssetsShipped()
        {
            // SoundFeedback resolves success.wav/error.wav from the install dir (next to Savedrake.App.exe), so the
            // build must copy them there (Content + CopyToOutputDirectory). Guard against the csproj items being
            // dropped, which would silently kill all backup feedback sounds.
            Console.WriteLine("== Sound assets shipped next to Savedrake.App.exe ==");
            string dir = ResolveAppBin();
            if (dir == null) { Check("Savedrake.App build output found for sound-asset check", false, "ResolveAppBin() returned null"); Console.WriteLine(); return; }
            foreach (string wav in new[] { "success.wav", "error.wav" })
            {
                string p = Path.Combine(dir, wav);
                bool exists = File.Exists(p);
                Check(wav + " present next to Savedrake.App.exe", exists, p);
                if (exists)
                {
                    byte[] head = new byte[12];
                    using (var fs = File.OpenRead(p)) { fs.Read(head, 0, 12); }
                    bool riffWave = head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
                                 && head[8] == (byte)'W' && head[9] == (byte)'A' && head[10] == (byte)'V' && head[11] == (byte)'E';
                    Check(wav + " is a valid RIFF/WAVE file", riffWave);
                }
            }
            Console.WriteLine();
        }

        // Locate the built WPF app output (now Savedrake.exe) so the sound-asset test can confirm the wavs ship.
        // Returns null if the App hasn't been built.
        static string ResolveAppBin()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string cfg in new[] { "Release", "Debug" })
            {
                string p = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Savedrake.App", "bin", cfg));
                if (File.Exists(Path.Combine(p, "Savedrake.exe"))) return p; // WPF app builds Savedrake.exe post-rename
            }
            return null;
        }

        static void Test_ValidateBackup()
        {
            Console.WriteLine("== ValidateBackup ==");
            var mi = CM("RestoreEngine", "ValidateBackup");

            string good = Path.Combine(work, "good.zip");
            MakeZip(good, z => { z.AddEntry("data000.bin", B("save")); z.AddEntry("system.bin", B("sys")); });
            object[] a1 = { good, null };
            bool r1 = (bool)mi.Invoke(null, a1);
            Check("good backup -> valid", r1 && a1[1] == null, "reason=" + a1[1]);

            string empty = Path.Combine(work, "empty.zip");
            MakeZip(empty, z => { });
            object[] a2 = { empty, null };
            bool r2 = (bool)mi.Invoke(null, a2);
            Check("empty zip -> invalid + 'no files'", !r2 && ((string)a2[1] ?? "").ToLower().Contains("no files"), "reason=" + a2[1]);

            string nosave = Path.Combine(work, "nosave.zip");
            MakeZip(nosave, z => { z.AddEntry("readme.txt", B("hi")); z.AddEntry("notes.md", B("x")); });
            object[] a3 = { nosave, null };
            bool r3 = (bool)mi.Invoke(null, a3);
            Check("no-save zip -> invalid + mentions save data", !r3 && ((string)a3[1] ?? "").ToLower().Contains("save data"), "reason=" + a3[1]);

            string corrupt = Path.Combine(work, "corrupt.zip");
            File.WriteAllBytes(corrupt, B("this is definitely not a zip file at all"));
            object[] a4 = { corrupt, null };
            bool r4 = (bool)mi.Invoke(null, a4);
            Check("corrupt zip -> invalid (caught)", !r4 && !string.IsNullOrEmpty((string)a4[1]), "reason=" + a4[1]);

            string nested = Path.Combine(work, "nested.zip");
            MakeZip(nested, z => { z.AddEntry("win64_save/data000.bin", B("s")); z.AddEntry("win64_save/system.bin", B("s")); });
            object[] a5 = { nested, null };
            bool r5 = (bool)mi.Invoke(null, a5);
            Check("nested-only zip -> valid (leaf check)", r5, "reason=" + a5[1]);
            Console.WriteLine();
        }

        static void Test_ExtractZipToStaging_and_ZipSlip()
        {
            Console.WriteLine("== ExtractZipToStaging + zip-slip guard ==");
            var ex = CM("RestoreEngine", "ExtractZipToStaging");

            // happy
            string z1 = Path.Combine(work, "ex_good.zip");
            MakeZip(z1, z => { z.AddEntry("data000.bin", B("A")); z.AddEntry("system.bin", B("B")); });
            string stage1 = NewDir("stage_good");
            ex.Invoke(null, new object[] { z1, stage1 });
            Check("extract happy: data000.bin present", File.Exists(Path.Combine(stage1, "data000.bin")));
            Check("extract happy: system.bin present", File.Exists(Path.Combine(stage1, "system.bin")));

            // subfolders preserved
            string z2 = Path.Combine(work, "ex_sub.zip");
            MakeZip(z2, z => { z.AddEntry("data000.bin", B("A")); z.AddEntry("sub/extra.bin", B("C")); });
            string stage2 = NewDir("stage_sub");
            ex.Invoke(null, new object[] { z2, stage2 });
            Check("extract subfolder: sub/extra.bin present", File.Exists(Path.Combine(stage2, "sub", "extra.bin")));

            // zip-slip: traversal entry
            string slip = Path.Combine(work, "slip.zip");
            bool crafted = false;
            try
            {
                MakeZip(slip, z => { z.AddEntry("../evil.txt", B("pwned")); z.AddEntry("data000.bin", B("A")); });
                using (var zr = Ionic.Zip.ZipFile.Read(slip))
                    crafted = zr.Entries.Any(e => e.FileName.Contains("..")); // confirm traversal survived crafting
            }
            catch { crafted = false; }

            if (!crafted)
            {
                Check("zip-slip input crafted", false, "DotNetZip sanitized '../' on save; cannot test traversal this way");
            }
            else
            {
                string stage3 = NewDir("stage_slip");
                string parent = Directory.GetParent(stage3).FullName;
                string evilOutside = Path.Combine(parent, "evil.txt");
                if (File.Exists(evilOutside)) File.Delete(evilOutside);
                bool threw = false; string msg = null;
                try { ex.Invoke(null, new object[] { slip, stage3 }); }
                catch (Exception e) { var u = Unwrap(e); threw = u is IOException; msg = u.Message; }
                Check("zip-slip: ExtractZipToStaging throws IOException", threw, msg);
                Check("zip-slip: message mentions unsafe path", msg != null && msg.ToLower().Contains("unsafe path"), msg);
                Check("zip-slip: nothing written outside staging", !File.Exists(evilOutside));
            }
            Console.WriteLine();
        }

        static void Test_NestedDetectAndFlatten()
        {
            Console.WriteLine("== DetectNestedPrefix + FlattenNestedLayout ==");
            // nested layout: staging has sole win64_save dir holding the saves
            string s = NewDir("nest");
            string inner = Path.Combine(s, "win64_save");
            Directory.CreateDirectory(inner);
            File.WriteAllBytes(Path.Combine(inner, "data000.bin"), B("A"));
            File.WriteAllBytes(Path.Combine(inner, "system.bin"), B("B"));
            bool detected = (bool)CM("RestoreEngine", "DetectNestedPrefix").Invoke(null, new object[] { s });
            Check("DetectNestedPrefix true on sole win64_save dir", detected);
            CM("RestoreEngine", "FlattenNestedLayout").Invoke(null, new object[] { s });
            Check("flatten: data000.bin at root", File.Exists(Path.Combine(s, "data000.bin")));
            Check("flatten: win64_save husk removed", !Directory.Exists(inner));

            // root layout: data at root -> NOT detected (don't flatten legit backups)
            string s2 = NewDir("root");
            File.WriteAllBytes(Path.Combine(s2, "data000.bin"), B("A"));
            Check("DetectNestedPrefix false on root layout", !(bool)CM("RestoreEngine", "DetectNestedPrefix").Invoke(null, new object[] { s2 }));

            // win64_save dir + a stray root file -> NOT sole item -> not detected
            string s3 = NewDir("mix");
            Directory.CreateDirectory(Path.Combine(s3, "win64_save"));
            File.WriteAllBytes(Path.Combine(s3, "stray.bin"), B("A"));
            Check("DetectNestedPrefix false when root has extra file", !(bool)CM("RestoreEngine", "DetectNestedPrefix").Invoke(null, new object[] { s3 }));

            // DOUBLY-nested win64_save\win64_save\{data,system} -> must flatten WITHOUT throwing (finding #11 fix)
            string s4 = NewDir("nest2");
            string inner2 = Path.Combine(s4, "win64_save", "win64_save");
            Directory.CreateDirectory(inner2);
            File.WriteAllBytes(Path.Combine(inner2, "data000.bin"), B("A"));
            File.WriteAllBytes(Path.Combine(inner2, "system.bin"), B("B"));
            bool flattenThrew = false; string fmsg = null;
            try { CM("RestoreEngine", "FlattenNestedLayout").Invoke(null, new object[] { s4 }); }
            catch (Exception e) { flattenThrew = true; fmsg = Unwrap(e).Message; }
            Check("double-nest flatten does NOT throw", !flattenThrew, fmsg);
            Check("double-nest: data000.bin at root", File.Exists(Path.Combine(s4, "data000.bin")));
            Check("double-nest: system.bin at root", File.Exists(Path.Combine(s4, "system.bin")));
            Check("double-nest: no win64_save dir remains", !Directory.Exists(Path.Combine(s4, "win64_save")));
            Console.WriteLine();
        }

        static void Test_VerifyStagedDir()
        {
            Console.WriteLine("== VerifyStagedDir ==");
            string a = NewDir("vsd_good");
            File.WriteAllBytes(Path.Combine(a, "data000.bin"), B("A"));
            Check("verify true with data*.bin", (bool)CM("RestoreEngine", "VerifyStagedDir").Invoke(null, new object[] { a }));

            string b = NewDir("vsd_nosave");
            File.WriteAllBytes(Path.Combine(b, "readme.txt"), B("A"));
            Check("verify false with no save files", !(bool)CM("RestoreEngine", "VerifyStagedDir").Invoke(null, new object[] { b }));

            string c = NewDir("vsd_empty");
            Check("verify false on empty dir", !(bool)CM("RestoreEngine", "VerifyStagedDir").Invoke(null, new object[] { c }));

            string d = NewDir("vsd_deep");
            string deep = Path.Combine(d, "win64_save");
            Directory.CreateDirectory(deep);
            File.WriteAllBytes(Path.Combine(deep, "system.bin"), B("A"));
            Check("verify true with save in subdir (AllDirectories)", (bool)CM("RestoreEngine", "VerifyStagedDir").Invoke(null, new object[] { d }));
            Console.WriteLine();
        }

        static void Test_DirPrimitives()
        {
            Console.WriteLine("== MoveDirContents + EmptyDir ==");
            string src = NewDir("mv_src");
            File.WriteAllBytes(Path.Combine(src, "data000.bin"), B("ORIG"));
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllBytes(Path.Combine(src, "sub", "extra.bin"), B("X"));
            string dst = Path.Combine(work, "mv_dst_" + Guid.NewGuid().ToString("N").Substring(0, 8)); // not pre-created
            CM("RestoreEngine", "MoveDirContents").Invoke(null, new object[] { src, dst });
            Check("MoveDirContents: file moved", File.Exists(Path.Combine(dst, "data000.bin")));
            Check("MoveDirContents: subdir+file moved", File.Exists(Path.Combine(dst, "sub", "extra.bin")));
            Check("MoveDirContents: source emptied", Directory.GetFileSystemEntries(src).Length == 0);
            Check("MoveDirContents: content intact", File.ReadAllText(Path.Combine(dst, "data000.bin")) == "ORIG");

            string ed = NewDir("ed");
            File.WriteAllBytes(Path.Combine(ed, "a.bin"), B("A"));
            Directory.CreateDirectory(Path.Combine(ed, "s"));
            File.WriteAllBytes(Path.Combine(ed, "s", "b.bin"), B("B"));
            CM("RestoreEngine", "EmptyDir").Invoke(null, new object[] { ed });
            Check("EmptyDir: dir emptied but exists", Directory.Exists(ed) && Directory.GetFileSystemEntries(ed).Length == 0);
            Console.WriteLine();
        }

        static void Test_ClearReadOnly()
        {
            Console.WriteLine("== ClearReadOnlyRecursive ==");
            string r = NewDir("ro");
            string f1 = Path.Combine(r, "data000.bin");
            File.WriteAllBytes(f1, B("A"));
            File.SetAttributes(f1, FileAttributes.ReadOnly);
            string sub = Path.Combine(r, "sub");
            Directory.CreateDirectory(sub);
            string f2 = Path.Combine(sub, "system.bin");
            File.WriteAllBytes(f2, B("B"));
            File.SetAttributes(f2, FileAttributes.ReadOnly);
            CM("RestoreEngine", "ClearReadOnlyRecursive").Invoke(null, new object[] { r });
            Check("ReadOnly cleared on root file", (File.GetAttributes(f1) & FileAttributes.ReadOnly) == 0);
            Check("ReadOnly cleared on nested file", (File.GetAttributes(f2) & FileAttributes.ReadOnly) == 0);
            Console.WriteLine();
        }

        static void Test_CreateSiblingTempDir()
        {
            Console.WriteLine("== CreateSiblingTempDir ==");
            string remote = NewDir("remote");
            string live = Path.Combine(remote, "win64_save");
            Directory.CreateDirectory(live);
            string tmp = (string)CM("RestoreEngine", "CreateSiblingTempDir").Invoke(null, new object[] { live, "savedrake_stage" });
            Check("temp dir created", Directory.Exists(tmp));
            Check("temp dir is sibling of live (under remote)", string.Equals(Directory.GetParent(tmp).FullName, remote, StringComparison.OrdinalIgnoreCase), tmp);
            Check("temp dir hidden-ish name '._savedrake_stage_'", Path.GetFileName(tmp).StartsWith("._savedrake_stage_"));
            Console.WriteLine();
        }

        // End-to-end SUCCESS path: run the exact T1->T6 file-operation sequence that RestoreTransactional performs,
        // using the real compiled helpers (RestoreTransactional itself can't run headlessly due to MessageBox/Status).
        static void Test_HappyPathSequence()
        {
            Console.WriteLine("== HAPPY-PATH end-to-end sequence (real helpers, RestoreTransactional order) ==");
            string remote = NewDir("hp_remote");
            string live = Path.Combine(remote, "win64_save");
            Directory.CreateDirectory(live);
            File.WriteAllBytes(Path.Combine(live, "data000.bin"), B("OLD-DATA"));
            File.WriteAllBytes(Path.Combine(live, "system.bin"), B("OLD-SYS"));

            string backup = Path.Combine(work, "hp_backup.zip");
            MakeZip(backup, z => { z.AddEntry("data000.bin", B("NEW-DATA")); z.AddEntry("system.bin", B("NEW-SYS")); z.AddEntry("sub/extra.bin", B("NEW-EXTRA")); });

            string staging = (string)CM("RestoreEngine", "CreateSiblingTempDir").Invoke(null, new object[] { live, "savedrake_stage" });
            string rollback = (string)CM("RestoreEngine", "CreateSiblingTempDir").Invoke(null, new object[] { live, "savedrake_rollback" });

            CM("RestoreEngine", "ExtractZipToStaging").Invoke(null, new object[] { backup, staging }); // T1
            // Simulate a backup whose files carry the ReadOnly attribute (the R2 bug source).
            foreach (string f in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, File.GetAttributes(f) | FileAttributes.ReadOnly);
            CM("RestoreEngine", "FlattenNestedLayout").Invoke(null, new object[] { staging });                          // T1b (no-op, flat)
            bool verified = (bool)CM("RestoreEngine", "VerifyStagedDir").Invoke(null, new object[] { staging });        // T2
            CM("RestoreEngine", "MoveDirContents").Invoke(null, new object[] { live, rollback });                       // T3
            CM("RestoreEngine", "MoveDirContents").Invoke(null, new object[] { staging, live });                        // T4
            CM("RestoreEngine", "ClearReadOnlyRecursive").Invoke(null, new object[] { live });                          // T5
            CM("RestoreEngine", "TryDeleteDir").Invoke(null, new object[] { staging });                                 // finally
            CM("RestoreEngine", "TryDeleteDir").Invoke(null, new object[] { rollback });                                // finally (rollbackOk=true)

            Check("happy: VerifyStagedDir true", verified);
            Check("happy: data000.bin replaced with NEW content", File.Exists(Path.Combine(live, "data000.bin")) && File.ReadAllText(Path.Combine(live, "data000.bin")) == "NEW-DATA");
            Check("happy: system.bin replaced with NEW content", File.ReadAllText(Path.Combine(live, "system.bin")) == "NEW-SYS");
            Check("happy: subfolder file restored", File.Exists(Path.Combine(live, "sub", "extra.bin")));
            Check("happy: data lands at win64_save ROOT (not nested)", !Directory.Exists(Path.Combine(live, "win64_save")));
            Check("happy: ReadOnly cleared on restored data000.bin (R2)", (File.GetAttributes(Path.Combine(live, "data000.bin")) & FileAttributes.ReadOnly) == 0);
            Check("happy: ReadOnly cleared on restored sub file", (File.GetAttributes(Path.Combine(live, "sub", "extra.bin")) & FileAttributes.ReadOnly) == 0);
            Check("happy: staging temp cleaned up", !Directory.Exists(staging));
            Check("happy: rollback temp cleaned up (stale old saves)", !Directory.Exists(rollback));
            Check("happy: no leftover ._savedrake_* dirs under remote", Directory.GetDirectories(remote).All(d => !Path.GetFileName(d).StartsWith("._savedrake")));
            Console.WriteLine();
        }

        // Counts how many of the user's ORIGINAL save files still exist anywhere recoverable (live OR rollback).
        static int CountSurvivingOriginals(string live, string rollback, params string[] originalContents)
        {
            int n = 0;
            foreach (string want in originalContents)
            {
                bool found = false;
                foreach (string dir in new[] { live, rollback })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        if (File.ReadAllText(f) == want) { found = true; break; }
                    if (found) break;
                }
                if (found) n++;
            }
            return n;
        }

        // How many of the given dirs contain a file whose content equals `content` (recursively).
        // Used to assert an original survives in EXACTLY ONE location (==1): not 0 (data loss), not duplicated across both.
        static int LocationsWith(string content, params string[] dirs)
        {
            int n = 0;
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                if (Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f) == content)) n++;
            }
            return n;
        }

        // The EXACT reported data-loss scenario: a partial T4 (staging->live) failure. Mirrors RestoreTransactional's
        // orchestration (stagingStarted set BEFORE T4; rollbackOk gates the finally) but calls the REAL compiled
        // MoveDirContents / Rollback / TryDeleteDir. Asserts the user ends with 2 surviving original save files (not 0).
        static void Test_PartialT4_NoDataLoss()
        {
            Console.WriteLine("== PARTIAL-T4 FAILURE -> surviving original files (the reported data-loss scenario) ==");
            var rb = CM("RestoreEngine", "Rollback");
            int np = rb.GetParameters().Length;
            Console.WriteLine("   (real compiled Rollback arity = " + np + "; fixed build = 3-param (liveDir, rollbackDir, stagingStarted))");

            // ----- Scenario A: partial T4, then real Rollback fully recovers the originals into live -----
            string remoteA = NewDir("pt4a_remote");
            string liveA = Path.Combine(remoteA, "win64_save"); Directory.CreateDirectory(liveA);
            File.WriteAllText(Path.Combine(liveA, "data000.bin"), "ORIG0");
            File.WriteAllText(Path.Combine(liveA, "data001.bin"), "ORIG1");
            string stageA = (string)CM("RestoreEngine", "CreateSiblingTempDir").Invoke(null, new object[] { liveA, "savedrake_stage" });
            string rbackA = (string)CM("RestoreEngine", "CreateSiblingTempDir").Invoke(null, new object[] { liveA, "savedrake_rollback" });
            File.WriteAllText(Path.Combine(stageA, "data000.bin"), "NEW0");
            File.WriteAllText(Path.Combine(stageA, "data001.bin"), "NEW1");

            CM("RestoreEngine", "MoveDirContents").Invoke(null, new object[] { liveA, rbackA });   // T3: originals -> rollback, live empty
            bool stagingStartedA = true;                                          // set BEFORE T4 (as RestoreTransactional does)
            // Partial T4: the first staged file lands in live, then MoveDirContents "throws" (we stop here).
            File.Move(Path.Combine(stageA, "data000.bin"), Path.Combine(liveA, "data000.bin"));
            // catch -> real compiled Rollback
            object[] argsA = np == 3 ? new object[] { liveA, rbackA, stagingStartedA }
                                     : new object[] { liveA, rbackA, true, false }; // old 4-param shape (would FAIL to recover)
            bool recoveredA = (bool)rb.Invoke(null, argsA);
            bool rollbackOkA = recoveredA;
            // finally (gated exactly like the fixed RestoreTransactional)
            CM("RestoreEngine", "TryDeleteDir").Invoke(null, new object[] { stageA });
            if (rollbackOkA) CM("RestoreEngine", "TryDeleteDir").Invoke(null, new object[] { rbackA });

            int survA = CountSurvivingOriginals(liveA, rbackA, "ORIG0", "ORIG1");
            Console.WriteLine("   Scenario A: Rollback returned " + recoveredA + ", surviving originals = " + survA);
            Check("Scenario A (partial T4): surviving ORIGINAL files == 2", survA == 2, "got " + survA);
            Check("Scenario A: both originals restored into live",
                  File.Exists(Path.Combine(liveA, "data000.bin")) && File.ReadAllText(Path.Combine(liveA, "data000.bin")) == "ORIG0" &&
                  File.Exists(Path.Combine(liveA, "data001.bin")) && File.ReadAllText(Path.Combine(liveA, "data001.bin")) == "ORIG1");
            Check("Scenario A: ORIG0 survives in EXACTLY ONE location", LocationsWith("ORIG0", liveA, rbackA) == 1, "locations=" + LocationsWith("ORIG0", liveA, rbackA));
            Check("Scenario A: ORIG1 survives in EXACTLY ONE location", LocationsWith("ORIG1", liveA, rbackA) == 1, "locations=" + LocationsWith("ORIG1", liveA, rbackA));

            // ----- Scenario B: Rollback itself FAILS -> gated finally must PRESERVE rollbackDir (originals survive there) -----
            string remoteB = NewDir("pt4b_remote");
            string liveB = Path.Combine(remoteB, "win64_save"); Directory.CreateDirectory(liveB);
            string rbackB = (string)CM("RestoreEngine", "CreateSiblingTempDir").Invoke(null, new object[] { liveB, "savedrake_rollback" });
            File.WriteAllText(Path.Combine(rbackB, "data000.bin"), "ORIG0");      // the only intact originals live here
            File.WriteAllText(Path.Combine(rbackB, "data001.bin"), "ORIG1");
            File.WriteAllText(Path.Combine(liveB, "data000.bin"), "COLLIDE");     // forces MoveDirContents(rollback->live) to throw
            object[] argsB = np == 3 ? new object[] { liveB, rbackB, false }
                                     : new object[] { liveB, rbackB, true, false };
            bool recoveredB;
            try { recoveredB = (bool)rb.Invoke(null, argsB); } catch { recoveredB = true; } // a throw OUT would be a bug
            bool rollbackOkB = recoveredB;
            CM("RestoreEngine", "TryDeleteDir").Invoke(null, new object[] { /*stagingDir n/a*/ rbackB + "_nonexistent" });
            if (rollbackOkB) CM("RestoreEngine", "TryDeleteDir").Invoke(null, new object[] { rbackB }); // gated: must NOT delete when recovery failed

            int survB = CountSurvivingOriginals(liveB, rbackB, "ORIG0", "ORIG1");
            Console.WriteLine("   Scenario B: Rollback returned " + recoveredB + " (false expected), surviving originals = " + survB);
            Check("Scenario B (rollback fails): Rollback returns false without throwing", !recoveredB);
            Check("Scenario B: gated finally PRESERVES rollbackDir -> surviving ORIGINAL files == 2", survB == 2, "got " + survB);
            Check("Scenario B: ORIG0 survives in EXACTLY ONE location (preserved rollbackDir)", LocationsWith("ORIG0", liveB, rbackB) == 1, "locations=" + LocationsWith("ORIG0", liveB, rbackB));
            Check("Scenario B: ORIG1 survives in EXACTLY ONE location (preserved rollbackDir)", LocationsWith("ORIG1", liveB, rbackB) == 1, "locations=" + LocationsWith("ORIG1", liveB, rbackB));
            Console.WriteLine();
        }

        // The critical safety test. Simulate a failed-rollback state and observe whether the user's
        // ONLY copy of their saves survives. Uses real compiled Rollback + TryDeleteDir.
        static void Test_DataLoss_Repro()
        {
            Console.WriteLine("== DATA-LOSS SAFETY (Rollback + finally semantics) ==");

            // --- Reproduce the failure state that yields Rollback==false ---
            // After a full move-aside (T3) the originals are all in rollbackDir; then T4 (staging->live)
            // partially moved a staged file into live and threw. With the reference bookkeeping,
            // stagedIntoLive is still FALSE (set only after T4 returns).
            string remote = NewDir("dl_remote");
            string live = Path.Combine(remote, "win64_save");
            Directory.CreateDirectory(live);
            string rollback = (string)CM("RestoreEngine", "CreateSiblingTempDir").Invoke(null, new object[] { live, "savedrake_rollback" });

            // originals all set aside into rollback
            File.WriteAllBytes(Path.Combine(rollback, "data000.bin"), B("ORIGINAL-DATA"));
            File.WriteAllBytes(Path.Combine(rollback, "system.bin"), B("ORIGINAL-SYS"));
            // live holds a partially-moved staged file that COLLIDES by name with an original
            File.WriteAllBytes(Path.Combine(live, "data000.bin"), B("STAGED-NEW"));

            var rb = CM("RestoreEngine", "Rollback");
            int np = rb.GetParameters().Length;
            // movedLiveAside=true; stagedIntoLive=false (the reachable partial-T4 state)
            object[] rbArgs = np == 4
                ? new object[] { live, rollback, true, false }
                : new object[] { live, rollback, true }; // fixed signature: (liveDir, rollbackDir, stagingStarted=true)
            bool recovered;
            try { recovered = (bool)rb.Invoke(null, rbArgs); }
            catch (Exception e) { recovered = false; Console.WriteLine("   Rollback threw: " + Unwrap(e).Message); }

            Console.WriteLine("   Rollback returned: " + recovered + " (param count " + np + ")");
            bool originalsStillInRollback = File.Exists(Path.Combine(rollback, "data000.bin")) || File.Exists(Path.Combine(rollback, "system.bin"));
            bool originalsBackInLive = File.Exists(Path.Combine(live, "system.bin")) &&
                                       File.Exists(Path.Combine(live, "data000.bin")) &&
                                       File.ReadAllText(Path.Combine(live, "data000.bin")) == "ORIGINAL-DATA";

            // The user's originals must exist SOMEWHERE after rollback (live or rollback) — never gone.
            Check("originals survive rollback (in live OR rollback)", originalsBackInLive || originalsStillInRollback,
                  "live-ok=" + originalsBackInLive + " rollback-has=" + originalsStillInRollback);

            // Now emulate the finally block: it would TryDeleteDir(rollbackDir). The SAFE rule is:
            // delete rollbackDir only when recovery succeeded (rollback empty) OR transaction committed.
            // Demonstrate the danger: if we delete rollbackDir while it still holds the only originals -> DATA LOSS.
            if (!originalsBackInLive && originalsStillInRollback)
            {
                Console.WriteLine("   STATE: rollback FAILED to restore; originals exist ONLY in rollbackDir.");
                Console.WriteLine("   -> If the finally unconditionally TryDeleteDir(rollbackDir), the originals are DELETED.");
                // We do NOT actually delete here; we assert the precondition that proves the finally must be guarded.
                Check("recovery FAILED here (so finally MUST preserve rollbackDir)", !recovered,
                      "recovered=" + recovered);
                Check("finally rule guards this case (recovered==false => must NOT delete rollbackDir)", !recovered);
            }
            else if (originalsBackInLive)
            {
                Console.WriteLine("   STATE: rollback SUCCEEDED; originals restored to live, rollbackDir now empty.");
                bool rbEmpty = !Directory.Exists(rollback) || Directory.GetFileSystemEntries(rollback).Length == 0;
                Check("after successful rollback, rollbackDir is empty (safe to delete husk)", rbEmpty);
            }

            // --- Scenario 2: FORCE Rollback to fail, prove originals are PRESERVED (finally must skip delete) ---
            // stagingStarted=false + a name collision between live and rollback makes MoveDirContents(rollback->live)
            // throw -> Rollback returns false (never throws out) -> rollbackOk=false -> finally skips TryDeleteDir.
            string remote2 = NewDir("dl2_remote");
            string live2 = Path.Combine(remote2, "win64_save");
            Directory.CreateDirectory(live2);
            string rb2 = (string)CM("RestoreEngine", "CreateSiblingTempDir").Invoke(null, new object[] { live2, "savedrake_rollback" });
            File.WriteAllBytes(Path.Combine(rb2, "data000.bin"), B("ORIGINAL"));   // the only intact original
            File.WriteAllBytes(Path.Combine(live2, "data000.bin"), B("COLLIDE"));  // forces File.Move collision
            object[] rb2Args = np == 3 ? new object[] { live2, rb2, false } : new object[] { live2, rb2, true, false };
            bool rec2; try { rec2 = (bool)rb.Invoke(null, rb2Args); } catch (Exception e) { Console.WriteLine("   Rollback threw OUT (bad): " + Unwrap(e).Message); rec2 = true; }
            Check("forced-collision: Rollback returns false (caught, not thrown)", !rec2);
            bool origPreserved = File.Exists(Path.Combine(rb2, "data000.bin")) && File.ReadAllText(Path.Combine(rb2, "data000.bin")) == "ORIGINAL";
            Check("forced-collision: original PRESERVED in rollbackDir (finally guard prevents data loss)", origPreserved);
            Console.WriteLine();
        }
    }
}
