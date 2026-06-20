using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using Ionic.Zip;
using Newtonsoft.Json.Linq;

namespace Savedrake
{
    // UI-agnostic backup-integrity / manifest helpers (P1). A backup carries an in-zip integrity manifest
    // (one path/length/SHA-256 record per source file) under a reserved "_savedrake" folder; these helpers build it,
    // verify a freshly written or on-disk archive against it, and classify a backup as Validated/Legacy/Corrupt.
    // Moved verbatim from the WinForms app's Main.cs into Savedrake.Core during the WPF migration (Phase 0); the app
    // keeps thin forwarders so call sites are unchanged. Uses DotNetZip + Newtonsoft.Json.Linq + System.Security.Cryptography.
    public static class Manifest
    {
        // Name of the integrity manifest written inside every new backup zip (P1 layer 2). Lives under a reserved
        // "_savedrake" folder so the restore can recognise and SKIP it — it must never be extracted into the live save
        // folder. Legacy backups made before this change simply have no manifest and are treated as unverified.
        public const string ManifestEntryName = "_savedrake/manifest.json";

        // True for any zip entry under Savedrake's reserved "_savedrake" metadata folder (the integrity manifest), at
        // the archive root or under any nesting. Such entries are skipped on restore.
        public static bool IsManifestEntry(string entryFileName)
        {
            string p = "/" + (entryFileName ?? "").Replace('\\', '/');
            return p.IndexOf("/_savedrake/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Sha256Hex(System.IO.Stream content)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(content)).Replace("-", "").ToLowerInvariant();
        }

        // Build the integrity manifest for a backup: one record per source file (path relative to the save folder,
        // byte length, SHA-256). Written inside the zip and verified against the zip's actual contents at creation, so
        // a backup missing a file or with silent bit-rot is detected up front instead of only failing at restore time.
        public static string BuildBackupManifest(string sourceDir)
        {
            string baseDir = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var files = new JArray();
            foreach (string file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetFullPath(file).Substring(baseDir.Length + 1).Replace('\\', '/');
                string sha;
                using (var fs = File.OpenRead(file)) sha = Sha256Hex(fs);
                files.Add(new JObject { ["path"] = rel, ["length"] = new FileInfo(file).Length, ["sha256"] = sha });
            }
            return new JObject
            {
                ["manifestVersion"] = 1,
                ["tool"] = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(),
                ["createdUtc"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["files"] = files
            }.ToString();
        }

        // Pure: derive a content-only hash from a BuildBackupManifest JSON string, ignoring its volatile
        // createdUtc/tool fields so identical content always yields the same fingerprint. Sort by path (Ordinal) so the
        // result is independent of enumeration order. Returns null if the JSON carries no files[] array.
        public static string StableManifestHash(string manifestJson)
        {
            JArray files = JObject.Parse(manifestJson)["files"] as JArray;
            if (files == null) return null;
            var rows = files
                .Select(f => ((string)f["path"]) + "|" + (long)f["length"] + "|" + ((string)f["sha256"]))
                .OrderBy(s => s, StringComparer.Ordinal);
            string joined = string.Join("\n", rows);
            using (var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(joined)))
                return Sha256Hex(ms);
        }

        // Backup integrity verification, layer 1 (P1): prove a freshly written archive is actually restorable before
        // we publish or trust it. DotNetZip's IsZipFile(testExtract:true) opens the zip, reads its directory, and
        // expands EVERY entry while checking CRCs, so truncation, bit-rot, or a half-written/locked source file is
        // caught at creation time instead of only when the user finally needs the backup. Returns false (with a
        // reason) on any failure. Static + file-path-only so the headless harness can test it directly.
        public static bool VerifyZipRestorable(string zipPath, out string reason)
        {
            reason = null;
            try
            {
                // testExtract = true: don't just check the signature, expand every entry and verify its CRC.
                if (!Ionic.Zip.ZipFile.IsZipFile(zipPath, true))
                {
                    reason = "the archive is not a valid, fully readable zip (it may be truncated or corrupt)";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = "the archive could not be verified: " + ex.Message;
                return false;
            }
        }

        // Backup integrity verification, layer 2 (P1): confirm the freshly written archive contains every file the
        // manifest declares, each with the recorded length + SHA-256. Catches a backup missing whole files (e.g. a zip
        // truncated at an entry boundary, which the layer-1 CRC test-extract can miss) and per-file bit-rot. Returns
        // false (with a reason) on any missing/mismatched file or a missing/garbled manifest.
        public static bool VerifyZipAgainstManifest(string zipPath, out string reason)
        {
            reason = null;
            try
            {
                using (Ionic.Zip.ZipFile zip = Ionic.Zip.ZipFile.Read(zipPath))
                {
                    Ionic.Zip.ZipEntry manifestEntry = zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.FileName.Replace('\\', '/'), ManifestEntryName, StringComparison.OrdinalIgnoreCase));
                    if (manifestEntry == null) { reason = "the backup has no integrity manifest"; return false; }

                    string json;
                    using (var ms = new System.IO.MemoryStream()) { manifestEntry.Extract(ms); json = System.Text.Encoding.UTF8.GetString(ms.ToArray()); }
                    JArray files = JObject.Parse(json)["files"] as JArray;
                    if (files == null) { reason = "the integrity manifest is unreadable"; return false; }

                    var byPath = zip.Entries.Where(e => !e.IsDirectory && !IsManifestEntry(e.FileName))
                        .ToDictionary(e => e.FileName.Replace('\\', '/'), e => e, StringComparer.OrdinalIgnoreCase);

                    foreach (JToken ft in files)
                    {
                        string rel = (string)ft["path"];
                        if (rel == null || !byPath.TryGetValue(rel, out Ionic.Zip.ZipEntry e))
                        { reason = "a file recorded in the manifest is missing from the backup: " + rel; return false; }
                        if (e.UncompressedSize != (long)ft["length"])
                        { reason = "a file's size does not match the manifest: " + rel; return false; }
                        string actual;
                        using (var ms = new System.IO.MemoryStream()) { e.Extract(ms); ms.Position = 0; actual = Sha256Hex(ms); }
                        if (!string.Equals(actual, (string)ft["sha256"], StringComparison.OrdinalIgnoreCase))
                        { reason = "a file's contents do not match the manifest (corrupt): " + rel; return false; }
                    }
                    return true;
                }
            }
            catch (Exception ex) { reason = "the backup could not be verified against its manifest: " + ex.Message; return false; }
        }

