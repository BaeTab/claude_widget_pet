using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Claude_Widget.Services;

namespace Claude_Widget
{
    public partial class App : Application
    {
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            string[] args = e.Args;

            // --- Headless modes: act as our own Claude Code hook handler, then exit. ---
            if (args.Contains("--hook", StringComparer.OrdinalIgnoreCase))
            {
                CliHookService.HandleHookFromStdin();
                Shutdown(0);
                return;
            }

            int notifyIdx = Array.FindIndex(args, a => a.Equals("--notify", StringComparison.OrdinalIgnoreCase));
            if (notifyIdx >= 0)
            {
                if (notifyIdx + 1 < args.Length)
                    CliHookService.WriteMessage(args[notifyIdx + 1]);
                Shutdown(0);
                return;
            }

            // --- UI mode: enforce a single running widget. ---
            _singleInstanceMutex = new Mutex(initiallyOwned: true, "ClaudeWidget.SingleInstance", out bool isNew);
            if (!isNew)
            {
                // Already running — a second launch (installer/startup/tray) just exits quietly.
                Shutdown(0);
                return;
            }

            base.OnStartup(e);

            var window = new MainWindow();
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
