using System;
using Microsoft.Win32;

namespace Savedrake
{
    // Game-running detection for Dragon's Dogma 2 (Steam app 2054970). Steam writes a "Running" DWORD (0/1) under
    // HKCU\Software\Valve\Steam\Apps\<appid> while the game is up, so a plain registry read tells us the live state
    // with no process scanning. Extracted verbatim from the WinForms CheckGameRunningStatus during the WPF migration
    // (Phase 5) so both the shipped app and the new WPF autobackup engine read the exact same key the same way.
    public static class GameDetect
    {
        // Dragon's Dogma 2's Steam application id.
        public const string Dd2SteamAppId = "2054970";

        private static string RunningKeyPath => @"Software\Valve\Steam\Apps\" + Dd2SteamAppId;

        // True only when Steam reports DD2 as Running (the "Running" value == 1). Any other state (key absent, value
        // absent, non-int, or 0) reads as not running. Never throws for a missing key — that is the normal "DD2 not
        // installed / not launched" case.
        public static bool IsDd2Running()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunningKeyPath))
            {
                if (key != null)
                {
                    object runningValue = key.GetValue("Running");
                    if (runningValue != null && runningValue is int)
                    {
                        return Convert.ToInt32(runningValue) == 1;
                    }
                }
            }
            return false;
        }
    }
}
