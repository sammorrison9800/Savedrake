using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Windows.Forms;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using System.Drawing;
using System.Linq;
using System.Threading;


namespace updater
{
    public partial class UpdaterForm : Form
    {
        //Check only one instance is running using Mutex
        // Add a static mutex field
        private static Mutex mutex = new Mutex(true, "4593632f-d6f1-425c-83b4-6b70fa3092a4");


        private string downloadUrl;
        private string extractPath;
        private string executablePath;
        private string latestVersion;
        private string currentVersion;

        private new const string Owner = "sammorrison9800";
        private const string Repo = "Savedrake";
        private const string GitHubTokenEnvironmentVariable = "GITHUB_TOKEN";

        // run4: version.txt and savedrake-updater.xml now live in %APPDATA%\Savedrake — the same path the main app
        // (Savedrake v1.2.3/Main.cs) writes them to. Computed identically so the two stay in lockstep.
        private static readonly string AppDataDir = CreateAppDataDir();
        private static string CreateAppDataDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Savedrake");
            Directory.CreateDirectory(dir); // no-op if it already exists
            return dir;
        }
        private static string VersionFilePath => Path.Combine(AppDataDir, "version.txt");
        private static string UpdaterXmlPath => Path.Combine(AppDataDir, "savedrake-updater.xml");

