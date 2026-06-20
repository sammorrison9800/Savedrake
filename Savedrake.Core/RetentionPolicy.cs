using System;
using System.Collections.Generic;
using System.Linq;

namespace Savedrake
{
    // UI-agnostic autobackup retention (tiered time-bucket thinning). Moved verbatim from the WinForms app's Main.cs
    // into Savedrake.Core during the WPF migration (Phase 0); the app keeps a thin forwarder so call sites are
    // unchanged. Dependency-free.
    public static class RetentionPolicy
    {
        // given the UTC tick timestamps of the THINNABLE autobackups only (the caller excludes manual backups, pinned
        // ones, the (Pre-Restore) checkpoint, and corrupt backups, which are never thinned). Tiered time-bucket policy
        // (Borg/restic style): keep EVERY backup in the last hour, then the NEWEST per bucket as buckets widen — one
        // per 30 min to 6h, per hour to 24h, per day to 7d, per week beyond. After bucketing, if more than maxKeep
        // survive (maxKeep <= 0 means no cap), the OLDEST survivors are thinned too until maxKeep remain. Deterministic,
        // idempotent (re-running at the same 'now' on the survivors thins nothing more), and independent of input order.
        // Returns the indices INTO candidateTicksUtc of the entries to delete.
        public static int[] SelectAutobackupsToThin(long[] candidateTicksUtc, long nowTicksUtc, int maxKeep)
        {
            if (candidateTicksUtc == null || candidateTicksUtc.Length == 0) return new int[0];

            long H = TimeSpan.FromHours(1).Ticks;
            long D = TimeSpan.FromDays(1).Ticks;
            long[] tierMaxAge = { 1 * H, 6 * H, 24 * H, 7 * D, long.MaxValue };
            long[] tierInterval = { 0L, TimeSpan.FromMinutes(30).Ticks, H, D, 7 * D };

            var keep = new HashSet<int>();
            var bucketBest = new Dictionary<string, int>(); // "tier:bucket" -> index of the newest candidate so far

            for (int i = 0; i < candidateTicksUtc.Length; i++)
            {
                long age = nowTicksUtc - candidateTicksUtc[i];
                if (age < 0) age = 0; // clock skew / future-dated backup -> treat as newest, keep it

                int t = 0;
                while (t < tierMaxAge.Length && age > tierMaxAge[t]) t++;
                if (t >= tierMaxAge.Length) t = tierMaxAge.Length - 1;

                if (tierInterval[t] == 0) { keep.Add(i); continue; } // recent tier: keep all

                long tierStart = t == 0 ? 0 : tierMaxAge[t - 1];
                long bucket = (age - tierStart) / tierInterval[t];
                string key = t + ":" + bucket;
                if (!bucketBest.TryGetValue(key, out int cur)) bucketBest[key] = i;
                else
                {
                    long a = candidateTicksUtc[i], b = candidateTicksUtc[cur];
                    if (a > b || (a == b && i > cur)) bucketBest[key] = i; // keep newest; deterministic tie-break
                }
            }
            foreach (var kv in bucketBest) keep.Add(kv.Value);

            // Hard cap: if more than the user's "max autobackups" still survive, thin the OLDEST survivors until maxKeep remain.
            if (maxKeep > 0 && keep.Count > maxKeep)
            {
                var keptOldestFirst = keep.OrderBy(i => candidateTicksUtc[i]).ThenBy(i => i).ToList();
                int toRemove = keep.Count - maxKeep;
                for (int k = 0; k < toRemove; k++) keep.Remove(keptOldestFirst[k]);
            }

            var del = new List<int>();
            for (int i = 0; i < candidateTicksUtc.Length; i++) if (!keep.Contains(i)) del.Add(i);
            return del.ToArray();
        }
    }
}
