using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claude_Widget.Services
{
    /// <summary>
    /// User-facing widget preferences plus lightweight usage stats,
    /// persisted as JSON in %APPDATA%\ClaudeWidget\settings.json.
    /// </summary>
    public class WidgetSettings
    {
        // --- Appearance / behavior ---
        public double Scale { get; set; } = 1.0;
        public double? Left { get; set; }
        public double? Top { get; set; }
        public bool Topmost { get; set; } = true;
        public string Theme { get; set; } = "Claude";
        public bool SpeechEnabled { get; set; } = true;
        public bool AutoUpdate { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;

        /// <summary>Show per-tool activity (badge + label) from PreToolUse hooks.</summary>
        public bool ToolAwareness { get; set; } = true;

        // --- Usage stats ---
        /// <summary>Date (yyyy-MM-dd) the WorkSecondsToday counter belongs to.</summary>
        public string StatsDate { get; set; } = "";
        public double WorkSecondsToday { get; set; }
        public double WorkSecondsTotal { get; set; }
        public long SessionsTotal { get; set; }

        /// <summary>Skip auto-prompting for this version again (user chose "later").</summary>
        public string SkippedUpdateVersion { get; set; } = "";
    }

    public static class SettingsService
    {
        private static readonly string FilePath =
            Path.Combine(AppInfo.DataDirectory, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        public static WidgetSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    var s = JsonSerializer.Deserialize<WidgetSettings>(json);
                    if (s != null)
                        return s;
                }
            }
            catch
            {
                // Corrupt/unreadable settings should never crash the widget — fall back to defaults.
            }
            return new WidgetSettings();
        }

        public static void Save(WidgetSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // Best-effort persistence; ignore transient IO errors.
            }
        }
    }
}
