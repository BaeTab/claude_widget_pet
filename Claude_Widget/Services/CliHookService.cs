using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Claude_Widget.Services
{
    /// <summary>
    /// Turns the widget executable into its own Claude Code hook handler.
    ///
    /// Claude Code can be configured to run a command on lifecycle events
    /// (Notification, Stop, SubagentStop). We register `ClaudeWidget.exe --hook`
    /// as that command. When invoked, the CLI pipes a JSON payload on stdin; we
    /// distill it into a short, friendly line and drop it into an "inbox" folder.
    /// The running widget watches that folder (<see cref="CliMessageService"/>) and
    /// pops the line up as a speech bubble.
    ///
    /// `--notify "text"` is a simpler entry point for manual/testing use.
    /// </summary>
    public static class CliHookService
    {
        /// <summary>%APPDATA%\ClaudeWidget\inbox — one file per pending message.</summary>
        public static string InboxDirectory
        {
            get
            {
                string dir = Path.Combine(AppInfo.DataDirectory, "inbox");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>Reads a hook payload from stdin and writes a friendly message to the inbox.</summary>
        public static void HandleHookFromStdin()
        {
            string raw;
            try
            {
                raw = Console.In.ReadToEnd();
            }
            catch
            {
                return;
            }

            string message = ComposeMessage(raw);
            if (!string.IsNullOrWhiteSpace(message))
                WriteMessage(message);
        }

        /// <summary>Distills a Claude Code hook JSON payload into a short display line.</summary>
        public static string ComposeMessage(string rawJson)
        {
            string eventName = "";
            string notifText = "";

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
                }
                catch
                {
                    // Malformed JSON: don't dump the raw blob. Only treat stdin as a plain
                    // message when it clearly isn't JSON (e.g. someone piped plain text).
                    string trimmed = rawJson.Trim();
                    notifText = (trimmed.StartsWith("{") || trimmed.StartsWith("[")) ? "" : trimmed;
                }
            }

            string text = eventName switch
            {
                "Notification" => string.IsNullOrWhiteSpace(notifText) ? "클로드가 기다리고 있어요" : "알림: " + notifText,
                "Stop" => "작업을 마쳤어요!",
                "SubagentStop" => "서브 작업 완료",
                "SessionStart" => "세션 시작! 함께 코딩해요",
                "SessionEnd" => "세션 종료. 수고했어요!",
                _ => !string.IsNullOrWhiteSpace(notifText) ? notifText
                    : (!string.IsNullOrWhiteSpace(eventName) ? eventName : "")
            };

            return Truncate(text, 140);
        }

        /// <summary>Writes a single message file into the inbox (best-effort).</summary>
        public static void WriteMessage(string text)
        {
            try
            {
                string name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.txt";
                File.WriteAllText(Path.Combine(InboxDirectory, name), text, new UTF8Encoding(false));
            }
            catch
            {
                // Ignore — a dropped notification should never surface an error.
            }
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
