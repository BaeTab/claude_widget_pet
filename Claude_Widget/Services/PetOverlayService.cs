using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Claude_Widget.Services
{
    /// <summary>
    /// Phase 1 IPC bridge to the Godot "face" overlay process (see
    /// docs/3D_OVERLAY_DESIGN.md §4). Owns the engine process lifecycle and the
    /// transport, exposes an async <see cref="SendAsync"/> for WPF→engine commands,
    /// and raises <see cref="Connected"/> / <see cref="CharacterClicked"/> for
    /// engine→WPF events.
    ///
    /// TRANSPORT — localhost TCP (not stdin/stdout as the original §4 draft said):
    /// Godot 4.7 has no non-blocking stdin read for a *windowed* process on Windows
    /// (OS.read_string_from_stdin() blocks the frame loop), so a line-delimited
    /// stdio protocol is unreliable for a GUI overlay. Instead the WPF host is a
    /// <see cref="TcpListener"/> on 127.0.0.1 with an OS-assigned ephemeral port;
    /// the port is handed to Godot as the user cmdline arg <c>--ipc-port=&lt;port&gt;</c>.
    /// Godot connects back as a client. Loopback only → no firewall/AV prompt in
    /// practice, and the ordering is race-free (host listens before it spawns).
    ///
    /// Protocol: one JSON object per line ("\n"-terminated), UTF-8, both directions.
    ///
    /// Crash-safety: every engine interaction is guarded; a dead or missing engine
    /// is logged and never throws into the caller (the WPF 2D character is the
    /// fallback, per §4). Instances are single-use: <see cref="Start"/> once, then
    /// <see cref="Dispose"/>.
    /// </summary>
    public sealed class PetOverlayService : IDisposable
    {
        public sealed class Options
        {
            /// <summary>Path to the Godot executable. Use the *_console.exe variant when you
            /// need to capture the engine's stdout/stderr (self-test); the plain exe otherwise.</summary>
            public string GodotExePath = "";

            /// <summary>Dev mode: the Godot project directory (folder containing project.godot),
            /// launched via <c>--path</c>. Ignored when <see cref="ExportedExePath"/> is set.</summary>
            public string ProjectPath = "";

            /// <summary>Future: a packaged Godot export (e.g. ClaudeWidgetPet.exe). When set it is
            /// launched directly (no <c>--path</c>); the project is embedded in the .pck beside it.</summary>
            public string? ExportedExePath;

            /// <summary>Capture the engine's own stdout/stderr into <see cref="Log"/> (needs the
            /// *_console.exe). Off in production so the GUI overlay runs windowless-quiet.</summary>
            public bool RedirectEngineOutput;

            /// <summary>Diagnostics sink. Never null-checked at call sites — supply one to observe.</summary>
            public Action<string>? Log;
        }

        private readonly Options _opt;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentQueue<string> _pending = new();
        private readonly CancellationTokenSource _cts = new();

        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private Process? _proc;

        private volatile bool _connected;
        private volatile bool _disposed;
        private int _port;

        /// <summary>Raised (off the UI thread) when the engine sends its <c>hello</c> handshake.</summary>
        public event Action? Connected;

        /// <summary>Raised (off the UI thread) when the pet is clicked in the overlay.</summary>
        public event Action? CharacterClicked;

        /// <summary>Raised (off the UI thread) if the engine process exits.</summary>
        public event Action<string>? EngineExited;

        public bool IsConnected => _connected;
        public int Port => _port;

        public PetOverlayService(Options opt)
        {
            _opt = opt ?? throw new ArgumentNullException(nameof(opt));
        }

        private void Log(string msg)
        {
            try { _opt.Log?.Invoke(msg); } catch { /* a logging fault must not break IPC */ }
        }

        /// <summary>
        /// Binds the loopback listener, spawns the engine pointed at the chosen port, and
        /// starts accepting the engine's connection in the background. Non-blocking; safe to
        /// call from the UI thread. Any failure is logged and swallowed (engine stays absent).
        /// </summary>
        public void Start()
        {
            if (_disposed) return;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                Log($"listening on 127.0.0.1:{_port}");

                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

                LaunchEngine();
            }
            catch (Exception ex)
            {
                Log($"start failed: {ex.Message}");
                SafeStopListener();
            }
        }

        private void LaunchEngine()
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                RedirectStandardOutput = _opt.RedirectEngineOutput,
                RedirectStandardError = _opt.RedirectEngineOutput,
            };

            bool exported = !string.IsNullOrWhiteSpace(_opt.ExportedExePath) && File.Exists(_opt.ExportedExePath);
            if (exported)
            {
                // Packaged build: <ClaudeWidgetPet.exe> -- --ipc-port=<port>
                psi.FileName = _opt.ExportedExePath!;
                psi.WorkingDirectory = Path.GetDirectoryName(_opt.ExportedExePath!) ?? "";
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add($"--ipc-port={_port}");
            }
            else
            {
                // Dev build: <godot.exe> --path <project> -- --ipc-port=<port>
                psi.FileName = _opt.GodotExePath;
                psi.WorkingDirectory = _opt.ProjectPath;
                psi.ArgumentList.Add("--path");
                psi.ArgumentList.Add(_opt.ProjectPath);
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add($"--ipc-port={_port}");
            }

            // Global rule #8: keep Godot's scratch off the AV-hooked default temp dir.
            psi.Environment["TMP"] = @"C:\temp\gradle-tmp";
            psi.Environment["TEMP"] = @"C:\temp\gradle-tmp";

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.Exited += (_, _) =>
            {
                _connected = false;
                int code = -1;
                try { code = _proc?.ExitCode ?? -1; } catch { }
                Log($"engine process exited (code {code})");
                try { EngineExited?.Invoke($"exit code {code}"); } catch { }
            };

            if (_opt.RedirectEngineOutput)
            {
                _proc.OutputDataReceived += (_, e) => { if (e.Data != null) Log($"[godot] {e.Data}"); };
                _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Log($"[godot!] {e.Data}"); };
            }

            _proc.Start();
            Log($"engine launched pid={_proc.Id} exe={Path.GetFileName(psi.FileName)}");

            if (_opt.RedirectEngineOutput)
            {
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _client = client;
                _stream = client.GetStream();
                Log("engine connected (tcp accepted)");

                // Anything queued before the socket existed goes out now.
                await DrainPendingAsync().ConfigureAwait(false);

                await ReadLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* disposing */ }
            catch (Exception ex)
            {
                Log($"accept/read loop ended: {ex.Message}");
            }
            finally
            {
                _connected = false;
            }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var buf = new byte[8192];
            var line = new MemoryStream();
            while (!ct.IsCancellationRequested && _stream != null)
            {
                int n;
                try
                {
                    n = await _stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"read error: {ex.Message}");
                    break;
                }
                if (n <= 0)
                {
                    Log("engine closed the connection");
                    break;
                }

                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == (byte)'\n')
                    {
                        HandleLine(Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length));
                        line.SetLength(0);
                    }
                    else if (b != (byte)'\r')
                    {
                        line.WriteByte(b);
                    }
                }
            }
            _connected = false;
        }

        private void HandleLine(string json)
        {
            json = json.Trim();
            if (json.Length == 0) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out var tEl) || tEl.ValueKind != JsonValueKind.String)
                {
                    Log($"engine line without type ignored: {json}");
                    return;
                }
                string type = tEl.GetString() ?? "";
                switch (type)
                {
                    case "hello":
                        _connected = true;
                        string ver = root.TryGetProperty("ver", out var vEl) ? vEl.ToString() : "?";
                        string pid = root.TryGetProperty("pid", out var pEl) ? pEl.ToString() : "?";
                        Log($"<= hello (engine pid={pid} ver={ver})");
                        try { Connected?.Invoke(); } catch { }
                        break;

                    case "click":
                        string target = root.TryGetProperty("target", out var gEl) ? gEl.GetString() ?? "" : "";
                        Log($"<= click target={target}");
                        if (target == "character")
                        {
                            try { CharacterClicked?.Invoke(); } catch { }
                        }
                        break;

                    case "error":
                        string em = root.TryGetProperty("msg", out var mEl) ? mEl.GetString() ?? "" : "";
                        Log($"<= engine error: {em}");
                        break;

                    case "bye":
                        Log("<= bye");
                        break;

                    default:
                        Log($"<= {json}");
                        break;
                }
            }
            catch (JsonException)
            {
                Log($"malformed engine line ignored: {json}");
            }
        }

        /// <summary>
        /// Queues a command object for the engine (serialized as one JSON line) and flushes it
        /// if the socket is up. Commands sent before the engine connects are buffered and
        /// delivered on connect. Never throws — transport faults are logged.
        /// </summary>
        public Task SendAsync(object command)
        {
            if (_disposed) return Task.CompletedTask;
            string line;
            try
            {
                line = JsonSerializer.Serialize(command) + "\n";
            }
            catch (Exception ex)
            {
                Log($"serialize failed: {ex.Message}");
                return Task.CompletedTask;
            }
            _pending.Enqueue(line);
            return DrainPendingAsync();
        }

        private async Task DrainPendingAsync()
        {
            var stream = _stream;
            if (stream == null) return;   // still buffered; AcceptLoop drains on connect

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                while (_pending.TryDequeue(out var line))
                {
                    var bytes = Encoding.UTF8.GetBytes(line);
                    try
                    {
                        await stream.WriteAsync(bytes.AsMemory(), _cts.Token).ConfigureAwait(false);
                        Log($"=> {line.TrimEnd()}");
                    }
                    catch (Exception ex)
                    {
                        Log($"send failed ({ex.Message}); dropping: {line.TrimEnd()}");
                        break;   // socket is likely dead; stop draining
                    }
                }
                try { await stream.FlushAsync(_cts.Token).ConfigureAwait(false); } catch { }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Graceful stop: send <c>shutdown</c>, wait up to <paramref name="graceMs"/> for the
        /// engine to quit, then force-kill if it lingers. Always safe to call.
        /// </summary>
        public async Task ShutdownAsync(int graceMs = 1500)
        {
            try { await SendAsync(new { type = "shutdown" }).ConfigureAwait(false); } catch { }

            var proc = _proc;
            if (proc == null) return;
            try
            {
                using var to = new CancellationTokenSource(graceMs);
                await proc.WaitForExitAsync(to.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
            }
            catch { }
        }

        private void SafeStopListener()
        {
            try { _listener?.Stop(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connected = false;

            try { _cts.Cancel(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }
            SafeStopListener();

            try
            {
                if (_proc != null && !_proc.HasExited)
                    _proc.Kill(true);
            }
            catch { }
            try { _proc?.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
            try { _writeLock.Dispose(); } catch { }
        }
    }
}
