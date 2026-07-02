using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claude_Widget.Services
{
    /// <summary>
    /// Enables/disables the widget's Claude Code integration by safely merging
    /// hook entries into the user's global settings (%USERPROFILE%\.claude\settings.json).
    ///
    /// The merge is idempotent and additive: existing hooks are preserved, and only
    /// entries whose command references this executable with <c>--hook</c> are touched.
    /// </summary>
    public static class CliHookInstaller
    {
        // Lifecycle events we surface as speech bubbles.
        private static readonly string[] Events = { "Notification", "Stop", "SubagentStop" };

        public static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json");

        private static string ExePath =>
            Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath ?? "ClaudeWidget.exe";

        private static string HookCommand => $"\"{ExePath}\" --hook";

        private static bool IsOurCommand(string? command) =>
            !string.IsNullOrEmpty(command)
            && command.Contains("--hook", StringComparison.OrdinalIgnoreCase)
            && command.Contains("ClaudeWidget", StringComparison.OrdinalIgnoreCase);

        public static bool IsEnabled()
        {
            try
            {
                var root = LoadRoot();
                if (root["hooks"] is not JsonObject hooks)
                    return false;

                foreach (var ev in Events)
                {
                    if (hooks[ev] is JsonArray arr && ContainsOurHook(arr))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Adds our hook entries. Returns true if the file was written successfully.</summary>
        public static bool Enable()
        {
            try
            {
                var root = LoadRoot();
                if (root["hooks"] is not JsonObject hooks)
                {
                    hooks = new JsonObject();
                    root["hooks"] = hooks;
                }

                foreach (var ev in Events)
                {
                    if (hooks[ev] is not JsonArray arr)
                    {
                        arr = new JsonArray();
                        hooks[ev] = arr;
                    }
                    if (!ContainsOurHook(arr))
                        arr.Add(BuildHookGroup());
                }

                Save(root);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Removes only our hook entries, leaving everything else intact.</summary>
        public static bool Disable()
        {
            try
            {
                var root = LoadRoot();
                if (root["hooks"] is not JsonObject hooks)
                    return true;

                foreach (var ev in Events)
                {
                    if (hooks[ev] is not JsonArray arr)
                        continue;

                    for (int i = arr.Count - 1; i >= 0; i--)
                    {
                        if (arr[i] is JsonObject group && GroupHasOurHook(group))
                            arr.RemoveAt(i);
                    }
                    if (arr.Count == 0)
                        hooks.Remove(ev);
                }

                Save(root);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // --- helpers ---

        private static JsonObject LoadRoot()
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                if (!string.IsNullOrWhiteSpace(json)
                    && JsonNode.Parse(json) is JsonObject obj)
                    return obj;
            }
            return new JsonObject();
        }

        private static void Save(JsonObject root)
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(SettingsPath, root.ToJsonString(opts));
        }

        private static JsonObject BuildHookGroup() => new()
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = HookCommand,
                }
            }
        };

        private static bool ContainsOurHook(JsonArray arr) =>
            arr.OfType<JsonObject>().Any(GroupHasOurHook);

        private static bool GroupHasOurHook(JsonObject group)
        {
            if (group["hooks"] is not JsonArray hooks)
                return false;
            return hooks.OfType<JsonObject>()
                .Any(h => IsOurCommand(h["command"]?.GetValue<string>()));
        }
    }
}
