using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Savedrake
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Logging + global crash handling (P2). A backup tool guarding irreplaceable saves must not crash
            // silently, so set up the rolling log and the three WinForms exception sinks before anything else.
            Log.Init();
            Log.Info("Savedrake " + Assembly.GetExecutingAssembly().GetName().Version + " starting on " + Environment.OSVersion);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Log.Error("Unhandled UI-thread exception", e.Exception);
                MessageBox.Show("An unexpected error occurred. Details were written to the log:\n" + Log.Directory(),
                    "Savedrake", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log.Error("Unhandled non-UI exception (terminating=" + e.IsTerminating + ")", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error("Unobserved task exception", e.Exception);
                e.SetObserved(); // updater + auto-backup use Task.Run; don't let a stray task fault escalate
            };

            // List of required DLLs
            string[] requiredDlls = {
                "DotNetZip.dll",
                "Microsoft.WindowsAPICodePack.dll",
                "Microsoft.WindowsAPICodePack.Shell.dll",
                "Newtonsoft.Json.dll",
                "System.CodeDom.dll"
            };

            foreach (var dll in requiredDlls)
            {
                if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dll)))
                {
                    MessageBox.Show($"The required DLL '{dll}' is missing.", "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.Exit(1);
                }
            }

            // Add the event handler for resolving missing assemblies
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);

            Application.Run(new Main());
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            string[] requiredDlls = {
                "DotNetZip.dll",
                "Microsoft.WindowsAPICodePack.dll",
                "Microsoft.WindowsAPICodePack.Shell.dll",
                "Newtonsoft.Json.dll",
                "System.CodeDom.dll"
            };
            // Check if the assembly name matches any of the required DLLs
            foreach (var dll in requiredDlls)
            {
                if (args.Name.Contains(dll))
                {
                    MessageBox.Show($"The required DLL '{dll}' could not be loaded.", "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.Exit(1);
                }
            }
            return null;
        }
    }

    // The Log class moved to Savedrake.Core (WPF migration, Phase 1). It stays in the `Savedrake` namespace there, so
    // every `Log.X` call site in the app resolves unchanged through the Savedrake.Core reference.
}