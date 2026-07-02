using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Claude_Widget.Services
{
    /// <summary>
    /// Watches the CLI "inbox" folder and raises <see cref="MessageReceived"/> for each
    /// message file dropped by <c>ClaudeWidget.exe --hook</c>/<c>--notify</c>. Files are
    /// consumed (deleted) once read. Events fire on a threadpool thread; the UI subscriber
    /// is responsible for marshaling to the dispatcher.
    /// </summary>
    public sealed class CliMessageService : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly string _dir;

        /// <summary>Raised with the message text (not on the UI thread).</summary>
        public event Action<string>? MessageReceived;

        public CliMessageService()
        {
            _dir = CliHookService.InboxDirectory;
            _watcher = new FileSystemWatcher(_dir, "*.txt")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
        }

        /// <summary>
        /// Consumes any messages that arrived while the widget was not running.
        /// Only the newest is surfaced (older ones are discarded) to avoid a burst of bubbles.
        /// </summary>
        public void DrainExisting()
        {
            try
            {
                var files = Directory.GetFiles(_dir, "*.txt")
                    .OrderBy(f => new FileInfo(f).CreationTimeUtc)
                    .ToList();
                if (files.Count == 0)
                    return;

                string? newest = files.Last();
                string? text = TryReadAndDelete(newest);
                // Delete the stale remainder without surfacing them.
                foreach (var f in files.Where(f => f != newest))
                    TryDelete(f);

                if (!string.IsNullOrWhiteSpace(text))
                    MessageReceived?.Invoke(text!);
            }
            catch { /* ignore */ }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            // The writer may still hold the handle for a moment; retry briefly.
            string? text = null;
            for (int attempt = 0; attempt < 5 && text == null; attempt++)
            {
                text = TryReadAndDelete(e.FullPath);
                if (text == null)
                    Thread.Sleep(40);
            }
            if (!string.IsNullOrWhiteSpace(text))
                MessageReceived?.Invoke(text!);
        }

        private static string? TryReadAndDelete(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                string content = File.ReadAllText(path).Trim();
                TryDelete(path);
                return content;
            }
            catch
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }

        public void Dispose()
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnChanged;
                _watcher.Changed -= OnChanged;
                _watcher.Dispose();
            }
            catch { /* ignore */ }
        }
    }
}
