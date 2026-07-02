using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Claude_Widget.Services
{
    /// <summary>
    /// Watches the CLI "inbox" folder and raises <see cref="EventReceived"/> for each
    /// event file dropped by <c>ClaudeWidget.exe --hook</c>/<c>--notify</c>. Files are
    /// consumed (deleted) once read. Events fire on a threadpool thread; the UI subscriber
    /// is responsible for marshaling to the dispatcher.
    /// </summary>
    public sealed class CliMessageService : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly string _dir;

        /// <summary>Raised with the parsed event (not on the UI thread).</summary>
        public event Action<CliEvent>? EventReceived;

        public CliMessageService()
        {
            _dir = CliHookService.InboxDirectory;
            _watcher = new FileSystemWatcher(_dir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
        }

        /// <summary>
        /// Consumes events that arrived while the widget was not running.
        /// Only the newest is surfaced (older ones are discarded) to avoid a burst.
        /// </summary>
        public void DrainExisting()
        {
            try
            {
                var files = Directory.GetFiles(_dir, "*.json")
                    .OrderBy(f => new FileInfo(f).CreationTimeUtc)
                    .ToList();
                if (files.Count == 0)
                    return;

                string newest = files.Last();
                CliEvent? ev = TryReadAndDelete(newest);
                foreach (var f in files.Where(f => f != newest))
                    TryDelete(f);

                if (ev != null)
                    EventReceived?.Invoke(ev);
            }
            catch { /* ignore */ }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            // The writer may still hold the handle for a moment; retry briefly.
            CliEvent? ev = null;
            for (int attempt = 0; attempt < 5 && ev == null; attempt++)
            {
                ev = TryReadAndDelete(e.FullPath);
                if (ev == null)
                    Thread.Sleep(40);
            }
            if (ev != null)
                EventReceived?.Invoke(ev);
        }

        private static CliEvent? TryReadAndDelete(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                string content = File.ReadAllText(path).Trim();
                TryDelete(path);
                if (string.IsNullOrWhiteSpace(content))
                    return null;

                // JSON event, or (defensively) a plain-text line.
                if (content.StartsWith("{"))
                {
                    var ev = JsonSerializer.Deserialize<CliEvent>(content);
                    if (ev != null && !string.IsNullOrWhiteSpace(ev.Kind))
                        return ev;
                }
                return new CliEvent { Kind = "text", Text = content };
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
