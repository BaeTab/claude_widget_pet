using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Claude_Widget.Services;

namespace Claude_Widget
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _processCheckTimer = null!;
        private DispatcherTimer _idleActionTimer = null!;
        private DispatcherTimer _greetTimer = null!;
        private DispatcherTimer _bubbleHideTimer = null!;
        private DispatcherTimer _updateCheckTimer = null!;
        private DispatcherTimer _activityHideTimer = null!;
        private DispatcherTimer _breakTimer = null!;
        private readonly Random _random = new();
        private bool _isWorking;
        private bool _wasWorking;
        private bool _breakNudged;

        // --- Enhanced state ---
        private const double BaseWidth = 220;
        private const double BaseHeight = 300;

        private WidgetSettings _settings = new();
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private CliMessageService? _cliMessages;

        private UpdateInfo? _pendingUpdate;
        private bool _bubbleIsUpdate;
        private DateTime? _workStartedAt;
        private bool _updateInProgress;

        private static readonly string[] WorkStartMessages =
        {
            "코딩 시작!", "가보자고!", "집중 모드 ON", "함께 만들어봐요", "오늘도 화이팅!",
        };
        private static readonly string[] WorkDoneMessages =
        {
            "작업 완료!", "휴— 끝났다", "잘했어요!", "수고했어요", "한 건 해냈네요",
        };
        private static readonly string[] IdleMessages =
        {
            "안녕하세요!", "물 한 잔 어때요?", "잠깐 스트레칭 어때요", "커밋은 자주자주!",
            "오늘도 좋은 하루", "무엇을 만들어볼까요?", "잠깐 쉬어도 괜찮아요",
        };

        // NOTE: As of the 2026 Claude Code update, the CLI ships a native launcher
        // installed at "<npm-prefix>/node_modules/@anthropic-ai/claude-code/bin/claude.exe"
        // that re-execs node internally. Older installs still spawn raw node.exe with the
        // cli.js script. Both shapes are handled below:
        //   - "claude" baseName: confirm via image path (must contain claude-code) so the
        //     Anthropic *desktop* chat app (also Claude.exe, but installed under
        //     %LOCALAPPDATA%\AnthropicClaude\...) is not a false positive.
        //   - "node"/"bun"/"deno" baseName: peek the command line via PEB and match hints.
        // The literal "claude-cli" entry is kept harmless for any future renaming.
        private static readonly string[] ClaudeCliProcessNames = [
            "claude-cli",
        ];

        // Substrings in a "claude.exe" image path that confirm it is the Claude Code CLI
        // rather than the Anthropic desktop chat app.
        private static readonly string[] ClaudeCliImagePathHints = [
            "claude-code\\bin",
            "claude-code/bin",
            "@anthropic-ai\\claude-code",
            "@anthropic-ai/claude-code",
        ];

        // Substrings that, when found in a process's command line, indicate Claude CLI is running.
        private static readonly string[] ClaudeCliCommandLineHints = [
            "claude-code",
            "@anthropic-ai/claude-code",
            "\\claude\\cli.js",
            "/claude/cli.js",
            "\\.claude\\",
            "/.claude/",
            "claude.js",
        ];

        // --- Win32 P/Invoke for reading a process's command line from its PEB ---
        // This is used instead of WMI's Win32_Process.CommandLine, which returns empty
        // under some Windows 11 / .NET System.Management configurations even though
        // PowerShell's Get-CimInstance succeeds in the same user session.
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_VM_READ = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle, int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(
            IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        /// <summary>
        /// Returns the full image path (.exe) for a process using only
        /// PROCESS_QUERY_LIMITED_INFORMATION, which avoids the ACCESS_DENIED
        /// problems that PROCESS_VM_READ-based PEB walks routinely hit.
        /// </summary>
        private static string? TryGetProcessImagePath(int pid)
        {
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
                return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint capacity = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
                    return null;
                return sb.ToString();
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// Reads the CommandLine of another process by walking its PEB.
        /// Returns null on failure; when `reason` is non-null, it contains a short diagnostic
        /// tag pinpointing which step failed (for logging).
        /// </summary>
        private static string? TryReadCommandLineFromPeb(int pid, out string reason)
        {
            reason = string.Empty;
            IntPtr hProcess = OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                reason = $"OpenProcess(win32err={Marshal.GetLastWin32Error()})";
                return null;
            }

            try
            {
                var pbi = new PROCESS_BASIC_INFORMATION();
                int status = NtQueryInformationProcess(
                    hProcess, 0, ref pbi,
                    Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
                if (status != 0)
                {
                    reason = $"NtQueryInfo(ntstatus=0x{status:X8})";
                    return null;
                }
                if (pbi.PebBaseAddress == IntPtr.Zero)
                {
                    reason = "PebBaseAddress=null (likely WOW64 mismatch)";
                    return null;
                }

                // PEB layout (x64): ProcessParameters pointer lives at offset 0x20.
                if (!TryReadPointer(hProcess, pbi.PebBaseAddress + 0x20, out IntPtr pp))
                {
                    reason = $"RPM(PEB+0x20 err={Marshal.GetLastWin32Error()})";
                    return null;
                }
                if (pp == IntPtr.Zero)
                {
                    reason = "ProcessParameters=null";
                    return null;
                }

                // RTL_USER_PROCESS_PARAMETERS (x64): CommandLine UNICODE_STRING at offset 0x70.
                // UNICODE_STRING { USHORT Length; USHORT MaxLength; pad; PWSTR Buffer; } = 16 bytes on x64.
                byte[] usBuf = new byte[16];
                if (!ReadProcessMemory(hProcess, pp + 0x70, usBuf, (IntPtr)usBuf.Length, out _))
                {
                    reason = $"RPM(PP+0x70 err={Marshal.GetLastWin32Error()})";
                    return null;
                }

                ushort length = BitConverter.ToUInt16(usBuf, 0);
                if (length == 0)
                    return string.Empty;
                IntPtr bufPtr = new IntPtr(BitConverter.ToInt64(usBuf, 8));
                if (bufPtr == IntPtr.Zero)
                {
                    reason = "CmdLine.Buffer=null";
                    return null;
                }

                byte[] strBuf = new byte[length];
                if (!ReadProcessMemory(hProcess, bufPtr, strBuf, (IntPtr)length, out _))
                {
                    reason = $"RPM(CmdLine.Buffer err={Marshal.GetLastWin32Error()})";
                    return null;
                }

                return Encoding.Unicode.GetString(strBuf);
            }
            catch (Exception ex)
            {
                reason = $"ex:{ex.GetType().Name}:{ex.Message}";
                return null;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        private static bool TryReadPointer(IntPtr hProcess, IntPtr addr, out IntPtr value)
        {
            byte[] buf = new byte[IntPtr.Size];
            if (!ReadProcessMemory(hProcess, addr, buf, (IntPtr)buf.Length, out _))
            {
                value = IntPtr.Zero;
                return false;
            }
            value = IntPtr.Size == 8
                ? new IntPtr(BitConverter.ToInt64(buf, 0))
                : new IntPtr(BitConverter.ToInt32(buf, 0));
            return true;
        }

        public MainWindow()
        {
            InitializeComponent();
            _settings = SettingsService.Load();
            ApplyLoadedSettings();
            Loaded += MainWindow_Loaded;
        }

        /// <summary>Applies persisted settings to the window before it is shown.</summary>
        private void ApplyLoadedSettings()
        {
            ApplyTheme(_settings.Theme);
            ApplyScale(_settings.Scale);

            Topmost = _settings.Topmost;
            MenuTopmost.IsChecked = _settings.Topmost;
            MenuSpeech.IsChecked = _settings.SpeechEnabled;
            MenuAutoUpdate.IsChecked = _settings.AutoUpdate;

            if (_settings.Left is double l && _settings.Top is double t
                && l > -2000 && t > -2000)
            {
                Left = l;
                Top = t;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
            else
            {
                PositionAtBottomRight();
            }

            CheckTheme(_settings.Theme);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Menu states that need runtime queries.
            MenuStartup.IsChecked = StartupService.IsEnabled();
            MenuCliHook.IsChecked = CliHookInstaller.IsEnabled(CliHookInstaller.BaseEvents);
            MenuToolAwareness.IsChecked = _settings.ToolAwareness;

            // Start permanent animations
            StartStoryboard("BreathingAnimation");
            StartStoryboard("SparkleAnimation");

            SetupTrayIcon();
            SetupCliMessages();

            // Process detection timer (every 2 seconds)
            _processCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _processCheckTimer.Tick += ProcessCheckTimer_Tick;
            _processCheckTimer.Start();

            // Idle action timer (random blinks, looks)
            _idleActionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _idleActionTimer.Tick += IdleActionTimer_Tick;
            _idleActionTimer.Start();

            // Occasional idle greeting
            _greetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
            _greetTimer.Tick += GreetTimer_Tick;
            _greetTimer.Start();

            // Bubble auto-hide (one-shot; restarted per message)
            _bubbleHideTimer = new DispatcherTimer();
            _bubbleHideTimer.Tick += (_, _) => HideSpeech();

            // Activity badge auto-hide (one-shot; restarted per tool event)
            _activityHideTimer = new DispatcherTimer();
            _activityHideTimer.Tick += (_, _) => HideActivity();

            // Break nudge: check once a minute for long continuous work sessions.
            _breakTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _breakTimer.Tick += (_, _) => CheckBreakNudge();
            _breakTimer.Start();

            // Periodic update check (every 6 hours) + one shortly after launch.
            _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _updateCheckTimer.Tick += async (_, _) => await CheckForUpdatesAsync(auto: true);
            _updateCheckTimer.Start();

            // Initial check
            CheckClaudeProcess();

            if (_settings.AutoUpdate)
                _ = DelayThenCheckUpdatesAsync();

            // Friendly hello on launch.
            ShowSpeech(PickRandom(IdleMessages), 3500);
        }

        private async Task DelayThenCheckUpdatesAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            await CheckForUpdatesAsync(auto: true);
        }

        // ===================== Tray icon =====================

        private void SetupTrayIcon()
        {
            try
            {
                _trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Text = "Claude Widget",
                    Visible = true,
                    Icon = LoadTrayIcon(),
                };
                _trayIcon.DoubleClick += (_, _) => ToggleVisibility();

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("보이기 / 숨기기", null, (_, _) => ToggleVisibility());
                menu.Items.Add("업데이트 확인", null, async (_, _) => await CheckForUpdatesAsync(auto: false));
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("종료", null, (_, _) => Application.Current.Shutdown());
                _trayIcon.ContextMenuStrip = menu;
            }
            catch
            {
                // Tray is a convenience; never let it break startup.
            }
        }

        private static System.Drawing.Icon LoadTrayIcon()
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            var res = Application.GetResourceStream(uri);
            if (res != null)
            {
                using var s = res.Stream;
                return new System.Drawing.Icon(s);
            }
            return System.Drawing.SystemIcons.Application;
        }

        private void ToggleVisibility()
        {
            if (IsVisible)
            {
                Hide();
            }
            else
            {
                Show();
                Activate();
            }
        }

        // ===================== CLI messages =====================

        private void SetupCliMessages()
        {
            try
            {
                _cliMessages = new CliMessageService();
                _cliMessages.EventReceived += OnCliEvent;
                _cliMessages.DrainExisting();
            }
            catch
            {
                // Optional feature; ignore setup failures.
            }
        }

        private void OnCliEvent(CliEvent ev)
        {
            // Raised off the UI thread — marshal in.
            Dispatcher.BeginInvoke(() => HandleCliEvent(ev));
        }

        private void HandleCliEvent(CliEvent ev)
        {
            switch (ev.Kind)
            {
                case "tool":
                    if (_settings.ToolAwareness)
                        ShowActivity(ev.Tool ?? "", ev.Text);
                    break;

                case "notify":
                    // Permission / attention: pop up, wave, bubble, and a tray toast so
                    // it's noticed even when the widget is hidden or on another monitor.
                    HideActivity();
                    if (!IsVisible) Show();
                    StartStoryboard("WaveAnimation");
                    ShowSpeech(ev.Text, 7000);
                    ShowTrayToast(ev.Text);
                    break;

                case "prompt":
                    HideActivity();
                    StartStoryboard("IdleBounceAnimation");
                    ShowSpeech(ev.Text, 2500);
                    break;

                case "stop":
                    HideActivity();
                    StartStoryboard("BlinkAnimation");
                    ShowSpeech(ev.Text, 3000);
                    break;

                case "substop":
                case "session":
                    ShowSpeech(ev.Text, 3000);
                    break;

                default: // "text"
                    if (!IsVisible) Show();
                    ShowSpeech(ev.Text, 6500);
                    break;
            }
        }

        // ===================== Tool activity badge =====================

        private DateTime _lastToolBubbleAt = DateTime.MinValue;

        private void ShowActivity(string tool, string label)
        {
            ActivityGlyph.Text = GlyphForTool(tool);
            ActivityBadge.ToolTip = label;
            StartStoryboard("ActivityPopIn");

            // Keep the badge visible while tools keep coming; auto-hide after a lull.
            _activityHideTimer.Stop();
            _activityHideTimer.Interval = TimeSpan.FromSeconds(6);
            _activityHideTimer.Start();

            // Throttle the textual bubble so rapid tool bursts don't flicker.
            if ((DateTime.Now - _lastToolBubbleAt).TotalSeconds >= 4)
            {
                _lastToolBubbleAt = DateTime.Now;
                ShowSpeech(label, 2200);
            }
        }

        private void HideActivity()
        {
            _activityHideTimer.Stop();
            if (ActivityBadge.Opacity > 0.01)
                StartStoryboard("ActivityFadeOut");
        }

        /// <summary>Segoe MDL2 Assets glyph for a Claude Code tool name.</summary>
        private static string GlyphForTool(string tool) => tool switch
        {
            "Read" or "NotebookRead" => "",       // Document
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "", // Edit (pencil)
            "Bash" or "BashOutput" or "KillShell" => "", // Command prompt
            "Glob" => "",                          // Folder
            "Grep" => "",                          // Search
            "WebFetch" or "WebSearch" => "",       // Globe
            "Task" => "",                          // People/agent-ish
            "TodoWrite" => "",                     // Checklist
            _ when tool.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase) => "", // Component
            _ => "",                               // Processing (gear-ish)
        };

        private void ShowTrayToast(string text)
        {
            try
            {
                _trayIcon?.ShowBalloonTip(6000, "Claude Widget", text,
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            catch { /* balloon is best-effort */ }
        }

        // ===================== Process detection =====================

        private void ProcessCheckTimer_Tick(object? sender, EventArgs e)
        {
            CheckClaudeProcess();
        }

        private static bool IsScriptRunnerName(string baseName) =>
            baseName is "node" or "bun" or "deno";

        private static readonly string DebugLogPath =
            Path.Combine(Path.GetTempPath(), "ClaudeWidget.log");

        // Verbose process-detection tracing. This runs on every 2-second timer
        // tick and writes one line per candidate process, so it grew
        // %TEMP%\ClaudeWidget.log to multiple GB when left enabled. Keep it OFF
        // for normal use; flip to true only while diagnosing detection problems.
        private const bool EnableDebugLog = false;

        // Hard cap so the log can never balloon again even when tracing is on.
        private const long MaxDebugLogBytes = 1 * 1024 * 1024; // 1 MB

        private static void DebugLog(string message)
        {
            if (!EnableDebugLog)
                return;

            try
            {
                // Bounded rotation: once the file passes the cap, start fresh.
                try
                {
                    var info = new FileInfo(DebugLogPath);
                    if (info.Exists && info.Length > MaxDebugLogBytes)
                        File.Delete(DebugLogPath);
                }
                catch { /* ignore rotation errors */ }

                File.AppendAllText(DebugLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { /* never let logging break the app */ }
        }

        private void CheckClaudeProcess()
        {
            // Demo/screenshot override: CLAUDEWIDGET_FORCE=idle|working pins the state
            // without scanning (handy for docs/tests). Unset for normal detection.
            string? force = Environment.GetEnvironmentVariable("CLAUDEWIDGET_FORCE")?.ToLowerInvariant();
            if (force == "idle" || force == "working")
            {
                _isWorking = force == "working";
                if (_isWorking != _wasWorking)
                {
                    _wasWorking = _isWorking;
                    UpdateAnimationState();
                }
                return;
            }

            bool foundClaude = false;
            int myPid = Environment.ProcessId;
            int total = 0, candidates = 0, cmdLineReads = 0;
            string? matched = null;

            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        total++;
                        if (proc.Id == myPid)
                            continue;

                        string baseName = proc.ProcessName.ToLowerInvariant();

                        // Direct name match (e.g. future "claude-cli.exe")
                        if (ClaudeCliProcessNames.Contains(baseName))
                        {
                            foundClaude = true;
                            matched = $"pid={proc.Id} name={proc.ProcessName} (name-match)";
                            break;
                        }

                        // Native "claude.exe" launcher shipped by @anthropic-ai/claude-code.
                        // Confirm via image path so the desktop chat app (also Claude.exe,
                        // installed under %LOCALAPPDATA%\AnthropicClaude\...) is not matched.
                        if (baseName == "claude")
                        {
                            string? imagePath = TryGetProcessImagePath(proc.Id);
                            if (imagePath == null)
                            {
                                DebugLog($"  claude.exe pid={proc.Id} image-read failed (win32err={Marshal.GetLastWin32Error()})");
                                continue;
                            }
                            DebugLog($"  claude.exe pid={proc.Id} image={imagePath}");
                            foreach (var hint in ClaudeCliImagePathHints)
                            {
                                if (imagePath.Contains(hint, StringComparison.OrdinalIgnoreCase))
                                {
                                    foundClaude = true;
                                    matched = $"pid={proc.Id} name={proc.ProcessName} image={imagePath}";
                                    break;
                                }
                            }
                            if (foundClaude)
                                break;
                            continue;
                        }

                        if (!IsScriptRunnerName(baseName))
                            continue;

                        // Script runner — need to peek at its command line via PEB.
                        candidates++;
                        string? cmd = TryReadCommandLineFromPeb(proc.Id, out string pebReason);
                        cmdLineReads++;
                        if (cmd == null)
                        {
                            DebugLog($"  peb-read failed pid={proc.Id} name={proc.ProcessName} reason={pebReason}");
                            continue;
                        }

                        DebugLog($"  candidate pid={proc.Id} name={proc.ProcessName} cmd={cmd}");

                        foreach (var hint in ClaudeCliCommandLineHints)
                        {
                            if (cmd.Contains(hint, StringComparison.OrdinalIgnoreCase))
                            {
                                foundClaude = true;
                                matched = $"pid={proc.Id} name={proc.ProcessName} hint={hint}";
                                break;
                            }
                        }
                        if (foundClaude)
                            break;
                    }
                    catch { /* skip inaccessible processes */ }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"ENUM ERROR: {ex.GetType().Name}: {ex.Message}");
            }

            DebugLog($"tick: total={total} candidates={candidates} pebReads={cmdLineReads} found={foundClaude} matched={matched ?? "(none)"}");

            _isWorking = foundClaude;

            if (_isWorking != _wasWorking)
            {
                _wasWorking = _isWorking;
                UpdateAnimationState();
            }
        }

        private void UpdateAnimationState()
        {
            if (_isWorking)
            {
                _workStartedAt = DateTime.Now;
                _breakNudged = false;

                // Switch to working mode
                StartStoryboard("LaptopFadeIn");
                StartStoryboard("ScreenGlowAnimation");

                // Move arms to keyboard position first, then start typing
                AnimateArmPosition(toKeyboard: true);
                StartStoryboard("TypingAnimation");

                // Body bobs while typing
                StartStoryboard("TypingBodyBob");

                // Eyes look down at the screen
                StartStoryboard("EyesLookDownAnimation");

                SetStatusDotForState();
                ShowSpeech(PickRandom(WorkStartMessages), 3000);
            }
            else
            {
                AccumulateWorkTime();
                HideActivity();

                // Switch to idle mode
                StartStoryboard("LaptopFadeOut");
                StopStoryboard("TypingAnimation");
                StopStoryboard("TypingBodyBob");
                StopStoryboard("ScreenGlowAnimation");

                // Move arms back to idle position
                AnimateArmPosition(toKeyboard: false);

                // Eyes look back to center
                StartStoryboard("EyesLookCenterAnimation");

                // Reset body position
                var resetY = new DoubleAnimation(0, TimeSpan.FromSeconds(0.3));
                CharacterTranslate.BeginAnimation(TranslateTransform.YProperty, resetY);

                SetStatusDotForState();
                ShowSpeech(PickRandom(WorkDoneMessages), 3000);
            }
        }

        /// <summary>Status dot colour: pending update (blue) wins over working (green)/idle (gray).</summary>
        private void SetStatusDotForState()
        {
            if (_pendingUpdate != null)
                return; // keep the blue update indicator

            string color = _isWorking ? "#44BB44" : "#888888";
            var anim = new ColorAnimation(
                (Color)ColorConverter.ConvertFromString(color), TimeSpan.FromSeconds(0.3));
            StatusDotFill.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            StatusDot.ToolTip = _isWorking ? "작업 중..." : "대기 중";
        }

        private void AccumulateWorkTime()
        {
            if (_workStartedAt is not DateTime start)
                return;

            double seconds = (DateTime.Now - start).TotalSeconds;
            _workStartedAt = null;
            if (seconds < 1)
                return;

            EnsureStatsDate();
            _settings.WorkSecondsToday += seconds;
            _settings.WorkSecondsTotal += seconds;
            _settings.SessionsTotal += 1;
            SettingsService.Save(_settings);
        }

        private void EnsureStatsDate()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_settings.StatsDate != today)
            {
                _settings.StatsDate = today;
                _settings.WorkSecondsToday = 0;
            }
        }

        /// <summary>Gentle pomodoro-style nudge after a long continuous work stretch.</summary>
        private const int BreakNudgeMinutes = 50;

        private void CheckBreakNudge()
        {
            if (!_isWorking || _breakNudged || _workStartedAt is not DateTime start)
                return;
            if ((DateTime.Now - start).TotalMinutes >= BreakNudgeMinutes)
            {
                _breakNudged = true;
                ShowSpeech($"{BreakNudgeMinutes}분째 열일 중! 잠깐 쉬어요", 5000);
            }
        }

        private void AnimateArmPosition(bool toKeyboard)
        {
            double targetX = toKeyboard ? 8 : 0;
            double targetY = toKeyboard ? 18 : 0;
            var duration = TimeSpan.FromSeconds(0.3);

            var leftXAnim = new DoubleAnimation(targetX, duration);
            var leftYAnim = new DoubleAnimation(targetY, duration);
            var rightXAnim = new DoubleAnimation(-targetX, duration);
            var rightYAnim = new DoubleAnimation(targetY, duration);

            LeftArmTypingTranslate.BeginAnimation(TranslateTransform.XProperty, leftXAnim);
            LeftArmTypingTranslate.BeginAnimation(TranslateTransform.YProperty, leftYAnim);
            RightArmTypingTranslate.BeginAnimation(TranslateTransform.XProperty, rightXAnim);
            RightArmTypingTranslate.BeginAnimation(TranslateTransform.YProperty, rightYAnim);
        }

        private void IdleActionTimer_Tick(object? sender, EventArgs e)
        {
            // Randomize next interval
            _idleActionTimer.Interval = TimeSpan.FromSeconds(2 + _random.NextDouble() * 4);

            // Pick a random idle action
            int action = _random.Next(10);

            if (action < 4)
            {
                // Blink (most common)
                StartStoryboard("BlinkAnimation");
            }
            else if (action < 6)
            {
                // Look left
                StartStoryboard("LookLeftAnimation");
            }
            else if (action < 8)
            {
                // Look right
                StartStoryboard("LookRightAnimation");
            }
            else if (!_isWorking)
            {
                // Bounce (only when idle)
                StartStoryboard("IdleBounceAnimation");
            }
        }

        private void GreetTimer_Tick(object? sender, EventArgs e)
        {
            _greetTimer.Interval = TimeSpan.FromSeconds(90 + _random.Next(90));
            if (!_isWorking && _pendingUpdate == null && SpeechBubble.Opacity < 0.05)
                ShowSpeech(PickRandom(IdleMessages), 3200);
        }

        // ===================== Speech bubble =====================

        private string PickRandom(string[] pool) => pool[_random.Next(pool.Length)];

        private void ShowSpeech(string text, int durationMs, bool isUpdate = false)
        {
            if (!_settings.SpeechEnabled && !isUpdate)
                return;

            _bubbleIsUpdate = isUpdate;
            SpeechText.Text = text;

            // Measure so the tail hugs the (variable-height) bubble bottom.
            SpeechBubble.UpdateLayout();
            double bottom = Canvas.GetTop(BubbleBorder) + BubbleBorder.ActualHeight;
            Canvas.SetTop(BubbleTail, bottom - 7);

            StartStoryboard("BubblePopIn");

            _bubbleHideTimer.Stop();
            _bubbleHideTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
            _bubbleHideTimer.Start();
        }

        private void HideSpeech()
        {
            _bubbleHideTimer.Stop();
            StartStoryboard("BubbleFadeOut");
        }

        private void SpeechBubble_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_bubbleIsUpdate && _pendingUpdate != null)
                _ = StartUpdateAsync();
            else
                HideSpeech();
        }

        private void StatusDot_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_pendingUpdate != null)
                _ = StartUpdateAsync();
            else
                ShowStatsBubble();
        }

        // ===================== Stats =====================

        private void ShowStatsBubble()
        {
            EnsureStatsDate();
            double todayMin = Math.Round(_settings.WorkSecondsToday / 60.0);
            double totalHr = Math.Round(_settings.WorkSecondsTotal / 3600.0, 1);
            ShowSpeech($"오늘 {todayMin:0}분 · 누적 {totalHr:0.#}시간 함께했어요", 4500);
        }

        // ===================== Themes =====================

        private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

        private void ApplyTheme(string name)
        {
            (string body1, string body2, string bodyStroke, string limb, string limbStroke) = name switch
            {
                "Ocean" => ("#6FB7EE", "#2E77C8", "#245FA6", "#3E86D0", "#285F9E"),
                "Forest" => ("#8FD08F", "#3F9E57", "#327A45", "#4FA061", "#2F6B41"),
                "Grape" => ("#C6A0EA", "#8A56C9", "#6E43A6", "#9A63CE", "#6B489E"),
                "Midnight" => ("#5A6488", "#2E3450", "#1C2138", "#464F6E", "#242A42"),
                _ => ("#E8906F", "#C9603F", "#B15038", "#CE6A4C", "#A9502F"), // Claude
            };

            if (Resources["BodyBrush"] is RadialGradientBrush body && body.GradientStops.Count >= 2)
            {
                body.GradientStops[0].Color = Hex(body1);
                body.GradientStops[1].Color = Hex(body2);
            }
            if (Resources["BodyStrokeBrush"] is SolidColorBrush bs) bs.Color = Hex(bodyStroke);
            if (Resources["LimbBrush"] is SolidColorBrush lb) lb.Color = Hex(limb);
            if (Resources["LimbStrokeBrush"] is SolidColorBrush ls) ls.Color = Hex(limbStroke);
        }

        private void CheckTheme(string name)
        {
            if (WidgetContextMenu == null)
                return;
            foreach (var item in WidgetContextMenu.Items.OfType<MenuItem>())
            {
                if (item.Header?.ToString() != "테마")
                    continue;
                foreach (var sub in item.Items.OfType<MenuItem>())
                    sub.IsChecked = (sub.Tag?.ToString() == name);
            }
        }

        // ===================== Updates =====================

        private async Task CheckForUpdatesAsync(bool auto)
        {
            if (_updateInProgress)
                return;

            UpdateInfo? info = await UpdateService.CheckForUpdateAsync();
            if (info != null)
            {
                _pendingUpdate = info;
                ShowUpdateIndicator();
                if (!auto || _settings.SkippedUpdateVersion != info.TagName)
                    ShowSpeech($"새 버전 {info.TagName} 나왔어요! 클릭해서 업데이트", 8000, isUpdate: true);
            }
            else if (!auto)
            {
                ShowSpeech($"최신 버전이에요! (v{AppInfo.CurrentVersionString})", 3500);
            }
        }

        private void ShowUpdateIndicator()
        {
            var anim = new ColorAnimation(Hex("#2E6BFF"), TimeSpan.FromSeconds(0.3));
            StatusDotFill.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            StatusDot.ToolTip = "업데이트 가능 — 클릭해서 설치";
            StartStoryboard("UpdateBadgePulse");
        }

        private async Task StartUpdateAsync()
        {
            if (_pendingUpdate == null || _updateInProgress)
                return;

            _updateInProgress = true;
            var info = _pendingUpdate;

            var progress = new Progress<double>(p =>
                ShowSpeech($"업데이트 다운로드 중… {p * 100:0}%", 60000));
            ShowSpeech("업데이트 다운로드 중…", 60000);

            string? path = await UpdateService.DownloadInstallerAsync(info, progress);
            if (path == null)
            {
                _updateInProgress = false;
                ShowSpeech("다운로드에 실패했어요. 나중에 다시 시도할게요", 4000);
                return;
            }

            ShowSpeech("설치를 시작할게요! 잠시만요", 4000);
            if (UpdateService.LaunchInstaller(path))
            {
                // Hand off to the installer; it relaunches the widget when done.
                Application.Current.Shutdown();
            }
            else
            {
                _updateInProgress = false;
                ShowSpeech("설치 프로그램을 실행하지 못했어요", 4000);
                Process.Start(new ProcessStartInfo(AppInfo.ReleasesUrl) { UseShellExecute = true });
            }
        }

        // ===================== Storyboards =====================

        private void StartStoryboard(string name)
        {
            if (Resources[name] is Storyboard sb)
            {
                sb.Begin(this, true);
            }
        }

        private void StopStoryboard(string name)
        {
            if (Resources[name] is Storyboard sb)
            {
                sb.Stop(this);
            }
        }

        private void PositionAtBottomRight()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 20;
            Top = workArea.Bottom - Height - 10;
        }

        private void ApplyScale(double scale)
        {
            WidgetScale.ScaleX = scale;
            WidgetScale.ScaleY = scale;
            Width = BaseWidth * scale;
            Height = BaseHeight * scale;
        }

        // ===================== Input =====================

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // Double-click: bounce!
                StartStoryboard("IdleBounceAnimation");
                StartStoryboard("BlinkAnimation");
            }
            else
            {
                DragMove();
                SavePosition();
            }
        }

        private void SavePosition()
        {
            _settings.Left = Left;
            _settings.Top = Top;
            SettingsService.Save(_settings);
        }

        // ===================== Menu handlers =====================

        private void ToggleTopmost_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                Topmost = menuItem.IsChecked;
                _settings.Topmost = menuItem.IsChecked;
                SettingsService.Save(_settings);
            }
        }

        private void ToggleSpeech_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                _settings.SpeechEnabled = menuItem.IsChecked;
                SettingsService.Save(_settings);
                if (_settings.SpeechEnabled)
                    ShowSpeech("말풍선을 켰어요", 2500);
            }
        }

        private void ToggleAutoUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                _settings.AutoUpdate = menuItem.IsChecked;
                SettingsService.Save(_settings);
            }
        }

        private void ToggleStartup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem)
                return;

            bool ok = StartupService.SetEnabled(menuItem.IsChecked);
            if (!ok)
            {
                menuItem.IsChecked = StartupService.IsEnabled();
                ShowSpeech("시작 설정을 바꾸지 못했어요 😢", 3000);
                return;
            }
            _settings.StartWithWindows = menuItem.IsChecked;
            SettingsService.Save(_settings);
            ShowSpeech(menuItem.IsChecked ? "이제 Windows와 함께 켜져요" : "자동 시작을 껐어요", 2800);
        }

        private void ToggleCliHook_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem)
                return;

            // Register/unregister every event (base lifecycle + PreToolUse for tool awareness).
            bool ok = menuItem.IsChecked
                ? CliHookInstaller.Enable(CliHookInstaller.AllEvents)
                : CliHookInstaller.Disable(CliHookInstaller.AllEvents);
            if (!ok)
            {
                menuItem.IsChecked = CliHookInstaller.IsEnabled(CliHookInstaller.BaseEvents);
                ShowSpeech("CLI 설정을 바꾸지 못했어요", 3200);
                return;
            }

            if (menuItem.IsChecked)
                ShowSpeech("Claude CLI 알림을 연동했어요!\n새 세션부터 적용돼요", 5000);
            else
                ShowSpeech("CLI 알림 연동을 껐어요", 3000);
        }

        private void ToggleToolAwareness_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem)
                return;

            _settings.ToolAwareness = menuItem.IsChecked;
            SettingsService.Save(_settings);
            if (!_settings.ToolAwareness)
                HideActivity();
            ShowSpeech(_settings.ToolAwareness ? "작업 상세 표시를 켰어요" : "작업 상세 표시를 껐어요", 2500);
        }

        private void ChangeTheme_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is string theme)
            {
                _settings.Theme = theme;
                ApplyTheme(theme);
                CheckTheme(theme);
                SettingsService.Save(_settings);
                ShowSpeech("테마를 바꿨어요", 2500);
            }
        }

        private void ChangeSize_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && double.TryParse(menuItem.Tag?.ToString(), out double scale))
            {
                ApplyScale(scale);
                _settings.Scale = scale;
                SettingsService.Save(_settings);
            }
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            ShowSpeech("업데이트를 확인할게요…", 2500);
            await CheckForUpdatesAsync(auto: false);
        }

        private void ShowStats_Click(object sender, RoutedEventArgs e) => ShowStatsBubble();

        private void ShowAbout_Click(object sender, RoutedEventArgs e)
        {
            ShowSpeech($"Claude Widget v{AppInfo.CurrentVersionString}", 3500);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            AccumulateWorkTime();
            SavePosition();

            _processCheckTimer?.Stop();
            _idleActionTimer?.Stop();
            _greetTimer?.Stop();
            _updateCheckTimer?.Stop();
            _bubbleHideTimer?.Stop();
            _activityHideTimer?.Stop();
            _breakTimer?.Stop();

            _cliMessages?.Dispose();

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            base.OnClosed(e);
        }
    }
}
