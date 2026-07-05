using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Claude_Widget.Services
{
    /// <summary>Coarse per-session lifecycle state, ordered by urgency (lower = more urgent).</summary>
    public enum SessionState
    {
        /// <summary>Blocked on the user — a permission prompt or attention notification.</summary>
        Waiting = 0,
        /// <summary>Actively running (a prompt was submitted, tools are firing).</summary>
        Working = 1,
        /// <summary>Turn finished (Stop); the session is alive but waiting for the next prompt.</summary>
        Idle = 2,
        /// <summary>SessionEnd received (or pruned). Shown briefly, then dropped.</summary>
        Ended = 3,
    }

    /// <summary>Immutable view of a single session, handed to the UI in a snapshot.</summary>
    public sealed class SessionInfo
    {
        public string SessionId { get; init; } = "";
        /// <summary>Last path segment of <see cref="Cwd"/> (e.g. "imjangpro"); "세션" if unknown.</summary>
        public string ProjectName { get; init; } = "세션";
        public string? Cwd { get; init; }
        public string? TranscriptPath { get; init; }
        public SessionState State { get; init; }
        /// <summary>Raw Claude Code tool name of the most recent tool event, if any.</summary>
        public string? CurrentTool { get; init; }
        /// <summary>Human label for the current activity (e.g. "코드 쓰는 중…").</summary>
        public string StatusLabel { get; init; } = "";
        public DateTime StartedAt { get; init; }
        public DateTime LastEventAt { get; init; }

        // --- Cost / usage (populated later from the transcript). ---
        public double EstimatedCostUsd { get; init; }
        public long TokensIn { get; init; }
        public long TokensOut { get; init; }
    }

    /// <summary>Aggregated view across all known sessions.</summary>
    public sealed class FleetSnapshot
    {
        public IReadOnlyList<SessionInfo> Sessions { get; init; } = Array.Empty<SessionInfo>();
        /// <summary>Most-urgent state across active sessions (drives the character).</summary>
        public SessionState AggregateState { get; init; } = SessionState.Idle;
        /// <summary>Sessions that have not ended.</summary>
        public int ActiveCount { get; init; }
        /// <summary>The session the character should "speak for" (most urgent / most recent).</summary>
        public SessionInfo? Primary { get; init; }
    }

    /// <summary>
    /// Tracks every concurrent Claude Code session by <c>session_id</c> and collapses them
    /// into a single <see cref="FleetSnapshot"/> for the widget. This is what lets one
    /// character + a chip strip represent N sessions without the old event ping-pong.
    ///
    /// Thread-safe: hook events arrive on a <see cref="System.IO.FileSystemWatcher"/>
    /// threadpool thread, so every mutation locks. <see cref="Changed"/> fires off the UI
    /// thread; subscribers marshal to the dispatcher themselves.
    /// </summary>
    public sealed class SessionRegistry
    {
        private sealed class Entry
        {
            public string SessionId = "";
            public string? Cwd;
            public string? TranscriptPath;
            public SessionState State = SessionState.Working;
            public string? CurrentTool;
            public string StatusLabel = "";
            public DateTime StartedAt;
            public DateTime LastEventAt;
            public DateTime? EndedAt;
            public double EstimatedCostUsd;
            public long TokensIn;
            public long TokensOut;
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _sessions = new();

        /// <summary>Ended sessions linger this long so the strip can show they finished.</summary>
        public TimeSpan EndedGrace { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>Absolute backstop: drop a session with no events for this long (crash without SessionEnd).</summary>
        public TimeSpan StaleAfter { get; set; } = TimeSpan.FromHours(6);

        /// <summary>Raised (off the UI thread) whenever the fleet changes in a user-visible way.</summary>
        public event Action? Changed;

        /// <summary>
        /// Applies a session-scoped hook event. Returns true if it belonged to a session
        /// (i.e. carried a session id); global events like manual --notify text return false
        /// and should be handled by the caller directly.
        /// </summary>
        public bool Apply(CliEvent ev)
        {
            if (ev == null || string.IsNullOrWhiteSpace(ev.SessionId))
                return false;

            bool changed;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(ev.SessionId!, out var e))
                {
                    e = new Entry
                    {
                        SessionId = ev.SessionId!,
                        StartedAt = Now,
                    };
                    _sessions[ev.SessionId!] = e;
                }

                if (!string.IsNullOrWhiteSpace(ev.Cwd)) e.Cwd = ev.Cwd;
                if (!string.IsNullOrWhiteSpace(ev.TranscriptPath)) e.TranscriptPath = ev.TranscriptPath;
                e.LastEventAt = Now;

                changed = ApplyKind(e, ev);
            }

            if (changed)
                RaiseChanged();
            return true;
        }

        /// <summary>Mutates the entry for the event kind. Returns true if the visible state changed.</summary>
        private bool ApplyKind(Entry e, CliEvent ev)
        {
            var prevState = e.State;
            var prevLabel = e.StatusLabel;
            var prevTool = e.CurrentTool;

            switch (ev.Kind)
            {
                case "session_start":
                    e.State = SessionState.Working;
                    e.EndedAt = null;
                    e.StatusLabel = "세션 시작";
                    break;

                case "prompt":
                    e.State = SessionState.Working;
                    e.StatusLabel = "요청 받았어요";
                    break;

                case "tool":
                    e.State = SessionState.Working;
                    e.CurrentTool = ev.Tool;
                    e.StatusLabel = ev.Text;
                    break;

                case "notify":
                    // Highest-urgency state: blocked on the user.
                    e.State = SessionState.Waiting;
                    e.StatusLabel = ev.Text;
                    break;

                case "substop":
                    // A subagent finished; the main turn typically continues. Keep working.
                    e.State = SessionState.Working;
                    e.StatusLabel = "서브 작업 완료";
                    break;

                case "stop":
                    e.State = SessionState.Idle;
                    e.CurrentTool = null;
                    e.StatusLabel = "작업 완료";
                    break;

                case "session_end":
                    e.State = SessionState.Ended;
                    e.EndedAt = Now;
                    e.StatusLabel = "세션 종료";
                    break;

                default:
                    // Non-session-shaped event that still carried an id; note it, no state change.
                    e.StatusLabel = string.IsNullOrEmpty(ev.Text) ? e.StatusLabel : ev.Text;
                    break;
            }

            return e.State != prevState || e.StatusLabel != prevLabel || e.CurrentTool != prevTool;
        }

        /// <summary>
        /// Removes ended sessions past their grace window and any session stale past
        /// <see cref="StaleAfter"/>. Call periodically. Fires <see cref="Changed"/> if anything dropped.
        /// </summary>
        public void Prune()
        {
            bool changed = false;
            lock (_gate)
            {
                var now = Now;
                var drop = new List<string>();
                foreach (var (id, e) in _sessions)
                {
                    if (e.State == SessionState.Ended && e.EndedAt is DateTime end
                        && now - end >= EndedGrace)
                        drop.Add(id);
                    else if (now - e.LastEventAt >= StaleAfter)
                        drop.Add(id);
                }
                foreach (var id in drop)
                    changed |= _sessions.Remove(id);
            }
            if (changed)
                RaiseChanged();
        }

        /// <summary>
        /// Fallback signal from the process poller: when Claude Code is nowhere in the
        /// process list, no session can still be alive, so clear everything that has not
        /// already ended. Guards against sessions whose terminal was killed without a
        /// SessionEnd hook ever firing.
        /// </summary>
        public void OnNoLiveProcesses()
        {
            bool changed = false;
            lock (_gate)
            {
                if (_sessions.Count == 0)
                    return;
                _sessions.Clear();
                changed = true;
            }
            if (changed)
                RaiseChanged();
        }

        /// <summary>Attaches computed cost/usage to a session (called by the cost sampler).</summary>
        public void UpdateUsage(string sessionId, long tokensIn, long tokensOut, double costUsd)
        {
            bool changed = false;
            lock (_gate)
            {
                if (_sessions.TryGetValue(sessionId, out var e))
                {
                    changed = e.TokensIn != tokensIn || e.TokensOut != tokensOut
                              || Math.Abs(e.EstimatedCostUsd - costUsd) > 0.0001;
                    e.TokensIn = tokensIn;
                    e.TokensOut = tokensOut;
                    e.EstimatedCostUsd = costUsd;
                }
            }
            if (changed)
                RaiseChanged();
        }

        /// <summary>Builds an immutable snapshot for the UI.</summary>
        public FleetSnapshot Snapshot()
        {
            lock (_gate)
            {
                var list = _sessions.Values
                    .OrderByDescending(e => e.LastEventAt)
                    .Select(ToInfo)
                    .ToList();

                var active = list.Where(s => s.State != SessionState.Ended).ToList();

                SessionState agg = SessionState.Idle;
                if (active.Any(s => s.State == SessionState.Waiting)) agg = SessionState.Waiting;
                else if (active.Any(s => s.State == SessionState.Working)) agg = SessionState.Working;

                // Primary = the session driving the aggregate state, most-recent first.
                SessionInfo? primary =
                    active.Where(s => s.State == agg).OrderByDescending(s => s.LastEventAt).FirstOrDefault()
                    ?? list.FirstOrDefault();

                return new FleetSnapshot
                {
                    Sessions = list,
                    AggregateState = agg,
                    ActiveCount = active.Count,
                    Primary = primary,
                };
            }
        }

        private static SessionInfo ToInfo(Entry e) => new()
        {
            SessionId = e.SessionId,
            ProjectName = ProjectNameFromCwd(e.Cwd),
            Cwd = e.Cwd,
            TranscriptPath = e.TranscriptPath,
            State = e.State,
            CurrentTool = e.CurrentTool,
            StatusLabel = e.StatusLabel,
            StartedAt = e.StartedAt,
            LastEventAt = e.LastEventAt,
            EstimatedCostUsd = e.EstimatedCostUsd,
            TokensIn = e.TokensIn,
            TokensOut = e.TokensOut,
        };

        /// <summary>Last folder segment of a path; falls back to "세션".</summary>
        public static string ProjectNameFromCwd(string? cwd)
        {
            if (string.IsNullOrWhiteSpace(cwd))
                return "세션";
            string trimmed = cwd.TrimEnd('\\', '/');
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch { /* a subscriber fault must not corrupt the registry */ }
        }

        // Centralized clock so tests/backstops read one source (kept simple: DateTime.Now).
        private static DateTime Now => DateTime.Now;
    }
}
