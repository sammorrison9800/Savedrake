using System.IO;

namespace Savedrake
{
    // The persisted "how many autobackups exist" counter. It is a single integer in a text file under the app data
    // directory; the autobackup limit is enforced against it. Reading is deliberately forgiving: a missing, empty,
    // garbled, or transiently locked file reads as 0 rather than throwing, because the count is advisory (the real
    // source of truth is the files on disk, recomputed after every backup) and a read hiccup must never crash or
    // wedge the autobackup loop. This is the one intentional hardening over the shipped inline read, which had no
    // guard around File.ReadAllText.
    public static class AutobackupCountStore
    {
        public static int Read(string countFilePath)
        {
            int count = 0;
            try
            {
                if (!string.IsNullOrEmpty(countFilePath) && File.Exists(countFilePath))
                {
                    int.TryParse(File.ReadAllText(countFilePath), out count);
                }
            }
            catch
            {
                // Locked / unreadable count file: treat as 0. Worst case is one extra eligible attempt, which the
                // change-aware gate and the on-disk recount immediately correct.
            }
            return count;
        }
    }
}