        public UpdaterForm()
        {
            // Attempt to acquire the mutex
            if (!mutex.WaitOne(TimeSpan.Zero, true))
            {
                // If the mutex is already acquired, it means another instance is running
                MessageBox.Show("Another instance of the application is already running.", "Instance Running", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                Environment.Exit(1); // Exit the application
            }


            InitializeComponent();
            extractPath = Application.StartupPath; // Initialize extractPath with the startup path
            executablePath = Path.Combine(extractPath, "Savedrake.exe");
            InitializeUpdateProcess();
        }

        private async void InitializeUpdateProcess()
        {
            currentVersion = GetCurrentVersion();
            if (currentVersion == null)
            {
                Environment.Exit(1); 
                return;
            }
            // Assuming GetLatestVersionFromGit is an async method that returns a Task<string>
            latestVersion = await GetLatestVersionFromGit();
            // Only build a download URL once we actually have a version. If the initial query failed/rate-limited,
            // latestVersion is null and a naive format string would yield a malformed ".../download//update.zip"
            // that 404s on Start Update. Leave downloadUrl null; ApplyUpdateAsync bails out cleanly on null.
            if (!string.IsNullOrEmpty(latestVersion))
            {
                downloadUrl = Uri.EscapeUriString($"https://github.com/{Owner}/{Repo}/releases/download/{latestVersion}/update.zip");
            }
        }

        private string GetCurrentVersion()
        {
            if (string.IsNullOrEmpty(extractPath))
            {
                MessageBox.Show("The extraction path is not set.", "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }

            // run4: version.txt now lives in %APPDATA%\Savedrake (written there by the main app). Fall back to the
            // legacy install-dir copy so an updater run BEFORE the new main app has migrated it forward still works.
            string versionFilePath = File.Exists(VersionFilePath)
                ? VersionFilePath
                : Path.Combine(extractPath, "version.txt");
            if (File.Exists(versionFilePath))
            {
                return File.ReadAllText(versionFilePath).Trim();
            }
            else
            {
                MessageBox.Show("The version file does not exist. Please run Savedrake at least once before checking for updates.", "File Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        private async Task<bool> CheckForUpdatesAsync()
        {
            bool isUpdateAvailable = false;
            if (TryParseVersion(GetCurrentVersion(), out Version currentVersion) &&
                TryParseVersion(await GetLatestVersionFromGit(), out Version latestVersion))
            {
                if (latestVersion > currentVersion)
                {
                    isUpdateAvailable = true;
                }
            }
            else
            {
                //MessageBox.Show("Failed to parse the version information.", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return isUpdateAvailable;
        }

        private bool TryParseVersion(string versionString, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(versionString))
            {
                return false;
            }

            // GitHub release tags are conventionally prefixed with 'v' (e.g. "v1.2.5").
            // Strip it so a future v-prefixed tag doesn't silently break update checks.
            if (versionString.Length > 1 && (versionString[0] == 'v' || versionString[0] == 'V'))
            {
                versionString = versionString.Substring(1);
            }

            string[] versionParts = versionString.Split('.');
            if (versionParts.Length < 2 || versionParts.Length > 4)
            {
                return false;
            }

            foreach (string part in versionParts)
            {
                if (!int.TryParse(part, out int _))
                {
                    return false;
                }
            }

            try
            {
                version = new Version(versionString);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        private bool IsAPIError = false;
        private async Task<string> GetLatestVersionFromGit()
        {
            string apiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

            using (HttpClient client = new HttpClient())
            {
                string token = Environment.GetEnvironmentVariable(GitHubTokenEnvironmentVariable);
                if (!string.IsNullOrEmpty(token))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"token {token}");
                }
                client.DefaultRequestHeaders.Add("User-Agent", "Savedrake Update Checker");

                try
                {
                    HttpResponseMessage response = await client.GetAsync(apiUrl);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            MessageBox.Show("Update check failed due to rate limiting. Please try again later.", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            MessageBox.Show($"Update check failed with status code: {response.StatusCode}.", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        return null;
                    }

                    string responseBody = await response.Content.ReadAsStringAsync();
                    JObject json = JObject.Parse(responseBody);

                    string tagName = json["tag_name"].ToString();
                    return tagName;
                }
                catch (HttpRequestException)
                {

                    //MessageBox.Show("Update check failed. Could not connect to the internet.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    IsAPIError = true;
                    // This catch can run on a ThreadPool thread (UpdaterForm_Load -> Task.Run -> ExecuteUpdateProcess
                    // -> ... -> here), so touching button3 directly is an unmarshaled cross-thread UI access. Marshal
                    // it like the rest of the file's run4 fixes already do.
                    SetStartButtonDisabled();
                }
                catch (Exception e)
                {
                    if (!IsAPIError)
                    {
                        MessageBox.Show($"An unexpected error occurred. {e.Message}", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    
                }
                return null;
            }
        }

        // UI event handlers and methods...

        private bool VerifyVersionFile(string versionFilePath)
        {
            // run4: real check (was a return-true stub). The local version.txt must exist and parse as a version
            // so we have a known current version before replacing anything.
            try
            {
                return File.Exists(versionFilePath)
                    && TryParseVersion(File.ReadAllText(versionFilePath).Trim(), out _);
            }
            catch
            {
                return false;
            }
        }

        private bool VerifyUpdatePackage(string packagePath)
        {
            // run4: real integrity check (was a return-true stub). NOTE: this is NOT cryptographic authenticity —
            // verifying the publisher's SIGNATURE would require a signing key Savedrake doesn't ship. The download
            // is HTTPS from github.com (transport integrity + GitHub account trust); this additionally confirms the
            // package is a well-formed Savedrake update for the version we resolved, which catches a corrupt /
            // truncated download or a wrong/unexpected archive before it overwrites the install.
            try
            {
                if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
                {
                    return false;
                }
                using (ZipArchive archive = ZipFile.OpenRead(packagePath))
                {
                    // Must contain the application executable — i.e. it really is a Savedrake build. Match on the
                    // leaf name (not the full path) so a future archive layout can't cause a false rejection.
                    bool hasExe = archive.Entries.Any(en =>
                        string.Equals(Path.GetFileName(en.FullName), "Savedrake.exe", StringComparison.OrdinalIgnoreCase));
                    if (!hasExe)
                    {
                        return false;
                    }

                    // No version cross-check here: the download URL already targets the resolved release's
                    // update.zip, so the package is for the right version by construction. (A naive System.Version
                    // equality would also be wrong — the 3-part release tag "1.2.4" != the 4-part AssemblyVersion
                    // "1.2.4.0" that the app writes into version.txt, which would reject every legitimate release.)
                    return true;
                }
            }
            catch
            {
                return false; // not a readable zip / IO error -> treat as failed verification
            }
        }

        // Entries that must NOT be overwritten by an update: the user's state files (settings/updater prefs/
        // version), the feedback sounds, and the updater's OWN running files (which are locked while it runs).
        private static bool IsSkippedUpdateEntry(string entryFullName)
        {
            return entryFullName.Equals("savedrake_settings.xml", StringComparison.OrdinalIgnoreCase)
                || entryFullName.Equals("Savedrake-Updater.exe", StringComparison.OrdinalIgnoreCase)
                || entryFullName.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase)
                || entryFullName.Equals("savedrake-updater.xml", StringComparison.OrdinalIgnoreCase)
                || entryFullName.Equals("success.wav", StringComparison.OrdinalIgnoreCase)
                || entryFullName.Equals("error.wav", StringComparison.OrdinalIgnoreCase);
        }

        // Disable the Start Update button safely from any thread (callers may be on a ThreadPool thread).
        private void SetStartButtonDisabled()
        {
            Action act = () =>
            {
                button3.Enabled = false;
                button3.BackColor = System.Drawing.ColorTranslator.FromHtml("#f0f0f0");
            };
            if (IsHandleCreated && InvokeRequired) this.Invoke(act);
            else act();
        }


        //UI
        #region UI
        private async void button3_Click(object sender, EventArgs e)
        {
            DialogResult dialogResult = MessageBox.Show("This will download, install , and restart the latest version of the application.\n\nYour savefiles and backups will not be affected. Do you wish to proceed?", "Confirm Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            // If the user chooses 'No', exit the method
            if (dialogResult == DialogResult.No)
            {
                button3.Enabled = true; // Re-enable the Start Update button
                button3.BackColor = Color.White;
                return;
            }
            else
            {
                button3.Enabled=false;
                button3.BackColor = System.Drawing.ColorTranslator.FromHtml("#f0f0f0");
                // run4: do NOT kill the running app here — that previously happened BEFORE the download, so a
                // failed/aborted download left the user with the app killed and no update. The kill now happens
                // inside ApplyUpdateAsync only after the package is downloaded and verified, right before extract.
                if (!IsAPIError)
                {
                    await Task.Run(async () => {
                        await ApplyUpdateAsync();
                    });
                }
            }

            
            
        }

        private void button4_Click(object sender, EventArgs e)
        {
            //Process.Start(executablePath);
            Environment.Exit(1);
        }

        private void button2_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("This will open the Savedrake latest release Github in your default web browser. Do you want to proceed?", "Open Github", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Process.Start($"https://github.com/{Owner}/{Repo}/releases/tag/{latestVersion}/");
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("This will open the Savedrake Github page in your default web browser. Do you want to proceed?", "Open Github", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start("https://github.com/sammorrison9800/Savedrake/releases/");
            }
        }

        private async void UpdaterForm_Load(object sender, EventArgs e)
        {
            this.Focus();
            try { LoadCheckBoxesFromXml(); } catch { }
            label4.Invoke((MethodInvoker)(() => label4.Text = currentVersion));
            

            // Set the current version in label4
            

            // Run the update process in the background
            await Task.Run(() => {
                ExecuteUpdateProcess();
            });

        }

        private async void ExecuteUpdateProcess()
        {
            bool isUpdateAvailable = await CheckForUpdatesAsync();
            if (isUpdateAvailable)
            {
                label1.Invoke((MethodInvoker)(() => label1.Text = "A new version of Savedrake is available.\nAn update is recommended."));
                linkLabel1.Invoke((MethodInvoker)(() => linkLabel1.Text = latestVersion));
            }
            else
            {
                if (!IsAPIError)
                {
                    label1.Invoke((MethodInvoker)(() => label1.Text = "Your Savedrake is up to date."));
                    linkLabel1.Invoke((MethodInvoker)(() => linkLabel1.Text = currentVersion));
                }
                else 
                {
                    label1.Invoke((MethodInvoker)(() => label1.Text = "Error connecting to the internet."));
                    linkLabel1.Invoke((MethodInvoker)(() => linkLabel1.Text = "????"));
                }
                
                
            }
            linkLabel1.Invoke((MethodInvoker)(() => linkLabel1.Visible = true)); // Make sure the linkLabel is visible
        }
        #endregion
        private string tempDownloadPath;
        private async Task ApplyUpdateAsync()
        {
            // Declare tempDownloadPath at the beginning of the method
            await Task.Run(() => progressBar1.Invoke(new Action(() => progressBar1.Style = ProgressBarStyle.Marquee))); // Indeterminate progress
            await Task.Run(() => label5.Invoke(new Action(() => label5.Text = "Please wait...")));

            // Disable the Start Update button to prevent multiple clicks
            //button3.Enabled = false;


            // run4: version.txt now lives in %APPDATA% (with a legacy install-dir fallback), same as GetCurrentVersion.
            string localVersionFile = File.Exists(VersionFilePath) ? VersionFilePath : Path.Combine(extractPath, "version.txt");
            if (!VerifyVersionFile(localVersionFile))
            {
                // run4: this method runs on a Task.Run pool thread, so marshal the dialog AND re-enable the button
                // on the UI thread — otherwise the button stays stuck disabled with no way to retry but a restart.
                if (this.IsHandleCreated)
                {
                    this.Invoke((MethodInvoker)(() =>
                    {
                        button3.Enabled = true;
                        button3.BackColor = Color.White;
                        MessageBox.Show("The version file failed the integrity check.", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                return;
            }

            // run4: package verification moved to AFTER the download (it used to run here on a null tempDownloadPath,
            // before the file was even fetched). See below, right after the download completes.

            

            // Ask the user if they want to proceed with the update
            

           

            // audit: bail early if we never resolved a download URL (initial version query failed/rate-limited),
            // instead of requesting a malformed ".../download//update.zip" and surfacing a confusing 404.
            if (string.IsNullOrEmpty(downloadUrl))
            {
                if (this.IsHandleCreated)
                {
                    this.Invoke((MethodInvoker)(() =>
                    {
                        button3.Enabled = true;
                        button3.BackColor = Color.White;
                        MessageBox.Show("Could not determine the latest version to download. Please check your connection and try again.", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                return;
            }

            bool appKilled = false;   // did we stop the running Savedrake? (governs relaunch-on-failure)
            string stagingDir = null;
            try
            {
                // Download the verified package to a temp file (the field, so cleanup is consistent).
                tempDownloadPath = Path.GetTempFileName();
                using (HttpClient client = new HttpClient())
                {
                    HttpResponseMessage response = await client.GetAsync(downloadUrl);
                    response.EnsureSuccessStatusCode();
                    byte[] updateBytes = await response.Content.ReadAsByteArrayAsync();
                    File.WriteAllBytes(tempDownloadPath, updateBytes);
                }

                // Verify the DOWNLOADED package before touching the install. On failure nothing is killed/extracted.
                if (!VerifyUpdatePackage(tempDownloadPath))
                {
                    try { File.Delete(tempDownloadPath); } catch { }
                    throw new Exception("The downloaded update package failed verification and was not installed.");
                }

                // Install root with a trailing separator so the zip-slip check below cannot be bypassed by a
                // sibling-prefix path like "<extractPath>-evil\..." matching StartsWith(extractPath).
                string installRoot = Path.GetFullPath(extractPath);
                if (!installRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                {
                    installRoot += Path.DirectorySeparatorChar;
                }

                // ---- PHASE 1: extract the whole package to a STAGING dir BEFORE killing the app or touching the
                // install. If any entry fails (a nested path, a disk error, a zip-slip attempt), the live install is
                // untouched and Savedrake is still running — no half-replaced, app-already-killed "brick".
                stagingDir = Path.Combine(Path.GetTempPath(), "Savedrake_update_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingDir);
                string stagingRoot = stagingDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? stagingDir : stagingDir + Path.DirectorySeparatorChar;

                var staged = new List<string>(); // entry FullNames actually written to staging
                // Dedup by normalized destination (case-insensitive): if a malformed package had two entries that
                // map to the SAME install path (e.g. "foo.dll" and "Foo.DLL" on Windows), recording both would put
                // a duplicate op in the swap list, and a later rollback would delete the just-restored original a
                // second time with no backup left. Stage/record each destination once.
                var seenDest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (ZipArchive archive = ZipFile.OpenRead(tempDownloadPath))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;      // directory entry — nothing to write
                        if (IsSkippedUpdateEntry(entry.FullName)) continue;  // user state + the updater's own files

                        string dest = Path.GetFullPath(Path.Combine(stagingRoot, entry.FullName));
                        if (!dest.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)); // ExtractToFile won't create parents
                        entry.ExtractToFile(dest, true);
                        if (seenDest.Add(dest)) staged.Add(entry.FullName);  // first entry for this dest wins
                    }
                }

                // ---- PHASE 2: package fully staged — now stop the running Savedrake so its files can be replaced.
                // WaitForExit so the OS releases the file locks before we swap over them. Only treat the app as
                // "killed" (→ relaunch on failure) if we actually stopped a running instance AND it exited; a
                // timed-out/failed kill must NOT set appKilled, or the failure path would spawn a 2nd instance
                // on top of the still-running one.
                bool killedAny = false, allExitedCleanly = true;
                foreach (Process p in Process.GetProcessesByName("Savedrake"))
                {
                    killedAny = true;
                    try { p.Kill(); if (!p.WaitForExit(5000)) allExitedCleanly = false; }
                    catch { allExitedCleanly = false; }
                }
                appKilled = killedAny && allExitedCleanly;

                // ---- PHASE 3: swap staged files into the install dir, backing up each replaced file so a mid-swap
                // failure can be rolled back to the prior install. ops: dest -> backup ("" = the file was new).
                var ops = new List<KeyValuePair<string, string>>();
                try
                {
                    foreach (string rel in staged)
                    {
                        string src = Path.Combine(stagingDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        string dest = Path.GetFullPath(Path.Combine(installRoot, rel));
                        if (!dest.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));

                        string bak = "";
                        if (File.Exists(dest))
                        {
                            bak = dest + ".sdbak";
                            // Set the original aside — but ONLY if a .sdbak doesn't already exist. A leftover
                            // .sdbak is the authoritative original from a previously-crashed update; the current
                            // dest may be the truncated file that crash left behind, so never overwrite the .sdbak
                            // with it. We just overwrite dest below; the real original stays safe in .sdbak.
                            if (!File.Exists(bak))
                            {
                                File.Move(dest, bak); // same volume → atomic-ish
                            }
                        }
                        // Record the op BEFORE the copy: File.Copy is not atomic, so a mid-write failure must still
                        // roll this file back (delete the truncated dest, restore bak) — otherwise the in-flight
                        // file would be stranded truncated with its original orphaned in .sdbak.
                        ops.Add(new KeyValuePair<string, string>(dest, bak));
                        File.Copy(src, dest, true);
                    }
                }
                catch
                {
                    // Roll back newest-first, restoring the prior install exactly.
                    for (int i = ops.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            string dest = ops[i].Key, bak = ops[i].Value;
                            if (string.IsNullOrEmpty(bak))
                            {
                                if (File.Exists(dest)) File.Delete(dest); // it was a newly added file
                            }
                            else
                            {
                                if (File.Exists(dest)) File.Delete(dest);
                                if (File.Exists(bak)) File.Move(bak, dest); // restore the original
                            }
                        }
                        catch { /* best-effort rollback */ }
                    }
                    throw; // surface to the outer catch, which relaunches the rolled-back app
                }

                // Swap committed — drop the per-file backups and scratch dirs.
                foreach (var op in ops)
                {
                    if (!string.IsNullOrEmpty(op.Value)) { try { File.Delete(op.Value); } catch { } }
                }
                try { Directory.Delete(stagingDir, true); } catch { }
                try { File.Delete(tempDownloadPath); } catch { }

                await Task.Run(() => progressBar1.Invoke(new Action(() => { progressBar1.Style = ProgressBarStyle.Continuous; progressBar1.Maximum = 100; progressBar1.Value = 100; progressBar1.Visible = true; })));
                await Task.Run(() => label5.Invoke(new Action(() => label5.Text = "Update complete.")));

                this.Invoke((MethodInvoker)(() => MessageBox.Show("Update successful! The application will now start.", "Update Finished", MessageBoxButtons.OK, MessageBoxIcon.Information)));

                Process.Start(executablePath); // launch the NEW version
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                // Re-enable the button on the UI thread (this runs on a pool thread).
                if (this.IsHandleCreated)
                {
                    this.Invoke((MethodInvoker)(() =>
                    {
                        button3.Enabled = true;
                        button3.BackColor = Color.White;
                    }));
                }

                // If we already killed the app, the install is now either fully updated or fully rolled back to the
                // prior version (never half-replaced), so relaunch it rather than leaving the user with nothing.
                if (appKilled)
                {
                    try { Process.Start(executablePath); } catch { }
                }

                if (stagingDir != null) { try { Directory.Delete(stagingDir, true); } catch { } }
                if (!string.IsNullOrEmpty(tempDownloadPath)) { try { File.Delete(tempDownloadPath); } catch { } }

                // Show the error message to the user
                if (!IsAPIError)
                {
                    if (this.InvokeRequired && this.IsHandleCreated)
                    {
                        this.Invoke((MethodInvoker)(() => MessageBox.Show($"An error occurred while updating: {ex.Message}", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                    }
                    else
                    {
                        MessageBox.Show($"An error occurred while updating: {ex.Message}", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }

            }
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            SaveCheckBoxesToXml(checkBox1.Checked, checkBox2.Checked);
        }
        public void SaveCheckBoxesToXml(bool checkBox1, bool checkBox2)
        {
            XDocument xmlDoc = new XDocument(
                new XElement("Root",
                    new XElement("CheckBox1", checkBox1),
                    new XElement("CheckBox2", checkBox2)
                )
            );

            xmlDoc.Save(UpdaterXmlPath); // run4: write to %APPDATA%\Savedrake (read by the main app from there too)
        }
        public void LoadCheckBoxesFromXml()
        {
            // run4: prefer %APPDATA%\Savedrake; fall back to a legacy working-dir copy during the transition.
            string path = File.Exists(UpdaterXmlPath) ? UpdaterXmlPath : "savedrake-updater.xml";
            XDocument xmlDoc = XDocument.Load(path);
            XElement root = xmlDoc.Element("Root");
            bool checkBox1Value = bool.Parse(root.Element("CheckBox1").Value);
            bool checkBox2Value = bool.Parse(root.Element("CheckBox2").Value);

            // Assuming 'checkBox1' and 'checkBox2' are the CheckBox controls on your form
            checkBox1.Checked = checkBox1Value;
            checkBox2.Checked = checkBox2Value;
        }

        private void checkBox2_CheckedChanged(object sender, EventArgs e)
        {
            SaveCheckBoxesToXml(checkBox1.Checked, checkBox2.Checked);
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start($"https://github.com/{Owner}/{Repo}/releases/tag/{latestVersion}/");
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // Release the mutex when the form is closed
            if (mutex != null)
            {
                mutex.ReleaseMutex();
            }
        }
    }

}