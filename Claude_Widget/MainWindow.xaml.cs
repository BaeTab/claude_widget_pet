using System;
using System.Collections.Generic;
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
        private DispatcherTimer _pruneTimer = null!;
        private readonly Random _random = new();
        private bool _isWorking;
        private bool _breakNudged;

        // Multi-session tracking: the registry aggregates every concurrent Claude Code
        // session; the character reflects the fleet's most-urgent state and the chip
        // strip lists each session. _effectiveState is the last state the UI rendered.
        private readonly SessionRegistry _registry = new();
        private SessionState _effectiveState = SessionState.Idle;

        // Expression/idle state: the character dozes off after a long quiet stretch.
        private bool _sleeping;
        private DateTime? _idleSince;
        private const int SleepAfterMinutes = 3;

        // Session dashboard (HUD): per-session cost from the transcript + aggregate system usage.
        private DispatcherTimer _costTimer = null!;
        private DispatcherTimer _hudTimer = null!;
        private bool _hudOpen;
        private bool _demoMode;
        private readonly List<int> _claudePids = new();
        private readonly TranscriptUsageReader _usageReader = new();
        private readonly SystemUsageSampler _sysSampler = new();

        // --- Enhanced state ---
        // Window is wider than the 220-wide character so the session strip has room;
        // the character canvas is centered within it.
        private const double BaseWidth = 300;
        private const double BaseHeight = 332;

        // Chips shown before collapsing the remainder into a "+N" pill.
        private const int MaxVisibleChips = 3;

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

        // Process detection (native claude.exe launcher + node/bun/deno cli.js via PEB) lives
        // in a dedicated service, shared with the dashboard's resource sampler.
        private readonly ClaudeProcessScanner _scanner = new();

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

            // Registry drives the character + chip strip; marshal its off-thread
            // Changed notifications onto the dispatcher.
            _registry.Changed += () => Dispatcher.BeginInvoke(ApplyEffectiveState);

            SetupTrayIcon();
            SetupCliMessages();
            SeedDemoSessionsIfRequested();

            // Process detection timer (every 2 seconds)
            _processCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _processCheckTimer.Tick += ProcessCheckTimer_Tick;
            _processCheckTimer.Start();

            // Prune ended/stale sessions periodically.
            _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _pruneTimer.Tick += (_, _) => _registry.Prune();
            _pruneTimer.Start();

            // Cost sampling: refresh each session's transcript-derived usage (cheap incremental
            // reads) so the chip detail and dashboard show up-to-date estimates. Always on.
            _costTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _costTimer.Tick += (_, _) => SampleCosts();
            _costTimer.Start();

            // Dashboard refresh: only runs while the HUD is open (resource sampling + rerender).
            _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _hudTimer.Tick += (_, _) => RefreshHud();

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

            // In demo/screenshot mode, optionally open the dashboard once layout settles
            // (CLAUDEWIDGET_DEMO=hud). Plain CLAUDEWIDGET_DEMO=1 only seeds the sessions so the
            // character/strip/expression can be captured on their own.
            if (_demoMode &&
                string.Equals(Environment.GetEnvironmentVariable("CLAUDEWIDGET_DEMO"), "hud",
                    StringComparison.OrdinalIgnoreCase))
                Dispatcher.BeginInvoke(new Action(OpenHud), DispatcherPriority.Background);
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
            // Feed the registry first: it updates the per-session state and (via Changed)
            // re-derives the character's aggregate state and the chip strip. Returns false
            // for global events (e.g. manual --notify) that carry no session id.
            _registry.Apply(ev);

            // Immediate, per-event feedback that shouldn't wait on aggregation.
            switch (ev.Kind)
            {
                case "tool":
                    if (_settings.ToolAwareness)
                        ShowActivity(ev.Tool ?? "", TagWithProject(ev, ev.Text));
                    break;

                case "notify":
                    // Permission / attention: pop up, wave, bubble, and a tray toast so
                    // it's noticed even when the widget is hidden or on another monitor.
                    HideActivity();
                    if (!IsVisible) Show();
                    StartStoryboard("WaveAnimation");
                    ShowSpeech(TagWithProject(ev, ev.Text), 7000);
                    ShowTrayToast(ev.Text);
                    break;

                case "prompt":
                    HideActivity();
                    StartStoryboard("IdleBounceAnimation");
                    ShowSpeech(TagWithProject(ev, ev.Text), 2500);
                    break;

                case "stop":
                    HideActivity();
                    Celebrate();
                    ShowSpeech(TagWithProject(ev, ev.Text), 3000);
                    break;

                case "substop":
                case "session_start":
                case "session_end":
                    ShowSpeech(TagWithProject(ev, ev.Text), 3000);
                    break;

                default: // "text" (global, e.g. manual --notify)
                    if (!IsVisible) Show();
                    ShowSpeech(ev.Text, 6500);
                    break;
            }
        }

        /// <summary>
        /// Prefixes a message with the project name when more than one session is active,
        /// so a bubble tells you *which* session it's about (e.g. "imjangpro · 코드 쓰는 중…").
        /// </summary>
        private string TagWithProject(CliEvent ev, string text)
        {
            if (string.IsNullOrWhiteSpace(ev.SessionId) || _registry.Snapshot().ActiveCount <= 1)
                return text;
            string project = SessionRegistry.ProjectNameFromCwd(ev.Cwd);
            return $"{project} · {text}";
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

        // ===================== Session chip strip =====================

        private static SolidColorBrush BrushOf(string hex) => new(Hex(hex));

        /// <summary>State → dot colour (matches the character status dot).</summary>
        private static Brush StateBrush(SessionState s) => s switch
        {
            SessionState.Waiting => BrushOf("#F5A623"),
            SessionState.Working => BrushOf("#44BB44"),
            SessionState.Idle => BrushOf("#9AA0A6"),
            _ => BrushOf("#C8CCD0"),
        };

        private static string StateWord(SessionState s) => s switch
        {
            SessionState.Waiting => "확인 대기",
            SessionState.Working => "작업 중",
            SessionState.Idle => "대기",
            _ => "종료",
        };

        /// <summary>Rebuilds the chip strip from a fleet snapshot (one pill per active session, then "+N").</summary>
        private void RenderStrip(FleetSnapshot snap)
        {
            SessionStrip.Children.Clear();

            var active = snap.Sessions.Where(s => s.State != SessionState.Ended).ToList();
            if (active.Count == 0)
            {
                SessionStripHost.Visibility = Visibility.Collapsed;
                return;
            }

            SessionStripHost.Visibility = Visibility.Visible;

            foreach (var s in active.Take(MaxVisibleChips))
                SessionStrip.Children.Add(BuildChip(s));

            int overflow = active.Count - MaxVisibleChips;
            if (overflow > 0)
                SessionStrip.Children.Add(BuildOverflowChip(overflow));
        }

        private Border BuildChip(SessionInfo s)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = StateBrush(s.State),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            };
            var label = new TextBlock
            {
                Text = s.ProjectName,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf("#2D2D2D"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 66,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(dot);
            content.Children.Add(label);

            var chip = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 2, 7, 3),
                Margin = new Thickness(2, 0, 2, 0),
                Background = Brushes.White,
                BorderBrush = BrushOf("#E4E4E7"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = s.SessionId,
                ToolTip = $"{s.ProjectName} · {s.StatusLabel}",
                Child = content,
            };
            chip.MouseLeftButtonDown += Chip_Click;
            return chip;
        }

        private Border BuildOverflowChip(int n)
        {
            var label = new TextBlock
            {
                Text = $"+{n}",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf("#6B7280"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 2, 8, 3),
                Margin = new Thickness(2, 0, 2, 0),
                Background = BrushOf("#F3F4F6"),
                BorderBrush = BrushOf("#E4E4E7"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = $"그 외 {n}개 세션",
                Child = label,
            };
            chip.MouseLeftButtonDown += (_, e) => { e.Handled = true; ShowAllSessionsSummary(); };
            return chip;
        }

        private void Chip_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.Tag is string sid)
                ShowSessionDetail(sid);
        }

        private void ShowSessionDetail(string sessionId)
        {
            var s = _registry.Snapshot().Sessions.FirstOrDefault(x => x.SessionId == sessionId);
            if (s == null)
                return;
            string elapsed = FormatElapsed(DateTime.Now - s.StartedAt);
            string label = string.IsNullOrWhiteSpace(s.StatusLabel) ? StateWord(s.State) : s.StatusLabel;
            ShowSpeech($"{s.ProjectName} · {StateWord(s.State)}\n{label} · {elapsed}", 4500);
        }

        private void ShowAllSessionsSummary() => OpenHud();

        private static string FormatElapsed(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}초";
            if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}분";
            return $"{(int)t.TotalHours}시간 {t.Minutes}분";
        }

        // ===================== Session dashboard (HUD) =====================

        private void ShowHud_Click(object sender, RoutedEventArgs e) => OpenHud();

        private void ToggleHud()
        {
            if (_hudOpen) CloseHud();
            else OpenHud();
        }

        private void OpenHud()
        {
            _hudOpen = true;
            SampleCosts();
            RenderHud(_registry.Snapshot(), default);
            HudPopup.IsOpen = true;
            _hudTimer.Start();
            // Kick a full (all-pids) scan now so the resource footer fills promptly.
            CheckClaudeProcess();
            RefreshHud();
        }

        private void CloseHud()
        {
            _hudOpen = false;
            _hudTimer.Stop();
            HudPopup.IsOpen = false;
        }

        private void HudPopup_Closed(object? sender, EventArgs e)
        {
            // Fires when the popup dismisses itself (click-away / StaysOpen=False).
            _hudOpen = false;
            _hudTimer.Stop();
        }

        /// <summary>Refreshes each active session's transcript-derived usage into the registry.</summary>
        private void SampleCosts()
        {
            var activePaths = new List<string>();
            foreach (var s in _registry.Snapshot().Sessions)
            {
                if (s.State == SessionState.Ended || string.IsNullOrWhiteSpace(s.TranscriptPath))
                    continue;
                activePaths.Add(s.TranscriptPath!);
                UsageTotals? u = _usageReader.Read(s.TranscriptPath);
                if (u != null)
                    _registry.UpdateUsage(s.SessionId, u.TokensIn, u.TokensOut, u.CostUsd);
            }
            _usageReader.Forget(activePaths);
        }

        /// <summary>Dashboard tick: sample aggregate system usage and re-render (HUD open only).</summary>
        private void RefreshHud()
        {
            if (!_hudOpen)
                return;
            UsageSample res = _demoMode ? DemoSample() : _sysSampler.Sample(_claudePids);
            SampleCosts();
            RenderHud(_registry.Snapshot(), res);
        }

        /// <summary>Plausible resource numbers for demo/screenshot mode (no live CLIs to sample).</summary>
        private static UsageSample DemoSample() => new()
        {
            CpuPercent = 34.0 * Math.Max(1, Environment.ProcessorCount),
            RamBytes = (long)(1.9 * 1024 * 1024 * 1024),
            ProcessCount = 5,
        };

        private void RenderHud(FleetSnapshot snap, UsageSample res)
        {
            HudRows.Children.Clear();

            var active = snap.Sessions.Where(s => s.State != SessionState.Ended).ToList();
            if (active.Count == 0)
            {
                HudRows.Children.Add(new TextBlock
                {
                    Text = "실행 중인 세션이 없어요",
                    FontSize = 11.5,
                    Foreground = BrushOf("#9AA0A6"),
                    FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                });
            }
            else
            {
                foreach (var s in active)
                    HudRows.Children.Add(BuildHudRow(s));
            }

            double machineCpu = SystemUsageSampler.ToMachinePercent(res.CpuPercent);
            string ram = SystemUsageSampler.FormatRam(res.RamBytes);

            EnsureStatsDate();
            double todayMin = Math.Round(_settings.WorkSecondsToday / 60.0);
            double totalHr = Math.Round(_settings.WorkSecondsTotal / 3600.0, 1);

            HudFooter.Text =
                $"Claude 프로세스 {res.ProcessCount}개 · CPU {machineCpu:0}% · RAM {ram}\n" +
                $"오늘 {todayMin:0}분 · 누적 {totalHr:0.#}시간 함께했어요";
        }

        private FrameworkElement BuildHudRow(SessionInfo s)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });   // dot
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // project
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // elapsed
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });    // cost

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = StateBrush(s.State),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Grid.SetColumn(dot, 0);

            var name = new TextBlock
            {
                Text = s.ProjectName,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf("#2D2D2D"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                ToolTip = string.IsNullOrWhiteSpace(s.StatusLabel) ? StateWord(s.State) : s.StatusLabel,
            };
            Grid.SetColumn(name, 1);

            var meta = new TextBlock
            {
                Text = FormatElapsed(DateTime.Now - s.StartedAt),
                FontSize = 10.5,
                Foreground = BrushOf("#9AA0A6"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
            };
            Grid.SetColumn(meta, 2);

            var cost = new TextBlock
            {
                Text = s.EstimatedCostUsd > 0 ? $"${s.EstimatedCostUsd:0.00}" : "—",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf("#4B5563"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                ToolTip = s.TokensIn + s.TokensOut > 0
                    ? $"입력 {s.TokensIn:N0} · 출력 {s.TokensOut:N0} 토큰 (예상 비용)"
                    : "토큰 데이터 없음",
            };
            Grid.SetColumn(cost, 3);

            row.Children.Add(dot);
            row.Children.Add(name);
            row.Children.Add(meta);
            row.Children.Add(cost);
            return row;
        }

        /// <summary>
        /// For docs/screenshots: CLAUDEWIDGET_DEMO seeds a few fake sessions so the strip and
        /// aggregate state can be captured without live Claude Code CLIs. Unset for normal use.
        /// </summary>
        private void SeedDemoSessionsIfRequested()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDEWIDGET_DEMO")))
                return;

            _demoMode = true;
            _registry.Apply(new CliEvent { Kind = "tool", Tool = "Write", Text = "코드 쓰는 중…", SessionId = "demo-1", Cwd = @"C:\src\imjangpro" });
            _registry.Apply(new CliEvent { Kind = "tool", Tool = "Bash", Text = "명령 실행 중…", SessionId = "demo-2", Cwd = @"C:\src\claude_widget" });
            _registry.Apply(new CliEvent { Kind = "notify", Text = "권한 확인을 기다려요", SessionId = "demo-3", Cwd = @"C:\src\blog-api" });
            // Fake usage so the dashboard has costs to render in screenshots.
            _registry.UpdateUsage("demo-1", 128_000, 42_000, 0.61);
            _registry.UpdateUsage("demo-2", 36_000, 9_500, 0.14);
            _registry.UpdateUsage("demo-3", 210_000, 71_000, 1.06);
        }

        // ===================== Process detection =====================

        private void ProcessCheckTimer_Tick(object? sender, EventArgs e)
        {
            CheckClaudeProcess();
        }

        private void CheckClaudeProcess()
        {
            // Demo/screenshot override: CLAUDEWIDGET_FORCE=idle|working pins the state
            // without scanning (handy for docs/tests). Unset for normal detection.
            string? force = Environment.GetEnvironmentVariable("CLAUDEWIDGET_FORCE")?.ToLowerInvariant();
            if (force == "idle" || force == "working")
            {
                _isWorking = force == "working";
                ApplyEffectiveState();
                return;
            }

            // Full pid list only while the dashboard is open (for the resource footer);
            // otherwise first-match is enough for the liveness signal, keeping the tick cheap.
            _claudePids.Clear();
            _claudePids.AddRange(_scanner.Scan(collectAll: _hudOpen, excludePid: Environment.ProcessId));
            _isWorking = _claudePids.Count > 0;

            // Backstop: if Claude Code is nowhere in the process list, no session can still
            // be alive — clear any that never sent a SessionEnd (e.g. terminal was killed).
            if (!_isWorking)
                _registry.OnNoLiveProcesses();

            ApplyEffectiveState();
        }

        /// <summary>
        /// Recomputes the character's effective state from the session registry (authoritative
        /// when any session is known) or, as a fallback, the process poll. Also refreshes the
        /// chip strip. Cheap to call often; it only animates on an actual state change.
        /// </summary>
        private void ApplyEffectiveState()
        {
            FleetSnapshot snap = _registry.Snapshot();

            // Hook-first: when sessions are tracked (hooks installed), state is driven by the
            // registry, so the PEB-walking process poll can back off. Stay responsive when no
            // sessions are known or the dashboard needs fresh resource pids.
            if (_processCheckTimer != null)
            {
                double sec = (snap.ActiveCount > 0 && !_hudOpen) ? 6 : 2;
                if (Math.Abs(_processCheckTimer.Interval.TotalSeconds - sec) > 0.1)
                    _processCheckTimer.Interval = TimeSpan.FromSeconds(sec);
            }

            SessionState target = snap.ActiveCount > 0
                ? snap.AggregateState
                : (_isWorking ? SessionState.Working : SessionState.Idle);

            if (target != _effectiveState)
            {
                SessionState prev = _effectiveState;
                _effectiveState = target;
                // When sessions are tracked, the per-event handlers (prompt/stop/notify) do the
                // talking; only the pure process-poll fallback narrates the transition itself.
                TransitionTo(prev, target, narrate: snap.ActiveCount == 0);

                ApplyExpression(target);
                _idleSince = target == SessionState.Idle ? DateTime.Now : null;
                if (target != SessionState.Idle)
                    WakeUp();
            }

            RenderStrip(snap);
        }

        private static bool IsActivePosture(SessionState s) =>
            s == SessionState.Working || s == SessionState.Waiting;

        /// <summary>Animates the character between idle and active (working/waiting) postures.</summary>
        private void TransitionTo(SessionState prev, SessionState target, bool narrate)
        {
            bool wasActive = IsActivePosture(prev);
            bool isActive = IsActivePosture(target);

            if (isActive && !wasActive)
            {
                _workStartedAt = DateTime.Now;
                _breakNudged = false;

                // Switch to working mode
                StartStoryboard("LaptopFadeIn");
                StartStoryboard("ScreenGlowAnimation");
                AnimateArmPosition(toKeyboard: true);
                StartStoryboard("TypingAnimation");
                StartStoryboard("TypingBodyBob");
                StartStoryboard("EyesLookDownAnimation");

                if (narrate)
                    ShowSpeech(PickRandom(WorkStartMessages), 3000);
            }
            else if (!isActive && wasActive)
            {
                AccumulateWorkTime();
                HideActivity();

                // Switch to idle mode
                StartStoryboard("LaptopFadeOut");
                StopStoryboard("TypingAnimation");
                StopStoryboard("TypingBodyBob");
                StopStoryboard("ScreenGlowAnimation");
                AnimateArmPosition(toKeyboard: false);
                StartStoryboard("EyesLookCenterAnimation");

                var resetY = new DoubleAnimation(0, TimeSpan.FromSeconds(0.3));
                CharacterTranslate.BeginAnimation(TranslateTransform.YProperty, resetY);

                if (narrate)
                {
                    Celebrate();
                    ShowSpeech(PickRandom(WorkDoneMessages), 3000);
                }
            }

            SetStatusDotForState();
        }

        /// <summary>Status dot colour: pending update (blue) &gt; waiting (amber) &gt; working (green) &gt; idle (gray).</summary>
        private void SetStatusDotForState()
        {
            if (_pendingUpdate != null)
                return; // keep the blue update indicator

            string color = _effectiveState switch
            {
                SessionState.Waiting => "#F5A623", // blocked on you
                SessionState.Working => "#44BB44",
                _ => "#888888",
            };
            var anim = new ColorAnimation(
                (Color)ColorConverter.ConvertFromString(color), TimeSpan.FromSeconds(0.3));
            StatusDotFill.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            StatusDot.ToolTip = _effectiveState switch
            {
                SessionState.Waiting => "확인을 기다려요",
                SessionState.Working => "작업 중...",
                _ => "대기 중",
            };
        }

        // ===================== Expressions / idle sleep =====================

        private static void FadeOpacity(UIElement el, double to) =>
            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(to, TimeSpan.FromSeconds(0.2)));

        /// <summary>Swaps facial features to match the state — worried when waiting, happy otherwise.</summary>
        private void ApplyExpression(SessionState state)
        {
            bool waiting = state == SessionState.Waiting;
            FadeOpacity(LeftBrow, waiting ? 1 : 0);
            FadeOpacity(RightBrow, waiting ? 1 : 0);
            FadeOpacity(WorriedMouth, waiting ? 1 : 0);
            FadeOpacity(HappyMouth, waiting ? 0 : 1);
            if (waiting)
                WakeUp();
        }

        /// <summary>A quick sparkle-pop + hop to celebrate a completed task.</summary>
        private void Celebrate()
        {
            WakeUp();
            StartStoryboard("CelebrateAnimation");
        }

        private void GoToSleep()
        {
            if (_sleeping)
                return;
            _sleeping = true;
            LeftEyeGroup.Opacity = 0;
            RightEyeGroup.Opacity = 0;
            LeftEyeClosed.Opacity = 1;
            RightEyeClosed.Opacity = 1;
            StartStoryboard("SleepZzzAnimation");
        }

        private void WakeUp()
        {
            if (!_sleeping)
                return;
            _sleeping = false;
            StopStoryboard("SleepZzzAnimation");
            SleepZzz.Opacity = 0;
            LeftEyeGroup.Opacity = 1;
            RightEyeGroup.Opacity = 1;
            LeftEyeClosed.Opacity = 0;
            RightEyeClosed.Opacity = 0;
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

            // Doze off after a long, quiet idle stretch (no bubble in view).
            if (!_sleeping && _effectiveState == SessionState.Idle && _idleSince is DateTime since
                && (DateTime.Now - since).TotalMinutes >= SleepAfterMinutes
                && SpeechBubble.Opacity < 0.05)
            {
                GoToSleep();
                return;
            }
            if (_sleeping)
                return; // no blinks/looks while asleep

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

            WakeUp(); // any message rouses a sleeping widget

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
                ToggleHud();
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
            _pruneTimer?.Stop();
            _costTimer?.Stop();
            _hudTimer?.Stop();

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
