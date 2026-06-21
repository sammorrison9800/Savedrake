using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Savedrake
{
    // Update-availability check, lifted out of the WinForms app during the WPF migration. TryParseVersion is the pure,
    // harness-tested half (strip a leading 'v', require 2-4 numeric parts); CheckAsync queries the GitHub "latest
    // release" API and compares. The result drives whether the app launches the external Savedrake-Updater.exe. All
    // failures (offline, timeout, non-200, missing tag) fail SAFE: ApiError is set and UpdateAvailable stays false, so
    // a connectivity hiccup never nags the user with a phantom update.
    public static class UpdateCheck
    {
        public static bool TryParseVersion(string versionString, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(versionString)) return false;

            // GitHub release tags are conventionally prefixed with 'v' (e.g. "v1.2.5"). Strip it.
            if (versionString.Length > 1 && (versionString[0] == 'v' || versionString[0] == 'V'))
                versionString = versionString.Substring(1);

            string[] parts = versionString.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;
            foreach (string part in parts)
                if (!int.TryParse(part, out int _)) return false;

            try { version = new Version(versionString); return true; }
            catch (ArgumentException) { return false; }
        }

        public sealed class Result
        {
            public bool UpdateAvailable;
            public bool ApiError;
            public string LatestTag;
            public string CurrentVersion;
        }

        public static async Task<Result> CheckAsync(string currentVersion, string owner, string repo)
        {
            var r = new Result { CurrentVersion = currentVersion };
            if (!TryParseVersion(currentVersion, out Version current)) return r;

            string tag = await GetLatestTagAsync(owner, repo, apiErr => r.ApiError = apiErr);
            r.LatestTag = tag;
            if (TryParseVersion(tag, out Version latest))
                r.UpdateAvailable = latest > current;
            return r;
        }

        private static async Task<string> GetLatestTagAsync(string owner, string repo, Action<bool> setApiError)
        {
            setApiError(false);
            string apiUrl = "https://api.github.com/repos/" + owner + "/" + repo + "/releases/latest";
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15); // a stalled connection must not hang the default ~100s
                    client.DefaultRequestHeaders.Add("User-Agent", "Savedrake Update Checker");
                    using (HttpResponseMessage response = await client.GetAsync(apiUrl))
                    {
                        response.EnsureSuccessStatusCode();
                        string body = await response.Content.ReadAsStringAsync();
                        // Null-safe: a 200 whose JSON lacks tag_name returns null (treated as "no version" -> up to date).
                        return JObject.Parse(body).Value<string>("tag_name");
                    }
                }
            }
            catch
            {
                setApiError(true); // offline / DNS / refused / timeout / parse error — fail safe
                return null;
            }
        }
    }
}