        // True if the backup zip carries an integrity manifest. Cheap: reads the zip directory only, does not hash.
        public static bool HasManifest(string zipPath)
        {
            try
            {
                using (Ionic.Zip.ZipFile zip = Ionic.Zip.ZipFile.Read(zipPath))
                    return zip.Entries.Any(e => string.Equals(e.FileName.Replace('\\', '/'), ManifestEntryName, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        // Read-side integrity gate for restore (P1): returns true if the restore should be BLOCKED because the backup
        // carries a manifest that no longer matches its contents — i.e. the backup has corrupted on disk since it was
        // created. Legacy backups (no manifest, made before this feature) are never blocked here, so old backups keep
        // restoring exactly as before. Static + file-path-only so the headless harness can test the decision.
        public static bool RestoreBlockedByManifest(string zipPath, out string reason)
        {
            reason = null;
            if (!HasManifest(zipPath)) return false;                                        // legacy backup -> not gated
            if (VerifyZipAgainstManifest(zipPath, out reason)) { reason = null; return false; } // matches -> allowed
            return true;                                                                    // manifest mismatch -> block
        }

        // Full integrity classification for the "Validate all backups" action (P1): "Validated" (CRC ok + manifest
        // matches), "Legacy" (no manifest — predates the feature), or "Corrupt" (CRC fails, or a manifest is present
        // but does not match). Hashes the archive, so it is on-demand only, never on a list refresh. Static for tests.
        public static string ClassifyBackupFully(string zipPath)
        {
            if (!VerifyZipRestorable(zipPath, out _)) return "Corrupt";
            if (!HasManifest(zipPath)) return "Legacy";
            return VerifyZipAgainstManifest(zipPath, out _) ? "Validated" : "Corrupt";
        }
    }
}
