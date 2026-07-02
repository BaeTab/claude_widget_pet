using System.Threading;
using System.Windows;

namespace Claude_Widget
{
    public partial class App : Application
    {
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Headless --hook/--notify modes are handled in Program.Main (no WPF).
            // Here we only run the UI, enforcing a single running widget.
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
