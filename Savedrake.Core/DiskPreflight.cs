using System;
using System.IO;
using System.Linq;
using Ionic.Zip;

namespace Savedrake
{
    // UI-agnostic disk-space preflight helpers. Pure size math (directory size, zip uncompressed size) plus a
    // free-space check that FAILS OPEN when the volume can't be determined, so a space-check error never blocks an
    // otherwise-legitimate operation. Moved verbatim from the WinForms app's Main.cs into Savedrake.Core during the
    // WPF migration (Phase 0); the app keeps thin forwarders so call sites are unchanged.
    public static class DiskPreflight
    {
        // Disk-space preflight headroom: never plan an operation that would leave the volume essentially full.
        public const long SafetyMarginBytes = 64L * 1024 * 1024; // 64 MB

        // Total size of all files under a directory (recursive). Best-effort: unreadable files are skipped, not fatal.
        public static long GetDirectorySize(string dir)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { total += new FileInfo(f).Length; } catch { }
            }
            catch { }
            return total;
        }

        // Sum of the UNCOMPRESSED sizes of a zip's entries (read from the central directory; does not extract). This
        // is how much space a restore's staging extraction needs on the save volume.
        public static long GetZipUncompressedSize(string zipPath)
        {
            try
            {
                using (Ionic.Zip.ZipFile zip = Ionic.Zip.ZipFile.Read(zipPath))
                    return zip.Entries.Where(e => !e.IsDirectory).Sum(e => e.UncompressedSize);
            }
            catch { return 0; }
        }

        // Disk-space preflight: is there room on targetDir's volume for requiredBytes plus a safety margin? Fails OPEN
        // (returns true) if free space can't be determined (e.g. a network path), so a space-check error never blocks
        // an otherwise-legitimate operation.
        public static bool HasFreeSpaceFor(string targetDir, long requiredBytes, out string reason)
        {
            reason = null;
            try
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(targetDir)));
                long needed = requiredBytes + SafetyMarginBytes;
                if (drive.AvailableFreeSpace < needed)
                {
                    reason = "not enough free space on " + drive.Name + " (about " + (needed / (1024 * 1024)) +
                             " MB needed, " + (drive.AvailableFreeSpace / (1024 * 1024)) + " MB free)";
                    return false;
                }
                return true;
            }
            catch (Exception) { return true; } // can't determine -> don't block a legitimate operation
        }
    }
}
