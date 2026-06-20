using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Savedrake.App
{
    // WPF application entry point. Enforces a single running instance via a named mutex, initializes the
    // Savedrake.Core rolling logger, and routes any unhandled exception (UI thread or background) into the log
    // so a crash is recorded rather than lost. `Log` here is Savedrake.Log from Savedrake.Core.
    public partial class App : Application
    {
        // Unique per-user name so a second launch detects the first and exits. The leading "Local\" keeps it
        // per-session, which is what we want for a desktop app.
        private const string MutexName = "Local\\Savedrake.App.SingleInstance";

        private Mutex _instanceMutex;
        private bool _ownsMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Single-instance guard. If we cannot create-and-own the mutex, another copy is already running.
            bool createdNew;
            _instanceMutex = new Mutex(true, MutexName, out createdNew);
            _ownsMutex = createdNew;
            if (!createdNew)
            {
                // Already running; bail out quietly before any window appears.
                Shutdown();
                return;
            }

            // Start logging and capture crashes from both the dispatcher (UI) and any other thread.
            Savedrake.Log.Init();
            Savedrake.Log.Info("Savedrake.App starting.");

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try { Savedrake.Log.Error("Unhandled UI exception.", e.Exception); }
            catch { /* logging must never throw into the handler */ }
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try { Savedrake.Log.Error("Unhandled non-UI exception.", e.ExceptionObject as Exception); }
            catch { /* logging must never throw into the handler */ }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (_instanceMutex != null)
                {
                    if (_ownsMutex)
                    {
                        try { _instanceMutex.ReleaseMutex(); } catch { }
                    }
                    _instanceMutex.Dispose();
                    _instanceMutex = null;
                }
            }
            catch { }
            base.OnExit(e);
        }
    }
}
