using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Claude_Widget.Services
{
    /// <summary>
    /// Turns the widget executable into its own Claude Code hook handler.
    ///
    /// Claude Code runs <c>ClaudeWidget.exe --hook</c> on lifecycle events and pipes a
    /// JSON payload on stdin. We distill that into a small <see cref="CliEvent"/> and drop
    /// it (as JSON) into an "inbox" folder. The running widget watches the folder
    /// (<see cref="CliMessageService"/>) and reacts — a speech bubble, a tool activity
    /// badge, a tray toast, etc. <c>--notify "text"</c> is a simple manual/testing entry point.
    /// </summary>
    public static class CliHookService
    {
        /// <summary>%APPDATA%\ClaudeWidget\inbox — one JSON file per pending event.</summary>
        public static string InboxDirectory
        {
            get
            {
                string dir = Path.Combine(AppInfo.DataDirectory, "inbox");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>Reads a hook payload from stdin and writes a distilled event to the inbox.</summary>
        public static void HandleHookFromStdin()
        {
            string raw;
            try
            {
                // Claude Code pipes UTF-8 JSON. Read the raw stdin stream as UTF-8 explicitly —
                // Console.In would otherwise decode with the console code page and mangle
                // non-ASCII (e.g. Korean) in the payload.
                using var reader = new StreamReader(
                    Console.OpenStandardInput(), new UTF8Encoding(false));
                raw = reader.ReadToEnd();
            }
            catch
            {
                return;
            }

            CliEvent? ev = Compose(raw);
            if (ev != null)
                WriteEvent(ev);
        }

        /// <summary>Distills a Claude Code hook JSON payload into a <see cref="CliEvent"/>, or null to ignore.</summary>
        public static CliEvent? Compose(string rawJson)
        {
            string eventName = "";
            string notifText = "";
            string tool = "";
            string sessionId = "";
            string cwd = "";
            string transcript = "";

            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("hook_event_name", out var ev))
                        eventName = ev.GetString() ?? "";
                    if (root.TryGetProperty("message", out var msg))
                        notifText = msg.GetString() ?? "";
                    if (root.TryGetProperty("tool_name", out var tn))
                        tool = tn.GetString() ?? "";
                    // Session identity — present on every hook payload.
                    if (root.TryGetProperty("session_id", out var sid))
                        sessionId = sid.GetString() ?? "";
                    if (root.TryGetProperty("cwd", out var cw))
                        cwd = cw.GetString() ?? "";
                    if (root.TryGetProperty("transcript_path", out var tp))
                        transcript = tp.GetString() ?? "";
                }
                catch
                {
                    // Malformed JSON: treat clearly-non-JSON stdin as plain text, else ignore.
                    string t = rawJson.Trim();
                    if (t.StartsWith("{") || t.StartsWith("["))
                        return null;
                    return new CliEvent { Kind = "text", Text = Truncate(t, 140) };
                }
            }

            CliEvent? ev2 = eventName switch
            {
                "PreToolUse" => new CliEvent { Kind = "tool", Tool = tool, Text = ToolLabel(tool) },
                "UserPromptSubmit" => new CliEvent { Kind = "prompt", Text = "요청 받았어요!" },
                "Notification" => new CliEvent
                {
                    Kind = "notify",
                    Text = string.IsNullOrWhiteSpace(notifText) ? "클로드가 기다리고 있어요" : Truncate(notifText, 140)
                },
                "Stop" => new CliEvent { Kind = "stop", Text = "작업을 마쳤어요!" },
                "SubagentStop" => new CliEvent { Kind = "substop", Text = "서브 작업 완료" },
                "SessionStart" => new CliEvent { Kind = "session_start", Text = "세션 시작! 함께 코딩해요" },
                "SessionEnd" => new CliEvent { Kind = "session_end", Text = "세션 종료. 수고했어요!" },
                _ when !string.IsNullOrWhiteSpace(notifText) => new CliEvent { Kind = "text", Text = Truncate(notifText, 140) },
                _ => null,
            };

            if (ev2 != null)
            {
                if (!string.IsNullOrWhiteSpace(sessionId)) ev2.SessionId = sessionId;
                if (!string.IsNullOrWhiteSpace(cwd)) ev2.Cwd = cwd;
                if (!string.IsNullOrWhiteSpace(transcript)) ev2.TranscriptPath = transcript;
            }
            return ev2;
        }

        /// <summary>Friendly Korean label for a Claude Code tool name.</summary>
        public static string ToolLabel(string tool) => tool switch
        {
            "Read" or "NotebookRead" => "파일 읽는 중…",
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "코드 쓰는 중…",
            "Bash" or "BashOutput" or "KillShell" => "명령 실행 중…",
            "Glob" => "파일 찾는 중…",
            "Grep" => "코드 검색 중…",
            "WebFetch" => "웹 읽는 중…",
            "WebSearch" => "웹 검색 중…",
            "Task" => "에이전트 실행 중…",
            "TodoWrite" => "할 일 정리 중…",
            _ when tool.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase) => "도구 사용 중…",
            _ => "작업 중…",
        };

        /// <summary>Writes a plain text event (used by <c>--notify</c>).</summary>
        public static void WriteText(string text) =>
            WriteEvent(new CliEvent { Kind = "text", Text = Truncate(text, 140) });

        private static void WriteEvent(CliEvent ev)
        {
            try
            {
                string json = JsonSerializer.Serialize(ev);
                string name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.json";
                File.WriteAllText(Path.Combine(InboxDirectory, name), json, new UTF8Encoding(false));
            }
            catch
            {
                // A dropped notification should never surface an error.
            }
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
