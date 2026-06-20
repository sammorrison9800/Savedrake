using System.IO;

namespace Savedrake
{
    // UI-agnostic backup-path naming. Moved verbatim from the WinForms app's Main.cs into Savedrake.Core during the
    // WPF migration (Phase 0); the app keeps a thin forwarder so call sites are unchanged. Dependency-free.
    public static class BackupNaming
    {
        public static string MakeUniquePath(string fullPath)
        {
            if (!File.Exists(fullPath)) return fullPath;
            string dir = Path.GetDirectoryName(fullPath);
            string name = Path.GetFileNameWithoutExtension(fullPath);
            string ext = Path.GetExtension(fullPath);
            int n = 2;
            string candidate;
            do { candidate = Path.Combine(dir, $"{name}_{n++}{ext}"); } while (File.Exists(candidate));
            return candidate;
        }
    }
}
