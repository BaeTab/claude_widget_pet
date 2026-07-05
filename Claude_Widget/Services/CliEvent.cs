namespace Claude_Widget.Services
{
    /// <summary>
    /// A distilled Claude Code signal passed from the hook handler (writer) to the
    /// running widget (reader) via a JSON file in the inbox.
    ///
    /// Every Claude Code hook payload carries <c>session_id</c>, <c>cwd</c> and
    /// <c>transcript_path</c>. Capturing them here is what lets the widget tell
    /// concurrent sessions apart (see <see cref="SessionRegistry"/>) and surface
    /// per-project / per-session state instead of one blurred global state.
    /// </summary>
    public sealed class CliEvent
    {
        /// <summary>
        /// text | tool | notify | prompt | stop | substop | session_start | session_end.
        /// (Legacy "session" is still accepted defensively by older readers.)
        /// </summary>
        public string Kind { get; set; } = "text";

        /// <summary>Raw Claude Code tool name for Kind == "tool" (e.g. "Bash", "Read", "mcp__x__y").</summary>
        public string? Tool { get; set; }

        /// <summary>Ready-to-show Korean line for the speech bubble.</summary>
        public string Text { get; set; } = "";

        /// <summary>Claude Code session id — the key used to group events per session.</summary>
        public string? SessionId { get; set; }

        /// <summary>Working directory of the session; its last path segment is the project name.</summary>
        public string? Cwd { get; set; }

        /// <summary>Path to the session transcript (JSONL) — used to compute tokens/cost.</summary>
        public string? TranscriptPath { get; set; }
    }
}
