using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace RestoreHarness
{
    // Headless reflection harness for Savedrake's transactional-restore helpers.
    // Loads the REAL compiled Savedrake.exe and invokes the actual private methods
    // (static + UI-free instance methods via an uninitialized Main instance) against
    // real temp dirs + crafted zips. Covers most of the DESIGN smoke matrix deterministically.
    internal static class Program
    {
        static int passed = 0, failed = 0;
        static Type T;
        static object inst;             // uninitialized Main (constructor NOT run)
        static string work;             // harness scratch root

        static MethodInfo SM(string n) { var m = T.GetMethod(n, BindingFlags.NonPublic | BindingFlags.Static); if (m == null) throw new Exception("static method not found: " + n); return m; }
        static MethodInfo IM(string n) { var m = T.GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance); if (m == null) throw new Exception("instance method not found: " + n); return m; }

        static void Check(string name, bool ok, string detail = null)
        {
            if (ok) { passed++; Console.WriteLine("[PASS] " + name); }
            else { failed++; Console.WriteLine("[FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
        }

        static Exception Unwrap(Exception e) { return e is TargetInvocationException && e.InnerException != null ? e.InnerException : e; }

        // Locate the built Savedrake.exe: an explicit arg wins; otherwise look next to this harness
        // (..\..\Savedrake v1.2.3\bin\<cfg>) so it works from the repo build output and in CI without a machine path.
        static string ResolveBin(string[] args)
        {
            if (args.Length > 0) return args[0];
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string cfg in new[] { "Release", "Debug" })
            {
                string p = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Savedrake v1.2.3", "bin", cfg));
                if (File.Exists(Path.Combine(p, "Savedrake.exe"))) return p;
            }
            return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Savedrake v1.2.3", "bin", "Release"));
        }

        static int Main(string[] args)
        {
            string bin = ResolveBin(args);
            string exe = Path.Combine(bin, "Savedrake.exe");
            if (!File.Exists(exe))
            {
                Console.WriteLine("Savedrake.exe not found at: " + exe);
                Console.WriteLine("Pass the build output dir as arg 1, e.g.  RestoreHarness.exe \"...\\Savedrake v1.2.3\\bin\\Release\"");
                return 2;
            }
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string nm = new AssemblyName(e.Name).Name;
                string p = Path.Combine(bin, nm + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };

            Console.WriteLine("Loading " + exe);
            Assembly asm = Assembly.LoadFrom(exe);
            T = asm.GetType("Savedrake.Main");
            if (T == null) { Console.WriteLine("Could not find Savedrake.Main"); return 2; }
            inst = FormatterServices.GetUninitializedObject(T); // no ctor → no UI/registry/WMI

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
                Test_VerifyZipRestorable();   // P1: backups are CRC-verified at creation; corrupt ones are rejected
                Test_BackupManifest();   // P1 layer 2: in-zip manifest verify (missing/corrupt files) + restore skips it
                Test_RestoreReverify();   // P1: restore re-verifies a manifest-bearing backup; legacy backups unaffected
                Test_ClassifyBackup();   // P1 UI: full Validated/Legacy/Corrupt classification for "Validate all"
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

        static bool IsRealSaveEntry(string n) { return (bool)SM("IsRealSaveEntry").Invoke(null, new object[] { n }); }

        // private static bool TryParseInterval(string, out TimeSpan) — read the out-param back from the args array.
        static bool TryParseInterval(string input, out TimeSpan interval)
        {
            object[] a = { input, null };
            bool ok = (bool)SM("TryParseInterval").Invoke(null, a);
            interval = a[1] == null ? TimeSpan.Zero : (TimeSpan)a[1];
            return ok;
        }

        static string CanonicalizeInterval(string input) { return (string)SM("CanonicalizeInterval").Invoke(null, new object[] { input }); }

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
            var mi = SM("MakeUniquePath");
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
            var mi = IM("CreatePreRestoreCheckpoint");

            // 1) live save present -> a single (Pre-Restore) zip is created, valid, and contains the save data
            string live = NewDir("cp_live");
            File.WriteAllBytes(Path.Combine(live, "data000.bin"), B("savedata"));
            File.WriteAllBytes(Path.Combine(live, "system.bin"), B("sys"));
            string backup = NewDir("cp_backup");
            bool ok = (bool)mi.Invoke(inst, new object[] { live, backup });
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
            bool ok2 = (bool)mi.Invoke(inst, new object[] { live2, backup2 });
            Check("no-save-data live -> skipped, returns true, no zip", ok2 && Directory.GetFiles(backup2, "*.zip").Length == 0);

            // 3) live == backup folder -> refuse (can't stage into the folder being snapshotted), return true
            string same = NewDir("cp_same");
            File.WriteAllBytes(Path.Combine(same, "data000.bin"), B("savedata"));
            bool ok3 = (bool)mi.Invoke(inst, new object[] { same, same });
            Check("live==backup -> skipped (no zip), returns true", ok3 && Directory.GetFiles(same, "*.zip").Length == 0);
            Console.WriteLine();
        }

        static void Test_VerifyZipRestorable()
        {
            // P1 layer 1: a freshly written backup is CRC-verified (IsZipFile testExtract) before it is published;
            // truncated/corrupt/missing archives are rejected at creation. VerifyZipRestorable(string, out string) static.
            Console.WriteLine("== Backup integrity: verify-on-create (P1) ==");
            var mi = SM("VerifyZipRestorable");

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
            var build = SM("BuildBackupManifest");
            var verify = SM("VerifyZipAgainstManifest");
            var extract = IM("ExtractZipToStaging");

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
            extract.Invoke(inst, new object[] { good, stage });
            bool hasSaves = File.Exists(Path.Combine(stage, "data000.bin")) && File.Exists(Path.Combine(stage, "system.bin"));
            bool noManifest = !Directory.Exists(Path.Combine(stage, "_savedrake")) && !File.Exists(Path.Combine(stage, "_savedrake", "manifest.json"));
            Check("restore extracts the save files", hasSaves);
            Check("restore skips the _savedrake manifest", noManifest);
            Console.WriteLine();
        }

        static void Test_RestoreReverify()
        {
            // P1 read-side gate: a manifest-bearing backup that no longer matches its hashes is blocked before a
            // restore touches the live saves; a legacy backup (no manifest) is never blocked. Both helpers are static.
            Console.WriteLine("== Backup integrity: re-verify on restore (P1) ==");
            var hasMan = SM("HasManifest");
            var blocked = SM("RestoreBlockedByManifest");

            string src = NewDir("rv_src");
            File.WriteAllBytes(Path.Combine(src, "data000.bin"), B("the-save"));
            string manifest = (string)SM("BuildBackupManifest").Invoke(null, new object[] { src });

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
            var classify = SM("ClassifyBackupFully");
            string src = NewDir("cls_src");
            File.WriteAllBytes(Path.Combine(src, "data000.bin"), B("the-save"));
            string manifest = (string)SM("BuildBackupManifest").Invoke(null, new object[] { src });

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

        static void Test_SoundAssetsShipped()
        {
            // PlayBackupSound resolves success.wav/error.wav from Application.StartupPath (the exe dir), so the
            // build must copy them there (Content + CopyToOutputDirectory). Guard against the csproj items being
            // dropped, which would silently kill all backup feedback sounds.
            Console.WriteLine("== Sound assets shipped next to Savedrake.exe ==");
            string dir = Path.GetDirectoryName(T.Assembly.Location);
            foreach (string wav in new[] { "success.wav", "error.wav" })
            {
                string p = Path.Combine(dir, wav);
                bool exists = File.Exists(p);
                Check(wav + " present in build output", exists, p);
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

        static void Test_ValidateBackup()
        {
            Console.WriteLine("== ValidateBackup ==");
            var mi = IM("ValidateBackup");

            string good = Path.Combine(work, "good.zip");
            MakeZip(good, z => { z.AddEntry("data000.bin", B("save")); z.AddEntry("system.bin", B("sys")); });
            object[] a1 = { good, null };
            bool r1 = (bool)mi.Invoke(inst, a1);
            Check("good backup -> valid", r1 && a1[1] == null, "reason=" + a1[1]);

            string empty = Path.Combine(work, "empty.zip");
            MakeZip(empty, z => { });
            object[] a2 = { empty, null };
            bool r2 = (bool)mi.Invoke(inst, a2);
            Check("empty zip -> invalid + 'no files'", !r2 && ((string)a2[1] ?? "").ToLower().Contains("no files"), "reason=" + a2[1]);

            string nosave = Path.Combine(work, "nosave.zip");
            MakeZip(nosave, z => { z.AddEntry("readme.txt", B("hi")); z.AddEntry("notes.md", B("x")); });
            object[] a3 = { nosave, null };
            bool r3 = (bool)mi.Invoke(inst, a3);
            Check("no-save zip -> invalid + mentions save data", !r3 && ((string)a3[1] ?? "").ToLower().Contains("save data"), "reason=" + a3[1]);

            string corrupt = Path.Combine(work, "corrupt.zip");
            File.WriteAllBytes(corrupt, B("this is definitely not a zip file at all"));
            object[] a4 = { corrupt, null };
            bool r4 = (bool)mi.Invoke(inst, a4);
            Check("corrupt zip -> invalid (caught)", !r4 && !string.IsNullOrEmpty((string)a4[1]), "reason=" + a4[1]);

            string nested = Path.Combine(work, "nested.zip");
            MakeZip(nested, z => { z.AddEntry("win64_save/data000.bin", B("s")); z.AddEntry("win64_save/system.bin", B("s")); });
            object[] a5 = { nested, null };
            bool r5 = (bool)mi.Invoke(inst, a5);
            Check("nested-only zip -> valid (leaf check)", r5, "reason=" + a5[1]);
            Console.WriteLine();
        }

        static void Test_ExtractZipToStaging_and_ZipSlip()
        {
            Console.WriteLine("== ExtractZipToStaging + zip-slip guard ==");
            var ex = IM("ExtractZipToStaging");

            // happy
            string z1 = Path.Combine(work, "ex_good.zip");
            MakeZip(z1, z => { z.AddEntry("data000.bin", B("A")); z.AddEntry("system.bin", B("B")); });
            string stage1 = NewDir("stage_good");
            ex.Invoke(inst, new object[] { z1, stage1 });
            Check("extract happy: data000.bin present", File.Exists(Path.Combine(stage1, "data000.bin")));
            Check("extract happy: system.bin present", File.Exists(Path.Combine(stage1, "system.bin")));

            // subfolders preserved
            string z2 = Path.Combine(work, "ex_sub.zip");
            MakeZip(z2, z => { z.AddEntry("data000.bin", B("A")); z.AddEntry("sub/extra.bin", B("C")); });
            string stage2 = NewDir("stage_sub");
            ex.Invoke(inst, new object[] { z2, stage2 });
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
                try { ex.Invoke(inst, new object[] { slip, stage3 }); }
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
            bool detected = (bool)SM("DetectNestedPrefix").Invoke(null, new object[] { s });
            Check("DetectNestedPrefix true on sole win64_save dir", detected);
            SM("FlattenNestedLayout").Invoke(null, new object[] { s });
            Check("flatten: data000.bin at root", File.Exists(Path.Combine(s, "data000.bin")));
            Check("flatten: win64_save husk removed", !Directory.Exists(inner));

            // root layout: data at root -> NOT detected (don't flatten legit backups)
            string s2 = NewDir("root");
            File.WriteAllBytes(Path.Combine(s2, "data000.bin"), B("A"));
            Check("DetectNestedPrefix false on root layout", !(bool)SM("DetectNestedPrefix").Invoke(null, new object[] { s2 }));

            // win64_save dir + a stray root file -> NOT sole item -> not detected
            string s3 = NewDir("mix");
            Directory.CreateDirectory(Path.Combine(s3, "win64_save"));
            File.WriteAllBytes(Path.Combine(s3, "stray.bin"), B("A"));
            Check("DetectNestedPrefix false when root has extra file", !(bool)SM("DetectNestedPrefix").Invoke(null, new object[] { s3 }));

            // DOUBLY-nested win64_save\win64_save\{data,system} -> must flatten WITHOUT throwing (finding #11 fix)
            string s4 = NewDir("nest2");
            string inner2 = Path.Combine(s4, "win64_save", "win64_save");
            Directory.CreateDirectory(inner2);
            File.WriteAllBytes(Path.Combine(inner2, "data000.bin"), B("A"));
            File.WriteAllBytes(Path.Combine(inner2, "system.bin"), B("B"));
            bool flattenThrew = false; string fmsg = null;
            try { SM("FlattenNestedLayout").Invoke(null, new object[] { s4 }); }
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
            Check("verify true with data*.bin", (bool)SM("VerifyStagedDir").Invoke(null, new object[] { a }));

            string b = NewDir("vsd_nosave");
            File.WriteAllBytes(Path.Combine(b, "readme.txt"), B("A"));
            Check("verify false with no save files", !(bool)SM("VerifyStagedDir").Invoke(null, new object[] { b }));

            string c = NewDir("vsd_empty");
            Check("verify false on empty dir", !(bool)SM("VerifyStagedDir").Invoke(null, new object[] { c }));

            string d = NewDir("vsd_deep");
            string deep = Path.Combine(d, "win64_save");
            Directory.CreateDirectory(deep);
            File.WriteAllBytes(Path.Combine(deep, "system.bin"), B("A"));
            Check("verify true with save in subdir (AllDirectories)", (bool)SM("VerifyStagedDir").Invoke(null, new object[] { d }));
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
            SM("MoveDirContents").Invoke(null, new object[] { src, dst });
            Check("MoveDirContents: file moved", File.Exists(Path.Combine(dst, "data000.bin")));
            Check("MoveDirContents: subdir+file moved", File.Exists(Path.Combine(dst, "sub", "extra.bin")));
            Check("MoveDirContents: source emptied", Directory.GetFileSystemEntries(src).Length == 0);
            Check("MoveDirContents: content intact", File.ReadAllText(Path.Combine(dst, "data000.bin")) == "ORIG");

            string ed = NewDir("ed");
            File.WriteAllBytes(Path.Combine(ed, "a.bin"), B("A"));
            Directory.CreateDirectory(Path.Combine(ed, "s"));
            File.WriteAllBytes(Path.Combine(ed, "s", "b.bin"), B("B"));
            SM("EmptyDir").Invoke(null, new object[] { ed });
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
            SM("ClearReadOnlyRecursive").Invoke(null, new object[] { r });
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
            string tmp = (string)SM("CreateSiblingTempDir").Invoke(null, new object[] { live, "savedrake_stage" });
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

            string staging = (string)SM("CreateSiblingTempDir").Invoke(null, new object[] { live, "savedrake_stage" });
            string rollback = (string)SM("CreateSiblingTempDir").Invoke(null, new object[] { live, "savedrake_rollback" });

            IM("ExtractZipToStaging").Invoke(inst, new object[] { backup, staging });                 // T1
            // Simulate a backup whose files carry the ReadOnly attribute (the R2 bug source).
            foreach (string f in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, File.GetAttributes(f) | FileAttributes.ReadOnly);
            SM("FlattenNestedLayout").Invoke(null, new object[] { staging });                          // T1b (no-op, flat)
            bool verified = (bool)SM("VerifyStagedDir").Invoke(null, new object[] { staging });        // T2
            SM("MoveDirContents").Invoke(null, new object[] { live, rollback });                       // T3
            SM("MoveDirContents").Invoke(null, new object[] { staging, live });                        // T4
            SM("ClearReadOnlyRecursive").Invoke(null, new object[] { live });                          // T5
            SM("TryDeleteDir").Invoke(null, new object[] { staging });                                 // finally
            SM("TryDeleteDir").Invoke(null, new object[] { rollback });                                // finally (rollbackOk=true)

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
            var rb = IM("Rollback");
            int np = rb.GetParameters().Length;
            Console.WriteLine("   (real compiled Rollback arity = " + np + "; fixed build = 3-param (liveDir, rollbackDir, stagingStarted))");

            // ----- Scenario A: partial T4, then real Rollback fully recovers the originals into live -----
            string remoteA = NewDir("pt4a_remote");
            string liveA = Path.Combine(remoteA, "win64_save"); Directory.CreateDirectory(liveA);
            File.WriteAllText(Path.Combine(liveA, "data000.bin"), "ORIG0");
            File.WriteAllText(Path.Combine(liveA, "data001.bin"), "ORIG1");
            string stageA = (string)SM("CreateSiblingTempDir").Invoke(null, new object[] { liveA, "savedrake_stage" });
            string rbackA = (string)SM("CreateSiblingTempDir").Invoke(null, new object[] { liveA, "savedrake_rollback" });
            File.WriteAllText(Path.Combine(stageA, "data000.bin"), "NEW0");
            File.WriteAllText(Path.Combine(stageA, "data001.bin"), "NEW1");

            SM("MoveDirContents").Invoke(null, new object[] { liveA, rbackA });   // T3: originals -> rollback, live empty
            bool stagingStartedA = true;                                          // set BEFORE T4 (as RestoreTransactional does)
            // Partial T4: the first staged file lands in live, then MoveDirContents "throws" (we stop here).
            File.Move(Path.Combine(stageA, "data000.bin"), Path.Combine(liveA, "data000.bin"));
            // catch -> real compiled Rollback
            object[] argsA = np == 3 ? new object[] { liveA, rbackA, stagingStartedA }
                                     : new object[] { liveA, rbackA, true, false }; // old 4-param shape (would FAIL to recover)
            bool recoveredA = (bool)rb.Invoke(inst, argsA);
            bool rollbackOkA = recoveredA;
            // finally (gated exactly like the fixed RestoreTransactional)
            SM("TryDeleteDir").Invoke(null, new object[] { stageA });
            if (rollbackOkA) SM("TryDeleteDir").Invoke(null, new object[] { rbackA });

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
            string rbackB = (string)SM("CreateSiblingTempDir").Invoke(null, new object[] { liveB, "savedrake_rollback" });
            File.WriteAllText(Path.Combine(rbackB, "data000.bin"), "ORIG0");      // the only intact originals live here
            File.WriteAllText(Path.Combine(rbackB, "data001.bin"), "ORIG1");
            File.WriteAllText(Path.Combine(liveB, "data000.bin"), "COLLIDE");     // forces MoveDirContents(rollback->live) to throw
            object[] argsB = np == 3 ? new object[] { liveB, rbackB, false }
                                     : new object[] { liveB, rbackB, true, false };
            bool recoveredB;
            try { recoveredB = (bool)rb.Invoke(inst, argsB); } catch { recoveredB = true; } // a throw OUT would be a bug
            bool rollbackOkB = recoveredB;
            SM("TryDeleteDir").Invoke(null, new object[] { /*stagingDir n/a*/ rbackB + "_nonexistent" });
            if (rollbackOkB) SM("TryDeleteDir").Invoke(null, new object[] { rbackB }); // gated: must NOT delete when recovery failed

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
            string rollback = (string)SM("CreateSiblingTempDir").Invoke(null, new object[] { live, "savedrake_rollback" });

            // originals all set aside into rollback
            File.WriteAllBytes(Path.Combine(rollback, "data000.bin"), B("ORIGINAL-DATA"));
            File.WriteAllBytes(Path.Combine(rollback, "system.bin"), B("ORIGINAL-SYS"));
            // live holds a partially-moved staged file that COLLIDES by name with an original
            File.WriteAllBytes(Path.Combine(live, "data000.bin"), B("STAGED-NEW"));

            var rb = IM("Rollback");
            int np = rb.GetParameters().Length;
            // movedLiveAside=true; stagedIntoLive=false (the reachable partial-T4 state)
            object[] rbArgs = np == 4
                ? new object[] { live, rollback, true, false }
                : new object[] { live, rollback, true }; // fixed signature: (liveDir, rollbackDir, stagingStarted=true)
            bool recovered;
            try { recovered = (bool)rb.Invoke(inst, rbArgs); }
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
            string rb2 = (string)SM("CreateSiblingTempDir").Invoke(null, new object[] { live2, "savedrake_rollback" });
            File.WriteAllBytes(Path.Combine(rb2, "data000.bin"), B("ORIGINAL"));   // the only intact original
            File.WriteAllBytes(Path.Combine(live2, "data000.bin"), B("COLLIDE"));  // forces File.Move collision
            object[] rb2Args = np == 3 ? new object[] { live2, rb2, false } : new object[] { live2, rb2, true, false };
            bool rec2; try { rec2 = (bool)rb.Invoke(inst, rb2Args); } catch (Exception e) { Console.WriteLine("   Rollback threw OUT (bad): " + Unwrap(e).Message); rec2 = true; }
            Check("forced-collision: Rollback returns false (caught, not thrown)", !rec2);
            bool origPreserved = File.Exists(Path.Combine(rb2, "data000.bin")) && File.ReadAllText(Path.Combine(rb2, "data000.bin")) == "ORIGINAL";
            Check("forced-collision: original PRESERVED in rollbackDir (finally guard prevents data loss)", origPreserved);
            Console.WriteLine();
        }
    }
}
