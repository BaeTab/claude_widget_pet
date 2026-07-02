namespace Claude_Widget.Services
{
    /// <summary>
    /// A distilled Claude Code signal passed from the hook handler (writer) to the
    /// running widget (reader) via a JSON file in the inbox.
    /// </summary>
    public sealed class CliEvent
    {
        /// <summary>text | tool | notify | prompt | stop | substop | session</summary>
        public string Kind { get; set; } = "text";

        /// <summary>Raw Claude Code tool name for Kind == "tool" (e.g. "Bash", "Read", "mcp__x__y").</summary>
        public string? Tool { get; set; }

        /// <summary>Ready-to-show Korean line for the speech bubble.</summary>
        public string Text { get; set; } = "";
    }
}
