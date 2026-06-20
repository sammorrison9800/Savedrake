using System;
using System.IO;

namespace Savedrake
{
    // UI-agnostic backup-pin token helpers. A pinned backup carries a "[PINNED]" token in its file name (not an
    // attribute or index) so it survives copy/move, is visible in Explorer and the backup list, and needs no extra
    // storage. Renaming a pinned file outside Savedrake to drop the token simply unpins it. Moved verbatim from the
    // WinForms app's Main.cs into Savedrake.Core during the WPF migration (Phase 0); the app keeps thin forwarders so
    // call sites are unchanged. Dependency-free.
    public static class Pinning
    {
        public const string PinTag = "[PINNED]";

        public static bool IsPinnedBackup(string fileName)
        {
            return !string.IsNullOrEmpty(fileName) && fileName.IndexOf(PinTag, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The pinned form of a backup path: insert " [PINNED]" before the extension. Idempotent (already-pinned -> unchanged).
        public static string PinnedPath(string path)
        {
            if (IsPinnedBackup(Path.GetFileName(path))) return path;
            return Path.Combine(Path.GetDirectoryName(path),
                Path.GetFileNameWithoutExtension(path) + " " + PinTag + Path.GetExtension(path));
        }

        // The unpinned form of a backup path: strip the " [PINNED]" token. Idempotent.
        public static string UnpinnedPath(string path)
        {
            string cleaned = Path.GetFileName(path).Replace(" " + PinTag, "").Replace(PinTag, "");
            return Path.Combine(Path.GetDirectoryName(path), cleaned);
        }
    }
}
